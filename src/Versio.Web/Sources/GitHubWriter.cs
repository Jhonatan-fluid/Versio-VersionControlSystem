using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Versio.Core.Logging;

namespace Versio.Web.Sources;

/// <summary>
/// Writes to GitHub with the person's own token: REST for new comments, GraphQL for replies,
/// edits, deletions and resolving threads (resolution exists only there).
/// </summary>
/// <remarks>
/// <para>
/// <b>The only class that sends a mutation</b> — POST, PATCH, PUT or DELETE with a body that
/// changes something, or a GraphQL <c>mutation</c>. The reader's GraphQL calls are queries; an Arch
/// test holds the line between the two (docs/web/PLAN.md, *Pull request review*, S1 and C6).
/// </para>
/// <para>
/// <b>Three outcomes, not two.</b> A 4xx means GitHub refused and nothing was written. But a
/// timeout, a dropped connection or a 5xx after the request left means GitHub may have done it:
/// that is <see cref="WriteOutcome.Unknown"/>, and the caller reconciles rather than retrying
/// (C5). Treating those as failures is how a retry posts a comment twice.
/// </para>
/// <para>
/// <b>Never an automatic retry, and never an installation token.</b> The first is C5; the second is
/// S1, and the caller enforces it before anything reaches here — this class refuses a credential
/// that is not a person's as a second wall.
/// </para>
/// </remarks>
public sealed class GitHubWriter(IHttpClientFactory clients, IOptions<WebOptions> options, IVersioLogger log) : IRepoWriter
{
    private GitHubOptions GitHub => options.Value.GitHub;

    public async Task<WriteResult<string>> AddConversationCommentAsync(RepoAccess access, string owner, string repo, int number, string body, CancellationToken ct) =>
        await RestAsync(access, HttpMethod.Post, $"repos/{E(owner)}/{E(repo)}/issues/{N(number)}/comments", new { body },
            root => root.GetProperty("node_id").GetString() ?? "", ct).ConfigureAwait(false);

    public async Task<WriteResult<string>> AddLineCommentAsync(RepoAccess access, string owner, string repo, int number, LineComment c, CancellationToken ct)
    {
        // A file-level comment has no line; a range starts at StartLine on the same side.
        object payload = c.Line is null
            ? new { body = c.Body, commit_id = c.Commit, path = c.Path, subject_type = "file" }
            : c.StartLine is { } start && start != c.Line
                ? new { body = c.Body, commit_id = c.Commit, path = c.Path, line = c.Line, side = c.Side, start_line = start, start_side = c.Side }
                : new { body = c.Body, commit_id = c.Commit, path = c.Path, line = c.Line, side = c.Side };
        return await RestAsync(access, HttpMethod.Post, $"repos/{E(owner)}/{E(repo)}/pulls/{N(number)}/comments", payload,
            root => root.GetProperty("node_id").GetString() ?? "", ct).ConfigureAwait(false);
    }

    private const string ReplyMutation = """
        mutation($thread: ID!, $body: String!) {
          addPullRequestReviewThreadReply(input: { pullRequestReviewThreadId: $thread, body: $body }) { comment { id } }
        }
        """;

    public Task<WriteResult<string>> ReplyAsync(RepoAccess access, string threadId, string body, CancellationToken ct) =>
        MutateAsync(access, ReplyMutation, new { thread = threadId, body },
            data => data.GetProperty("addPullRequestReviewThreadReply").GetProperty("comment").GetProperty("id").GetString() ?? "", ct);

    private const string EditIssueComment = """
        mutation($id: ID!, $body: String!) { updateIssueComment(input: { id: $id, body: $body }) { issueComment { id } } }
        """;

    private const string EditReviewComment = """
        mutation($id: ID!, $body: String!) { updatePullRequestReviewComment(input: { pullRequestReviewCommentId: $id, body: $body }) { pullRequestReviewComment { id } } }
        """;

    public Task<WriteResult<bool>> EditCommentAsync(RepoAccess access, string commentId, string type, string body, CancellationToken ct) =>
        type switch
        {
            "IssueComment" => MutateAsync(access, EditIssueComment, new { id = commentId, body }, _ => true, ct),
            "PullRequestReviewComment" => MutateAsync(access, EditReviewComment, new { id = commentId, body }, _ => true, ct),
            _ => Task.FromResult(WriteResult<bool>.Refused(RepoFailure.NotFound, "not a comment")),
        };

    private const string DeleteIssueComment = """
        mutation($id: ID!) { deleteIssueComment(input: { id: $id }) { clientMutationId } }
        """;

    private const string DeleteReviewComment = """
        mutation($id: ID!) { deletePullRequestReviewComment(input: { id: $id }) { clientMutationId } }
        """;

    public Task<WriteResult<bool>> DeleteCommentAsync(RepoAccess access, string commentId, string type, CancellationToken ct) =>
        type switch
        {
            "IssueComment" => MutateAsync(access, DeleteIssueComment, new { id = commentId }, _ => true, ct),
            "PullRequestReviewComment" => MutateAsync(access, DeleteReviewComment, new { id = commentId }, _ => true, ct),
            _ => Task.FromResult(WriteResult<bool>.Refused(RepoFailure.NotFound, "not a comment")),
        };

    private const string ResolveMutation = """
        mutation($id: ID!) { resolveReviewThread(input: { threadId: $id }) { thread { isResolved } } }
        """;

    private const string UnresolveMutation = """
        mutation($id: ID!) { unresolveReviewThread(input: { threadId: $id }) { thread { isResolved } } }
        """;

    public Task<WriteResult<bool>> SetThreadResolvedAsync(RepoAccess access, string threadId, bool resolved, CancellationToken ct) =>
        MutateAsync(access, resolved ? ResolveMutation : UnresolveMutation, new { id = threadId }, _ => true, ct);

