namespace Versio.Web.Sources;

/// <summary>Whose credential a read is made with.</summary>
public enum AccessKind
{
    /// <summary>No token: GitHub's anonymous access, public repositories only.</summary>
    Anonymous,

    /// <summary>The signed-in user's own token: what they can see, intersected with the App's installation.</summary>
    User,

    /// <summary>The App's installation token, used only anonymously and only for a repository GitHub says is public.</summary>
    Installation,
}

/// <summary>A credential for one request. The token is never logged and never leaves the server.</summary>
public sealed record RepoAccess(AccessKind Kind, string? Token, string Who)
{
    public static readonly RepoAccess Anonymous = new(AccessKind.Anonymous, null, "anonymous");

    /// <summary>What the log may say about this access: who, and how, never the token.</summary>
    public override string ToString() => $"{Who} ({Kind})";
}

/// <summary>Why a read did not return what was asked for.</summary>
public enum RepoFailure
{
    None,

    /// <summary>Missing, or not visible to this credential. GitHub does not say which, and neither do we.</summary>
    NotFound,

    /// <summary>The token was refused: expired or revoked. The page sends the user to sign in.</summary>
    Unauthorized,

    /// <summary>The organisation enforces SAML SSO and this token is not authorised for it.</summary>
    SsoRequired,

    /// <summary>GitHub's rate limit for this credential is spent.</summary>
    RateLimited,

    /// <summary>Too large to fetch at all (<see cref="WebOptions.MaxBlobBytes"/>).</summary>
    TooLarge,

    /// <summary>GitHub failed or could not be reached.</summary>
    Upstream,
}

/// <summary>A read's outcome: a value, or why there is none.</summary>
public sealed record RepoResult<T>(T? Value, RepoFailure Failure, string? Detail = null)
{
    public bool Ok => Failure == RepoFailure.None && Value is not null;

    public static RepoResult<T> Of(T value) => new(value, RepoFailure.None);

    public static RepoResult<T> Fail(RepoFailure failure, string? detail = null) => new(default, failure, detail);

    public RepoResult<TOther> As<TOther>() => new(default, Failure, Detail);
}

public sealed record RepoInfo(string Owner, string Name, bool Private, string DefaultBranch);

public sealed record BranchInfo(string Name, string CommitSha);

/// <summary>A repository's branches, and whether there were more than were listed.</summary>
public sealed record BranchList(IReadOnlyList<BranchInfo> Branches, bool Truncated);

/// <summary>One file in a tree: its path, its blob, its size.</summary>
public sealed record TreeEntry(string Path, string BlobSha, long Size);

/// <summary>A commit's whole tree, flat.</summary>
/// <param name="Truncated">GitHub stopped listing (over 100,000 entries); the page says the tree is partial.</param>
public sealed record TreeSnapshot(string CommitSha, IReadOnlyList<TreeEntry> Files, bool Truncated);

public sealed record CommitInfo(string Sha, string Message, string Author, DateTimeOffset When);

/// <summary>
/// An open pull request as the list shows it. <paramref name="HeadSha"/> rather than a head ref
/// for comparing: the head branch may live in a fork, where its NAME means nothing in this
/// repository, but its commit is reachable here through the pull request itself.
/// </summary>
public sealed record PullInfo(int Number, string Title, string Author, DateTimeOffset Updated, string BaseRef, string HeadRef, string HeadSha, bool Draft);

/// <summary>One pull request with its description (Markdown, possibly empty).</summary>
public sealed record PullDetail(PullInfo Pull, string Body);

/// <summary>One changed file as GitHub's own diff reports it.</summary>
/// <param name="Status">GitHub's word: added, removed, modified, renamed, copied, changed, unchanged.</param>
/// <param name="PreviousPath">Where a renamed or copied file came from.</param>
/// <param name="Patch">The unified hunks GitHub computed, or null when it gave none (binary, too large, past its limits).</param>
public sealed record PullFile(string Path, string Status, string? PreviousPath, string? Patch);

/// <summary>GitHub's changed files for a diff, and whether it stopped listing before the end.</summary>
public sealed record PullFileList(IReadOnlyList<PullFile> Files, bool Truncated);

