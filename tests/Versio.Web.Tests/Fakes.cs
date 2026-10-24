using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Versio.Web.Sources;

namespace Versio.Web.Tests;

/// <summary>The regex timeout before any test builds a pattern, as Program.cs does before the host.</summary>
internal static class ProcessSetup
{
    [ModuleInitializer]
    internal static void Run()
    {
        WebHost.ConfigureProcess();
        WebHost.RegisterFormats();
    }
}

/// <summary>Where this repository is, so the fakes can serve its real fixtures.</summary>
internal static class RepoRoot
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Versio.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Versio.sln not found above the test output.");
    }

    /// <summary>Every file under <paramref name="relative"/>, repo-relative with forward slashes.</summary>
    public static IEnumerable<string> Files(string relative) =>
        Directory.EnumerateFiles(System.IO.Path.Combine(Path, relative), "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(Path, f).Replace('\\', '/'));
}

/// <summary>
/// A GitHub in memory: repositories made of commits made of files, each read recorded with the
/// credential it used.
/// </summary>
/// <remarks>
/// It answers as GitHub does where the security rules care: a credential that may not see a
/// private repository gets "not found", and the App's installation token CAN see a private
/// repository it is installed on, which is exactly the case the resolver must refuse.
/// </remarks>
internal sealed class FakeGitHub : IRepoSource
{
    public sealed class FakeRepo
    {
        public required string Owner { get; init; }

        public required string Name { get; init; }

        public bool Private { get; init; }

        public string DefaultBranch { get; init; } = "main";

        /// <summary>Branch or commit name to commit sha.</summary>
        public Dictionary<string, string> Refs { get; } = new(StringComparer.Ordinal);

        /// <summary>Commit sha to its files.</summary>
        public Dictionary<string, Dictionary<string, byte[]>> Commits { get; } = new(StringComparer.Ordinal);

        /// <summary>User tokens allowed to read it when private. The installation token always can.</summary>
        public HashSet<string> Readers { get; } = new(StringComparer.Ordinal);

        /// <summary>Open pull requests, as the pulls tab lists them, each with its Markdown body.</summary>
        public List<(PullInfo Info, string Body)> Pulls { get; } = [];

        /// <summary>
        /// The merge-base of (base commit, head commit). The fake has no commit graph, so a test
        /// that compares a pull request says what git would answer; a pair not listed has none.
        /// </summary>
        public Dictionary<(string Base, string Head), string> MergeBases { get; } = [];

        /// <summary>A pull request's changed files as GitHub lists them for its live head.</summary>
        public Dictionary<int, PullFileList> PullFiles { get; } = [];

        /// <summary>The changed files GitHub's compare lists between two commits.</summary>
        public Dictionary<(string Base, string Head), PullFileList> CompareFiles { get; } = [];

        /// <summary>Labels and reviewers per pull request, for the live summary.</summary>
        public Dictionary<int, (IReadOnlyList<PullLabel> Labels, IReadOnlyList<PullReviewer> Reviewers)> PullPeople { get; } = [];

        /// <summary>Review threads per pull request.</summary>
        public Dictionary<int, ReviewThreadList> Threads { get; } = [];

        /// <summary>The conversation per pull request.</summary>
        public Dictionary<int, Timeline> Timelines { get; } = [];

        /// <summary>What GitHub's header counts and marks per pull request: the diff's size, and the checks branch protection requires.</summary>
        public Dictionary<int, (int Additions, int Deletions, int ChangedFiles, IReadOnlyList<string> Required)> PullHeader { get; } = [];

        /// <summary>The branch's commits per pull request, oldest first.</summary>
        public Dictionary<int, PullCommitList> PullCommits { get; } = [];

        /// <summary>The viewer's pending review per pull request, by user token: GitHub shows each person only their own.</summary>
        public Dictionary<(int Number, string Token), PendingReview> Pending { get; } = [];

        /// <summary>Viewed paths per pull request, by user token.</summary>
        public Dictionary<(int Number, string Token), List<string>> Viewed { get; } = [];

        /// <summary>The pull request's author, and each person's latest submitted review commit, by user token.</summary>
        public Dictionary<int, string> PullAuthorToken { get; } = [];

        public Dictionary<(int Number, string Token), string> LastReviewCommit { get; } = [];