    public Task<WriteResult<string>> SubmitReviewAsync(RepoAccess access, string owner, string repo, int number, string commit, string reviewEvent, string body, CancellationToken ct) =>
        RestAsync(access, HttpMethod.Post, $"repos/{E(owner)}/{E(repo)}/pulls/{N(number)}/reviews", new { commit_id = commit, @event = reviewEvent, body },
            root => root.GetProperty("node_id").GetString() ?? "", ct);

    private const string StartPendingMutation = """
        mutation($pull: ID!, $commit: GitObjectID!) {
          addPullRequestReview(input: { pullRequestId: $pull, commitOID: $commit }) { pullRequestReview { id } }
        }
        """;

    public Task<WriteResult<string>> StartPendingReviewAsync(RepoAccess access, string pullNodeId, string commit, CancellationToken ct) =>
        MutateAsync(access, StartPendingMutation, new { pull = pullNodeId, commit },
            data => data.GetProperty("addPullRequestReview").GetProperty("pullRequestReview").GetProperty("id").GetString() ?? "", ct);

    private const string AddPendingThreadMutation = """
        mutation($review: ID!, $path: String!, $body: String!, $line: Int, $side: DiffSide, $startLine: Int, $startSide: DiffSide, $subject: PullRequestReviewThreadSubjectType) {
          addPullRequestReviewThread(input: { pullRequestReviewId: $review, path: $path, body: $body, line: $line, side: $side, startLine: $startLine, startSide: $startSide, subjectType: $subject }) { thread { id } }
        }
        """;

    public Task<WriteResult<string>> AddPendingThreadAsync(RepoAccess access, string reviewId, LineComment c, CancellationToken ct) =>
        MutateAsync(access, AddPendingThreadMutation,
            new
            {
                review = reviewId,
                path = c.Path,
                body = c.Body,
                line = c.Line,
                side = c.Line is null ? null : c.Side,
                startLine = c.StartLine is { } s && s != c.Line ? s : (int?)null,
                startSide = c.StartLine is { } s2 && s2 != c.Line ? c.Side : null,
                subject = c.Line is null ? "FILE" : "LINE",
            },
            data => data.GetProperty("addPullRequestReviewThread").GetProperty("thread").GetProperty("id").GetString() ?? "", ct);

    private const string SubmitPendingMutation = """
        mutation($review: ID!, $event: PullRequestReviewEvent!, $body: String) {
          submitPullRequestReview(input: { pullRequestReviewId: $review, event: $event, body: $body }) { pullRequestReview { id state } }
        }
        """;

    public Task<WriteResult<bool>> SubmitPendingReviewAsync(RepoAccess access, string reviewId, string reviewEvent, string body, CancellationToken ct) =>
        MutateAsync(access, SubmitPendingMutation, new { review = reviewId, @event = reviewEvent, body }, _ => true, ct);

    private const string DeletePendingMutation = """
        mutation($review: ID!) { deletePullRequestReview(input: { pullRequestReviewId: $review }) { clientMutationId } }
        """;

    public Task<WriteResult<bool>> DeletePendingReviewAsync(RepoAccess access, string reviewId, CancellationToken ct) =>
        MutateAsync(access, DeletePendingMutation, new { review = reviewId }, _ => true, ct);

    /// <remarks>
    /// Two calls when both halves are set: the additions first. Each is safe to repeat, so a
    /// reconciliation simply runs both again. People and teams travel in the same call, as REST takes
    /// them (<c>reviewers</c> and <c>team_reviewers</c>); a person who already reviewed is re-requested by
    /// the same POST, which is GitHub's "re-request review".
    /// </remarks>
    public async Task<WriteResult<bool>> SetReviewersAsync(RepoAccess access, string owner, string repo, int number, ReviewerChange change, CancellationToken ct)
    {
        var path = $"repos/{E(owner)}/{E(repo)}/pulls/{N(number)}/requested_reviewers";
        if (change.Add.Count + change.AddTeams.Count > 0)
        {
            var added = await RestAsync(access, HttpMethod.Post, path, new { reviewers = change.Add, team_reviewers = change.AddTeams }, _ => true, ct).ConfigureAwait(false);
            if (added.Outcome != WriteOutcome.Done)
            {
                return added;
            }
        }

        return change.Remove.Count + change.RemoveTeams.Count > 0
            ? await RestAsync(access, HttpMethod.Delete, path, new { reviewers = change.Remove, team_reviewers = change.RemoveTeams }, _ => true, ct).ConfigureAwait(false)
            : WriteResult<bool>.Done(true);
    }

    private const string DismissReviewMutation = """
        mutation($review: ID!, $message: String!) { dismissPullRequestReview(input: { pullRequestReviewId: $review, message: $message }) { pullRequestReview { id state } } }
        """;

    public Task<WriteResult<bool>> DismissReviewAsync(RepoAccess access, string reviewId, string message, CancellationToken ct) =>
        MutateAsync(access, DismissReviewMutation, new { review = reviewId, message }, _ => true, ct);

    private const string EditReviewMutation = """
        mutation($review: ID!, $body: String!) { updatePullRequestReview(input: { pullRequestReviewId: $review, body: $body }) { pullRequestReview { id } } }
        """;

    public Task<WriteResult<bool>> EditReviewAsync(RepoAccess access, string reviewId, string body, CancellationToken ct) =>
        MutateAsync(access, EditReviewMutation, new { review = reviewId, body }, _ => true, ct);

    private const string AddReactionMutation = """
        mutation($subject: ID!, $content: ReactionContent!) { addReaction(input: { subjectId: $subject, content: $content }) { reaction { id } } }
        """;

