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
public sealed record NodeOwner(string Type, string Repository, int Number, string? Author);

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
}