        /// <summary>Check runs and statuses per commit.</summary>
        public Dictionary<string, IReadOnlyList<CheckInfo>> Checks { get; } = new(StringComparer.Ordinal);

        /// <summary>Published releases, newest first, each with its Markdown notes.</summary>
        public List<(ReleaseInfo Info, string Body)> Releases { get; } = [];

        /// <summary>A pull request's state when not OPEN: CLOSED or MERGED.</summary>
        public Dictionary<int, string> PullStates { get; } = [];

        /// <summary>Assignees per pull request.</summary>
        public Dictionary<int, List<string>> Assignees { get; } = [];

        /// <summary>The repository's labels.</summary>
        public List<PullLabel> Labels { get; } = [];

        /// <summary>Who can be assigned here.</summary>
        public HashSet<string> Assignable { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tokens GitHub says may not edit the pull requests (viewerCanUpdate false); everyone else may.</summary>
        public HashSet<string> CannotUpdate { get; } = new(StringComparer.Ordinal);

        /// <summary>Each commit's parents, first parent first; a commit not listed has none known.</summary>
        public Dictionary<string, List<string>> Parents { get; } = new(StringComparer.Ordinal);

        /// <summary>Commits whose checks are reported cut short.</summary>
        public HashSet<string> ChecksCut { get; } = new(StringComparer.Ordinal);

        /// <summary>GitHub's mergeStateStatus per pull request; CLEAN when not set.</summary>
        public Dictionary<int, string> MergeStatus { get; } = [];

        /// <summary>The merge methods the repository allows.</summary>
        public List<string> MergeMethods { get; } = ["MERGE", "SQUASH", "REBASE"];

        public bool AutoMergeAllowed { get; set; } = true;

        /// <summary>Auto-merge per pull request: its method, who turned it on, and for which head.</summary>
        public Dictionary<int, (string Method, string By, string Head)> AutoMerge { get; } = [];

        /// <summary>Merge commits per merged pull request.</summary>
        public Dictionary<int, string> MergeCommits { get; } = [];

        /// <summary>When set, the merge-state read fails with this: what GitHub refusing the preview field looks like.</summary>
        public RepoFailure? MergeStateFailure { get; set; }

        /// <summary>The repository's open milestones.</summary>
        public List<MilestoneInfo> Milestones { get; } = [];

        /// <summary>Each pull request's milestone number, when it has one.</summary>
        public Dictionary<int, int> PullMilestone { get; } = [];

        /// <summary>Locked conversations, with their reason.</summary>
        public Dictionary<int, string?> Locked { get; } = [];

        /// <summary>Each person's notifications per pull request, by user token; SUBSCRIBED when unset.</summary>
        public Dictionary<(int Number, string Token), string> Subscriptions { get; } = [];

        /// <summary>Each commit's message, for the commits a writer made here.</summary>
        public Dictionary<string, string> CommitMessages { get; } = new(StringComparer.Ordinal);

        /// <summary>The repository's "Automatically delete head branches" setting.</summary>
        public bool DeleteBranchOnMerge { get; set; }

        /// <summary>Base branches with a merge queue.</summary>
        public HashSet<string> MergeQueueBranches { get; } = new(StringComparer.Ordinal);

        /// <summary>Pull requests in a merge queue, with their position.</summary>
        public Dictionary<int, int> Queued { get; } = [];

        /// <summary>Pull requests whose head lives in a fork.</summary>
        public HashSet<int> CrossRepository { get; } = [];
    }

    private readonly Dictionary<string, FakeRepo> _repos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    public ConcurrentQueue<(string Repo, string What, RepoAccess Access)> Reads { get; } = new();

    /// <summary>
    /// Runs before every pull request read, with how many have been made: a test moves the head
    /// here to land a push between two reads the server meant to agree.
    /// </summary>
    public Action<int>? BeforePullRead { get; set; }

    private int _pullReads;

    /// <summary>Blobs whose read fails, and how: a rate limit, an outage, for as long as they stay listed.</summary>
    public ConcurrentDictionary<string, RepoFailure> FailingBlobs { get; } = new(StringComparer.Ordinal);

    public FakeRepo Add(string owner, string name, bool isPrivate = false)
    {
        var repo = new FakeRepo { Owner = owner, Name = name, Private = isPrivate };
        _repos[$"{owner}/{name}"] = repo;
        return repo;
    }

