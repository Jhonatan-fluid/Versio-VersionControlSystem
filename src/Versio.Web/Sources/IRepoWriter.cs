namespace Versio.Web.Sources;

/// <summary>How a write ended, as far as this server can know.</summary>
public enum WriteOutcome
{
    /// <summary>GitHub said it did it.</summary>
    Done,

    /// <summary>GitHub said no: nothing was written.</summary>
    Refused,

    /// <summary>
    /// The request may have reached GitHub and the answer did not come back — a timeout, a dropped
    /// connection, a 502. GitHub may well have done it. Never retried blindly (docs/web/PLAN.md, C5).
    /// </summary>
    Unknown,
}

/// <summary>A write's outcome; <paramref name="Message"/> is GitHub's own refusal text, shown spelled.</summary>
public sealed record WriteResult<T>(WriteOutcome Outcome, T? Value, RepoFailure Failure = RepoFailure.None, string? Message = null)
{
    public static WriteResult<T> Done(T value) => new(WriteOutcome.Done, value);

    public static WriteResult<T> Refused(RepoFailure failure, string? message = null) => new(WriteOutcome.Refused, default, failure, message);

    public static WriteResult<T> Unknown() => new(WriteOutcome.Unknown, default, RepoFailure.Upstream);
}

/// <summary>What a GraphQL node id points at: its type, the repository and pull request it belongs to, its author.</summary>
/// <param name="State">A review's state (PENDING, APPROVED, ...); null for anything else.</param>
/// <param name="Commit">A review's commit; null for anything else.</param>
public sealed record NodeOwner(string Type, string Repository, int Number, string? Author, string? State = null, string? Commit = null);

/// <summary>A new pull request between two branches of the same repository.</summary>
public sealed record NewPull(string Base, string Head, string Title, string Body, bool Draft);

/// <summary>What an edit changes: each field null to leave it as it is.</summary>
public sealed record PullEdit(string? Title, string? Body, string? Base);

/// <summary>A comment on a line, or on a whole file when <paramref name="Line"/> is null.</summary>
/// <param name="Commit">The commit it is written against: always the review snapshot's head.</param>
public sealed record LineComment(string Commit, string Path, int? Line, string Side, int? StartLine, string Body);

/// <summary>
/// Where the viewer WRITES to a repository host: comments, replies, resolutions (docs/web/PLAN.md,
/// *Pull request review*, slice 2). Every method takes the person's own credential.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="IRepoSource"/> on purpose.</b> Reads and writes have different
/// rules: writes go only with a signed-in person's token (S1), behind the request-forgery checks and
/// the kill switch (S4, S9), reserved in the operation ledger (C5). Keeping them on their own seam
/// makes "only the writer sends a mutation" something the Arch tests can check, rather than a habit.
/// </para>
/// <para>
/// The reconciliation reads live here too, because only an uncertain write needs them.
/// </para>
/// </remarks>
public interface IRepoWriter
{
    Task<WriteResult<string>> AddConversationCommentAsync(RepoAccess access, string owner, string repo, int number, string body, CancellationToken ct);

    Task<WriteResult<string>> AddLineCommentAsync(RepoAccess access, string owner, string repo, int number, LineComment comment, CancellationToken ct);

    Task<WriteResult<string>> ReplyAsync(RepoAccess access, string threadId, string body, CancellationToken ct);

    /// <param name="type">IssueComment or PullRequestReviewComment, from <see cref="NodeOwnerAsync"/>.</param>
    Task<WriteResult<bool>> EditCommentAsync(RepoAccess access, string commentId, string type, string body, CancellationToken ct);

    Task<WriteResult<bool>> DeleteCommentAsync(RepoAccess access, string commentId, string type, CancellationToken ct);

    Task<WriteResult<bool>> SetThreadResolvedAsync(RepoAccess access, string threadId, bool resolved, CancellationToken ct);

    /// <summary>
    /// A review created and submitted in one call, on <paramref name="commit"/>: the atomic form, with
    /// no pending state in between (docs/web/PLAN.md, C2).
    /// </summary>
    /// <param name="reviewEvent">APPROVE, REQUEST_CHANGES or COMMENT.</param>
    Task<WriteResult<string>> SubmitReviewAsync(RepoAccess access, string owner, string repo, int number, string commit, string reviewEvent, string body, CancellationToken ct);

    /// <summary>Starts the viewer's pending review ON <paramref name="commit"/>: the commit it will approve is fixed here, not at submission.</summary>
    Task<WriteResult<string>> StartPendingReviewAsync(RepoAccess access, string pullNodeId, string commit, CancellationToken ct);

