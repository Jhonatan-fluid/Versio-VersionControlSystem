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

    /// <summary>Runs as each write reaches GitHub, after the server's own checks.</summary>
    public Action? BeforeEffect { get; set; }

    /// <summary>The repository whose pending reviews this writer keeps, so the server's own reads see them.</summary>
    public FakeGitHub.FakeRepo? Repo { get; set; }

    /// <summary>Submitted reviews: id, author, commit, event, body.</summary>
    public ConcurrentQueue<(string Id, string Author, string Commit, string Event, string Body)> Reviews { get; } = new();

    /// <summary>Every review id this writer made, pending or not: number, author's token, state, commit.</summary>
    public ConcurrentDictionary<string, (int Number, string Token, string Author, string State, string Commit)> ReviewNodes { get; } = new(StringComparer.Ordinal);

    /// <summary>Viewed changes as made: path and state.</summary>
    public ConcurrentQueue<(string Path, bool Viewed)> ViewedMarks { get; } = new();

    /// <summary>Reviewer changes as made.</summary>
    public ConcurrentQueue<(string Add, string Remove)> ReviewerChanges { get; } = new();

    private int _ids;

    private async Task<WriteResult<T>> Run<T>(string action, RepoAccess access, string detail, Func<T> effect)
    {
        Calls.Enqueue((action, access, detail));
        if (Hold is { } hold)
        {
            await hold.Task;
        }

        // After every check the server made, before GitHub acts: where a push lands mid-submit.
        BeforeEffect?.Invoke();
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

    private static int NumberOf(string pullNodeId) => int.Parse(pullNodeId["PR_".Length..], System.Globalization.CultureInfo.InvariantCulture);

    public Task<WriteResult<string>> SubmitReviewAsync(RepoAccess access, string owner, string repo, int number, string commit, string reviewEvent, string body, CancellationToken ct) =>
        Run("review", access, $"{commit} {reviewEvent}", () =>
        {
            var id = $"PRR_{Interlocked.Increment(ref _ids)}abcd";
            Reviews.Enqueue((id, access.Who, commit, reviewEvent, body));
            ReviewNodes[id] = (number, access.Token!, access.Who, reviewEvent, commit);
            return id;
        });

    public Task<WriteResult<string>> StartPendingReviewAsync(RepoAccess access, string pullNodeId, string commit, CancellationToken ct) =>
        Run("start-pending", access, commit, () =>
        {
            var id = $"PRR_{Interlocked.Increment(ref _ids)}abcd";
            var number = NumberOf(pullNodeId);
            ReviewNodes[id] = (number, access.Token!, access.Who, "PENDING", commit);
            Repo!.Pending[(number, access.Token!)] = new PendingReview(id, access.Who, commit, []);
            return id;
        });

    public Task<WriteResult<string>> AddPendingThreadAsync(RepoAccess access, string reviewId, LineComment comment, CancellationToken ct) =>
        Run("pending-thread", access, $"{comment.Path} {comment.Side} {comment.StartLine}-{comment.Line}", () =>
        {
            var node = ReviewNodes[reviewId];
            var pending = Repo!.Pending[(node.Number, node.Token)];
            var id = $"PRRC_{Interlocked.Increment(ref _ids)}abcd";
            Repo.Pending[(node.Number, node.Token)] = pending with { Comments = [.. pending.Comments, new PendingComment(id, comment.Path, comment.Body)] };
            return id;
        });

    public Task<WriteResult<bool>> SubmitPendingReviewAsync(RepoAccess access, string reviewId, string reviewEvent, string body, CancellationToken ct) =>
        Run("submit-pending", access, $"{reviewId} {reviewEvent}", () =>
        {
            var node = ReviewNodes[reviewId];
            ReviewNodes[reviewId] = node with { State = reviewEvent };
            Repo!.Pending.Remove((node.Number, node.Token));
            Reviews.Enqueue((reviewId, access.Who, node.Commit, reviewEvent, body));
            return true;
        });

    public Task<WriteResult<bool>> DeletePendingReviewAsync(RepoAccess access, string reviewId, CancellationToken ct) =>
        Run("discard-pending", access, reviewId, () =>
        {
            var node = ReviewNodes[reviewId];
            ReviewNodes.TryRemove(reviewId, out _);
            Repo!.Pending.Remove((node.Number, node.Token));
            return true;
        });

    public Task<WriteResult<bool>> SetReviewersAsync(RepoAccess access, string owner, string repo, int number, IReadOnlyList<string> add, IReadOnlyList<string> remove, CancellationToken ct) =>
        Run("reviewers", access, string.Join(',', add) + "|" + string.Join(',', remove), () =>
        {
            ReviewerChanges.Enqueue((string.Join(',', add), string.Join(',', remove)));
            return true;
        });

    public Task<WriteResult<bool>> SetViewedAsync(RepoAccess access, string pullNodeId, string path, bool viewed, CancellationToken ct) =>
        Run("viewed", access, path + (viewed ? " on" : " off"), () =>
        {
            ViewedMarks.Enqueue((path, viewed));
            return true;
        });

    public Task<RepoResult<string>> FindTaggedReviewAsync(RepoAccess access, string owner, string repo, int number, string tag, CancellationToken ct)
    {
        Calls.Enqueue(("find", access, tag));
        return Task.FromResult(RepoResult<string>.Of(Reviews.FirstOrDefault(r => r.Author == access.Who && r.Body.Contains(tag, StringComparison.Ordinal)).Id ?? ""));
    }

    public Task<RepoResult<NodeOwner>> NodeOwnerAsync(RepoAccess access, string nodeId, CancellationToken ct)
    {
        Calls.Enqueue(("node", access, nodeId));
        if (ReviewNodes.TryGetValue(nodeId, out var review))
        {
            return Task.FromResult(RepoResult<NodeOwner>.Of(new NodeOwner("PullRequestReview", Repo is { } r ? $"{r.Owner}/{r.Name}" : "acme/plant", review.Number, review.Author, review.State, review.Commit)));
        }

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