    private const string RemoveReactionMutation = """
        mutation($subject: ID!, $content: ReactionContent!) { removeReaction(input: { subjectId: $subject, content: $content }) { reaction { id } } }
        """;

    public Task<WriteResult<bool>> SetReactionAsync(RepoAccess access, string subjectId, string content, bool on, CancellationToken ct) =>
        MutateAsync(access, on ? AddReactionMutation : RemoveReactionMutation, new { subject = subjectId, content }, _ => true, ct);

    private const string MinimizeMutation = """
        mutation($id: ID!, $classifier: ReportedContentClassifiers!) { minimizeComment(input: { subjectId: $id, classifier: $classifier }) { minimizedComment { isMinimized } } }
        """;

    private const string UnminimizeMutation = """
        mutation($id: ID!) { unminimizeComment(input: { subjectId: $id }) { unminimizedComment { isMinimized } } }
        """;

    public Task<WriteResult<bool>> SetMinimizedAsync(RepoAccess access, string commentId, string? classifier, CancellationToken ct) =>
        classifier is null
            ? MutateAsync(access, UnminimizeMutation, new { id = commentId }, _ => true, ct)
            : MutateAsync(access, MinimizeMutation, new { id = commentId, classifier }, _ => true, ct);

    private const string LockMutation = """
        mutation($id: ID!, $reason: LockReason) { lockLockable(input: { lockableId: $id, lockReason: $reason }) { lockedRecord { locked } } }
        """;

    private const string UnlockMutation = """
        mutation($id: ID!) { unlockLockable(input: { lockableId: $id }) { unlockedRecord { locked } } }
        """;

    public Task<WriteResult<bool>> SetLockedAsync(RepoAccess access, string pullNodeId, bool locked, string? reason, CancellationToken ct) =>
        locked
            ? MutateAsync(access, LockMutation, new { id = pullNodeId, reason }, _ => true, ct)
            : MutateAsync(access, UnlockMutation, new { id = pullNodeId }, _ => true, ct);

    /// <remarks>A pull request is an issue to this endpoint; <c>milestone: null</c> clears it, so the field is always sent.</remarks>
    public Task<WriteResult<bool>> SetMilestoneAsync(RepoAccess access, string owner, string repo, int number, int? milestone, CancellationToken ct) =>
        RestAsync(access, HttpMethod.Patch, $"repos/{E(owner)}/{E(repo)}/issues/{N(number)}", new { milestone }, _ => true, ct);

    private const string SubscriptionMutation = """
        mutation($id: ID!, $state: SubscriptionState!) { updateSubscription(input: { subscribableId: $id, state: $state }) { subscribable { viewerSubscription } } }
        """;

    public Task<WriteResult<bool>> SetSubscriptionAsync(RepoAccess access, string pullNodeId, string state, CancellationToken ct) =>
        MutateAsync(access, SubscriptionMutation, new { id = pullNodeId, state }, _ => true, ct);

    private const string CommitMutation = """
        mutation($repo: String!, $branch: String!, $head: GitObjectID!, $headline: String!, $body: String, $additions: [FileAddition!]!) {
          createCommitOnBranch(input: {
            branch: { repositoryNameWithOwner: $repo, branchName: $branch }, expectedHeadOid: $head,
            message: { headline: $headline, body: $body }, fileChanges: { additions: $additions }
          }) { commit { oid } }
        }
        """;

    /// <remarks>
    /// <c>expectedHeadOid</c> is GitHub's atomic precondition, as <c>sha</c> is for a merge: the commit
    /// lands only if the branch is still at the reviewed head, so a suggestion is never applied over a
    /// push that changed the lines it was written for. The commit is signed by GitHub as the person's.
    /// </remarks>
    public Task<WriteResult<string>> CommitFilesAsync(RepoAccess access, string owner, string repo, string branch, string expectedHeadOid, string message, IReadOnlyList<FileChange> files, CancellationToken ct)
    {
        var newline = message.IndexOf('\n', StringComparison.Ordinal);
        var headline = newline < 0 ? message : message[..newline];
        var body = newline < 0 ? null : message[(newline + 1)..].Trim('\n');
        var additions = files.Select(f => new { path = f.Path, contents = Convert.ToBase64String(f.Content) }).ToArray();
        return MutateAsync(access, CommitMutation, new { repo = $"{owner}/{repo}", branch, head = expectedHeadOid, headline, body, additions },
            data => data.GetProperty("createCommitOnBranch").GetProperty("commit").GetProperty("oid").GetString() ?? "", ct);
    }

    private const string MarkViewedMutation = """
        mutation($pull: ID!, $path: String!) { markFileAsViewed(input: { pullRequestId: $pull, path: $path }) { clientMutationId } }
        """;

    private const string UnmarkViewedMutation = """
        mutation($pull: ID!, $path: String!) { unmarkFileAsViewed(input: { pullRequestId: $pull, path: $path }) { clientMutationId } }
        """;

    public Task<WriteResult<bool>> SetViewedAsync(RepoAccess access, string pullNodeId, string path, bool viewed, CancellationToken ct) =>
        MutateAsync(access, viewed ? MarkViewedMutation : UnmarkViewedMutation, new { pull = pullNodeId, path }, _ => true, ct);

