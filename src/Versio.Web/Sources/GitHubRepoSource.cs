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

        // The blob is a pointer; the file is in LFS, as a vendor's large exports are (Open item 1).
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

    /// <remarks>GitHub's commit answer carries the files with their patches (up to 300); the patches are discarded here, the viewer draws its own.</remarks>
    public Task<RepoResult<CommitDetail>> GetCommitAsync(RepoAccess access, string owner, string repo, string sha, CancellationToken ct) =>
        GetJsonAsync(access, $"repos/{E(owner)}/{E(repo)}/commits/{E(sha)}", root =>
        {
            var commit = root.GetProperty("commit");
            var author = commit.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            var committer = commit.TryGetProperty("committer", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            var stats = root.TryGetProperty("stats", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
            var files = root.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(x => new CommitFile(Text(x, "filename"), Text(x, "status"), Int(x, "additions") ?? 0, Int(x, "deletions") ?? 0, Text(x, "previous_filename") is { Length: > 0 } prev ? prev : null)).ToList()
                : [];
            var authorName = author.ValueKind == JsonValueKind.Object ? Text(author, "name") : "";
            var committerName = committer.ValueKind == JsonValueKind.Object ? Text(committer, "name") : "";
            return new CommitDetail(Text(root, "sha"), Text(commit, "message"), authorName,
                (author.ValueKind == JsonValueKind.Object ? Date(author, "date") : null) ?? default,
                root.TryGetProperty("parents", out var parents) && parents.ValueKind == JsonValueKind.Array ? parents.EnumerateArray().Select(p => Text(p, "sha")).ToList() : [])
            {
                AuthorLogin = root.TryGetProperty("author", out var who) && who.ValueKind == JsonValueKind.Object ? Text(who, "login") is { Length: > 0 } login ? login : null : null,
                AuthorAvatar = Avatar(root, "author"),
                Committer = committerName.Length > 0 && committerName != authorName ? committerName : null,
                CommitDate = committer.ValueKind == JsonValueKind.Object ? Date(committer, "date") : null,
                Additions = stats.ValueKind == JsonValueKind.Object ? Int(stats, "additions") ?? 0 : 0,
                Deletions = stats.ValueKind == JsonValueKind.Object ? Int(stats, "deletions") ?? 0 : 0,
                Files = files,
                // GitHub returns at most 300 files on a commit; a stats total past the files' own sum says the list is cut.
                FilesTruncated = files.Count >= 300,
                Verified = commit.TryGetProperty("verification", out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("verified", out var ver) ? ver.ValueKind == JsonValueKind.True : null,
                Url = Https(Text(root, "html_url")),
            };
        }, ct);

    public Task<RepoResult<IReadOnlyList<TagInfo>>> GetTagsAsync(RepoAccess access, string owner, string repo, CancellationToken ct) =>
        GetJsonAsync<IReadOnlyList<TagInfo>>(access, $"repos/{E(owner)}/{E(repo)}/tags?per_page=100",
            root => root.EnumerateArray().Select(t => new TagInfo(Text(t, "name"), t.TryGetProperty("commit", out var c) ? Text(c, "sha") : "")).ToList(), ct);

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

    private const string SearchQuery = """
        query($q: String!, $open: String!, $closed: String!, $first: Int!, $after: String) {
          list: search(query: $q, type: ISSUE, first: $first, after: $after) {
            issueCount
            pageInfo { hasNextPage endCursor }
            nodes {
              ... on PullRequest {
                number title state isDraft createdAt updatedAt baseRefName headRefName headRefOid reviewDecision totalCommentsCount
                author { login avatarUrl }
                labels(first: 10) { nodes { name color } }
                assignees(first: 5) { nodes { login avatarUrl } }
                commits(last: 1) { nodes { commit { statusCheckRollup { state } } } }
              }
            }
          }
          open: search(query: $open, type: ISSUE, first: 0) { issueCount }
          closed: search(query: $closed, type: ISSUE, first: 0) { issueCount }
        }
        """;

    /// <remarks>
    /// Three searches in one call: the page asked for, and the open and closed counts of the same
    /// query, which GitHub's own tabs show. GitHub's search index lags a write by a moment, which
    /// the page says beside the counts rather than this read pretending otherwise.
    /// </remarks>
    public Task<RepoResult<PullSearch>> SearchPullsAsync(RepoAccess access, string search, string open, string closed, int first, string? after, CancellationToken ct) =>
        GraphAsync(access, SearchQuery, new { q = search, open, closed, first = Math.Clamp(first, 1, 100), after }, data =>
        {
            var list = data.GetProperty("list");
            var info = list.GetProperty("pageInfo");
            var rows = list.GetProperty("nodes").EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Object && n.TryGetProperty("number", out _)).Select(n =>
            {
                string? rollup = null;
                if (n.TryGetProperty("commits", out var commits) && commits.ValueKind == JsonValueKind.Object)
                {
                    foreach (var node in commits.TryGetProperty("nodes", out var cn) && cn.ValueKind == JsonValueKind.Array ? cn.EnumerateArray() : default)
                    {
                        if (node.TryGetProperty("commit", out var commit) && commit.TryGetProperty("statusCheckRollup", out var roll) && roll.ValueKind == JsonValueKind.Object)
                        {
                            rollup = Text(roll, "state") is { Length: > 0 } st ? st : null;
                        }
                    }
                }

                return new PullRow(
                    Int(n, "number") ?? 0,
                    Text(n, "title"),
                    Login(n, "author"),
                    Avatar(n, "author"),
                    Date(n, "createdAt") ?? default,
                    Date(n, "updatedAt") ?? default,
                    Text(n, "state"),
                    Bool(n, "isDraft"),
                    Text(n, "baseRefName"),
                    Text(n, "headRefName"),
                    Text(n, "headRefOid"),
                    n.TryGetProperty("labels", out var labels) ? labels.GetProperty("nodes").EnumerateArray().Select(l => new PullLabel(Text(l, "name"), HexColor(Text(l, "color")))).ToList() : [],
                    n.TryGetProperty("assignees", out var assignees) ? assignees.GetProperty("nodes").EnumerateArray().Select(a => (Text(a, "login"), Avatar(a))).ToList() : [],
                    Text(n, "reviewDecision") is { Length: > 0 } decision ? decision : null,
                    rollup,
                    Int(n, "totalCommentsCount") ?? 0);
            }).ToList();
            return new PullSearch(rows, Int(list, "issueCount") ?? rows.Count,
                Int(data.GetProperty("open"), "issueCount") ?? 0, Int(data.GetProperty("closed"), "issueCount") ?? 0,
                Bool(info, "hasNextPage") ? Text(info, "endCursor") : null);
        }, ct);

    /// <remarks>REST's list items carry the labels, the assignees and the dates, not the review decision or the checks: those stay null, and the page draws nothing for them.</remarks>
    public Task<RepoResult<IReadOnlyList<PullRow>>> GetPullRowsAsync(RepoAccess access, string owner, string repo, string state, string sort, string direction, int limit, int page, CancellationToken ct) =>
        GetJsonAsync<IReadOnlyList<PullRow>>(access,
            $"repos/{E(owner)}/{E(repo)}/pulls?state={E(state)}&sort={E(sort == "comments" ? "popularity" : sort)}&direction={E(direction)}&per_page={Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture)}&page={Math.Clamp(page, 1, 1000).ToString(CultureInfo.InvariantCulture)}",
            root => root.EnumerateArray().Select(p =>
            {
                var merged = p.TryGetProperty("merged_at", out var ma) && ma.ValueKind == JsonValueKind.String;
                return new PullRow(
                    Int(p, "number") ?? 0,
                    Text(p, "title"),
                    Login(p, "user"),
                    Avatar(p, "user"),
                    Date(p, "created_at") ?? default,
                    Date(p, "updated_at") ?? default,
                    merged ? "MERGED" : Text(p, "state").ToUpperInvariant() is { Length: > 0 } s ? s : "OPEN",
                    Bool(p, "draft"),
                    p.TryGetProperty("base", out var b) ? Text(b, "ref") : "",
                    p.TryGetProperty("head", out var h) ? Text(h, "ref") : "",
                    p.TryGetProperty("head", out var h2) ? Text(h2, "sha") : "",
                    p.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array ? labels.EnumerateArray().Select(l => new PullLabel(Text(l, "name"), HexColor(Text(l, "color")))).ToList() : [],
                    p.TryGetProperty("assignees", out var assignees) && assignees.ValueKind == JsonValueKind.Array ? assignees.EnumerateArray().Select(a => (Text(a, "login"), Avatar(a))).ToList() : [],
                    null,
                    null,
                    0);
            }).ToList(), ct);

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

    /// <summary>The fields of a reaction group, the same on a body, a comment and a review.</summary>
    private const string ReactionFields = "reactionGroups { content viewerHasReacted reactors { totalCount } }";

    private const string SummaryQuery = $$"""
        query($owner: String!, $repo: String!, $number: Int!) {
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $number) {
              state isDraft merged mergeable reviewDecision headRefOid baseRefName title body viewerCanUpdate
              additions deletions changedFiles totalCommentsCount createdAt closedAt mergedAt locked activeLockReason
              isCrossRepository authorAssociation viewerSubscription viewerCanReact
              author { login avatarUrl }
              mergedBy { login }
              milestone { title number }
              headRepository { nameWithOwner }
              baseRef { target { oid } }
              labels(first: 30) { nodes { name color } }
              assignees(first: 20) { nodes { login } }
              reviewRequests(first: 30) { nodes { requestedReviewer { __typename ... on User { login avatarUrl } ... on Team { name slug } } } }
              latestOpinionatedReviews(first: 30) { nodes { author { login avatarUrl } state } }
              id viewerDidAuthor
              viewerLatestReview { state commit { oid } }
              commits(last: 1) {
                totalCount
                nodes { commit { statusCheckRollup { state contexts(first: 100) { nodes {
                  __typename
                  ... on CheckRun { name isRequired(pullRequestNumber: $number) }
                  ... on StatusContext { context isRequired(pullRequestNumber: $number) }
                } } } } }
              }
              {{ReactionFields}}
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
                reviewers.Add(new PullReviewer(Login(r, "author"), r.GetProperty("state").GetString() ?? "", false, Avatar(r, "author")));
            }

            foreach (var r in p.GetProperty("reviewRequests").GetProperty("nodes").EnumerateArray())
            {
                var who = r.GetProperty("requestedReviewer");
                // A team is a team: it is asked for as one and cannot be withdrawn by a login (FINISH.md, B7).
                var team = who.ValueKind == JsonValueKind.Object && Text(who, "__typename") == "Team";
                var name = who.ValueKind == JsonValueKind.Object
                    ? Text(who, "login") is { Length: > 0 } login ? login : Text(who, "name")
                    : "";
                if (name.Length > 0 && reviewers.All(x => x.Login != name))
                {
                    // The slug is what REST asks and withdraws a team by; the name is what is shown.
                    reviewers.Add(new PullReviewer(name, "REQUESTED", team, team ? null : Avatar(who)) { Slug = team ? Text(who, "slug") is { Length: > 0 } slug ? slug : null : null });
                }
            }

            // The live head's checks, as branch protection sees them: which are required, and the whole's state.
            string? rollup = null;
            var required = new List<string>();
            if (p.TryGetProperty("commits", out var commits) && commits.ValueKind == JsonValueKind.Object)
            {
                foreach (var node in commits.TryGetProperty("nodes", out var cn) && cn.ValueKind == JsonValueKind.Array ? cn.EnumerateArray() : default)
                {
                    if (node.TryGetProperty("commit", out var commit) && commit.TryGetProperty("statusCheckRollup", out var roll) && roll.ValueKind == JsonValueKind.Object)
                    {
                        rollup = Text(roll, "state") is { Length: > 0 } st ? st : null;
                        foreach (var c in roll.TryGetProperty("contexts", out var cx) && cx.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array ? nodes.EnumerateArray() : default)
                        {
                            if (Bool(c, "isRequired"))
                            {
                                required.Add(Text(c, "name") is { Length: > 0 } n ? n : Text(c, "context"));
                            }
                        }
                    }
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
                Title = Text(p, "title"),
                Body = Text(p, "body"),
                ViewerCanUpdate = Bool(p, "viewerCanUpdate"),
                // Every field below is read tolerantly: a server answering an older shape (the tests'
                // fake, an Enterprise behind on its schema) leaves them at their defaults rather than
                // failing the read that carries the "this pull request changed" signal.
                Additions = Int(p, "additions") ?? 0,
                Deletions = Int(p, "deletions") ?? 0,
                ChangedFiles = Int(p, "changedFiles") ?? 0,
                CommitCount = p.TryGetProperty("commits", out var cc) && cc.ValueKind == JsonValueKind.Object ? Int(cc, "totalCount") ?? 0 : 0,
                CommentCount = Int(p, "totalCommentsCount") ?? 0,
                CreatedAt = Date(p, "createdAt"),
                ClosedAt = Date(p, "closedAt"),
                MergedAt = Date(p, "mergedAt"),
                MergedBy = p.TryGetProperty("mergedBy", out var mb) && mb.ValueKind == JsonValueKind.Object ? Text(mb, "login") : null,
                Author = Login(p, "author"),
                AuthorAvatar = Avatar(p, "author"),
                AuthorAssociation = Text(p, "authorAssociation"),
                Locked = Bool(p, "locked"),
                LockReason = Text(p, "activeLockReason") is { Length: > 0 } lr ? lr : null,
                Milestone = p.TryGetProperty("milestone", out var ms) && ms.ValueKind == JsonValueKind.Object ? Text(ms, "title") : null,
                MilestoneNumber = ms.ValueKind == JsonValueKind.Object ? Int(ms, "number") : null,
                ViewerSubscription = Text(p, "viewerSubscription") is { Length: > 0 } sub ? sub : null,
                ViewerCanReact = Bool(p, "viewerCanReact"),
                HeadRepository = Bool(p, "isCrossRepository") && p.TryGetProperty("headRepository", out var hr) && hr.ValueKind == JsonValueKind.Object ? Text(hr, "nameWithOwner") : null,
                Reactions = Reactions(p),
                CheckRollup = rollup,
                RequiredChecks = required,
            };
        }, ct);

    private const string MergeStateQuery = """
        query($owner: String!, $repo: String!, $number: Int!) {
          repository(owner: $owner, name: $repo) {
            mergeCommitAllowed squashMergeAllowed rebaseMergeAllowed autoMergeAllowed deleteBranchOnMerge
            pullRequest(number: $number) {
              id state merged headRefOid mergeStateStatus headRefName isCrossRepository
              headRef { name }
              mergeCommit { oid }
              viewerCanEnableAutoMerge viewerCanDisableAutoMerge
              autoMergeRequest { mergeMethod enabledBy { login } }
              isInMergeQueue
              mergeQueueEntry { position }
              mergeQueue { id }
            }
          }
        }
        """;

    /// <remarks>
    /// Its own query, apart from the summary: <c>mergeStateStatus</c> has been behind GitHub's
    /// <c>merge-info-preview</c>, and a field GitHub refuses fails the whole query. Here that costs
    /// only the merge box, which then offers nothing — failing closed — while the summary, and the
    /// "this pull request changed" signal it carries, go on working.
    /// </remarks>
    public Task<RepoResult<MergeState>> GetMergeStateAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct) =>
        GraphAsync(access, MergeStateQuery, new { owner, repo, number }, data =>
        {
            // The pull request first: it throws "not found" for a missing repository or pull request.
            var p = PullRequestNode(data);
            var r = data.GetProperty("repository");
            var methods = new List<string>();
            if (Bool(r, "mergeCommitAllowed"))
            {
                methods.Add("MERGE");
            }

            if (Bool(r, "squashMergeAllowed"))
            {
                methods.Add("SQUASH");
            }

            if (Bool(r, "rebaseMergeAllowed"))
            {
                methods.Add("REBASE");
            }

            var auto = p.TryGetProperty("autoMergeRequest", out var a) && a.ValueKind == JsonValueKind.Object ? a : (JsonElement?)null;
            return new MergeState(
                Text(p, "state"),
                Bool(p, "merged"),
                Text(p, "headRefOid"),
                Text(p, "mergeStateStatus") is { Length: > 0 } status ? status : "UNKNOWN",
                methods,
                Bool(r, "autoMergeAllowed"),
                auto is { } on ? Text(on, "mergeMethod") : null,
                auto is { } by ? Login(by, "enabledBy") : null,
                Bool(p, "viewerCanEnableAutoMerge"),
                Bool(p, "viewerCanDisableAutoMerge"),
                Text(p, "id"),
                Oid(p, "mergeCommit"))
            {
                // The merge box's rest (FINISH.md Step 6): the branch to delete or restore, whether
                // the repository does that itself, and the merge queue. Each read tolerantly.
                HeadRef = Text(p, "headRefName"),
                HeadRefExists = p.TryGetProperty("headRef", out var hr) && hr.ValueKind == JsonValueKind.Object,
                IsCrossRepository = Bool(p, "isCrossRepository"),
                DeleteBranchOnMerge = Bool(r, "deleteBranchOnMerge"),
                HasMergeQueue = p.TryGetProperty("mergeQueue", out var mq) && mq.ValueKind == JsonValueKind.Object,
                InMergeQueue = Bool(p, "isInMergeQueue"),
                QueuePosition = p.TryGetProperty("mergeQueueEntry", out var entry) && entry.ValueKind == JsonValueKind.Object ? Int(entry, "position") : null,
            };
        }, ct, preview: "application/vnd.github.merge-info-preview+json");

    public Task<RepoResult<IReadOnlyList<string>>> GetCommitParentsAsync(RepoAccess access, string owner, string repo, string sha, CancellationToken ct) =>
        GetJsonAsync<IReadOnlyList<string>>(access, $"repos/{E(owner)}/{E(repo)}/commits/{E(sha)}",
            root => root.GetProperty("parents").EnumerateArray().Select(p => Text(p, "sha")).ToList(), ct);

    public Task<RepoResult<IReadOnlyList<PullLabel>>> GetLabelsAsync(RepoAccess access, string owner, string repo, CancellationToken ct) =>
        GetJsonAsync<IReadOnlyList<PullLabel>>(access, $"repos/{E(owner)}/{E(repo)}/labels?per_page=100",
            root => root.EnumerateArray().Select(l => new PullLabel(Text(l, "name"), HexColor(Text(l, "color")))).ToList(), ct);

    public Task<RepoResult<IReadOnlyList<MilestoneInfo>>> GetMilestonesAsync(RepoAccess access, string owner, string repo, CancellationToken ct) =>
        GetJsonAsync<IReadOnlyList<MilestoneInfo>>(access, $"repos/{E(owner)}/{E(repo)}/milestones?state=open&per_page=100",
            root => root.EnumerateArray().Where(m => Int(m, "number") is not null).Select(m => new MilestoneInfo(Int(m, "number")!.Value, Text(m, "title"), Text(m, "state"))).ToList(), ct);

    public Task<RepoResult<string>> GetCommitMessageAsync(RepoAccess access, string owner, string repo, string sha, CancellationToken ct) =>
        GetJsonAsync<string>(access, $"repos/{E(owner)}/{E(repo)}/commits/{E(sha)}",
            root => root.TryGetProperty("commit", out var c) && c.ValueKind == JsonValueKind.Object ? Text(c, "message") : "", ct);

    /// <remarks>GitHub answers 204 for yes and 404 for no: no body either way, so not <see cref="GetJsonAsync{T}"/>.</remarks>
    public async Task<RepoResult<bool>> CanAssignAsync(RepoAccess access, string owner, string repo, string login, CancellationToken ct)
    {
        var relative = $"repos/{E(owner)}/{E(repo)}/assignees/{E(login)}";
        using var request = Request(access, HttpMethod.Get, Api(relative));
        try
        {
            using var response = await clients.CreateClient(ApiClient).SendAsync(request, ct).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.NotFound ? RepoResult<bool>.Of(false)
                : Failure(response) is { } failed ? RepoResult<bool>.Fail(failed, Redact(relative))
                : RepoResult<bool>.Of(true);
        }
        catch (HttpRequestException e)
        {
            log.Warn(LogCategory.Web, $"GitHub unreachable for {Redact(relative)}: {e.Message}");
            return RepoResult<bool>.Fail(RepoFailure.Upstream);
        }
    }

    private const string PendingQuery = """
        query($owner: String!, $repo: String!, $number: Int!) {
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $number) {
              reviews(states: PENDING, first: 10) {
                nodes {
                  id viewerDidAuthor author { login } commit { oid }
                  comments(first: 100) { pageInfo { hasNextPage endCursor } nodes { id path body line startLine } }
                }
              }
            }
          }
        }
        """;

    private const string PendingCommentsQuery = """
        query($id: ID!, $after: String) {
          node(id: $id) {
            ... on PullRequestReview { comments(first: 100, after: $after) { pageInfo { hasNextPage endCursor } nodes { id path body line startLine } } }
          }
        }
        """;

    /// <summary>The most pages of a pending review's comments read: 1,000. Past it the review says it was cut.</summary>
    internal const int PendingCommentPages = 10;

    private static (List<PendingComment> Comments, string? Next) CommentsPage(JsonElement comments)
    {
        var info = comments.GetProperty("pageInfo");
        return (comments.GetProperty("nodes").EnumerateArray().Select(c => new PendingComment(Text(c, "id"), Text(c, "path"), Text(c, "body")) { Line = Int(c, "line"), StartLine = Int(c, "startLine") }).ToList(),
            Bool(info, "hasNextPage") ? Text(info, "endCursor") : null);
    }

    /// <remarks>
    /// <para>
    /// Another person's pending review is invisible to the viewer, but the author check is made all
    /// the same: a review the viewer did not author is never treated as theirs to submit (C2).
    /// </para>
    /// <para>
    /// Every comment is read, page by page, to a bound. Only the first 100 were, at first: the page
    /// showed the rest as not there, and reconciling a draft comment past them could not find its
    /// tag (found by review, 2026-10-20). Past the bound the review is marked cut, so neither the
    /// page nor reconciliation reads a partial list as whole.
    /// </para>
    /// </remarks>
    public async Task<RepoResult<PendingReviewRead>> GetPendingReviewAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        var first = await GraphAsync(access, PendingQuery, new { owner, repo, number }, data =>
        {
            var mine = PullRequestNode(data).GetProperty("reviews").GetProperty("nodes").EnumerateArray().FirstOrDefault(r => Bool(r, "viewerDidAuthor"));
            if (mine.ValueKind != JsonValueKind.Object)
            {
                return (Review: (PendingReview?)null, Next: (string?)null);
            }

            var (comments, next) = CommentsPage(mine.GetProperty("comments"));
            return (Review: new PendingReview(mine.GetProperty("id").GetString() ?? "", Login(mine, "author"), Oid(mine, "commit") ?? "", comments), Next: next);
        }, ct).ConfigureAwait(false);
        if (!first.Ok)
        {
            return first.As<PendingReviewRead>();
        }

        var (review, after) = first.Value;
        if (review is null || after is null)
        {
            return RepoResult<PendingReviewRead>.Of(new PendingReviewRead(review));
        }

        var all = review.Comments.ToList();
        for (var page = 1; after is not null && page < PendingCommentPages; page++)
        {
            var more = await GraphAsync(access, PendingCommentsQuery, new { id = review.Id, after }, data => CommentsPage(data.GetProperty("node").GetProperty("comments")), ct).ConfigureAwait(false);
            if (!more.Ok)
            {
                // A later page unreadable: what was read, said to be cut — never passed off as all of them.
                return RepoResult<PendingReviewRead>.Of(new PendingReviewRead(review with { Comments = all, Truncated = true }));
            }

            all.AddRange(more.Value.Comments);
            after = more.Value.Next;
        }

        return RepoResult<PendingReviewRead>.Of(new PendingReviewRead(review with { Comments = all, Truncated = after is not null }));
    }

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

    private const string ThreadsQuery = $$"""
        query($owner: String!, $repo: String!, $number: Int!, $after: String) {
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $number) {
              headRefOid baseRefName baseRef { target { oid } }
              reviewThreads(first: 50, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  id path isResolved isOutdated line startLine originalLine diffSide subjectType viewerCanReply viewerCanResolve viewerCanUnresolve
                  comments(first: 50) {
                    pageInfo { hasNextPage endCursor }
                    nodes { {{CommentFields}} }
                  }
                }
              }
            }
          }
        }
        """;

    private const string ThreadCommentsQuery = $$"""
        query($id: ID!, $after: String) {
          node(id: $id) {
            ... on PullRequestReviewThread {
              comments(first: 100, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes { {{CommentFields}} }
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
        PullRevision? revision = null;
        var consistent = true;
        while (true)
        {
            var page = await GraphAsync(access, ThreadsQuery, new { owner, repo, number, after }, data =>
            {
                var pull = PullRequestNode(data);
                var baseRef = pull.TryGetProperty("baseRef", out var b) && b.ValueKind == JsonValueKind.Object ? Text(b.GetProperty("target"), "oid") : null;
                var seen = new PullRevision(Text(pull, "headRefOid"), Text(pull, "baseRefName"), baseRef is { Length: > 0 } ? baseRef : null);
                var list = pull.GetProperty("reviewThreads");
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
                return (read, Next: info.GetProperty("hasNextPage").GetBoolean() ? Text(info, "endCursor") : null, Seen: seen);
            }, ct).ConfigureAwait(false);
            if (!page.Ok)
            {
                return page.As<ReviewThreadList>();
            }

            // Each page says which revision its lines are positions in; pages that disagree mean the
            // pull request moved mid-read, and then no one revision describes them all.
            consistent &= revision is null || revision == page.Value.Seen;
            revision ??= page.Value.Seen;

            foreach (var (thread, moreComments) in page.Value.read)
            {
                threads.Add(moreComments is null ? thread : await RestOfThreadAsync(access, thread, moreComments, ct).ConfigureAwait(false));
            }

            after = page.Value.Next;
            if (after is null || after.Length == 0)
            {
                return RepoResult<ReviewThreadList>.Of(new ReviewThreadList(threads, false, consistent ? revision : null));
            }

            if (threads.Count >= MaxThreads)
            {
                log.Warn(LogCategory.Web, $"review threads {owner}/{repo}#{number}: stopped at {threads.Count}; the rest are on GitHub");
                return RepoResult<ReviewThreadList>.Of(new ReviewThreadList(threads, true, consistent ? revision : null));
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
        Bool(c, "viewerCanDelete"))
    {
        ReviewId = c.TryGetProperty("pullRequestReview", out var review) && review.ValueKind == JsonValueKind.Object && Text(review, "id") is { Length: > 0 } rid ? rid : null,
        DiffHunk = Text(c, "diffHunk") is { Length: > 0 } hunk ? hunk : null,
        AuthorAssociation = Text(c, "authorAssociation"),
        AvatarUrl = Avatar(c, "author"),
        EditedAt = Date(c, "lastEditedAt"),
        Url = Https(Text(c, "url")),
        Reactions = Reactions(c),
        Pending = Text(c, "state") == "PENDING",
        Minimized = Bool(c, "isMinimized"),
        MinimizedReason = Text(c, "minimizedReason") is { Length: > 0 } why ? why : null,
    };

    /// <summary>The fields read of every review comment, in the threads query and the thread's later pages alike.</summary>
    private const string CommentFields = "id author { login avatarUrl } body createdAt lastEditedAt url authorAssociation state diffHunk commit { oid } originalCommit { oid } pullRequestReview { id } viewerCanUpdate viewerCanDelete isMinimized minimizedReason " + ReactionFields;

    private const string TimelineQuery = $$"""
        query($owner: String!, $repo: String!, $number: Int!, $after: String) {
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $number) {
              timelineItems(first: 100, after: $after, itemTypes: [ISSUE_COMMENT, PULL_REQUEST_REVIEW, PULL_REQUEST_COMMIT,
                HEAD_REF_FORCE_PUSHED_EVENT, MERGED_EVENT, CLOSED_EVENT, REOPENED_EVENT, READY_FOR_REVIEW_EVENT, CONVERT_TO_DRAFT_EVENT,
                LABELED_EVENT, UNLABELED_EVENT, ASSIGNED_EVENT, UNASSIGNED_EVENT, REVIEW_REQUESTED_EVENT, REVIEW_REQUEST_REMOVED_EVENT,
                REVIEW_DISMISSED_EVENT, RENAMED_TITLE_EVENT, BASE_REF_CHANGED_EVENT, BASE_REF_FORCE_PUSHED_EVENT, HEAD_REF_DELETED_EVENT,
                HEAD_REF_RESTORED_EVENT, CROSS_REFERENCED_EVENT, REFERENCED_EVENT, MILESTONED_EVENT, DEMILESTONED_EVENT, LOCKED_EVENT,
                UNLOCKED_EVENT, AUTO_MERGE_ENABLED_EVENT, AUTO_MERGE_DISABLED_EVENT, ADDED_TO_MERGE_QUEUE_EVENT, REMOVED_FROM_MERGE_QUEUE_EVENT,
                DEPLOYED_EVENT]) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  __typename
                  ... on IssueComment { id author { login avatarUrl } body createdAt lastEditedAt url authorAssociation viewerCanUpdate viewerCanDelete isMinimized minimizedReason {{ReactionFields}} }
                  ... on PullRequestReview { id author { login avatarUrl } body state submittedAt createdAt lastEditedAt url authorAssociation commit { oid } viewerCanUpdate {{ReactionFields}} }
                  ... on PullRequestCommit { url commit { oid messageHeadline committedDate author { name avatarUrl user { login } } signature { isValid } } }
                  ... on HeadRefForcePushedEvent { actor { login } createdAt beforeCommit { oid } afterCommit { oid } }
                  ... on MergedEvent { actor { login } createdAt commit { oid } mergeRefName }
                  ... on ClosedEvent { actor { login } createdAt }
                  ... on ReopenedEvent { actor { login } createdAt }
                  ... on ReadyForReviewEvent { actor { login } createdAt }
                  ... on ConvertToDraftEvent { actor { login } createdAt }
                  ... on LabeledEvent { actor { login } createdAt label { name color } }
                  ... on UnlabeledEvent { actor { login } createdAt label { name color } }
                  ... on AssignedEvent { actor { login } createdAt assignee { __typename ... on User { login } ... on Bot { login } ... on Mannequin { login } ... on Organization { login } } }
                  ... on UnassignedEvent { actor { login } createdAt assignee { __typename ... on User { login } ... on Bot { login } ... on Mannequin { login } ... on Organization { login } } }
                  ... on ReviewRequestedEvent { actor { login } createdAt requestedReviewer { __typename ... on User { login } ... on Team { name } } }
                  ... on ReviewRequestRemovedEvent { actor { login } createdAt requestedReviewer { __typename ... on User { login } ... on Team { name } } }
                  ... on ReviewDismissedEvent { actor { login } createdAt dismissalMessage review { author { login } } }
                  ... on RenamedTitleEvent { actor { login } createdAt previousTitle currentTitle }
                  ... on BaseRefChangedEvent { actor { login } createdAt previousRefName currentRefName }
                  ... on BaseRefForcePushedEvent { actor { login } createdAt beforeCommit { oid } afterCommit { oid } }
                  ... on HeadRefDeletedEvent { actor { login } createdAt headRefName }
                  ... on HeadRefRestoredEvent { actor { login } createdAt }
                  ... on CrossReferencedEvent { actor { login } createdAt willCloseTarget source { __typename
                    ... on Issue { number title url state repository { nameWithOwner } }
                    ... on PullRequest { number title url state repository { nameWithOwner } } } }
                  ... on ReferencedEvent { actor { login } createdAt commit { oid messageHeadline } commitRepository { nameWithOwner } }
                  ... on MilestonedEvent { actor { login } createdAt milestoneTitle }
                  ... on DemilestonedEvent { actor { login } createdAt milestoneTitle }
                  ... on LockedEvent { actor { login } createdAt lockReason }
                  ... on UnlockedEvent { actor { login } createdAt }
                  ... on AutoMergeEnabledEvent { actor { login } createdAt }
                  ... on AutoMergeDisabledEvent { actor { login } createdAt reason }
                  ... on AddedToMergeQueueEvent { actor { login } createdAt }
                  ... on RemovedFromMergeQueueEvent { actor { login } createdAt }
                  ... on DeployedEvent { actor { login } createdAt deployment { environment } }
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
        // An event names what it did to: a label, a person or team, a title, a branch, a milestone.
        TimelineItem Event(string kind, string? target = null, string? previous = null, string? message = null, string? sha = null, string? color = null) =>
            new(kind, Login(n, "actor"), At("createdAt"), null, null, sha) { Target = target, Previous = previous, Message = message, TargetColor = color };
        string Named(string property, params string[] names)
        {
            if (!n.TryGetProperty(property, out var who) || who.ValueKind != JsonValueKind.Object)
            {
                return "";
            }

            foreach (var name in names)
            {
                if (Text(who, name) is { Length: > 0 } found)
                {
                    return found;
                }
            }

            return "";
        }

        return n.GetProperty("__typename").GetString() switch
        {
            "IssueComment" => new TimelineItem("comment", Login(n, "author"), At("createdAt"), Text(n, "body"), null, null,
                Text(n, "id") is { Length: > 0 } id ? id : null, Bool(n, "viewerCanUpdate"), Bool(n, "viewerCanDelete"))
            {
                AuthorAssociation = Text(n, "authorAssociation"), AvatarUrl = Avatar(n, "author"), EditedAt = Date(n, "lastEditedAt"), Url = Https(Text(n, "url")), Reactions = Reactions(n),
                Minimized = Bool(n, "isMinimized"), MinimizedReason = Text(n, "minimizedReason") is { Length: > 0 } why ? why : null,
            },
            // A review's summary is the author's to edit (viewerCanUpdate), as on github.com; deleting a submitted review is not a thing GitHub offers.
            "PullRequestReview" => new TimelineItem("review", Login(n, "author"),
                n.TryGetProperty("submittedAt", out var s) && s.ValueKind == JsonValueKind.String ? s.GetDateTimeOffset() : At("createdAt"),
                Text(n, "body"), Text(n, "state"), Oid(n, "commit"), Text(n, "id") is { Length: > 0 } rid ? rid : null, Bool(n, "viewerCanUpdate"))
            {
                AuthorAssociation = Text(n, "authorAssociation"), AvatarUrl = Avatar(n, "author"), EditedAt = Date(n, "lastEditedAt"), Url = Https(Text(n, "url")), Reactions = Reactions(n),
            },
            "PullRequestCommit" => Commit(n.GetProperty("commit"), Https(Text(n, "url"))),
            "HeadRefForcePushedEvent" => Event("force-push", sha: Oid(n, "afterCommit"), previous: Oid(n, "beforeCommit")),
            "MergedEvent" => Event("merged", target: Text(n, "mergeRefName"), sha: Oid(n, "commit")),
            "ClosedEvent" => Event("closed"),
            "ReopenedEvent" => Event("reopened"),
            "ReadyForReviewEvent" => Event("ready"),
            "ConvertToDraftEvent" => Event("draft"),
            "LabeledEvent" => Event("labeled", target: Named("label", "name"), color: HexColor(Named("label", "color"))),
            "UnlabeledEvent" => Event("unlabeled", target: Named("label", "name"), color: HexColor(Named("label", "color"))),
            "AssignedEvent" => Event("assigned", target: Named("assignee", "login")),
            "UnassignedEvent" => Event("unassigned", target: Named("assignee", "login")),
            "ReviewRequestedEvent" => Event("review-requested", target: Named("requestedReviewer", "login", "name")),
            "ReviewRequestRemovedEvent" => Event("review-request-removed", target: Named("requestedReviewer", "login", "name")),
            "ReviewDismissedEvent" => Event("review-dismissed", target: n.TryGetProperty("review", out var r) && r.ValueKind == JsonValueKind.Object ? Login(r, "author") : "", message: Text(n, "dismissalMessage")),
            "RenamedTitleEvent" => Event("renamed", target: Text(n, "currentTitle"), previous: Text(n, "previousTitle")),
            "BaseRefChangedEvent" => Event("base-changed", target: Text(n, "currentRefName"), previous: Text(n, "previousRefName")),
            "BaseRefForcePushedEvent" => Event("base-force-push", sha: Oid(n, "afterCommit"), previous: Oid(n, "beforeCommit")),
            "HeadRefDeletedEvent" => Event("head-deleted", target: Text(n, "headRefName")),
            "HeadRefRestoredEvent" => Event("head-restored"),
            "CrossReferencedEvent" => Event("cross-referenced", message: Bool(n, "willCloseTarget") ? "closes" : null) with { Reference = Reference(n) },
            "ReferencedEvent" => Event("referenced", target: n.TryGetProperty("commitRepository", out var cr) && cr.ValueKind == JsonValueKind.Object ? Text(cr, "nameWithOwner") : null,
                message: n.TryGetProperty("commit", out var rc) && rc.ValueKind == JsonValueKind.Object ? Text(rc, "messageHeadline") : null, sha: Oid(n, "commit")),
            "MilestonedEvent" => Event("milestoned", target: Text(n, "milestoneTitle")),
            "DemilestonedEvent" => Event("demilestoned", target: Text(n, "milestoneTitle")),
            "LockedEvent" => Event("locked", message: Text(n, "lockReason") is { Length: > 0 } reason ? reason : null),
            "UnlockedEvent" => Event("unlocked"),
            "AutoMergeEnabledEvent" => Event("auto-merge-on"),
            "AutoMergeDisabledEvent" => Event("auto-merge-off", message: Text(n, "reason") is { Length: > 0 } why ? why : null),
            "AddedToMergeQueueEvent" => Event("queued"),
            "RemovedFromMergeQueueEvent" => Event("dequeued"),
            "DeployedEvent" => Event("deployed", target: n.TryGetProperty("deployment", out var d) && d.ValueKind == JsonValueKind.Object ? Text(d, "environment") : null),
            _ => null,
        };

        static TimelineItem Commit(JsonElement c, string? url)
        {
            var author = c.GetProperty("author");
            var user = author.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? Text(u, "login") : "";
            return new TimelineItem("commit", user.Length > 0 ? user : Text(author, "name"), c.GetProperty("committedDate").GetDateTimeOffset(),
                Text(c, "messageHeadline"), null, Text(c, "oid"))
            {
                AvatarUrl = Avatar(author),
                Url = url,
                Verified = c.TryGetProperty("signature", out var sig) && sig.ValueKind == JsonValueKind.Object ? Bool(sig, "isValid") : null,
            };
        }

        static TimelineReference? Reference(JsonElement n)
        {
            if (!n.TryGetProperty("source", out var src) || src.ValueKind != JsonValueKind.Object || Int(src, "number") is not { } number)
            {
                return null;
            }

            var repo = src.TryGetProperty("repository", out var rp) && rp.ValueKind == JsonValueKind.Object ? Text(rp, "nameWithOwner") : "";
            return new TimelineReference(number, Text(src, "title"), repo, Https(Text(src, "url")), Text(src, "__typename") == "PullRequest", Text(src, "state"));
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
                    var user = c.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
                    var login = user.ValueKind == JsonValueKind.Object ? Text(user, "login") : "";
                    var message = commit.GetProperty("message").GetString() ?? "";
                    var newline = message.IndexOf('\n', StringComparison.Ordinal);
                    var committer = commit.TryGetProperty("committer", out var cm) && cm.ValueKind == JsonValueKind.Object ? Text(cm, "name") : "";
                    return new PullCommit(
                        c.GetProperty("sha").GetString() ?? "",
                        newline < 0 ? message : message[..newline],
                        login.Length > 0 ? login : author.GetProperty("name").GetString() ?? "",
                        author.GetProperty("date").GetDateTimeOffset())
                    {
                        AuthorLogin = login.Length > 0 ? login : null,
                        AuthorAvatar = user.ValueKind == JsonValueKind.Object ? Avatar(user) : null,
                        // Named only when it is somebody else: a rebase, or a merge made by another hand.
                        Committer = committer.Length > 0 && committer != Text(author, "name") ? committer : null,
                        Body = newline < 0 ? "" : message[(newline + 1)..].Trim('\n', '\r'),
                        Verified = commit.TryGetProperty("verification", out var v) && v.ValueKind == JsonValueKind.Object ? Bool(v, "verified") : null,
                        ParentCount = c.TryGetProperty("parents", out var ps) && ps.ValueKind == JsonValueKind.Array ? ps.GetArrayLength() : 1,
                        Url = Https(Text(c, "html_url")),
                    };
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
    /// <summary>The most pages of check runs, and of statuses, read for one commit: 1,000 of each.</summary>
    internal const int MaxCheckPages = 10;

    /// <remarks>
    /// Both lists are paged by GitHub. Only the first page of each was read until a review
    /// supplied 100 passing check runs and one failing one: the failure was never shown, and nothing
    /// said the list was cut (2026-10-20). Every page is now read, to a bound; past it the answer
    /// says it was cut. Every page goes with its own ETag, so an unchanged poll still costs nothing;
    /// a changed page answers in full, and a new run changes <c>total_count</c> on every page.
    /// </remarks>
    public async Task<RepoResult<CheckList>> GetChecksAsync(RepoAccess access, string owner, string repo, string commitSha, CancellationToken ct)
    {
        var runs = await PagedChecksAsync(access, $"repos/{E(owner)}/{E(repo)}/commits/{E(commitSha)}/check-runs", "check_runs", r =>
        {
            var output = r.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
            return new CheckInfo(
                r.GetProperty("name").GetString() ?? "",
                r.GetProperty("status").GetString() ?? "",
                Text(r, "conclusion") is { Length: > 0 } c ? c : null,
                Https(Text(r, "html_url")),
                "check-run")
            {
                Id = r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : null,
                StartedAt = Date(r, "started_at"),
                CompletedAt = Date(r, "completed_at"),
                App = r.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.Object ? Text(app, "name") is { Length: > 0 } an ? an : null : null,
                Suite = r.TryGetProperty("check_suite", out var suite) && suite.ValueKind == JsonValueKind.Object && suite.TryGetProperty("id", out var sid) && sid.ValueKind == JsonValueKind.Number ? sid.GetInt64() : null,
                Title = output.ValueKind == JsonValueKind.Object && Text(output, "title") is { Length: > 0 } title ? title : null,
                Summary = output.ValueKind == JsonValueKind.Object && Text(output, "summary") is { Length: > 0 } summary ? summary : null,
                AnnotationCount = output.ValueKind == JsonValueKind.Object ? Int(output, "annotations_count") ?? 0 : 0,
            };
        }, ct).ConfigureAwait(false);
        if (!runs.Ok)
        {
            return runs;
        }

        var statuses = await PagedChecksAsync(access, $"repos/{E(owner)}/{E(repo)}/commits/{E(commitSha)}/status", "statuses", s =>
        {
            var state = s.GetProperty("state").GetString() ?? "";
            return new CheckInfo(
                s.GetProperty("context").GetString() ?? "",
                state == "pending" ? "pending" : "completed",
                state == "pending" ? null : state,
                Https(Text(s, "target_url")),
                "status")
            {
                StartedAt = Date(s, "created_at"),
                CompletedAt = state == "pending" ? null : Date(s, "updated_at"),
                App = s.TryGetProperty("creator", out var creator) && creator.ValueKind == JsonValueKind.Object ? Text(creator, "login") is { Length: > 0 } cl ? cl : null : null,
                Description = Text(s, "description") is { Length: > 0 } d ? d : null,
            };
        }, ct).ConfigureAwait(false);
        return statuses.Ok
            ? RepoResult<CheckList>.Of(new CheckList([.. runs.Value!.Checks, .. statuses.Value!.Checks], runs.Value.Truncated || statuses.Value.Truncated))
            : statuses;
    }

    /// <summary>One paged list of checks: every page while GitHub's <c>total_count</c> says there is more, to <see cref="MaxCheckPages"/>.</summary>
    private async Task<RepoResult<CheckList>> PagedChecksAsync(RepoAccess access, string relative, string property, Func<JsonElement, CheckInfo> one, CancellationToken ct)
    {
        (IReadOnlyList<CheckInfo> Items, int Total) Page(JsonElement root)
        {
            var items = root.GetProperty(property).EnumerateArray().Select(one).ToList();
            // No total_count: the page is the whole list (and an older answer shape reads as one page).
            return (items, Int(root, "total_count") ?? items.Count);
        }

        var first = await GetConditionalAsync(access, $"{relative}?per_page=100&page=1", Page, ct).ConfigureAwait(false);
        if (!first.Ok)
        {
            return first.As<CheckList>();
        }

        var all = first.Value.Items.ToList();
        var total = first.Value.Total;
        for (var page = 2; all.Count < total && page <= MaxCheckPages; page++)
        {
            // With its own ETag too: an unchanged poll of a long list was one 304 and nine full
            // reads, every 30 s per viewer (found in review, 2026-10-20). Now every page is a 304.
            var next = await GetConditionalAsync(access, $"{relative}?per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}", Page, ct).ConfigureAwait(false);
            if (!next.Ok)
            {
                // A later page that cannot be read: what was read, said to be cut — never passed off as all.
                log.Warn(LogCategory.Web, $"checks page {page} of {Redact(relative)} unreadable ({next.Failure}); the list is shown as cut");
                return RepoResult<CheckList>.Of(new CheckList(all, true));
            }

            if (next.Value.Items.Count == 0)
            {
                break;
            }

            all.AddRange(next.Value.Items);
        }

        return RepoResult<CheckList>.Of(new CheckList(all, all.Count < total));
    }

    private static string? Https(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u.AbsoluteUri : null;

    /// <summary>
    /// A person's picture, only from GitHub's avatar host: the page's CSP admits images from there
    /// alone, so any other address would be a broken picture, and a CI's or a Markdown's address is
    /// not ours to vouch for.
    /// </summary>
    private static string? Avatar(JsonElement who) =>
        who.ValueKind == JsonValueKind.Object && Https(Text(who, "avatarUrl") is { Length: > 0 } a ? a : Text(who, "avatar_url")) is { } url
            && new Uri(url).Host.Equals("avatars.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ? url : null;

    private static string? Avatar(JsonElement element, string property) =>
        element.TryGetProperty(property, out var who) ? Avatar(who) : null;

    private static DateTimeOffset? Date(JsonElement element, string property) =>
        element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var when) ? when : null;

    /// <summary>GitHub's reaction groups on a node, those with anyone in them.</summary>
    private static IReadOnlyList<ReactionGroup> Reactions(JsonElement element)
    {
        if (!element.TryGetProperty("reactionGroups", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return groups.EnumerateArray()
            .Select(g => new ReactionGroup(Text(g, "content"),
                g.TryGetProperty("reactors", out var r) && r.ValueKind == JsonValueKind.Object ? Int(r, "totalCount") ?? 0 : 0,
                Bool(g, "viewerHasReacted")))
            .Where(g => g.Count > 0 && g.Content.Length > 0)
            .ToList();
    }

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
    /// <param name="preview">A GraphQL preview's media type, for a field GitHub still gates behind one.</param>
    private async Task<RepoResult<T>> GraphAsync<T>(RepoAccess access, string query, object variables, Func<JsonElement, T> read, CancellationToken ct, string? preview = null)
    {
        if (access.Token is null)
        {
            return RepoResult<T>.Fail(RepoFailure.Unauthorized, "GraphQL needs a token");
        }

        var url = GitHub.GraphQlUrl.Length > 0 ? new Uri(GitHub.GraphQlUrl) : Api("graphql");
        using var request = Request(access, HttpMethod.Post, url);
        if (preview is not null)
        {
            request.Headers.Accept.ParseAdd(preview);
        }

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