/// <summary>A label on a pull request. <paramref name="Color"/> is six hex digits, or empty when GitHub sent anything else.</summary>
public sealed record PullLabel(string Name, string Color);

/// <summary>A reviewer's standing: REQUESTED, or their latest APPROVED, CHANGES_REQUESTED, COMMENTED or DISMISSED review.</summary>
public sealed record PullReviewer(string Login, string State);

/// <summary>
/// A pull request's live state, as of the read: what the page polls to notice that the review's
/// revision is no longer the pull request's.
/// </summary>
/// <param name="State">OPEN, CLOSED or MERGED.</param>
/// <param name="Mergeable">MERGEABLE, CONFLICTING or UNKNOWN (GitHub computes it lazily).</param>
/// <param name="ReviewDecision">APPROVED, CHANGES_REQUESTED, REVIEW_REQUIRED, or null when no review is required.</param>
/// <param name="BaseTipSha">The base branch's live tip, or null when the branch is gone.</param>
public sealed record PullSummary(
    string State,
    bool Draft,
    bool Merged,
    string Mergeable,
    string? ReviewDecision,
    string HeadSha,
    string BaseRef,
    string? BaseTipSha,
    IReadOnlyList<PullLabel> Labels,
    IReadOnlyList<string> Assignees,
    IReadOnlyList<PullReviewer> Reviewers)
{
    /// <summary>The viewer opened this pull request: GitHub refuses an author's approval or request for changes.</summary>
    public bool ViewerIsAuthor { get; init; }

    /// <summary>The commit of the viewer's latest submitted review, for "changes since your last review"; null when none.</summary>
    public string? ViewerLastReviewCommit { get; init; }

    /// <summary>The pull request's GraphQL node id, which the review and viewed-file mutations name.</summary>
    public string NodeId { get; init; } = "";

    /// <summary>The title as it is now: edits made anywhere show without reloading the review.</summary>
    public string Title { get; init; } = "";

    /// <summary>The description as it is now (Markdown, untrusted): what an edit starts from and is checked against.</summary>
    public string Body { get; init; } = "";

    /// <summary>GitHub's own word on whether the viewer may edit the pull request (title, body, base, state, labels).</summary>
    public bool ViewerCanUpdate { get; init; }
}

/// <summary>One comment of a pending review, as its author sees it. <paramref name="Body"/> is Markdown: untrusted.</summary>
public sealed record PendingComment(string Id, string Path, string Body);

/// <summary>
/// The viewer's own pending review on a pull request: started, not submitted, visible only to them.
/// GitHub allows one per person per pull request.
/// </summary>
/// <param name="Commit">The commit it was created on, which is the commit it will approve (docs/web/PLAN.md, C2).</param>
public sealed record PendingReview(string Id, string Author, string Commit, IReadOnlyList<PendingComment> Comments);

/// <summary>
/// The answer to "does the viewer have a pending review": <paramref name="Review"/> null is "no".
/// </summary>
/// <remarks>
/// A wrapper because <see cref="RepoResult{T}.Ok"/> needs a value: a bare null review read as a
/// failed read, and the first run of the review tests refused every approval as "not found".
/// </remarks>
public sealed record PendingReviewRead(PendingReview? Review);

/// <summary>One comment in a review thread. <paramref name="Body"/> is Markdown written by anyone who can comment: untrusted.</summary>
/// <param name="CanEdit">GitHub's own word on whether the viewer may edit it (viewerCanUpdate); controls follow it, GitHub still decides.</param>
public sealed record ReviewComment(string Id, string Author, string Body, DateTimeOffset When, string? Commit, string? OriginalCommit, bool CanEdit = false, bool CanDelete = false);

/// <summary>A review thread on a file, as GitHub anchors it.</summary>
/// <param name="Line">The line in GitHub's CURRENT diff, or null when the thread is outdated or on the file.</param>
/// <param name="Side">LEFT (the old file) or RIGHT (the new one).</param>
/// <param name="OriginalLine">Where it was written, in the diff of <see cref="ReviewComment.OriginalCommit"/>.</param>
/// <param name="SubjectType">LINE or FILE.</param>
/// <param name="Truncated">More comments than were read (the work is bounded); the rest are on GitHub.</param>
public sealed record ReviewThread(
    string Id,
    string Path,
    bool Resolved,
    bool Outdated,
    int? Line,
    int? StartLine,
    string Side,
    int? OriginalLine,
    string SubjectType,
    IReadOnlyList<ReviewComment> Comments,
    bool Truncated,
    bool CanReply = false,
    bool CanResolve = false,
    bool CanUnresolve = false);