    /// <remarks>
    /// <c>sha</c> is GitHub's own atomic precondition: it merges only if the head is still that
    /// commit, and answers 409 otherwise. That is what makes "merge what was reviewed" true rather
    /// than hoped — the server's own staleness check before it only narrows the window.
    /// </remarks>
    public Task<WriteResult<string>> MergePullAsync(RepoAccess access, string owner, string repo, int number, string headSha, string method, string? title, string? message, CancellationToken ct)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal) { ["sha"] = headSha, ["merge_method"] = method.ToLowerInvariant() };
        if (title is not null)
        {
            payload["commit_title"] = title;
        }

        if (message is not null)
        {
            payload["commit_message"] = message;
        }

        return RestAsync(access, HttpMethod.Put, $"repos/{E(owner)}/{E(repo)}/pulls/{N(number)}/merge", payload,
            root => root.TryGetProperty("sha", out var sha) ? sha.GetString() ?? "merged" : "merged", ct);
    }

    private const string UpdateBranchMutation = """
        mutation($pull: ID!, $head: GitObjectID!, $method: PullRequestBranchUpdateMethod!) {
          updatePullRequestBranch(input: { pullRequestId: $pull, expectedHeadOid: $head, updateMethod: $method }) { pullRequest { id } }
        }
        """;

    /// <remarks>
    /// <c>expected_head_sha</c> (REST, a merge commit) or <c>expectedHeadOid</c> (GraphQL, which alone
    /// offers the rebase): GitHub refuses if the head is no longer the reviewed commit.
    /// </remarks>
    public Task<WriteResult<bool>> UpdateBranchAsync(RepoAccess access, string owner, string repo, int number, string expectedHeadSha, string method, string pullNodeId, CancellationToken ct) =>
        method == "REBASE"
            ? MutateAsync(access, UpdateBranchMutation, new { pull = pullNodeId, head = expectedHeadSha, method }, _ => true, ct)
            : RestAsync(access, HttpMethod.Put, $"repos/{E(owner)}/{E(repo)}/pulls/{N(number)}/update-branch", new { expected_head_sha = expectedHeadSha }, _ => true, ct);

    /// <remarks>A DELETE with no body; GitHub answers 204. A 422 "Reference does not exist" is a branch already gone, which is what was asked.</remarks>
    public async Task<WriteResult<bool>> DeleteBranchAsync(RepoAccess access, string owner, string repo, string branch, CancellationToken ct)
    {
        var result = await RestAsync(access, HttpMethod.Delete, $"repos/{E(owner)}/{E(repo)}/git/refs/heads/{E(branch)}", new { }, _ => true, ct).ConfigureAwait(false);
        return result.Outcome == WriteOutcome.Refused && result.Message is { } m && m.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            ? WriteResult<bool>.Done(true)
            : result;
    }

    public Task<WriteResult<bool>> CreateBranchAsync(RepoAccess access, string owner, string repo, string branch, string sha, CancellationToken ct) =>
        RestAsync(access, HttpMethod.Post, $"repos/{E(owner)}/{E(repo)}/git/refs", new { @ref = "refs/heads/" + branch, sha }, _ => true, ct);

    private const string EnqueueMutation = """
        mutation($pull: ID!, $head: GitObjectID!) { enqueuePullRequest(input: { pullRequestId: $pull, expectedHeadOid: $head }) { mergeQueueEntry { id } } }
        """;

    private const string DequeueMutation = """
        mutation($pull: ID!) { dequeuePullRequest(input: { id: $pull }) { mergeQueueEntry { id } } }
        """;

    public Task<WriteResult<bool>> SetQueuedAsync(RepoAccess access, string pullNodeId, bool queued, string expectedHeadSha, CancellationToken ct) =>
        queued
            ? MutateAsync(access, EnqueueMutation, new { pull = pullNodeId, head = expectedHeadSha }, _ => true, ct)
            : MutateAsync(access, DequeueMutation, new { pull = pullNodeId }, _ => true, ct);

    private const string EnableAutoMergeMutation = """
        mutation($pull: ID!, $method: PullRequestMergeMethod!, $head: GitObjectID!) {
          enablePullRequestAutoMerge(input: { pullRequestId: $pull, mergeMethod: $method, expectedHeadOid: $head }) { pullRequest { id } }
        }
        """;

    private const string DisableAutoMergeMutation = """
        mutation($pull: ID!) { disablePullRequestAutoMerge(input: { pullRequestId: $pull }) { pullRequest { id } } }
        """;

    /// <remarks>
    /// <c>expectedHeadOid</c> makes GitHub refuse to turn auto-merge on unless the head is still the
    /// reviewed commit. That is all it does. It does not pin the merge to that commit: GitHub turns
    /// auto-merge off when someone WITHOUT write access pushes, and a push by someone with it stays
    /// eligible and is merged when the requirements pass. An earlier comment here said otherwise
    /// (found by review, 2026-10-20); the endpoint asks for that consent instead of promising it away.
    /// </remarks>
    public Task<WriteResult<bool>> EnableAutoMergeAsync(RepoAccess access, string pullNodeId, string method, string expectedHeadSha, CancellationToken ct) =>
        MutateAsync(access, EnableAutoMergeMutation, new { pull = pullNodeId, method, head = expectedHeadSha }, _ => true, ct);

    public Task<WriteResult<bool>> DisableAutoMergeAsync(RepoAccess access, string pullNodeId, CancellationToken ct) =>
        MutateAsync(access, DisableAutoMergeMutation, new { pull = pullNodeId }, _ => true, ct);

    public Task<WriteResult<int>> CreatePullAsync(RepoAccess access, string owner, string repo, NewPull pull, CancellationToken ct) =>
        RestAsync(access, HttpMethod.Post, $"repos/{E(owner)}/{E(repo)}/pulls",
            new { title = pull.Title, body = pull.Body, head = pull.Head, @base = pull.Base, draft = pull.Draft },
            root => root.GetProperty("number").GetInt32(), ct);

    /// <remarks>Only the fields being changed are sent: a field left out is left as it is on GitHub, whoever changed it since.</remarks>
    public Task<WriteResult<bool>> EditPullAsync(RepoAccess access, string owner, string repo, int number, PullEdit edit, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (edit.Title is { } title)
        {
            fields["title"] = title;
        }

        if (edit.Body is { } body)
        {
            fields["body"] = body;
        }

        if (edit.Base is { } @base)
        {
            fields["base"] = @base;
        }

        return RestAsync(access, HttpMethod.Patch, $"repos/{E(owner)}/{E(repo)}/pulls/{N(number)}", fields, _ => true, ct);
    }

    public Task<WriteResult<bool>> SetPullOpenAsync(RepoAccess access, string owner, string repo, int number, bool open, CancellationToken ct) =>
        RestAsync(access, HttpMethod.Patch, $"repos/{E(owner)}/{E(repo)}/pulls/{N(number)}", new { state = open ? "open" : "closed" }, _ => true, ct);

    private const string ToDraftMutation = """
        mutation($pull: ID!) { convertPullRequestToDraft(input: { pullRequestId: $pull }) { pullRequest { isDraft } } }
        """;

    private const string ReadyMutation = """
        mutation($pull: ID!) { markPullRequestReadyForReview(input: { pullRequestId: $pull }) { pullRequest { isDraft } } }
        """;

    public Task<WriteResult<bool>> SetDraftAsync(RepoAccess access, string pullNodeId, bool draft, CancellationToken ct) =>
        MutateAsync(access, draft ? ToDraftMutation : ReadyMutation, new { pull = pullNodeId }, _ => true, ct);

    /// <remarks>
    /// Additions in one call; each removal is its own (GitHub removes labels one by one). A removal
    /// GitHub answers 404 for is a label already gone, which is what was asked.
    /// </remarks>
    public async Task<WriteResult<bool>> SetLabelsAsync(RepoAccess access, string owner, string repo, int number, IReadOnlyList<string> add, IReadOnlyList<string> remove, CancellationToken ct)
    {
        var path = $"repos/{E(owner)}/{E(repo)}/issues/{N(number)}/labels";
        if (add.Count > 0)
        {
            var added = await RestAsync(access, HttpMethod.Post, path, new { labels = add }, _ => true, ct).ConfigureAwait(false);
            if (added.Outcome != WriteOutcome.Done)
            {
                return added;
            }
        }

        foreach (var name in remove)
        {
            var removed = await RestAsync(access, HttpMethod.Delete, $"{path}/{E(name)}", new { }, _ => true, ct).ConfigureAwait(false);
            if (removed.Outcome != WriteOutcome.Done && !(removed.Outcome == WriteOutcome.Refused && removed.Failure == RepoFailure.NotFound))
            {
                return removed;
            }
        }

        return WriteResult<bool>.Done(true);
    }

    public async Task<WriteResult<bool>> SetAssigneesAsync(RepoAccess access, string owner, string repo, int number, IReadOnlyList<string> add, IReadOnlyList<string> remove, CancellationToken ct)
    {
        var path = $"repos/{E(owner)}/{E(repo)}/issues/{N(number)}/assignees";
        if (add.Count > 0)
        {
            var added = await RestAsync(access, HttpMethod.Post, path, new { assignees = add }, _ => true, ct).ConfigureAwait(false);
            if (added.Outcome != WriteOutcome.Done)
            {
                return added;
            }
        }

        return remove.Count > 0
            ? await RestAsync(access, HttpMethod.Delete, path, new { assignees = remove }, _ => true, ct).ConfigureAwait(false)
            : WriteResult<bool>.Done(true);
    }

    /// <remarks>
    /// GitHub allows one open pull request per head and base, and lists them by <c>head=owner:branch</c>;
    /// all states, because the one created may have been closed since. Its author must be the viewer
    /// and its body must carry the tag: someone else's pull request between the same branches is not
    /// this operation's.
    /// </remarks>
    /// <summary>The most pages a reconciliation search reads: 3,000 items. Past it the answer is "cannot tell", never "not there".</summary>
    internal const int SearchPages = 30;

    /// <summary>
    /// Searches a REST list page by page for the item <paramref name="pick"/> recognises. Found: its
    /// id. The end of the list reached without it: empty, which is "not there". The bound reached
    /// first: a failure, which keeps the operation unknown.
    /// </summary>
    /// <remarks>
    /// Every search read one page of 100 at first. An operation's tag past it — the 101st submitted
    /// review, a busy pull request's 101st comment since the write — was "not found", which is
    /// exactly the wrong answer for reconciliation: it says nothing happened when it did (found by
    /// review, 2026-10-20). "Not found" is now only ever concluded from a whole list.
    /// </remarks>
    private async Task<RepoResult<string>> SearchAsync(RepoAccess access, string relative, Func<JsonElement, string?> pick, CancellationToken ct)
    {
        for (var page = 1; page <= SearchPages; page++)
        {
            var one = await GetAsync(access, $"{relative}{(relative.Contains('?', StringComparison.Ordinal) ? '&' : '?')}per_page=100&page={N(page)}", root =>
            {
                var items = root.EnumerateArray().ToList();
                return (Found: items.Select(pick).FirstOrDefault(id => id is { Length: > 0 }), Count: items.Count);
            }, ct).ConfigureAwait(false);
            if (!one.Ok)
            {
                return one.As<string>();
            }

            if (one.Value.Found is { } found)
            {
                return RepoResult<string>.Of(found);
            }

            if (one.Value.Count < 100)
            {
                return RepoResult<string>.Of("");
            }
        }

        log.Warn(LogCategory.Web, $"reconciliation search {relative.Split('?')[0]} passed {SearchPages} pages without an end; outcome stays unknown");
        return RepoResult<string>.Fail(RepoFailure.Upstream);
    }

    /// <summary>The viewer's own item whose body carries <paramref name="tag"/>: its <paramref name="id"/>, or null.</summary>
    private static Func<JsonElement, string?> Tagged(RepoAccess access, string tag, Func<JsonElement, string?> id) => item =>
        item.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object && user.TryGetProperty("login", out var login) && login.GetString() == access.Who
        && item.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String && (b.GetString() ?? "").Contains(tag, StringComparison.Ordinal)
            ? id(item)
            : null;

    public async Task<RepoResult<int>> FindCreatedPullAsync(RepoAccess access, string owner, string repo, string head, string @base, string tag, CancellationToken ct)
    {
        var found = await SearchAsync(access, $"repos/{E(owner)}/{E(repo)}/pulls?state=all&head={E(owner + ":" + head)}&base={E(@base)}",
            Tagged(access, tag, p => p.GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
        return found.Ok ? RepoResult<int>.Of(found.Value!.Length > 0 ? int.Parse(found.Value, CultureInfo.InvariantCulture) : 0) : found.As<int>();
    }

    public Task<RepoResult<string>> FindTaggedReviewAsync(RepoAccess access, string owner, string repo, int number, string tag, CancellationToken ct) =>
        SearchAsync(access, $"repos/{E(owner)}/{E(repo)}/pulls/{N(number)}/reviews", Tagged(access, tag, r => r.GetProperty("node_id").GetString()), ct);

    // ---- the reads only writes need -------------------------------------------------------------

    private const string NodeQuery = """
        query($id: ID!) {
          node(id: $id) {
            __typename
            ... on PullRequestReviewThread { pullRequest { number repository { nameWithOwner } } }
            ... on PullRequestReviewComment { author { login } pullRequest { number repository { nameWithOwner } } }
            ... on IssueComment { author { login } issue { number } repository { nameWithOwner } }
            ... on PullRequestReview { author { login } state commit { oid } pullRequest { number repository { nameWithOwner } } }
            ... on PullRequest { number author { login } repository { nameWithOwner } }
          }
        }
        """;

    public async Task<RepoResult<NodeOwner>> NodeOwnerAsync(RepoAccess access, string nodeId, CancellationToken ct)
    {
        var result = await QueryAsync(access, NodeQuery, new { id = nodeId }, data =>
        {
            var node = data.GetProperty("node");
            if (node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var type = node.GetProperty("__typename").GetString() ?? "";
            var author = node.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetProperty("login").GetString() : null;
            return type switch
            {
                "PullRequestReviewThread" or "PullRequestReviewComment" => new NodeOwner(type,
                    node.GetProperty("pullRequest").GetProperty("repository").GetProperty("nameWithOwner").GetString() ?? "",
                    node.GetProperty("pullRequest").GetProperty("number").GetInt32(), author),
                "PullRequestReview" => new NodeOwner(type,
                    node.GetProperty("pullRequest").GetProperty("repository").GetProperty("nameWithOwner").GetString() ?? "",
                    node.GetProperty("pullRequest").GetProperty("number").GetInt32(), author,
                    node.GetProperty("state").GetString(),
                    node.TryGetProperty("commit", out var c) && c.ValueKind == JsonValueKind.Object ? c.GetProperty("oid").GetString() : null),
                "IssueComment" => new NodeOwner(type,
                    node.GetProperty("repository").GetProperty("nameWithOwner").GetString() ?? "",
                    node.GetProperty("issue").GetProperty("number").GetInt32(), author),
                // The pull request itself is a reaction's subject (its description), and belongs to itself.
                "PullRequest" => new NodeOwner(type,
                    node.GetProperty("repository").GetProperty("nameWithOwner").GetString() ?? "",
                    node.GetProperty("number").GetInt32(), author),
                _ => new NodeOwner(type, "", 0, author),
            };
        }, ct).ConfigureAwait(false);
        return result.Ok && result.Value is null ? RepoResult<NodeOwner>.Fail(RepoFailure.NotFound) : result!;
    }

    /// <remarks>
    /// Conversation comments, then line comments and replies (both are review comments to REST),
    /// each since the operation was reserved, newest pages first being unnecessary: a write's own
    /// comment is created after <paramref name="since"/>, so the window is small.
    /// </remarks>
    public async Task<RepoResult<string>> FindTaggedCommentAsync(RepoAccess access, string owner, string repo, int number, string tag, DateTimeOffset since, CancellationToken ct)
    {
        var from = E(since.AddMinutes(-1).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        foreach (var list in new[] { $"repos/{E(owner)}/{E(repo)}/issues/{N(number)}/comments", $"repos/{E(owner)}/{E(repo)}/pulls/{N(number)}/comments" })
        {
            var found = await SearchAsync(access, $"{list}?since={from}", Tagged(access, tag, c => c.GetProperty("node_id").GetString()), ct).ConfigureAwait(false);
            if (!found.Ok || found.Value!.Length > 0)
            {
                return found;
            }
        }

        return RepoResult<string>.Of("");
    }

    private const string BodyQuery = """
        query($id: ID!) { node(id: $id) { ... on IssueComment { body } ... on PullRequestReviewComment { body } } }
        """;

    public async Task<RepoResult<string>> CommentBodyAsync(RepoAccess access, string commentId, CancellationToken ct)
    {
        var result = await QueryAsync(access, BodyQuery, new { id = commentId }, data =>
            data.GetProperty("node") is { ValueKind: JsonValueKind.Object } n && n.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", ct).ConfigureAwait(false);
        // A deleted comment is "not found" to GraphQL; for reconciliation that is an answer, not a failure.
        return result.Failure == RepoFailure.NotFound ? RepoResult<string>.Of("") : result;
    }

    private const string ResolvedQuery = """
        query($id: ID!) { node(id: $id) { ... on PullRequestReviewThread { isResolved } } }
        """;

    public Task<RepoResult<bool>> ThreadResolvedAsync(RepoAccess access, string threadId, CancellationToken ct) =>
        QueryAsync(access, ResolvedQuery, new { id = threadId }, data => data.GetProperty("node").GetProperty("isResolved").GetBoolean(), ct);

    private const string ReviewBodyQuery = """
        query($id: ID!) { node(id: $id) { ... on PullRequestReview { body } } }
        """;

    public async Task<RepoResult<string>> ReviewBodyAsync(RepoAccess access, string reviewId, CancellationToken ct)
    {
        var result = await QueryAsync(access, ReviewBodyQuery, new { id = reviewId }, data =>
            data.GetProperty("node") is { ValueKind: JsonValueKind.Object } n && n.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", ct).ConfigureAwait(false);
        return result.Failure == RepoFailure.NotFound ? RepoResult<string>.Of("") : result;
    }

    private const string ReactedQuery = """
        query($id: ID!) { node(id: $id) {
          ... on Reactable { reactionGroups { content viewerHasReacted } }
        } }
        """;

    public Task<RepoResult<bool>> ViewerReactedAsync(RepoAccess access, string subjectId, string content, CancellationToken ct) =>
        QueryAsync(access, ReactedQuery, new { id = subjectId }, data =>
            data.GetProperty("node") is { ValueKind: JsonValueKind.Object } n && n.TryGetProperty("reactionGroups", out var groups) && groups.ValueKind == JsonValueKind.Array
            && groups.EnumerateArray().Any(g => g.TryGetProperty("content", out var c) && c.GetString() == content && g.TryGetProperty("viewerHasReacted", out var v) && v.ValueKind == JsonValueKind.True), ct);

    private const string MinimizedQuery = """
        query($id: ID!) { node(id: $id) { ... on Minimizable { isMinimized } } }
        """;

    public Task<RepoResult<bool>> CommentMinimizedAsync(RepoAccess access, string commentId, CancellationToken ct) =>
        QueryAsync(access, MinimizedQuery, new { id = commentId }, data => data.GetProperty("node").GetProperty("isMinimized").GetBoolean(), ct);

    // ---- transport ------------------------------------------------------------------------------

    private async Task<WriteResult<T>> RestAsync<T>(RepoAccess access, HttpMethod method, string relative, object payload, Func<JsonElement, T> read, CancellationToken ct)
    {
        if (access.Kind != AccessKind.User || access.Token is null)
        {
            return WriteResult<T>.Refused(RepoFailure.Unauthorized, "writes need a signed-in person");
        }

        using var request = Request(access, method, new Uri($"{GitHub.ApiBaseUrl.TrimEnd('/')}/{relative}"), payload);
        return await SendAsync(request, read, isGraph: false, ct).ConfigureAwait(false);
    }

    private async Task<WriteResult<T>> MutateAsync<T>(RepoAccess access, string mutation, object variables, Func<JsonElement, T> read, CancellationToken ct)
    {
        if (access.Kind != AccessKind.User || access.Token is null)
        {
            return WriteResult<T>.Refused(RepoFailure.Unauthorized, "writes need a signed-in person");
        }

        using var request = Request(access, HttpMethod.Post, GraphUrl(), new { query = mutation, variables });
        return await SendAsync(request, read, isGraph: true, ct).ConfigureAwait(false);
    }

    private async Task<WriteResult<T>> SendAsync<T>(HttpRequestMessage request, Func<JsonElement, T> read, bool isGraph, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(GitHubRepoSource.ApiClient).SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            // Could be before the request left or after: no way to tell, so no way to call it a failure.
            log.Warn(LogCategory.Web, $"GitHub write {request.Method} {Path(request)} lost: {e.Message}; outcome unknown");
            return WriteResult<T>.Unknown();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            log.Warn(LogCategory.Web, $"GitHub write {request.Method} {Path(request)} timed out; outcome unknown");
            return WriteResult<T>.Unknown();
        }

        using (response)
        {
            if ((int)response.StatusCode >= 500)
            {
                log.Warn(LogCategory.Web, $"GitHub write {request.Method} {Path(request)} answered {(int)response.StatusCode}; outcome unknown");
                return WriteResult<T>.Unknown();
            }

            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // The status said success or refusal, but the body was lost: for a success that is
                // still an unknown id, and reconciliation finds it.
                return response.IsSuccessStatusCode ? WriteResult<T>.Unknown() : WriteResult<T>.Refused(Refusal(response), null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return WriteResult<T>.Refused(Refusal(response), Message(text));
            }

            // A 204 (a deleted ref) has no body to read: done, with nothing to read out of it. A
            // reader that needs a body on an empty one is the unexpected shape below.
            if (!isGraph && text.Length == 0)
            {
                try
                {
                    return WriteResult<T>.Done(read(default));
                }
                catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
                {
                    log.Warn(LogCategory.Web, $"GitHub write {request.Method} {Path(request)} succeeded with no body where one was needed; outcome unknown");
                    return WriteResult<T>.Unknown();
                }
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                if (isGraph)
                {
                    if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                    {
                        var type = errors[0].TryGetProperty("type", out var t) ? t.GetString() : null;
                        var message = errors[0].TryGetProperty("message", out var m) ? m.GetString() : null;
                        if (!NothingRan(doc.RootElement, errors))
                        {
                            log.Warn(LogCategory.Web, $"GitHub write {request.Method} {Path(request)} answered GraphQL errors ({type ?? "untyped"}) after execution began; outcome unknown");
                            return WriteResult<T>.Unknown();
                        }

                        return WriteResult<T>.Refused(type switch
                        {
                            "NOT_FOUND" => RepoFailure.NotFound,
                            "FORBIDDEN" => RepoFailure.NotFound,
                            "RATE_LIMITED" => RepoFailure.RateLimited,
                            _ => RepoFailure.Upstream,
                        }, Clip(message));
                    }

                    return WriteResult<T>.Done(read(doc.RootElement.GetProperty("data")));
                }

                return WriteResult<T>.Done(read(doc.RootElement));
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // 2xx with a body we cannot read: GitHub did it, we do not know the result.
                log.Warn(LogCategory.Web, $"GitHub write {request.Method} {Path(request)} succeeded in an unexpected shape ({e.GetType().Name}); outcome unknown");
                return WriteResult<T>.Unknown();
            }
        }
    }

    /// <summary>The error types GitHub raises before a mutation acts: resolving, permission, validation, rate.</summary>
    private static readonly string[] BeforeEffect = ["NOT_FOUND", "FORBIDDEN", "UNPROCESSABLE", "RATE_LIMITED", "INSUFFICIENT_SCOPES"];

    /// <summary>
    /// Whether a GraphQL answer with errors proves the mutation did nothing — the only case in which
    /// "refused" is true, and so the only one in which the operation key may be released.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GraphQL answers errors in two kinds (spec, section 7.1.2). A REQUEST error (the document did not
    /// parse or validate) has no <c>data</c> entry at all: nothing ran. An EXECUTION error comes with
    /// <c>data</c>, possibly partial: execution began, and an error does not show that the mutation had
    /// no effect. Every error was read as a refusal at first, so an internal error beside a mutation's
    /// own returned data released the key and let the person submit again (found by review,
    /// 2026-10-20).
    /// </para>
    /// <para>
    /// So: no <c>data</c> — nothing ran. Otherwise nothing ran only if every field of <c>data</c> is
    /// null (no mutation returned anything) AND every error is of a type GitHub raises before acting.
    /// An internal error, an untyped one, or any returned data is "unknown": the caller reconciles.
    /// </para>
    /// </remarks>
    internal static bool NothingRan(JsonElement root, JsonElement errors)
    {
        if (!root.TryGetProperty("data", out var data))
        {
            return true;
        }

        var anyReturned = data.ValueKind == JsonValueKind.Object && data.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.Null);
        var allBefore = errors.EnumerateArray().All(e => e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && BeforeEffect.Contains(t.GetString()));
        return !anyReturned && allBefore;
    }

    private async Task<RepoResult<T>> QueryAsync<T>(RepoAccess access, string query, object variables, Func<JsonElement, T> read, CancellationToken ct)
    {
        if (access.Token is null)
        {
            return RepoResult<T>.Fail(RepoFailure.Unauthorized);
        }

        using var request = Request(access, HttpMethod.Post, GraphUrl(), new { query, variables });
        return await ReadAsync(request, doc =>
        {
            if (doc.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var type = errors[0].TryGetProperty("type", out var t) ? t.GetString() : null;
                return RepoResult<T>.Fail(type is "NOT_FOUND" or "FORBIDDEN" ? RepoFailure.NotFound : RepoFailure.Upstream);
            }

            return RepoResult<T>.Of(read(doc.GetProperty("data")));
        }, ct).ConfigureAwait(false);
    }

    private async Task<RepoResult<T>> GetAsync<T>(RepoAccess access, string relative, Func<JsonElement, T> read, CancellationToken ct)
    {
        using var request = Request(access, HttpMethod.Get, new Uri($"{GitHub.ApiBaseUrl.TrimEnd('/')}/{relative}"), null);
        return await ReadAsync(request, doc => RepoResult<T>.Of(read(doc)), ct).ConfigureAwait(false);
    }

    private async Task<RepoResult<T>> ReadAsync<T>(HttpRequestMessage request, Func<JsonElement, RepoResult<T>> read, CancellationToken ct)
    {
        try
        {
            using var response = await clients.CreateClient(GitHubRepoSource.ApiClient).SendAsync(request, ct).ConfigureAwait(false);
            if (GitHubRepoSource.Failure(response) is { } failed)
            {
                return RepoResult<T>.Fail(failed);
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return read(doc.RootElement);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.Warn(LogCategory.Web, $"GitHub reconciliation read {Path(request)} failed: {e.GetType().Name}");
            return RepoResult<T>.Fail(RepoFailure.Upstream);
        }
    }

    /// <summary>A refusal's kind. A 403 here is often the App lacking a write permission, which GitHub's message says.</summary>
    private static RepoFailure Refusal(HttpResponseMessage response) =>
        response.StatusCode switch
        {
            HttpStatusCode.Forbidden when response.Headers.Contains("Retry-After")
                                          || (response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0") => RepoFailure.RateLimited,
            _ => GitHubRepoSource.Failure(response) ?? RepoFailure.Upstream,
        };

    /// <summary>GitHub's own refusal text, bounded; the endpoint spells it before the page sees it (S8).</summary>
    private static string? Message(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("message", out var m) ? Clip(m.GetString()) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Clip(string? text) => text is null ? null : text.Length > 300 ? text[..300] : text;

    private Uri GraphUrl() => GitHub.GraphQlUrl.Length > 0 ? new Uri(GitHub.GraphQlUrl) : new Uri($"{GitHub.ApiBaseUrl.TrimEnd('/')}/graphql");

    private static HttpRequestMessage Request(RepoAccess access, HttpMethod method, Uri uri, object? payload)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("Versio-Web");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (access.Token is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (payload is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>What a log line may say about a request: its path, never its query or body.</summary>
    private static string Path(HttpRequestMessage request) => request.RequestUri?.AbsolutePath ?? "";

    private static string E(string value) => Uri.EscapeDataString(value);

    private static string N(int number) => number.ToString(CultureInfo.InvariantCulture);
}