    /// <summary>Adds a commit and points <paramref name="branch"/> at it.</summary>
    public string Commit(FakeRepo repo, string branch, Dictionary<string, byte[]> files)
    {
        var sha = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(branch + string.Join(',', files.Keys.Order()) + files.Values.Sum(v => v.Length)))).ToLowerInvariant();
        repo.Commits[sha] = files;
        repo.Refs[branch] = sha;
        foreach (var bytes in files.Values)
        {
            _blobs[BlobSha(bytes)] = bytes;
        }

        return sha;
    }

    /// <summary>The files of the commit <paramref name="branch"/> points at.</summary>
    public Dictionary<string, byte[]> CommitFiles(FakeRepo repo, string branch) => repo.Commits[repo.Refs[branch]];

    /// <summary>The files of this repository under the given folders, as on disk.</summary>
    public static Dictionary<string, byte[]> FromDisk(params string[] folders)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (File.Exists(System.IO.Path.Combine(RepoRoot.Path, folder)))
            {
                files[folder] = File.ReadAllBytes(System.IO.Path.Combine(RepoRoot.Path, folder));
                continue;
            }

            foreach (var file in RepoRoot.Files(folder))
            {
                files[file] = File.ReadAllBytes(System.IO.Path.Combine(RepoRoot.Path, file));
            }
        }

        return files;
    }

    /// <summary>Git's blob id: SHA-1 of "blob {length}\0" and the bytes.</summary>
    public static string BlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes])).ToLowerInvariant();
    }

    public const string InstallationToken = "installation-token";

    private RepoResult<FakeRepo> See(RepoAccess access, string owner, string repo, string what)
    {
        Reads.Enqueue(($"{owner}/{repo}", what, access));
        if (!_repos.TryGetValue($"{owner}/{repo}", out var r))
        {
            return RepoResult<FakeRepo>.Fail(RepoFailure.NotFound);
        }

        var allowed = !r.Private
                      || access.Kind == AccessKind.Installation
                      || (access.Kind == AccessKind.User && access.Token is { } t && r.Readers.Contains(t));
        return allowed ? RepoResult<FakeRepo>.Of(r) : RepoResult<FakeRepo>.Fail(RepoFailure.NotFound);
    }

    public Task<RepoResult<RepoInfo>> GetRepoAsync(RepoAccess access, string owner, string repo, CancellationToken ct)
    {
        var r = See(access, owner, repo, "repo");
        return Task.FromResult(r.Ok ? RepoResult<RepoInfo>.Of(new RepoInfo(r.Value!.Owner, r.Value.Name, r.Value.Private, r.Value.DefaultBranch)) : r.As<RepoInfo>());
    }

    public Task<RepoResult<BranchList>> GetBranchesAsync(RepoAccess access, string owner, string repo, CancellationToken ct)
    {
        var r = See(access, owner, repo, "branches");
        return Task.FromResult(r.Ok
            ? RepoResult<BranchList>.Of(new BranchList(r.Value!.Refs.Select(kv => new BranchInfo(kv.Key, kv.Value)).ToList(), false))
            : r.As<BranchList>());
    }

    public Task<RepoResult<string>> ResolveCommitAsync(RepoAccess access, string owner, string repo, string reference, CancellationToken ct)
    {
        var r = See(access, owner, repo, "resolve " + reference);
        if (!r.Ok)
        {
            return Task.FromResult(r.As<string>());
        }

        var sha = r.Value!.Refs.TryGetValue(reference, out var s) ? s : r.Value.Commits.ContainsKey(reference) ? reference : null;
        return Task.FromResult(sha is null ? RepoResult<string>.Fail(RepoFailure.NotFound) : RepoResult<string>.Of(sha));
    }

    /// <summary>Runs before a tree is read, with the credential reading it: may hold the read, or fail it.</summary>
    public Func<RepoAccess, Task<RepoFailure>>? BeforeTree { get; set; }

    public async Task<RepoResult<TreeSnapshot>> GetTreeAsync(RepoAccess access, string owner, string repo, string commitSha, CancellationToken ct)
    {
        var r = See(access, owner, repo, "tree " + commitSha);
        if (!r.Ok || !r.Value!.Commits.TryGetValue(commitSha, out var files))
        {
            return RepoResult<TreeSnapshot>.Fail(RepoFailure.NotFound);
        }

        if (BeforeTree is { } before && await before(access) is var failure and not RepoFailure.None)
        {
            return RepoResult<TreeSnapshot>.Fail(failure);
        }

        return RepoResult<TreeSnapshot>.Of(new TreeSnapshot(
            commitSha, files.Select(f => new TreeEntry(f.Key, BlobSha(f.Value), f.Value.Length)).ToList(), false));
    }

    public Task<RepoResult<byte[]>> GetBlobAsync(RepoAccess access, string owner, string repo, string blobSha, long maxBytes, CancellationToken ct)
    {
        var r = See(access, owner, repo, "blob " + blobSha);
        if (!r.Ok)
        {
            return Task.FromResult(r.As<byte[]>());
        }

        if (FailingBlobs.TryGetValue(blobSha, out var failure))
        {
            return Task.FromResult(RepoResult<byte[]>.Fail(failure));
        }

        // Only a blob that is in one of THIS repository's commits: a blob id is not a key to
        // every repository on the server.
        var inRepo = r.Value!.Commits.Values.Any(c => c.Values.Any(v => BlobSha(v) == blobSha));
        if (!inRepo || !_blobs.TryGetValue(blobSha, out var bytes))
        {
            return Task.FromResult(RepoResult<byte[]>.Fail(RepoFailure.NotFound));
        }

        return Task.FromResult(bytes.Length > maxBytes ? RepoResult<byte[]>.Fail(RepoFailure.TooLarge) : RepoResult<byte[]>.Of(bytes));
    }

    public Task<RepoResult<IReadOnlyList<CommitInfo>>> GetCommitsAsync(RepoAccess access, string owner, string repo, string reference, string? path, int limit, CancellationToken ct)
    {
        var r = See(access, owner, repo, "commits");
        return Task.FromResult(r.Ok
            ? RepoResult<IReadOnlyList<CommitInfo>>.Of(r.Value!.Refs.Select(kv => new CommitInfo(kv.Value, "commit " + kv.Key, "Tester", DateTimeOffset.UnixEpoch)).ToList())
            : r.As<IReadOnlyList<CommitInfo>>());
    }

    public Task<RepoResult<IReadOnlyList<PullInfo>>> GetPullsAsync(RepoAccess access, string owner, string repo, int limit, CancellationToken ct)
    {
        var r = See(access, owner, repo, "pulls");
        return Task.FromResult(r.Ok
            ? RepoResult<IReadOnlyList<PullInfo>>.Of(r.Value!.Pulls.Select(p => p.Info).Take(limit).ToList())
            : r.As<IReadOnlyList<PullInfo>>());
    }

    /// <summary>A pull request as the list draws it, from the fake's parts: state, labels, assignees, comments, the merge state's decision.</summary>
    private static PullRow RowOf(FakeRepo r, (PullInfo Info, string Body) p)
    {
        var state = r.PullStates.TryGetValue(p.Info.Number, out var s) ? s : "OPEN";
        var people = r.PullPeople.TryGetValue(p.Info.Number, out var known) ? known : ([], []);
        var comments = (r.Timelines.TryGetValue(p.Info.Number, out var tl) ? tl.Items.Count(i => i.Kind == "comment") : 0)
            + (r.Threads.TryGetValue(p.Info.Number, out var th) ? th.Threads.Sum(x => x.Comments.Count) : 0);
        return new PullRow(p.Info.Number, p.Info.Title, p.Info.Author, null, p.Info.Updated, p.Info.Updated, state, p.Info.Draft, p.Info.BaseRef, p.Info.HeadRef, p.Info.HeadSha,
            people.Labels, (r.Assignees.TryGetValue(p.Info.Number, out var a) ? a : []).Select(x => (x, (string?)null)).ToList(),
            people.Reviewers.Any(x => x.State == "CHANGES_REQUESTED") ? "CHANGES_REQUESTED" : people.Reviewers.Any(x => x.State == "APPROVED") ? "APPROVED" : null,
            r.Checks.TryGetValue(p.Info.HeadSha, out var checks) && checks.Count > 0 ? (checks.Any(c => c.Conclusion is "failure") ? "FAILURE" : checks.All(c => c.Status == "completed") ? "SUCCESS" : "PENDING") : null,
            comments);
    }

    /// <summary>
    /// GitHub's search, as far as the tests need it: the tokens PullQuery writes, matched against the
    /// fake's rows — is:, author:, label:, assignee:, review:, and words against the title.
    /// </summary>
    private static bool Matches(FakeRepo r, PullRow row, string search)
    {
        foreach (var token in search.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = token.IndexOf(':', StringComparison.Ordinal);
            var (key, value) = colon > 0 ? (token[..colon], token[(colon + 1)..].Trim('"')) : ("", token.Trim('"'));
            var ok = key switch
            {
                "repo" or "sort" => true,
                "is" => value switch { "pr" => true, "open" => row.State == "OPEN", "closed" => row.State != "OPEN", "merged" => row.State == "MERGED", "draft" => row.Draft, _ => false },
                "author" => row.Author == value,
                "assignee" => row.Assignees.Any(a => a.Login == value),
                "label" => row.Labels.Any(l => l.Name == value),
                "review" => value switch { "approved" => row.ReviewDecision == "APPROVED", "changes_requested" => row.ReviewDecision == "CHANGES_REQUESTED", "none" => row.ReviewDecision is null, _ => true },
                "" => row.Title.Contains(value, StringComparison.OrdinalIgnoreCase),
                _ => true,
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    public Task<RepoResult<PullSearch>> SearchPullsAsync(RepoAccess access, string search, string open, string closed, int first, string? after, CancellationToken ct)
    {
        // The repository is named in the query, as GitHub's search names it.
        var slug = search.Split(' ')[0]["repo:".Length..];
        var r = SeeGraph(access, slug.Split('/')[0], slug.Split('/')[1], "search " + search);
        if (!r.Ok)
        {
            return Task.FromResult(r.As<PullSearch>());
        }

        var rows = r.Value!.Pulls.Select(p => RowOf(r.Value, p)).ToList();
        var matched = rows.Where(row => Matches(r.Value, row, search)).OrderByDescending(row => row.UpdatedAt).ToList();
        var skip = after is null ? 0 : int.Parse(after, System.Globalization.CultureInfo.InvariantCulture);
        var page = matched.Skip(skip).Take(first).ToList();
        return Task.FromResult(RepoResult<PullSearch>.Of(new PullSearch(page, matched.Count,
            rows.Count(row => Matches(r.Value, row, open)), rows.Count(row => Matches(r.Value, row, closed)),
            skip + first < matched.Count ? (skip + first).ToString(System.Globalization.CultureInfo.InvariantCulture) : null)));
    }

    public Task<RepoResult<IReadOnlyList<PullRow>>> GetPullRowsAsync(RepoAccess access, string owner, string repo, string state, string sort, string direction, int limit, int page, CancellationToken ct)
    {
        var r = See(access, owner, repo, "pull-rows " + state);
        return Task.FromResult(r.Ok
            ? RepoResult<IReadOnlyList<PullRow>>.Of(r.Value!.Pulls.Select(p => RowOf(r.Value, p))
                .Where(row => state == "all" || (state == "open" ? row.State == "OPEN" : row.State != "OPEN"))
                .OrderByDescending(row => row.UpdatedAt).Skip((page - 1) * limit).Take(limit).ToList())
            : r.As<IReadOnlyList<PullRow>>());
    }

    public Task<RepoResult<PullDetail>> GetPullAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        BeforePullRead?.Invoke(Interlocked.Increment(ref _pullReads));
        var r = See(access, owner, repo, "pull " + number);
        if (!r.Ok)
        {
            return Task.FromResult(r.As<PullDetail>());
        }

        var found = r.Value!.Pulls.Where(p => p.Info.Number == number).ToList();
        return Task.FromResult(found.Count == 0
            ? RepoResult<PullDetail>.Fail(RepoFailure.NotFound)
            : RepoResult<PullDetail>.Of(new PullDetail(found[0].Info, found[0].Body)));
    }

    public Task<RepoResult<string>> GetMergeBaseAsync(RepoAccess access, string owner, string repo, string baseSha, string headSha, CancellationToken ct)
    {
        var r = See(access, owner, repo, $"merge-base {baseSha}...{headSha}");
        if (!r.Ok)
        {
            return Task.FromResult(r.As<string>());
        }

        return Task.FromResult(r.Value!.MergeBases.TryGetValue((baseSha, headSha), out var mergeBase)
            ? RepoResult<string>.Of(mergeBase)
            : RepoResult<string>.Fail(RepoFailure.NotFound));
    }

    public Task<RepoResult<PullFileList>> GetPullFilesAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        var r = See(access, owner, repo, "pull-files " + number);
        if (!r.Ok)
        {
            return Task.FromResult(r.As<PullFileList>());
        }

        return Task.FromResult(r.Value!.PullFiles.TryGetValue(number, out var files)
            ? RepoResult<PullFileList>.Of(files)
            : RepoResult<PullFileList>.Fail(RepoFailure.NotFound));
    }

    public Task<RepoResult<PullFileList>> GetCompareFilesAsync(RepoAccess access, string owner, string repo, string baseSha, string headSha, CancellationToken ct)
    {
        var r = See(access, owner, repo, $"compare-files {baseSha}...{headSha}");
        if (!r.Ok)
        {
            return Task.FromResult(r.As<PullFileList>());
        }

        return Task.FromResult(r.Value!.CompareFiles.TryGetValue((baseSha, headSha), out var files)
            ? RepoResult<PullFileList>.Of(files)
            : RepoResult<PullFileList>.Fail(RepoFailure.NotFound));
    }

    /// <summary>
    /// The fixture preview only: answer GraphQL without a token, so a signed-out preview shows
    /// threads. Every test leaves it off, because GitHub never does this.
    /// </summary>
    public bool PreviewGraphWithoutToken { get; set; }

    /// <summary>GitHub's GraphQL refuses a caller without a token outright; so does this.</summary>
    private RepoResult<FakeRepo> SeeGraph(RepoAccess access, string owner, string repo, string what) =>
        access.Token is null && !PreviewGraphWithoutToken ? RepoResult<FakeRepo>.Fail(RepoFailure.Unauthorized) : See(access, owner, repo, what);

    public Task<RepoResult<PullSummary>> GetPullSummaryAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        var r = SeeGraph(access, owner, repo, "summary " + number);
        if (!r.Ok)
        {
            return Task.FromResult(r.As<PullSummary>());
        }

        var found = r.Value!.Pulls.Where(p => p.Info.Number == number).Select(p => p.Info).ToList();
        if (found.Count == 0)
        {
            return Task.FromResult(RepoResult<PullSummary>.Fail(RepoFailure.NotFound));
        }

        var p = found[0];
        var body = r.Value.Pulls.First(x => x.Info.Number == number).Body;
        var state = r.Value.PullStates.TryGetValue(number, out var s) ? s : "OPEN";
        var people = r.Value.PullPeople.TryGetValue(number, out var known) ? known : ([], []);
        return Task.FromResult(RepoResult<PullSummary>.Of(new PullSummary(
            state, p.Draft, state == "MERGED", "MERGEABLE", null, p.HeadSha, p.BaseRef,
            r.Value.Refs.TryGetValue(p.BaseRef, out var tip) ? tip : null, people.Labels,
            r.Value.Assignees.TryGetValue(number, out var assigned) ? assigned.ToList() : [], people.Reviewers)
        {
            Title = p.Title,
            Body = body,
            ViewerCanUpdate = access.Token is { } can && !r.Value.CannotUpdate.Contains(can),
            ViewerIsAuthor = access.Token is { } token && r.Value.PullAuthorToken.TryGetValue(number, out var author) && author == token,
            ViewerLastReviewCommit = access.Token is { } t && r.Value.LastReviewCommit.TryGetValue((number, t), out var last) ? last : null,
            NodeId = "PR_" + number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Author = p.Author,
            CreatedAt = p.Updated,
            CommitCount = r.Value.PullCommits.TryGetValue(number, out var commits) ? commits.Commits.Count : 0,
            // GitHub's totalCommentsCount: conversation comments and review comments together.
            CommentCount = (r.Value.Timelines.TryGetValue(number, out var tl) ? tl.Items.Count(i => i.Kind == "comment") : 0)
                + (r.Value.Threads.TryGetValue(number, out var th) ? th.Threads.Sum(x => x.Comments.Count) : 0),
            Additions = r.Value.PullHeader.TryGetValue(number, out var header) ? header.Additions : 0,
            Deletions = header.Deletions,
            ChangedFiles = header.ChangedFiles,
            RequiredChecks = header.Required ?? [],
            Locked = r.Value.Locked.ContainsKey(number),
            LockReason = r.Value.Locked.TryGetValue(number, out var why) ? why : null,
            MilestoneNumber = r.Value.PullMilestone.TryGetValue(number, out var milestone) ? milestone : null,
            Milestone = r.Value.PullMilestone.TryGetValue(number, out var m2) ? r.Value.Milestones.FirstOrDefault(x => x.Number == m2)?.Title : null,
            ViewerSubscription = access.Token is { } st ? r.Value.Subscriptions.TryGetValue((number, st), out var sub) ? sub : "SUBSCRIBED" : null,
            ViewerCanReact = access.Kind == AccessKind.User,
        }));
    }

    public Task<RepoResult<IReadOnlyList<MilestoneInfo>>> GetMilestonesAsync(RepoAccess access, string owner, string repo, CancellationToken ct)
    {
        var r = See(access, owner, repo, "milestones");
        return Task.FromResult(r.Ok ? RepoResult<IReadOnlyList<MilestoneInfo>>.Of(r.Value!.Milestones.ToList()) : r.As<IReadOnlyList<MilestoneInfo>>());
    }

    public Task<RepoResult<string>> GetCommitMessageAsync(RepoAccess access, string owner, string repo, string sha, CancellationToken ct)
    {
        var r = See(access, owner, repo, "message " + sha);
        return Task.FromResult(!r.Ok ? r.As<string>()
            : r.Value!.CommitMessages.TryGetValue(sha, out var message) ? RepoResult<string>.Of(message) : RepoResult<string>.Fail(RepoFailure.NotFound));
    }

    public Task<RepoResult<PendingReviewRead>> GetPendingReviewAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        var r = SeeGraph(access, owner, repo, "pending " + number);
        return Task.FromResult(!r.Ok ? r.As<PendingReviewRead>()
            : RepoResult<PendingReviewRead>.Of(new PendingReviewRead(access.Token is { } t && r.Value!.Pending.TryGetValue((number, t), out var p) ? p : null)));
    }

    public Task<RepoResult<IReadOnlyList<string>>> GetViewedAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        var r = SeeGraph(access, owner, repo, "viewed " + number);
        return Task.FromResult(!r.Ok ? r.As<IReadOnlyList<string>>()
            : RepoResult<IReadOnlyList<string>>.Of(access.Token is { } t && r.Value!.Viewed.TryGetValue((number, t), out var v) ? v.ToList() : []));
    }

    public Task<RepoResult<ReviewThreadList>> GetReviewThreadsAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        var r = SeeGraph(access, owner, repo, "threads " + number);
        if (!r.Ok)
        {
            return Task.FromResult(r.As<ReviewThreadList>());
        }

        // The revision the lines are positions in, as GitHub's answer carries it: the pull request as it is now.
        var pull = r.Value!.Pulls.FirstOrDefault(p => p.Info.Number == number).Info;
        var revision = pull is null ? null : new PullRevision(pull.HeadSha, pull.BaseRef, r.Value.Refs.TryGetValue(pull.BaseRef, out var tip) ? tip : null);
        return Task.FromResult(RepoResult<ReviewThreadList>.Of((r.Value.Threads.TryGetValue(number, out var t) ? t : new ReviewThreadList([], false)) with { Revision = revision }));
    }

    public Task<RepoResult<Timeline>> GetTimelineAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        var r = SeeGraph(access, owner, repo, "timeline " + number);
        return Task.FromResult(!r.Ok ? r.As<Timeline>()
            : RepoResult<Timeline>.Of(r.Value!.Timelines.TryGetValue(number, out var t) ? t : new Timeline([], false)));
    }

    public Task<RepoResult<PullCommitList>> GetPullCommitsAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        var r = See(access, owner, repo, "pull-commits " + number);
        return Task.FromResult(!r.Ok ? r.As<PullCommitList>()
            : RepoResult<PullCommitList>.Of(r.Value!.PullCommits.TryGetValue(number, out var c) ? c : new PullCommitList([], false)));
    }

    public Task<RepoResult<MergeState>> GetMergeStateAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct)
    {
        var r = SeeGraph(access, owner, repo, "merge-state " + number);
        if (!r.Ok || r.Value!.MergeStateFailure is { } failure)
        {
            return Task.FromResult(r.Ok ? RepoResult<MergeState>.Fail(r.Value!.MergeStateFailure!.Value) : r.As<MergeState>());
        }

        var found = r.Value.Pulls.Where(p => p.Info.Number == number).Select(p => p.Info).ToList();
        if (found.Count == 0)
        {
            return Task.FromResult(RepoResult<MergeState>.Fail(RepoFailure.NotFound));
        }

        var state = r.Value.PullStates.TryGetValue(number, out var s) ? s : "OPEN";
        var auto = r.Value.AutoMerge.TryGetValue(number, out var a) ? a : ((string, string, string)?)null;
        return Task.FromResult(RepoResult<MergeState>.Of(new MergeState(
            state, state == "MERGED", found[0].HeadSha, r.Value.MergeStatus.TryGetValue(number, out var status) ? status : "CLEAN",
            r.Value.MergeMethods.ToList(), r.Value.AutoMergeAllowed, auto?.Item1, auto?.Item2,
            state == "OPEN" && auto is null, auto is not null, "PR_" + number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            r.Value.MergeCommits.TryGetValue(number, out var commit) ? commit : null)
        {
            HeadRef = found[0].HeadRef,
            HeadRefExists = r.Value.Refs.ContainsKey(found[0].HeadRef),
            IsCrossRepository = r.Value.CrossRepository.Contains(number),
            DeleteBranchOnMerge = r.Value.DeleteBranchOnMerge,
            HasMergeQueue = r.Value.MergeQueueBranches.Contains(found[0].BaseRef),
            InMergeQueue = r.Value.Queued.ContainsKey(number),
            QueuePosition = r.Value.Queued.TryGetValue(number, out var position) ? position : null,
        }));
    }

    public Task<RepoResult<IReadOnlyList<string>>> GetCommitParentsAsync(RepoAccess access, string owner, string repo, string sha, CancellationToken ct)
    {
        var r = See(access, owner, repo, "parents " + sha);
        return Task.FromResult(!r.Ok ? r.As<IReadOnlyList<string>>()
            : r.Value!.Parents.TryGetValue(sha, out var parents) ? RepoResult<IReadOnlyList<string>>.Of(parents.ToList()) : RepoResult<IReadOnlyList<string>>.Fail(RepoFailure.NotFound));
    }

    public Task<RepoResult<IReadOnlyList<PullLabel>>> GetLabelsAsync(RepoAccess access, string owner, string repo, CancellationToken ct)
    {
        var r = See(access, owner, repo, "labels");
        return Task.FromResult(r.Ok ? RepoResult<IReadOnlyList<PullLabel>>.Of(r.Value!.Labels.ToList()) : r.As<IReadOnlyList<PullLabel>>());
    }

    public Task<RepoResult<bool>> CanAssignAsync(RepoAccess access, string owner, string repo, string login, CancellationToken ct)
    {
        var r = See(access, owner, repo, "assignable " + login);
        return Task.FromResult(r.Ok ? RepoResult<bool>.Of(r.Value!.Assignable.Contains(login)) : r.As<bool>());
    }

    public Task<RepoResult<CheckList>> GetChecksAsync(RepoAccess access, string owner, string repo, string commitSha, CancellationToken ct)
    {
        var r = See(access, owner, repo, "checks " + commitSha);
        return Task.FromResult(!r.Ok ? r.As<CheckList>()
            : RepoResult<CheckList>.Of(new CheckList(r.Value!.Checks.TryGetValue(commitSha, out var c) ? c : [], r.Value.ChecksCut.Contains(commitSha))));
    }

    public Task<RepoResult<IReadOnlyList<ReleaseInfo>>> GetReleasesAsync(RepoAccess access, string owner, string repo, int limit, CancellationToken ct)
    {
        var r = See(access, owner, repo, "releases");
        return Task.FromResult(r.Ok
            ? RepoResult<IReadOnlyList<ReleaseInfo>>.Of(r.Value!.Releases.Select(p => p.Info).Take(limit).ToList())
            : r.As<IReadOnlyList<ReleaseInfo>>());
    }

    public Task<RepoResult<ReleaseDetail>> GetReleaseAsync(RepoAccess access, string owner, string repo, string tag, CancellationToken ct)
    {
        var r = See(access, owner, repo, "release " + tag);
        if (!r.Ok)
        {
            return Task.FromResult(r.As<ReleaseDetail>());
        }

        var found = r.Value!.Releases.Where(p => p.Info.Tag == tag).ToList();
        return Task.FromResult(found.Count == 0
            ? RepoResult<ReleaseDetail>.Fail(RepoFailure.NotFound)
            : RepoResult<ReleaseDetail>.Of(new ReleaseDetail(found[0].Info, found[0].Body)));
    }
}