    /// <summary>Adds a draft comment to a pending review: on a line (or range), or on the whole file when the line is null.</summary>
    Task<WriteResult<string>> AddPendingThreadAsync(RepoAccess access, string reviewId, LineComment comment, CancellationToken ct);

    Task<WriteResult<bool>> SubmitPendingReviewAsync(RepoAccess access, string reviewId, string reviewEvent, string body, CancellationToken ct);

    Task<WriteResult<bool>> DeletePendingReviewAsync(RepoAccess access, string reviewId, CancellationToken ct);

    /// <summary>
    /// Requests reviews from <paramref name="add"/> and withdraws them from <paramref name="remove"/>
    /// (people, by login), and likewise from teams by slug. Asking someone who already reviewed is
    /// GitHub's "re-request review".
    /// </summary>
    Task<WriteResult<bool>> SetReviewersAsync(RepoAccess access, string owner, string repo, int number, ReviewerChange change, CancellationToken ct);

    /// <summary>Dismisses a submitted review (APPROVED or CHANGES_REQUESTED) with the reason GitHub requires.</summary>
    Task<WriteResult<bool>> DismissReviewAsync(RepoAccess access, string reviewId, string message, CancellationToken ct);

    /// <summary>Rewrites a submitted review's summary: the viewer's own, never a pending one (its submission writes that).</summary>
    Task<WriteResult<bool>> EditReviewAsync(RepoAccess access, string reviewId, string body, CancellationToken ct);

    /// <summary>Adds (<paramref name="on"/>) or removes the viewer's reaction on a comment, a review or the pull request's body.</summary>
    /// <param name="content">THUMBS_UP, THUMBS_DOWN, LAUGH, HOORAY, CONFUSED, HEART, ROCKET or EYES.</param>
    Task<WriteResult<bool>> SetReactionAsync(RepoAccess access, string subjectId, string content, bool on, CancellationToken ct);

    /// <summary>Hides a comment as <paramref name="classifier"/> (SPAM, ABUSE, OFF_TOPIC, OUTDATED, DUPLICATE, RESOLVED), or shows it again when null.</summary>
    Task<WriteResult<bool>> SetMinimizedAsync(RepoAccess access, string commentId, string? classifier, CancellationToken ct);

    /// <summary>Locks the conversation with a reason (OFF_TOPIC, TOO_HEATED, RESOLVED, SPAM, or none), or unlocks it.</summary>
    Task<WriteResult<bool>> SetLockedAsync(RepoAccess access, string pullNodeId, bool locked, string? reason, CancellationToken ct);

    /// <summary>Sets the milestone by number, or clears it with null.</summary>
    Task<WriteResult<bool>> SetMilestoneAsync(RepoAccess access, string owner, string repo, int number, int? milestone, CancellationToken ct);

    /// <summary>The viewer's own notifications for the pull request: SUBSCRIBED, UNSUBSCRIBED or IGNORED.</summary>
    Task<WriteResult<bool>> SetSubscriptionAsync(RepoAccess access, string pullNodeId, string state, CancellationToken ct);

    /// <summary>
    /// One commit on <paramref name="branch"/> replacing the given files, only if the branch is still at
    /// <paramref name="expectedHeadOid"/> (GitHub's own atomic precondition); the value is the new commit.
    /// </summary>
    Task<WriteResult<string>> CommitFilesAsync(RepoAccess access, string owner, string repo, string branch, string expectedHeadOid, string message, IReadOnlyList<FileChange> files, CancellationToken ct);

    Task<WriteResult<bool>> SetViewedAsync(RepoAccess access, string pullNodeId, string path, bool viewed, CancellationToken ct);

    /// <summary>Merges, only if the head is still <paramref name="headSha"/>; the value is the merge commit.</summary>
    /// <param name="method">MERGE, SQUASH or REBASE.</param>
    Task<WriteResult<string>> MergePullAsync(RepoAccess access, string owner, string repo, int number, string headSha, string method, string? title, string? message, CancellationToken ct);

    /// <summary>Brings the base into the head branch, only if the head is still <paramref name="expectedHeadSha"/>.</summary>
    Task<WriteResult<bool>> UpdateBranchAsync(RepoAccess access, string owner, string repo, int number, string expectedHeadSha, CancellationToken ct);

    /// <summary>
    /// Turns auto-merge on, only if the head is still <paramref name="expectedHeadSha"/>. Not a pin:
    /// later pushes by people with write access are merged too when the requirements pass.
    /// </summary>
    Task<WriteResult<bool>> EnableAutoMergeAsync(RepoAccess access, string pullNodeId, string method, string expectedHeadSha, CancellationToken ct);

