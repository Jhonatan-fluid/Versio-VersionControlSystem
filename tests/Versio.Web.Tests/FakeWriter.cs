using System.Collections.Concurrent;
using Versio.Web.Sources;

namespace Versio.Web.Tests;

/// <summary>
/// GitHub's write side in memory: every call recorded with the credential it used, and the ways a
/// write can end that matter to C5 — done, refused, lost before it reached GitHub, or done and then
/// lost on the way back.
/// </summary>
internal sealed class FakeWriter : IRepoWriter
{
    public enum Mode
    {
        Done,
        Refused,

        /// <summary>Never reached GitHub; the answer is lost. Nothing happened.</summary>
        LostBefore,

        /// <summary>GitHub did it; the answer is lost. The effect is there to be found.</summary>
        LostAfter,
    }

    public sealed record Comment(string Id, string Type, string Repository, int Number, string Author, string Body, DateTimeOffset Created, string? ThreadId);

    public ConcurrentQueue<(string Action, RepoAccess Access, string Detail)> Calls { get; } = new();

    public ConcurrentDictionary<string, Comment> Comments { get; } = new(StringComparer.Ordinal);

    /// <summary>Thread id to (repository, pull request, resolved).</summary>
    public ConcurrentDictionary<string, (string Repository, int Number, bool Resolved)> Threads { get; } = new(StringComparer.Ordinal);

    /// <summary>How the next writes end, in order; empty means done.</summary>
    public ConcurrentQueue<Mode> Next { get; } = new();

    /// <summary>When set, every write waits for it: two requests can then race for one key.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    private int _ids;

    private async Task<WriteResult<T>> Run<T>(string action, RepoAccess access, string detail, Func<T> effect)
    {
        Calls.Enqueue((action, access, detail));
        if (Hold is { } hold)
        {
            await hold.Task;
        }

        var mode = Next.TryDequeue(out var m) ? m : Mode.Done;
        switch (mode)
        {
            case Mode.Refused:
                return WriteResult<T>.Refused(RepoFailure.Upstream, "Validation Failed");
            case Mode.LostBefore:
                return WriteResult<T>.Unknown();
            case Mode.LostAfter:
                effect();
                return WriteResult<T>.Unknown();
            default:
                return WriteResult<T>.Done(effect());
        }
    }

    private string NewComment(string type, string owner, string repo, int number, string author, string body, string? thread)
    {
        var id = $"C_{Interlocked.Increment(ref _ids)}abcd";
        Comments[id] = new Comment(id, type, $"{owner}/{repo}", number, author, body, Now, thread);
        return id;
    }

    public Task<WriteResult<string>> AddConversationCommentAsync(RepoAccess access, string owner, string repo, int number, string body, CancellationToken ct) =>
        Run("comment", access, body, () => NewComment("IssueComment", owner, repo, number, access.Who, body, null));

    public Task<WriteResult<string>> AddLineCommentAsync(RepoAccess access, string owner, string repo, int number, LineComment comment, CancellationToken ct) =>
        Run("line-comment", access, $"{comment.Commit} {comment.Path} {comment.Side} {comment.StartLine}-{comment.Line}", () =>
            NewComment("PullRequestReviewComment", owner, repo, number, access.Who, comment.Body, null));

    public Task<WriteResult<string>> ReplyAsync(RepoAccess access, string threadId, string body, CancellationToken ct) =>
        Run("reply", access, threadId, () =>
        {
            var t = Threads[threadId];
            var slash = t.Repository.IndexOf('/');
            return NewComment("PullRequestReviewComment", t.Repository[..slash], t.Repository[(slash + 1)..], t.Number, access.Who, body, threadId);
        });

    public Task<WriteResult<bool>> EditCommentAsync(RepoAccess access, string commentId, string type, string body, CancellationToken ct) =>
        Run("edit", access, commentId, () =>
        {
            Comments[commentId] = Comments[commentId] with { Body = body };
            return true;
        });

    public Task<WriteResult<bool>> DeleteCommentAsync(RepoAccess access, string commentId, string type, CancellationToken ct) =>
        Run("delete", access, commentId, () => Comments.TryRemove(commentId, out _) || true);

    public Task<WriteResult<bool>> SetThreadResolvedAsync(RepoAccess access, string threadId, bool resolved, CancellationToken ct) =>
        Run(resolved ? "resolve" : "unresolve", access, threadId, () =>
        {
            Threads[threadId] = Threads[threadId] with { Resolved = resolved };
            return true;
        });

    public Task<RepoResult<NodeOwner>> NodeOwnerAsync(RepoAccess access, string nodeId, CancellationToken ct)
    {
        Calls.Enqueue(("node", access, nodeId));
        if (Threads.TryGetValue(nodeId, out var t))
        {
            return Task.FromResult(RepoResult<NodeOwner>.Of(new NodeOwner("PullRequestReviewThread", t.Repository, t.Number, null)));
        }

        return Task.FromResult(Comments.TryGetValue(nodeId, out var c)
            ? RepoResult<NodeOwner>.Of(new NodeOwner(c.Type, c.Repository, c.Number, c.Author))
            : RepoResult<NodeOwner>.Fail(RepoFailure.NotFound));
    }

    public Task<RepoResult<string>> FindTaggedCommentAsync(RepoAccess access, string owner, string repo, int number, string tag, DateTimeOffset since, CancellationToken ct)
    {
        Calls.Enqueue(("find", access, tag));
        var found = Comments.Values.FirstOrDefault(c => c.Repository == $"{owner}/{repo}" && c.Number == number && c.Author == access.Who
                                                        && c.Created >= since.AddMinutes(-1) && c.Body.Contains(tag, StringComparison.Ordinal));
        return Task.FromResult(RepoResult<string>.Of(found?.Id ?? ""));
    }

    public Task<RepoResult<string>> CommentBodyAsync(RepoAccess access, string commentId, CancellationToken ct) =>
        Task.FromResult(RepoResult<string>.Of(Comments.TryGetValue(commentId, out var c) ? c.Body : ""));

    public Task<RepoResult<bool>> ThreadResolvedAsync(RepoAccess access, string threadId, CancellationToken ct) =>
        Task.FromResult(Threads.TryGetValue(threadId, out var t) ? RepoResult<bool>.Of(t.Resolved) : RepoResult<bool>.Fail(RepoFailure.NotFound));

    /// <summary>The write calls only, without the reads (node checks, reconciliation).</summary>
    public IReadOnlyList<(string Action, RepoAccess Access, string Detail)> Writes =>
        Calls.Where(c => c.Action is not ("node" or "find")).ToList();
}