/// <summary>A pull request's review threads, every page, and whether a bound stopped the reading.</summary>
public sealed record ReviewThreadList(IReadOnlyList<ReviewThread> Threads, bool Truncated);

/// <summary>
/// One entry in a pull request's conversation.
/// </summary>
/// <param name="Kind">comment, review, commit, force-push, merged, closed, reopened, ready, draft.</param>
/// <param name="Body">Markdown for comments and reviews (untrusted); the headline for a commit.</param>
/// <param name="State">A review's APPROVED, CHANGES_REQUESTED, COMMENTED or DISMISSED.</param>
/// <param name="Id">A conversation comment's node id, for editing and deleting it; null for everything else.</param>
public sealed record TimelineItem(string Kind, string Actor, DateTimeOffset When, string? Body, string? State, string? Sha, string? Id = null, bool CanEdit = false, bool CanDelete = false);

/// <summary>A pull request's conversation in order, and whether it was cut.</summary>
public sealed record Timeline(IReadOnlyList<TimelineItem> Items, bool Truncated);

/// <summary>A commit on a pull request's branch.</summary>
public sealed record PullCommit(string Sha, string Headline, string Author, DateTimeOffset When);

/// <summary>A pull request's commits, oldest first, and whether GitHub's 250 cut them.</summary>
public sealed record PullCommitList(IReadOnlyList<PullCommit> Commits, bool Truncated);

/// <summary>
/// One check on a commit: a check run (GitHub Actions and Apps) or a commit status (older CI).
/// </summary>
/// <param name="Status">queued, in_progress or completed for a run; pending or completed for a status.</param>
/// <param name="Conclusion">success, failure, neutral, cancelled, skipped, timed_out, action_required, stale, error; null while running.</param>
/// <param name="Url">Where its log is, https only, or null: a CI's link is not ours to vouch for, so it is only ever opened, never fetched.</param>
public sealed record CheckInfo(string Name, string Status, string? Conclusion, string? Url, string Source);

/// <summary>A published release as the list shows it. Drafts are never listed.</summary>
public sealed record ReleaseInfo(string Tag, string Name, string Author, DateTimeOffset When, bool Prerelease);

/// <summary>One release with its notes (Markdown, possibly empty).</summary>
public sealed record ReleaseDetail(ReleaseInfo Release, string Body);

/// <summary>
/// Where the viewer reads repositories from. GitHub first; GitHub Enterprise Server is the same
/// implementation with another base URL; GitLab and Azure DevOps would be new implementations,
/// with nothing above this interface changing (docs/web/PLAN.md).
/// </summary>
/// <remarks>
/// Every method takes the credential explicitly. There is no ambient token, so a read cannot
/// quietly fall back to a more powerful credential than the one its caller resolved for this
/// person and this repository (security rule 1).
/// </remarks>
public interface IRepoSource
{
    Task<RepoResult<RepoInfo>> GetRepoAsync(RepoAccess access, string owner, string repo, CancellationToken ct);

    Task<RepoResult<BranchList>> GetBranchesAsync(RepoAccess access, string owner, string repo, CancellationToken ct);

    /// <summary>The commit a branch, tag or SHA names, as this credential sees it.</summary>
    Task<RepoResult<string>> ResolveCommitAsync(RepoAccess access, string owner, string repo, string reference, CancellationToken ct);

    /// <summary>The tree of a commit already resolved with <see cref="ResolveCommitAsync"/>.</summary>
    Task<RepoResult<TreeSnapshot>> GetTreeAsync(RepoAccess access, string owner, string repo, string commitSha, CancellationToken ct);

    /// <summary>A blob's bytes, with Git LFS pointers followed to their content.</summary>
    Task<RepoResult<byte[]>> GetBlobAsync(RepoAccess access, string owner, string repo, string blobSha, long maxBytes, CancellationToken ct);