    Task<WriteResult<bool>> DisableAutoMergeAsync(RepoAccess access, string pullNodeId, CancellationToken ct);

    /// <summary>Opens a pull request; the value is its number.</summary>
    Task<WriteResult<int>> CreatePullAsync(RepoAccess access, string owner, string repo, NewPull pull, CancellationToken ct);

    Task<WriteResult<bool>> EditPullAsync(RepoAccess access, string owner, string repo, int number, PullEdit edit, CancellationToken ct);

    /// <summary>Closes (<paramref name="open"/> false) or reopens a pull request.</summary>
    Task<WriteResult<bool>> SetPullOpenAsync(RepoAccess access, string owner, string repo, int number, bool open, CancellationToken ct);

    /// <summary>Converts to a draft, or marks ready for review: GraphQL only.</summary>
    Task<WriteResult<bool>> SetDraftAsync(RepoAccess access, string pullNodeId, bool draft, CancellationToken ct);

    /// <summary>Adds and removes labels, by name; each half is safe to repeat.</summary>
    Task<WriteResult<bool>> SetLabelsAsync(RepoAccess access, string owner, string repo, int number, IReadOnlyList<string> add, IReadOnlyList<string> remove, CancellationToken ct);

    /// <summary>Adds and removes assignees, by login; each half is safe to repeat.</summary>
    Task<WriteResult<bool>> SetAssigneesAsync(RepoAccess access, string owner, string repo, int number, IReadOnlyList<string> add, IReadOnlyList<string> remove, CancellationToken ct);

    /// <summary>
    /// The number of a pull request from <paramref name="head"/> into <paramref name="base"/> that the
    /// viewer opened and whose body carries <paramref name="tag"/>; 0 when there is none.
    /// </summary>
    Task<RepoResult<int>> FindCreatedPullAsync(RepoAccess access, string owner, string repo, string head, string @base, string tag, CancellationToken ct);

    /// <summary>The id of the viewer's submitted review on this pull request whose body carries <paramref name="tag"/>; empty when none.</summary>
    Task<RepoResult<string>> FindTaggedReviewAsync(RepoAccess access, string owner, string repo, int number, string tag, CancellationToken ct);

    /// <summary>What a node id is and where it belongs: checked before any mutation names it (S7).</summary>
    Task<RepoResult<NodeOwner>> NodeOwnerAsync(RepoAccess access, string nodeId, CancellationToken ct);

    /// <summary>
    /// The id of the viewer's comment on this pull request (conversation, line or reply) that carries
    /// <paramref name="tag"/>, created since <paramref name="since"/>; empty when there is none.
    /// </summary>
    Task<RepoResult<string>> FindTaggedCommentAsync(RepoAccess access, string owner, string repo, int number, string tag, DateTimeOffset since, CancellationToken ct);

    /// <summary>A comment's current body, or empty when the comment no longer exists.</summary>
    Task<RepoResult<string>> CommentBodyAsync(RepoAccess access, string commentId, CancellationToken ct);

    Task<RepoResult<bool>> ThreadResolvedAsync(RepoAccess access, string threadId, CancellationToken ct);

    /// <summary>A submitted review's current summary, or empty when the review no longer exists.</summary>
    Task<RepoResult<string>> ReviewBodyAsync(RepoAccess access, string reviewId, CancellationToken ct);

    /// <summary>Whether the viewer's reaction of this kind is on the subject now.</summary>
    Task<RepoResult<bool>> ViewerReactedAsync(RepoAccess access, string subjectId, string content, CancellationToken ct);

    /// <summary>Whether a comment is hidden now.</summary>
    Task<RepoResult<bool>> CommentMinimizedAsync(RepoAccess access, string commentId, CancellationToken ct);
}

/// <summary>Who to ask for a review, and who to stop asking: people by login, teams by slug.</summary>
public sealed record ReviewerChange(IReadOnlyList<string> Add, IReadOnlyList<string> Remove, IReadOnlyList<string> AddTeams, IReadOnlyList<string> RemoveTeams)
{
    public static ReviewerChange People(IReadOnlyList<string> add, IReadOnlyList<string> remove) => new(add, remove, [], []);

    public int Count => Add.Count + Remove.Count + AddTeams.Count + RemoveTeams.Count;
}

/// <summary>A file as it will be after a commit: the whole content, never a patch.</summary>
public sealed record FileChange(string Path, byte[] Content);
