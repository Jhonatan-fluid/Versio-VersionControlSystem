using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Versio.Core.Logging;

namespace Versio.Web.Sources;

/// <summary>
/// Reads a repository through GitHub's REST API, following Git LFS pointers to their content.
/// </summary>
/// <remarks>
/// <para>
/// <b>Trees and blobs, never a clone.</b> The Git Trees API lists a whole commit in one call and
/// the Blobs API returns one file by its hash, which is all a viewer needs. Nothing touches disk
/// and nothing starts a process (docs/web/PLAN.md Step 2; the Arch tests hold the server to it).
/// </para>
/// <para>
/// <b>A refusal is reported as "not found"</b> unless GitHub said precisely why. GitHub answers a
/// private repository's outsiders with 404 so its existence does not leak, and turning a bare 403
/// into anything more specific would undo that (security, hygiene).
/// </para>
/// </remarks>
public sealed class GitHubRepoSource(
    IHttpClientFactory clients,
    IOptions<WebOptions> options,
    IVersioLogger log) : IRepoSource
{
    /// <summary>The named client for the REST API and LFS batch calls.</summary>
    public const string ApiClient = "github";

    /// <summary>The named client for LFS downloads, built on <see cref="LfsGuard.CreateHandler"/>.</summary>
    public const string LfsClient = "github-lfs";

    private GitHubOptions GitHub => options.Value.GitHub;

    public Task<RepoResult<RepoInfo>> GetRepoAsync(RepoAccess access, string owner, string repo, CancellationToken ct) =>
        GetJsonAsync(access, $"repos/{E(owner)}/{E(repo)}", root => new RepoInfo(
            root.GetProperty("owner").GetProperty("login").GetString() ?? owner,
            root.GetProperty("name").GetString() ?? repo,
            root.GetProperty("private").GetBoolean(),
            root.GetProperty("default_branch").GetString() ?? "main"), ct);

    /// <remarks>
    /// Page by page, until a short page or <see cref="BranchPages"/>. The first version read one
    /// page of 100, so in a repository with more branches the rest were missing from the picker
    /// (found in review). The cap keeps a repository with thousands of branches from costing
    /// thousands of calls on every load; a ref past it still opens from its URL. A cut list says
    /// so (<see cref="BranchList.Truncated"/>): the first version stopped at the cap in silence, so
    /// a repository with more branches looked complete in the picker (found in review).
    /// </remarks>
    public async Task<RepoResult<BranchList>> GetBranchesAsync(RepoAccess access, string owner, string repo, CancellationToken ct)
    {
        var all = new List<BranchInfo>();
        for (var page = 1; page <= BranchPages; page++)
        {
            var one = await GetJsonAsync<IReadOnlyList<BranchInfo>>(access,
                $"repos/{E(owner)}/{E(repo)}/branches?per_page={BranchPageSize}&page={page.ToString(CultureInfo.InvariantCulture)}", root => root.EnumerateArray()
                    .Select(b => new BranchInfo(b.GetProperty("name").GetString() ?? "", b.GetProperty("commit").GetProperty("sha").GetString() ?? ""))
                    .ToList(), ct).ConfigureAwait(false);
            if (!one.Ok)
            {
                return one.As<BranchList>();
            }

            all.AddRange(one.Value!);
            if (one.Value!.Count < BranchPageSize)
            {
                return RepoResult<BranchList>.Of(new BranchList(all, false));
            }
        }

        // Every page full, up to the cap: there may be more, or exactly this many. One more page
        // decides, and is paid only by a repository this large.
        var past = await GetJsonAsync(access,
            $"repos/{E(owner)}/{E(repo)}/branches?per_page={BranchPageSize}&page={(BranchPages + 1).ToString(CultureInfo.InvariantCulture)}",
            root => root.GetArrayLength(), ct).ConfigureAwait(false);
        return RepoResult<BranchList>.Of(new BranchList(all, !past.Ok || past.Value > 0));
    }

    internal const int BranchPageSize = 100;

    internal const int BranchPages = 10;

    /// <remarks>
    /// Asks for <c>application/vnd.github.sha</c>, which answers with the forty characters and
    /// nothing else. The first version fetched the commit as JSON, which carries up to 300 files'
    /// patches, on every page load, to read one field (found reviewing).
    /// </remarks>
    public async Task<RepoResult<string>> ResolveCommitAsync(RepoAccess access, string owner, string repo, string reference, CancellationToken ct)
    {
        using var request = Request(access, HttpMethod.Get, Api($"repos/{E(owner)}/{E(repo)}/commits/{E(reference)}"));
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/vnd.github.sha");
        var bytes = await SendForBytesAsync(clients.CreateClient(ApiClient), request, 1024, ct).ConfigureAwait(false);
        if (!bytes.Ok)
        {
            return bytes.As<string>();
        }

        var sha = Encoding.ASCII.GetString(bytes.Value!).Trim();
        return sha.Length == 40 && sha.All(Uri.IsHexDigit)
            ? RepoResult<string>.Of(sha.ToLowerInvariant())
            : RepoResult<string>.Fail(RepoFailure.Upstream, "not a commit id");
    }

    public Task<RepoResult<TreeSnapshot>> GetTreeAsync(RepoAccess access, string owner, string repo, string commitSha, CancellationToken ct) =>
        GetJsonAsync(access, $"repos/{E(owner)}/{E(repo)}/git/trees/{E(commitSha)}?recursive=1", root => new TreeSnapshot(
            commitSha,
            root.GetProperty("tree").EnumerateArray()
                .Where(e => e.GetProperty("type").GetString() == "blob")
                .Select(e => new TreeEntry(
                    e.GetProperty("path").GetString() ?? "",
                    e.GetProperty("sha").GetString() ?? "",
                    e.TryGetProperty("size", out var size) ? size.GetInt64() : 0))
                .ToList(),
            root.TryGetProperty("truncated", out var t) && t.GetBoolean()), ct);

    public async Task<RepoResult<byte[]>> GetBlobAsync(RepoAccess access, string owner, string repo, string blobSha, long maxBytes, CancellationToken ct)
    {
        using var request = Request(access, HttpMethod.Get, Api($"repos/{E(owner)}/{E(repo)}/git/blobs/{E(blobSha)}"));
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/vnd.github.raw");
        var bytes = await SendForBytesAsync(clients.CreateClient(ApiClient), request, maxBytes, ct).ConfigureAwait(false);
        if (!bytes.Ok || LfsPointer.Parse(bytes.Value!) is not { } pointer)
        {
            return bytes;
        }

        // The blob is a pointer; the file is in LFS. A TIA project's export is (Open item 1).
        if (pointer.Size > maxBytes)
        {
            return RepoResult<byte[]>.Fail(RepoFailure.TooLarge, $"LFS object of {pointer.Size} bytes");
        }

        try
        {
            return await GetLfsObjectAsync(access, owner, repo, pointer, maxBytes, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            // The batch call itself failing, or answering in a shape LFS does not document.
            log.Warn(LogCategory.Web, $"LFS batch for {owner}/{repo} failed: {e.GetType().Name}");
            return RepoResult<byte[]>.Fail(RepoFailure.Upstream, "LFS batch failed");
        }
    }

    public async Task<RepoResult<IReadOnlyList<CommitInfo>>> GetCommitsAsync(
        RepoAccess access, string owner, string repo, string reference, string? path, int limit, CancellationToken ct)
    {
        var query = $"repos/{E(owner)}/{E(repo)}/commits?sha={E(reference)}&per_page={Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture)}"
                    + (string.IsNullOrEmpty(path) ? "" : "&path=" + E(path));
        return await GetJsonAsync<IReadOnlyList<CommitInfo>>(access, query, root => root.EnumerateArray().Select(c =>
        {
            var commit = c.GetProperty("commit");
            var author = commit.GetProperty("author");
            return new CommitInfo(
                c.GetProperty("sha").GetString() ?? "",
                commit.GetProperty("message").GetString() ?? "",
                author.GetProperty("name").GetString() ?? "",
                author.GetProperty("date").GetDateTimeOffset());
        }).ToList(), ct).ConfigureAwait(false);
    }

    /// <remarks>
    /// Sorted by update, newest first: the list is for "what is waiting for review now". One page
    /// (the API's cap is 100); a repository with more open pull requests than that shows the
    /// newest and says the list was cut, as the branch list does.
    /// </remarks>
    public Task<RepoResult<IReadOnlyList<PullInfo>>> GetPullsAsync(RepoAccess access, string owner, string repo, int limit, CancellationToken ct) =>
        GetJsonAsync<IReadOnlyList<PullInfo>>(access,
            $"repos/{E(owner)}/{E(repo)}/pulls?state=open&sort=updated&direction=desc&per_page={Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture)}",
            root => root.EnumerateArray().Select(Pull).ToList(), ct);

    public Task<RepoResult<PullDetail>> GetPullAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct) =>
        GetJsonAsync(access, $"repos/{E(owner)}/{E(repo)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}",
            root => new PullDetail(Pull(root), Text(root, "body")), ct);

    /// <remarks>
    /// The compare endpoint is the only REST answer that names the merge-base. It also carries up
    /// to 300 files' patches, which this read discards; <c>per_page=1</c> at least keeps its
    /// commit list to one. An answer whose merge-base is not a commit id is "upstream", so a
    /// malformed reply can never be compared against as if it were one.
    /// </remarks>
    public async Task<RepoResult<string>> GetMergeBaseAsync(RepoAccess access, string owner, string repo, string baseSha, string headSha, CancellationToken ct)
    {
        var result = await GetJsonAsync(access, $"repos/{E(owner)}/{E(repo)}/compare/{E(baseSha)}...{E(headSha)}?per_page=1",
            root => root.GetProperty("merge_base_commit").GetProperty("sha").GetString() ?? "", ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return result;
        }

        return result.Value!.Length == 40 && result.Value.All(Uri.IsHexDigit)
            ? RepoResult<string>.Of(result.Value.ToLowerInvariant())
            : RepoResult<string>.Fail(RepoFailure.Upstream, "not a commit id");
    }

    /// <remarks>
    /// Page by page to GitHub's own cap (30 pages of 100 = 3,000 files), stopping at a short
    /// page. A thirtieth full page is reported as truncated: GitHub lists no further, so a
    /// pull request that large may have files this list does not name, and the caller must not
    /// treat the list as the whole change.
    /// </remarks>
    public async Task<RepoResult<PullFileList>> GetPullFilesAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        var all = new List<PullFile>();
        for (var page = 1; page <= PullFilePages; page++)
        {
            var one = await GetJsonAsync<IReadOnlyList<PullFile>>(access,
                $"repos/{E(owner)}/{E(repo)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/files?per_page={PullFilePageSize}&page={page.ToString(CultureInfo.InvariantCulture)}",
                root => root.EnumerateArray().Select(ChangedFile).ToList(), ct).ConfigureAwait(false);
            if (!one.Ok)
            {
                return one.As<PullFileList>();
            }

            all.AddRange(one.Value!);
            if (one.Value!.Count < PullFilePageSize)
            {
                return RepoResult<PullFileList>.Of(new PullFileList(all, false));
            }
        }

        return RepoResult<PullFileList>.Of(new PullFileList(all, true));
    }

    internal const int PullFilePageSize = 100;

    internal const int PullFilePages = 30;

    /// <remarks>
    /// GitHub's compare lists at most 300 files and its paging pages COMMITS, not files, so there
    /// is no asking for more. Exactly 300 is reported as truncated: it is the cap, and GitHub does
    /// not say whether it stopped there or the change happens to be that size.
    /// </remarks>
    public async Task<RepoResult<PullFileList>> GetCompareFilesAsync(RepoAccess access, string owner, string repo, string baseSha, string headSha, CancellationToken ct)
    {
        var files = await GetJsonAsync<IReadOnlyList<PullFile>>(access, $"repos/{E(owner)}/{E(repo)}/compare/{E(baseSha)}...{E(headSha)}?per_page=1",
            root => root.TryGetProperty("files", out var f) ? f.EnumerateArray().Select(ChangedFile).ToList() : [], ct).ConfigureAwait(false);
        return files.Ok
            ? RepoResult<PullFileList>.Of(new PullFileList(files.Value!, files.Value!.Count >= CompareFileCap))
            : files.As<PullFileList>();
    }

    internal const int CompareFileCap = 300;

    private static PullFile ChangedFile(JsonElement f) => new(
        f.GetProperty("filename").GetString() ?? "",
        f.GetProperty("status").GetString() ?? "",
        Text(f, "previous_filename") is { Length: > 0 } previous ? previous : null,
        Text(f, "patch") is { Length: > 0 } patch ? patch : null);

    public Task<RepoResult<IReadOnlyList<ReleaseInfo>>> GetReleasesAsync(RepoAccess access, string owner, string repo, int limit, CancellationToken ct) =>
        GetJsonAsync<IReadOnlyList<ReleaseInfo>>(access,
            $"repos/{E(owner)}/{E(repo)}/releases?per_page={Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture)}",
            root => root.EnumerateArray().Where(r => !r.GetProperty("draft").GetBoolean()).Select(Release).ToList(), ct);

    public Task<RepoResult<ReleaseDetail>> GetReleaseAsync(RepoAccess access, string owner, string repo, string tag, CancellationToken ct) =>
        GetJsonAsync(access, $"repos/{E(owner)}/{E(repo)}/releases/tags/{E(tag)}",
            root => new ReleaseDetail(Release(root), Text(root, "body")), ct);

    // ---- review reads (docs/web/PLAN.md, *Pull request review*, slice 1) ------------------------

    private const string SummaryQuery = """
        query($owner: String!, $repo: String!, $number: Int!) {
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $number) {
              state isDraft merged mergeable reviewDecision headRefOid baseRefName
              baseRef { target { oid } }
              labels(first: 30) { nodes { name color } }
              assignees(first: 20) { nodes { login } }
              reviewRequests(first: 30) { nodes { requestedReviewer { __typename ... on User { login } ... on Team { name } } } }
              latestOpinionatedReviews(first: 30) { nodes { author { login } state } }
              id viewerDidAuthor
              viewerLatestReview { state commit { oid } }
            }
          }
        }
        """;

    /// <remarks>
    /// <c>mergeStateStatus</c> (what blocks a merge) is not asked for yet: the merge box is slice
    /// 6, and a field GitHub still gates behind a preview would fail the whole query, taking the
    /// "this pull request changed" signal down with it.
    /// </remarks>
    public Task<RepoResult<PullSummary>> GetPullSummaryAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct) =>
        GraphAsync(access, SummaryQuery, new { owner, repo, number }, data =>
        {
            var p = PullRequestNode(data);
            var reviewers = new List<PullReviewer>();
            foreach (var r in p.GetProperty("latestOpinionatedReviews").GetProperty("nodes").EnumerateArray())
            {
                reviewers.Add(new PullReviewer(Login(r, "author"), r.GetProperty("state").GetString() ?? ""));
            }

            foreach (var r in p.GetProperty("reviewRequests").GetProperty("nodes").EnumerateArray())
            {
                var who = r.GetProperty("requestedReviewer");
                var name = who.ValueKind == JsonValueKind.Object
                    ? Text(who, "login") is { Length: > 0 } login ? login : Text(who, "name")
                    : "";
                if (name.Length > 0 && reviewers.All(x => x.Login != name))
                {
                    reviewers.Add(new PullReviewer(name, "REQUESTED"));
                }
            }

            var baseRef = p.GetProperty("baseRef");
            return new PullSummary(
                p.GetProperty("state").GetString() ?? "",
                p.GetProperty("isDraft").GetBoolean(),
                p.GetProperty("merged").GetBoolean(),
                p.GetProperty("mergeable").GetString() ?? "UNKNOWN",
                Text(p, "reviewDecision") is { Length: > 0 } decision ? decision : null,
                p.GetProperty("headRefOid").GetString() ?? "",
                p.GetProperty("baseRefName").GetString() ?? "",
                baseRef.ValueKind == JsonValueKind.Object ? Text(baseRef.GetProperty("target"), "oid") : null,
                p.GetProperty("labels").GetProperty("nodes").EnumerateArray()
                    .Select(l => new PullLabel(l.GetProperty("name").GetString() ?? "", HexColor(Text(l, "color")))).ToList(),
                p.GetProperty("assignees").GetProperty("nodes").EnumerateArray().Select(a => Text(a, "login")).ToList(),
                reviewers)
            {
                ViewerIsAuthor = Bool(p, "viewerDidAuthor"),
                // A pending review is not a review the viewer has given: "since your last review" counts submitted ones.
                ViewerLastReviewCommit = p.TryGetProperty("viewerLatestReview", out var last) && last.ValueKind == JsonValueKind.Object && Text(last, "state") != "PENDING"
                    ? Oid(last, "commit")
                    : null,
                NodeId = Text(p, "id"),
            };
        }, ct);

    private const string PendingQuery = """
        query($owner: String!, $repo: String!, $number: Int!) {
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $number) {
              reviews(states: PENDING, first: 10) {
                nodes {
                  id viewerDidAuthor author { login } commit { oid }
                  comments(first: 100) { nodes { id path body } }
                }
              }
            }
          }
        }
        """;

    /// <remarks>
    /// Another person's pending review is invisible to the viewer, but the author check is made all
    /// the same: a review the viewer did not author is never treated as theirs to submit (C2).
    /// </remarks>
    public Task<RepoResult<PendingReviewRead>> GetPendingReviewAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct) =>
        GraphAsync(access, PendingQuery, new { owner, repo, number }, data =>
        {
            var mine = PullRequestNode(data).GetProperty("reviews").GetProperty("nodes").EnumerateArray().FirstOrDefault(r => Bool(r, "viewerDidAuthor"));
            return new PendingReviewRead(mine.ValueKind != JsonValueKind.Object
                ? null
                : new PendingReview(
                    mine.GetProperty("id").GetString() ?? "",
                    Login(mine, "author"),
                    Oid(mine, "commit") ?? "",
                    mine.GetProperty("comments").GetProperty("nodes").EnumerateArray()
                        .Select(c => new PendingComment(Text(c, "id"), Text(c, "path"), Text(c, "body"))).ToList()));
        }, ct);

    private const string ViewedQuery = """
        query($owner: String!, $repo: String!, $number: Int!, $after: String) {
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $number) {
              files(first: 100, after: $after) { pageInfo { hasNextPage endCursor } nodes { path viewerViewedState } }
            }
          }
        }
        """;

    /// <summary>Viewed paths, paged to GitHub's own end of the file list (bounded: 30 pages).</summary>
    public async Task<RepoResult<IReadOnlyList<string>>> GetViewedAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        var viewed = new List<string>();
        string? after = null;
        for (var page = 0; page < PullFilePages; page++)
        {
            var one = await GraphAsync(access, ViewedQuery, new { owner, repo, number, after }, data =>
            {
                var files = PullRequestNode(data).GetProperty("files");
                var info = files.GetProperty("pageInfo");
                return (Paths: files.GetProperty("nodes").EnumerateArray().Where(f => Text(f, "viewerViewedState") == "VIEWED").Select(f => Text(f, "path")).ToList(),
                    Next: info.GetProperty("hasNextPage").GetBoolean() ? Text(info, "endCursor") : null);
            }, ct).ConfigureAwait(false);
            if (!one.Ok)
            {
                return one.As<IReadOnlyList<string>>();
            }

            viewed.AddRange(one.Value.Paths);
            after = one.Value.Next;
            if (after is not { Length: > 0 })
            {
                break;
            }
        }

        return RepoResult<IReadOnlyList<string>>.Of(viewed);
    }

    private const string ThreadsQuery = """
        query($owner: String!, $repo: String!, $number: Int!, $after: String) {
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $number) {
              reviewThreads(first: 50, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  id path isResolved isOutdated line startLine originalLine diffSide subjectType viewerCanReply viewerCanResolve viewerCanUnresolve
                  comments(first: 50) {
                    pageInfo { hasNextPage endCursor }
                    nodes { id author { login } body createdAt commit { oid } originalCommit { oid } viewerCanUpdate viewerCanDelete }
                  }
                }
              }
            }
          }
        }
        """;

    private const string ThreadCommentsQuery = """
        query($id: ID!, $after: String) {
          node(id: $id) {
            ... on PullRequestReviewThread {
              comments(first: 100, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes { id author { login } body createdAt commit { oid } originalCommit { oid } viewerCanUpdate viewerCanDelete }
              }
            }
          }
        }
        """;

    /// <summary>Threads read before stopping (security rule 8: every read is bounded).</summary>
    internal const int MaxThreads = 2000;

    /// <summary>Comments read per thread before stopping.</summary>
    internal const int MaxCommentsPerThread = 1000;

    /// <remarks>
    /// <para>
    /// <b>To completion, within a bound.</b> GitHub pages threads and, inside each, comments. The
    /// first version of a list here read one page and was silent past it; a review that drops
    /// threads past the hundredth hides the discussion a reviewer most needs. Both levels are
    /// paged until GitHub says there is no more, or until <see cref="MaxThreads"/> /
    /// <see cref="MaxCommentsPerThread"/>, and a cut list says so.
    /// </para>
    /// </remarks>
    public async Task<RepoResult<ReviewThreadList>> GetReviewThreadsAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        var threads = new List<ReviewThread>();
        string? after = null;
        while (true)
        {
            var page = await GraphAsync(access, ThreadsQuery, new { owner, repo, number, after }, data =>
            {
                var list = PullRequestNode(data).GetProperty("reviewThreads");
                var info = list.GetProperty("pageInfo");
                var read = list.GetProperty("nodes").EnumerateArray().Select(t =>
                {
                    var comments = t.GetProperty("comments");
                    var more = comments.GetProperty("pageInfo");
                    return (Thread: new ReviewThread(
                        t.GetProperty("id").GetString() ?? "",
                        t.GetProperty("path").GetString() ?? "",
                        t.GetProperty("isResolved").GetBoolean(),
                        t.GetProperty("isOutdated").GetBoolean(),
                        Int(t, "line"),
                        Int(t, "startLine"),
                        Text(t, "diffSide") is { Length: > 0 } side ? side : "RIGHT",
                        Int(t, "originalLine"),
                        Text(t, "subjectType") is { Length: > 0 } subject ? subject : "LINE",
                        comments.GetProperty("nodes").EnumerateArray().Select(Comment).ToList(),
                        false,
                        Bool(t, "viewerCanReply"),
                        Bool(t, "viewerCanResolve"),
                        Bool(t, "viewerCanUnresolve")), MoreComments: more.GetProperty("hasNextPage").GetBoolean() ? Text(more, "endCursor") : null);
                }).ToList();
                return (read, Next: info.GetProperty("hasNextPage").GetBoolean() ? Text(info, "endCursor") : null);
            }, ct).ConfigureAwait(false);
            if (!page.Ok)
            {
                return page.As<ReviewThreadList>();
            }

            foreach (var (thread, moreComments) in page.Value.read)
            {
                threads.Add(moreComments is null ? thread : await RestOfThreadAsync(access, thread, moreComments, ct).ConfigureAwait(false));
            }

            after = page.Value.Next;
            if (after is null || after.Length == 0)
            {
                return RepoResult<ReviewThreadList>.Of(new ReviewThreadList(threads, false));
            }

            if (threads.Count >= MaxThreads)
            {
                log.Warn(LogCategory.Web, $"review threads {owner}/{repo}#{number}: stopped at {threads.Count}; the rest are on GitHub");
                return RepoResult<ReviewThreadList>.Of(new ReviewThreadList(threads, true));
            }
        }
    }

    /// <summary>A thread's comments past its first page. A failed page leaves the thread marked cut, never dropped.</summary>
    private async Task<ReviewThread> RestOfThreadAsync(RepoAccess access, ReviewThread thread, string after, CancellationToken ct)
    {
        var comments = thread.Comments.ToList();
        string? cursor = after;
        while (cursor is { Length: > 0 } && comments.Count < MaxCommentsPerThread)
        {
            var page = await GraphAsync(access, ThreadCommentsQuery, new { id = thread.Id, after = cursor }, data =>
            {
                var list = data.GetProperty("node").GetProperty("comments");
                var info = list.GetProperty("pageInfo");
                return (Comments: list.GetProperty("nodes").EnumerateArray().Select(Comment).ToList(),
                    Next: info.GetProperty("hasNextPage").GetBoolean() ? Text(info, "endCursor") : null);
            }, ct).ConfigureAwait(false);
            if (!page.Ok)
            {
                return thread with { Comments = comments, Truncated = true };
            }

            comments.AddRange(page.Value.Comments);
            cursor = page.Value.Next;
        }

        return thread with { Comments = comments, Truncated = cursor is { Length: > 0 } };
    }

    private static ReviewComment Comment(JsonElement c) => new(
        c.GetProperty("id").GetString() ?? "",
        Login(c, "author"),
        Text(c, "body"),
        c.GetProperty("createdAt").GetDateTimeOffset(),
        Oid(c, "commit"),
        Oid(c, "originalCommit"),
        Bool(c, "viewerCanUpdate"),
        Bool(c, "viewerCanDelete"));

    private const string TimelineQuery = """
        query($owner: String!, $repo: String!, $number: Int!, $after: String) {
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $number) {
              timelineItems(first: 100, after: $after, itemTypes: [ISSUE_COMMENT, PULL_REQUEST_REVIEW, PULL_REQUEST_COMMIT,
                HEAD_REF_FORCE_PUSHED_EVENT, MERGED_EVENT, CLOSED_EVENT, REOPENED_EVENT, READY_FOR_REVIEW_EVENT, CONVERT_TO_DRAFT_EVENT]) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  __typename
                  ... on IssueComment { id author { login } body createdAt viewerCanUpdate viewerCanDelete }
                  ... on PullRequestReview { author { login } body state submittedAt createdAt }
                  ... on PullRequestCommit { commit { oid messageHeadline committedDate author { name user { login } } } }
                  ... on HeadRefForcePushedEvent { actor { login } createdAt afterCommit { oid } }
                  ... on MergedEvent { actor { login } createdAt commit { oid } }
                  ... on ClosedEvent { actor { login } createdAt }
                  ... on ReopenedEvent { actor { login } createdAt }
                  ... on ReadyForReviewEvent { actor { login } createdAt }
                  ... on ConvertToDraftEvent { actor { login } createdAt }
                }
              }
            }
          }
        }
        """;

    /// <summary>Timeline pages read before stopping: 300 items, the rest on GitHub.</summary>
    internal const int TimelinePages = 3;

    public async Task<RepoResult<Timeline>> GetTimelineAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        var items = new List<TimelineItem>();
        string? after = null;
        for (var page = 1; page <= TimelinePages; page++)
        {
            var one = await GraphAsync(access, TimelineQuery, new { owner, repo, number, after }, data =>
            {
                var list = PullRequestNode(data).GetProperty("timelineItems");
                var info = list.GetProperty("pageInfo");
                return (Items: list.GetProperty("nodes").EnumerateArray().Select(TimelineEntry).Where(i => i is not null).Select(i => i!).ToList(),
                    Next: info.GetProperty("hasNextPage").GetBoolean() ? Text(info, "endCursor") : null);
            }, ct).ConfigureAwait(false);
            if (!one.Ok)
            {
                return one.As<Timeline>();
            }

            items.AddRange(one.Value.Items);
            after = one.Value.Next;
            if (after is null || after.Length == 0)
            {
                return RepoResult<Timeline>.Of(new Timeline(items, false));
            }
        }

        return RepoResult<Timeline>.Of(new Timeline(items, true));
    }

    private static TimelineItem? TimelineEntry(JsonElement n)
    {
        DateTimeOffset At(string property) => n.GetProperty(property).GetDateTimeOffset();
        return n.GetProperty("__typename").GetString() switch
        {
            "IssueComment" => new TimelineItem("comment", Login(n, "author"), At("createdAt"), Text(n, "body"), null, null,
                Text(n, "id") is { Length: > 0 } id ? id : null, Bool(n, "viewerCanUpdate"), Bool(n, "viewerCanDelete")),
            "PullRequestReview" => new TimelineItem("review", Login(n, "author"),
                n.TryGetProperty("submittedAt", out var s) && s.ValueKind == JsonValueKind.String ? s.GetDateTimeOffset() : At("createdAt"),
                Text(n, "body"), Text(n, "state"), null),
            "PullRequestCommit" => Commit(n.GetProperty("commit")),
            "HeadRefForcePushedEvent" => new TimelineItem("force-push", Login(n, "actor"), At("createdAt"), null, null, Oid(n, "afterCommit")),
            "MergedEvent" => new TimelineItem("merged", Login(n, "actor"), At("createdAt"), null, null, Oid(n, "commit")),
            "ClosedEvent" => new TimelineItem("closed", Login(n, "actor"), At("createdAt"), null, null, null),
            "ReopenedEvent" => new TimelineItem("reopened", Login(n, "actor"), At("createdAt"), null, null, null),
            "ReadyForReviewEvent" => new TimelineItem("ready", Login(n, "actor"), At("createdAt"), null, null, null),
            "ConvertToDraftEvent" => new TimelineItem("draft", Login(n, "actor"), At("createdAt"), null, null, null),
            _ => null,
        };

        static TimelineItem Commit(JsonElement c)
        {
            var author = c.GetProperty("author");
            var user = author.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? Text(u, "login") : "";
            return new TimelineItem("commit", user.Length > 0 ? user : Text(author, "name"), c.GetProperty("committedDate").GetDateTimeOffset(),
                Text(c, "messageHeadline"), null, Text(c, "oid"));
        }
    }

    /// <summary>GitHub's cap on a pull request's listed commits.</summary>
    internal const int MaxPullCommits = 250;

    public async Task<RepoResult<PullCommitList>> GetPullCommitsAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        var all = new List<PullCommit>();
        for (var page = 1; page <= 3; page++)
        {
            var one = await GetJsonAsync<IReadOnlyList<PullCommit>>(access,
                $"repos/{E(owner)}/{E(repo)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/commits?per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}",
                root => root.EnumerateArray().Select(c =>
                {
                    var commit = c.GetProperty("commit");
                    var author = commit.GetProperty("author");
                    var login = c.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? Text(a, "login") : "";
                    var message = commit.GetProperty("message").GetString() ?? "";
                    return new PullCommit(
                        c.GetProperty("sha").GetString() ?? "",
                        message.Split('\n')[0],
                        login.Length > 0 ? login : author.GetProperty("name").GetString() ?? "",
                        author.GetProperty("date").GetDateTimeOffset());
                }).ToList(), ct).ConfigureAwait(false);
            if (!one.Ok)
            {
                return one.As<PullCommitList>();
            }

            all.AddRange(one.Value!);
            if (one.Value!.Count < 100)
            {
                break;
            }
        }

        return RepoResult<PullCommitList>.Of(new PullCommitList(all, all.Count >= MaxPullCommits));
    }

    /// <remarks>
    /// <para>
    /// <b>Polled, so conditional.</b> The page asks every 30 seconds while it is open. Both reads
    /// send the ETag of their last answer: a 304 costs nothing against GitHub's rate limit and
    /// returns the answer kept, so an idle review page polls for free.
    /// </para>
    /// <para>
    /// <b>Links are https or nothing.</b> A check's <c>html_url</c> and a status's
    /// <c>target_url</c> are whatever the CI said; anything but https is dropped rather than handed
    /// to the page as a link (security rule 6 of *Pull request review*).
    /// </para>
    /// </remarks>
    public async Task<RepoResult<IReadOnlyList<CheckInfo>>> GetChecksAsync(RepoAccess access, string owner, string repo, string commitSha, CancellationToken ct)
    {
        var runs = await GetConditionalAsync<IReadOnlyList<CheckInfo>>(access, $"repos/{E(owner)}/{E(repo)}/commits/{E(commitSha)}/check-runs?per_page=100",
            root => root.GetProperty("check_runs").EnumerateArray().Select(r => new CheckInfo(
                r.GetProperty("name").GetString() ?? "",
                r.GetProperty("status").GetString() ?? "",
                Text(r, "conclusion") is { Length: > 0 } c ? c : null,
                Https(Text(r, "html_url")),
                "check-run")).ToList(), ct).ConfigureAwait(false);
        if (!runs.Ok)
        {
            return runs;
        }

        var statuses = await GetConditionalAsync<IReadOnlyList<CheckInfo>>(access, $"repos/{E(owner)}/{E(repo)}/commits/{E(commitSha)}/status",
            root => root.GetProperty("statuses").EnumerateArray().Select(s =>
            {
                var state = s.GetProperty("state").GetString() ?? "";
                return new CheckInfo(
                    s.GetProperty("context").GetString() ?? "",
                    state == "pending" ? "pending" : "completed",
                    state == "pending" ? null : state,
                    Https(Text(s, "target_url")),
                    "status");
            }).ToList(), ct).ConfigureAwait(false);
        return statuses.Ok ? RepoResult<IReadOnlyList<CheckInfo>>.Of([.. runs.Value!, .. statuses.Value!]) : statuses;
    }

    private static string? Https(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u.AbsoluteUri : null;

    private static string HexColor(string color) => color.Length == 6 && color.All(Uri.IsHexDigit) ? color.ToLowerInvariant() : "";

    private static string Login(JsonElement element, string property) =>
        element.TryGetProperty(property, out var who) && who.ValueKind == JsonValueKind.Object ? Text(who, "login") is { Length: > 0 } l ? l : "ghost" : "ghost";

    private static string? Oid(JsonElement element, string property) =>
        element.TryGetProperty(property, out var c) && c.ValueKind == JsonValueKind.Object && Text(c, "oid") is { Length: > 0 } oid ? oid : null;

    private static bool Bool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement element, string property) =>
        element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    /// <summary>The pull request under <c>data.repository</c>; a missing one is GitHub's "not found".</summary>
    private static JsonElement PullRequestNode(JsonElement data)
    {
        var repository = data.GetProperty("repository");
        if (repository.ValueKind != JsonValueKind.Object || repository.GetProperty("pullRequest") is not { ValueKind: JsonValueKind.Object } pull)
        {
            throw new GraphNotFoundException();
        }

        return pull;
    }

    private sealed class GraphNotFoundException : Exception;

    /// <summary>
    /// One GraphQL query, read with <paramref name="read"/> while its document is alive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Needs a token.</b> GitHub's GraphQL refuses anonymous callers outright, so without one
    /// the answer is <see cref="RepoFailure.Unauthorized"/> before any call: the page says to sign
    /// in to see review threads, which is true.
    /// </para>
    /// <para>
    /// <b>A 200 can carry a refusal.</b> GraphQL reports "not found" and "forbidden" as
    /// <c>errors</c> in a successful response, with <c>data</c> null or partial. They are mapped
    /// as the REST refusals are — both are "not found", so a private repository's existence does
    /// not leak — and nothing is read from a response that carries errors.
    /// </para>
    /// </remarks>
    private async Task<RepoResult<T>> GraphAsync<T>(RepoAccess access, string query, object variables, Func<JsonElement, T> read, CancellationToken ct)
    {
        if (access.Token is null)
        {
            return RepoResult<T>.Fail(RepoFailure.Unauthorized, "GraphQL needs a token");
        }

        var url = GitHub.GraphQlUrl.Length > 0 ? new Uri(GitHub.GraphQlUrl) : Api("graphql");
        using var request = Request(access, HttpMethod.Post, url);
        request.Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json");
        try
        {
            using var response = await clients.CreateClient(ApiClient).SendAsync(request, ct).ConfigureAwait(false);
            if (Failure(response) is { } failed)
            {
                return RepoResult<T>.Fail(failed, "graphql");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var type = errors[0].TryGetProperty("type", out var t) ? t.GetString() : null;
                return RepoResult<T>.Fail(type switch
                {
                    "NOT_FOUND" or "FORBIDDEN" => RepoFailure.NotFound,
                    "RATE_LIMITED" => RepoFailure.RateLimited,
                    _ => RepoFailure.Upstream,
                }, "graphql " + type);
            }

            return RepoResult<T>.Of(read(document.RootElement.GetProperty("data")));
        }
        catch (GraphNotFoundException)
        {
            return RepoResult<T>.Fail(RepoFailure.NotFound, "graphql");
        }
        catch (HttpRequestException e)
        {
            log.Warn(LogCategory.Web, $"GitHub GraphQL unreachable: {e.Message}");
            return RepoResult<T>.Fail(RepoFailure.Upstream);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            log.Warn(LogCategory.Web, $"GitHub GraphQL answered in an unexpected shape: {e.GetType().Name}");
            return RepoResult<T>.Fail(RepoFailure.Upstream);
        }
    }

    /// <summary>
    /// ETags and the answers they name, per credential and URL: never shared across people, because
    /// the key is a hash of the token (security rule 2), and bounded, because it only saves calls.
    /// </summary>
    private readonly MemoryCache _conditional = new(new MemoryCacheOptions { SizeLimit = 4096 });

    /// <summary>A GET that sends the last answer's ETag and, on 304, returns that answer.</summary>
    private async Task<RepoResult<T>> GetConditionalAsync<T>(RepoAccess access, string relative, Func<JsonElement, T> read, CancellationToken ct)
    {
        var credential = access.Token is null ? "anonymous" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(access.Token)));
        var key = credential + ":" + relative;
        _conditional.TryGetValue(key, out (string ETag, T Value) known);
        using var request = Request(access, HttpMethod.Get, Api(relative));
        if (known.ETag is { Length: > 0 })
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", known.ETag);
        }

        try
        {
            using var response = await clients.CreateClient(ApiClient).SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && known.ETag is { Length: > 0 })
            {
                return RepoResult<T>.Of(known.Value);
            }

            if (Failure(response) is { } failed)
            {
                return RepoResult<T>.Fail(failed, Redact(relative));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var value = read(document.RootElement);
            if (response.Headers.ETag?.ToString() is { Length: > 0 } etag)
            {
                _conditional.Set(key, (etag, value), new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(10) });
            }

            return RepoResult<T>.Of(value);
        }
        catch (HttpRequestException e)
        {
            log.Warn(LogCategory.Web, $"GitHub unreachable for {Redact(relative)}: {e.Message}");
            return RepoResult<T>.Fail(RepoFailure.Upstream);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            log.Warn(LogCategory.Web, $"GitHub answered {Redact(relative)} in an unexpected shape: {e.GetType().Name}");
            return RepoResult<T>.Fail(RepoFailure.Upstream);
        }
    }

    private static PullInfo Pull(JsonElement p) => new(
        p.GetProperty("number").GetInt32(),
        p.GetProperty("title").GetString() ?? "",
        p.GetProperty("user").GetProperty("login").GetString() ?? "",
        p.GetProperty("updated_at").GetDateTimeOffset(),
        p.GetProperty("base").GetProperty("ref").GetString() ?? "",
        p.GetProperty("head").GetProperty("ref").GetString() ?? "",
        p.GetProperty("head").GetProperty("sha").GetString() ?? "",
        p.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True);

    private static ReleaseInfo Release(JsonElement r) => new(
        r.GetProperty("tag_name").GetString() ?? "",
        // An untitled release is shown by its tag, which GitHub's own page does too.
        Text(r, "name") is { Length: > 0 } name ? name : r.GetProperty("tag_name").GetString() ?? "",
        r.GetProperty("author").GetProperty("login").GetString() ?? "",
        // published_at is null while a release is a draft; drafts are filtered out, but the
        // fallback keeps a malformed answer from throwing.
        r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTimeOffset() : r.GetProperty("created_at").GetDateTimeOffset(),
        r.GetProperty("prerelease").GetBoolean());

    /// <summary>A string property that may be missing or JSON null, as GitHub's optional bodies are.</summary>
    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private async Task<RepoResult<byte[]>> GetLfsObjectAsync(
        RepoAccess access, string owner, string repo, LfsPointer pointer, long maxBytes, CancellationToken ct)
    {
        var batch = new Uri($"{GitHub.WebBaseUrl.TrimEnd('/')}/{E(owner)}/{E(repo)}.git/info/lfs/objects/batch");
        using var request = new HttpRequestMessage(HttpMethod.Post, batch)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    operation = "download",
                    transfers = new[] { "basic" },
                    objects = new[] { new { oid = pointer.Oid, size = pointer.Size } },
                }),
                Encoding.UTF8,
                "application/vnd.git-lfs+json"),
        };
        request.Headers.Accept.ParseAdd("application/vnd.git-lfs+json");
        request.Headers.UserAgent.ParseAdd("Versio-Web");
        if (access.Token is { } token)
        {
            // LFS takes basic auth; GitHub accepts an App or user token as the password.
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("x-access-token:" + token)));
        }

        using var response = await clients.CreateClient(ApiClient).SendAsync(request, ct).ConfigureAwait(false);
        if (Failure(response) is { } failed)
        {
            return RepoResult<byte[]>.Fail(failed, "LFS batch");
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var obj = doc.RootElement.GetProperty("objects")[0];
        if (!obj.TryGetProperty("actions", out var actions) || !actions.TryGetProperty("download", out var download))
        {
            return RepoResult<byte[]>.Fail(RepoFailure.NotFound, "LFS object missing");
        }

        var href = download.GetProperty("href").GetString();
        if (!LfsGuard.IsAllowed(href, GitHub.LfsHosts))
        {
            log.Warn(LogCategory.Web, $"LFS download for {owner}/{repo} pointed at a host that is not allowlisted; refused (security rule 4)");
            return RepoResult<byte[]>.Fail(RepoFailure.Upstream, "LFS host not allowed");
        }

        using var get = new HttpRequestMessage(HttpMethod.Get, href);
        if (download.TryGetProperty("header", out var headers))
        {
            foreach (var header in headers.EnumerateObject())
            {
                get.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
            }
        }

        // A refused connect (an internal address) or a failure is logged and reported as upstream
        // inside SendForBytesAsync.
        var content = await SendForBytesAsync(clients.CreateClient(LfsClient), get, maxBytes, ct).ConfigureAwait(false);
        if (!content.Ok)
        {
            return content;
        }

        // The pointer names its content by SHA-256 and size, and the pointer is what the commit
        // holds; the download comes from a host the LFS server chose. Bytes that do not match are
        // not this commit's file, whatever the reason (a cache, a proxy, a tampered store), and
        // were drawn as if they were until 2026-10-11 (found in review).
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content.Value!));
        if (content.Value!.LongLength != pointer.Size || !hash.Equals(pointer.Oid, StringComparison.OrdinalIgnoreCase))
        {
            log.Warn(LogCategory.Web, $"LFS download for {owner}/{repo} does not match its pointer (oid {pointer.Oid[..12]}); refused");
            return RepoResult<byte[]>.Fail(RepoFailure.Upstream, "LFS content does not match its pointer");
        }

        return content;
    }

    /// <summary>A GET that reads its answer with <paramref name="read"/> and disposes the document.</summary>
    /// <remarks>
    /// <para>
    /// <b>The document never leaves this method.</b> A <see cref="JsonDocument"/> rents pooled
    /// memory until it is disposed; the first version returned it to callers that never disposed
    /// it (found reviewing). Handing in the reader makes forgetting impossible.
    /// </para>
    /// <para>
    /// <b>An answer that is not the shape GitHub documents is "upstream"</b>, the same as an
    /// unreachable GitHub: a missing property must be a 502 the page can word, never a 500.
    /// </para>
    /// </remarks>
    private async Task<RepoResult<T>> GetJsonAsync<T>(RepoAccess access, string relative, Func<JsonElement, T> read, CancellationToken ct)
    {
        using var request = Request(access, HttpMethod.Get, Api(relative));
        try
        {
            using var response = await clients.CreateClient(ApiClient).SendAsync(request, ct).ConfigureAwait(false);
            if (Failure(response) is { } failed)
            {
                return RepoResult<T>.Fail(failed, Redact(relative));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return RepoResult<T>.Of(read(document.RootElement));
        }
        catch (HttpRequestException e)
        {
            log.Warn(LogCategory.Web, $"GitHub unreachable for {Redact(relative)}: {e.Message}");
            return RepoResult<T>.Fail(RepoFailure.Upstream);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            log.Warn(LogCategory.Web, $"GitHub answered {Redact(relative)} in an unexpected shape: {e.GetType().Name}");
            return RepoResult<T>.Fail(RepoFailure.Upstream);
        }
    }

    private async Task<RepoResult<byte[]>> SendForBytesAsync(HttpClient client, HttpRequestMessage request, long maxBytes, CancellationToken ct)
    {
        try
        {
            return await ReadBytesAsync(client, request, maxBytes, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            // Unreachable GitHub, or an LFS host the connect callback refused: a 502 the page can
            // word, never a 500. The first version let this escape from blob reads (found reviewing).
            log.Warn(LogCategory.Web, $"Fetching {request.RequestUri?.Host} failed: {e.Message}");
            return RepoResult<byte[]>.Fail(RepoFailure.Upstream);
        }
    }

    private static async Task<RepoResult<byte[]>> ReadBytesAsync(HttpClient client, HttpRequestMessage request, long maxBytes, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (Failure(response) is { } failed)
        {
            return RepoResult<byte[]>.Fail(failed);
        }

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            return RepoResult<byte[]>.Fail(RepoFailure.TooLarge);
        }

        // Read with a cap whatever the header claimed: a missing or lying Content-Length must not
        // let a response grow without bound.
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return RepoResult<byte[]>.Fail(RepoFailure.TooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        return RepoResult<byte[]>.Of(buffer.ToArray());
    }

    /// <summary>GitHub's answer as a failure, or null when it succeeded.</summary>
    internal static RepoFailure? Failure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        var exhausted = response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0";
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => RepoFailure.NotFound,
            HttpStatusCode.Unauthorized => RepoFailure.Unauthorized,
            HttpStatusCode.Forbidden when response.Headers.Contains("X-GitHub-SSO") => RepoFailure.SsoRequired,
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests when exhausted => RepoFailure.RateLimited,
            HttpStatusCode.TooManyRequests => RepoFailure.RateLimited,
            HttpStatusCode.Forbidden => RepoFailure.NotFound,
            _ => RepoFailure.Upstream,
        };
    }

    private HttpRequestMessage Request(RepoAccess access, HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("Versio-Web");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (access.Token is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private Uri Api(string relative) => new($"{GitHub.ApiBaseUrl.TrimEnd('/')}/{relative}");

    /// <summary>A path segment or query value, escaped so a repository or path name cannot reshape the URL.</summary>
    private static string E(string value) => Uri.EscapeDataString(value);

    /// <summary>What a log line may say about a request: the path without its query.</summary>
    private static string Redact(string relative) => relative.Split('?')[0];
}