    Task<RepoResult<IReadOnlyList<CommitInfo>>> GetCommitsAsync(RepoAccess access, string owner, string repo, string reference, string? path, int limit, CancellationToken ct);

    /// <summary>The open pull requests, most recently updated first, up to <paramref name="limit"/>.</summary>
    Task<RepoResult<IReadOnlyList<PullInfo>>> GetPullsAsync(RepoAccess access, string owner, string repo, int limit, CancellationToken ct);

    /// <summary>One pull request, open or not, with its description.</summary>
    Task<RepoResult<PullDetail>> GetPullAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct);

    /// <summary>
    /// The best common ancestor of two commits: what a pull request's changes are measured from.
    /// </summary>
    /// <remarks>
    /// GitHub's "Files changed" is a three-dot diff, <c>base...head</c>: the head against the
    /// merge-base, not against the base branch's tip. Comparing to the tip shows every commit that
    /// landed on the base since the branch was cut as if the pull request reverted it.
    /// </remarks>
    Task<RepoResult<string>> GetMergeBaseAsync(RepoAccess access, string owner, string repo, string baseSha, string headSha, CancellationToken ct);

    /// <summary>
    /// A pull request's changed files with GitHub's patches, as its LIVE head has them, up to
    /// GitHub's cap of 3,000.
    /// </summary>
    /// <remarks>
    /// Follows the pull request's current head, not any commit the caller names, so a caller that
    /// needs one revision checks the head before and after (docs/web/PLAN.md, C1).
    /// </remarks>
    Task<RepoResult<PullFileList>> GetPullFilesAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct);

    /// <summary>The changed files between two commits with GitHub's patches: at most 300, by GitHub's limit.</summary>
    Task<RepoResult<PullFileList>> GetCompareFilesAsync(RepoAccess access, string owner, string repo, string baseSha, string headSha, CancellationToken ct);

    /// <summary>A pull request's live state. GraphQL: it needs a token, so an anonymous reader without the App gets <see cref="RepoFailure.Unauthorized"/>.</summary>
    Task<RepoResult<PullSummary>> GetPullSummaryAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct);

    /// <summary>Every review thread with every comment, paged to completion within a bound.</summary>
    Task<RepoResult<ReviewThreadList>> GetReviewThreadsAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct);

    /// <summary>The conversation: comments, reviews, commits and state changes, oldest first, within a bound.</summary>
    Task<RepoResult<Timeline>> GetTimelineAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct);

    /// <summary>The pull request's commits, oldest first, up to GitHub's 250.</summary>
    Task<RepoResult<PullCommitList>> GetPullCommitsAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct);

    /// <summary>The viewer's pending review on the pull request, if they have one.</summary>
    Task<RepoResult<PendingReviewRead>> GetPendingReviewAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct);

    /// <summary>The paths the viewer marked viewed on the pull request (GitHub keeps this per person).</summary>
    Task<RepoResult<IReadOnlyList<string>>> GetViewedAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct);

    /// <summary>The repository's labels, by name, up to 100: what may be put on a pull request (labels are not created here).</summary>
    Task<RepoResult<IReadOnlyList<PullLabel>>> GetLabelsAsync(RepoAccess access, string owner, string repo, CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="login"/> can be assigned in this repository. GitHub silently drops an
    /// assignee who cannot be, so the server asks first rather than reporting an assignment that did
    /// not happen.
    /// </summary>
    Task<RepoResult<bool>> CanAssignAsync(RepoAccess access, string owner, string repo, string login, CancellationToken ct);

    /// <summary>Every check run and commit status on one commit.</summary>
    Task<RepoResult<IReadOnlyList<CheckInfo>>> GetChecksAsync(RepoAccess access, string owner, string repo, string commitSha, CancellationToken ct);

    /// <summary>The published releases, newest first, up to <paramref name="limit"/>.</summary>
    Task<RepoResult<IReadOnlyList<ReleaseInfo>>> GetReleasesAsync(RepoAccess access, string owner, string repo, int limit, CancellationToken ct);

    /// <summary>One release by its tag, with its notes.</summary>
    Task<RepoResult<ReleaseDetail>> GetReleaseAsync(RepoAccess access, string owner, string repo, string tag, CancellationToken ct);
}
