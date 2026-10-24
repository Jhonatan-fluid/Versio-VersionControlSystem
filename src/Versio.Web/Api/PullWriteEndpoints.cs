using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Versio.Core.Abstractions;
using Versio.Core.Diff;
using Versio.Core.Logging;
using Versio.Web.Auth;
using Versio.Web.Review;
using Versio.Web.Review.GitHub;
using Versio.Web.Sources;

namespace Versio.Web.Api;

/// <summary>Marks an endpoint as built by <see cref="PullWriteEndpoints.MapWrite"/>, for the test that every write is.</summary>
public sealed class WriteEndpoint;

/// <summary>What every write request carries.</summary>
/// <param name="OpKey">Made by the page when the person first submits; the same key on every retry (C5).</param>
public record WriteRequest(string? OpKey);

public sealed record CommentRequest(string? OpKey, string? Body) : WriteRequest(OpKey);

public sealed record LineCommentRequest(string? OpKey, string? Body, string? Path, int? Line, string? Side, int? StartLine) : WriteRequest(OpKey);

/// <param name="Path">The file on screen, whose drawing holds the element.</param>
/// <param name="From">A renamed file's old path, as the files list gave it, so the drawing is the one the page showed.</param>
/// <param name="Dom">The anchor id the person clicked (a <c>data-anchors</c> entry); everything else about it is the server's to know.</param>
public sealed record AnchorCommentRequest(string? OpKey, string? Body, string? Path, string? From, string? Dom) : WriteRequest(OpKey);

/// <param name="Event">APPROVE, REQUEST_CHANGES or COMMENT.</param>
public sealed record ReviewRequest(string? OpKey, string? Event, string? Body) : WriteRequest(OpKey);

/// <param name="Commit">The commit the pending review was created on, as the dialog showed it: the historical route submits nothing else.</param>
public sealed record HistoricalReviewRequest(string? OpKey, string? Event, string? Body, string? Commit) : WriteRequest(OpKey);

/// <summary>A draft comment for the viewer's pending review: on a drawn element (Dom), a line, or the whole file.</summary>
public sealed record PendingThreadRequest(string? OpKey, string? Body, string? Path, int? Line, string? Side, int? StartLine, string? From, string? Dom) : WriteRequest(OpKey);

/// <param name="AddTeams">Teams by slug, asked for a review; <paramref name="RemoveTeams"/> likewise withdrawn.</param>
public sealed record ReviewersRequest(string? OpKey, string[]? Add, string[]? Remove, string[]? AddTeams = null, string[]? RemoveTeams = null) : WriteRequest(OpKey);

public sealed record ViewedRequest(string? OpKey, string? Path, bool Viewed) : WriteRequest(OpKey);

public sealed record ResolveRequest(string? OpKey, bool Resolved) : WriteRequest(OpKey);

public sealed record PreviewRequest(string? Body);

/// <summary>
/// Pull request review, write side (docs/web/PLAN.md, *Pull request review*, slice 2):
/// conversation comments, line comments, replies, edits, deletions and resolving threads — each in
/// the signed-in person's own name, each reserved in the operation ledger, none ever retried blindly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every write goes through <see cref="MapWrite"/></b>, which applies the rules that do not
/// depend on the action: the kill switch (S9), the request-forgery checks (S4), the body cap (S8),
/// a person's credential and nothing else (S1), and the per-session rate limit. A test enumerates
/// the server's endpoints and fails on any non-GET route not built through it, so a new write
/// cannot quietly skip them.
/// </para>
/// <para>
/// <b>Then, per action, in this order</b>: the snapshot (the viewer and the pull request it was
/// sealed for), the operation key (well formed and not expired), the reservation (a duplicate
/// returns the first request's outcome), the action's own preconditions (a node id that belongs to
/// this pull request, S7; a line GitHub will accept, against this snapshot's head), the write, and
/// the record of how it ended. Duplicates are recognised before preconditions on purpose: a retry
/// of a comment that was posted must say "posted", even if the pull request has moved since.
/// </para>
/// </remarks>
public static partial class PullWriteEndpoints
{
    /// <summary>
    /// Set on a refusal when it is this OPERATION's outcome, recorded in the ledger as failed — not
    /// merely this request's. The page may let a held key go only on that, or on a first attempt.
    /// </summary>
    /// <remarks>
    /// A refusal of a retry proves only that the retry did nothing: a rate limit, a sign-in, a stale
    /// snapshot, a mismatch are all said before the ledger is consulted, while the first attempt
    /// may well have succeeded. Reading such a refusal as "released" cleared the key after a 429,
    /// and the next submission went under a new one and could post twice (found by review,
    /// 2026-10-20). Only a precondition's or GitHub's refusal, recorded under the key, settles it.
    /// </remarks>
    public const string SettledHeader = "X-Versio-Settled";

    /// <summary>A pending reservation older than this has lost its request (a crash, a restart): it is reconciled, not waited on.</summary>
    internal static readonly TimeSpan AbandonedAfter = TimeSpan.FromSeconds(45);

    /// <summary>GitHub's cap on a comment body, less room for the operation tag.</summary>
    internal const int MaxBodyChars = 65_536 - 128;

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var pulls = app.MapGroup("/api/repos/{owner}/{repo}/pulls/{number:int}").RequireRateLimiting(WebHost.RatePolicy);

        pulls.MapWrite("POST", "/comments", async (string owner, string repo, int number, CommentRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (Body(request.Body) is not { } body)
            {
                return Invalid("body", "A comment needs some text.");
            }

            return await w.RunAsync(http, owner, repo, number, request, "comment", ReviewTags.PayloadHash(body),
                precondition: null,
                (who, s, key) => w.Writer.AddConversationCommentAsync(who, owner, repo, number, ReviewTags.WithOperation(body, key), CancellationToken.None),
                (who, s, key, since) => w.Writer.FindTaggedCommentAsync(who, owner, repo, number, ReviewTags.OperationTag(key), since, ct),
                idempotent: false, ct).ConfigureAwait(false);
        });

        pulls.MapWrite("POST", "/line-comments", async (string owner, string repo, int number, LineCommentRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (Body(request.Body) is not { } body)
            {
                return Invalid("body", "A comment needs some text.");
            }

            if (!ApiEndpoints.ValidPath(request.Path) || (request.Line is { } l && (l < 1 || request.Side is not ("LEFT" or "RIGHT")))
                || (request.StartLine is { } st && (request.Line is not { } end || st < 1 || st > end)))
            {
                return Invalid("line", "That is not a line of this file.");
            }

            var comment = new LineComment("", request.Path!, request.Line, request.Side ?? "RIGHT", request.StartLine, body);
            return await w.RunAsync(http, owner, repo, number, request, "line-comment",
                ReviewTags.PayloadHash(body, comment.Path, comment.Line?.ToString(System.Globalization.CultureInfo.InvariantCulture), comment.Side, comment.StartLine?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                precondition: (who, s) => w.LinePreconditionAsync(who, s, comment, ct),
                (who, s, key) => w.Writer.AddLineCommentAsync(who, owner, repo, number, comment with { Commit = s.HeadSha, Body = ReviewTags.WithOperation(body, key) }, CancellationToken.None),
                (who, s, key, since) => w.Writer.FindTaggedCommentAsync(who, owner, repo, number, ReviewTags.OperationTag(key), since, ct),
                idempotent: false, ct).ConfigureAwait(false);
        });

        // A comment on a drawn element: a member row, a network (docs/web/PLAN.md, C3). The page
        // names only the element it clicked; the server re-renders the snapshot's drawing, finds that
        // anchor, decides the GitHub line from GitHub's own hunks (or the whole file when the diff
        // shows none of the element's lines), and writes the anchor tag itself.
        pulls.MapWrite("POST", "/anchor-comments", async (string owner, string repo, int number, AnchorCommentRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (Body(request.Body) is not { } body)
            {
                return Invalid("body", "A comment needs some text.");
            }

            if (!ApiEndpoints.ValidPath(request.Path) || (request.From is not null && !ApiEndpoints.ValidPath(request.From))
                || request.Dom is null || !AnchorDom().IsMatch(request.Dom))
            {
                return Invalid("anchor", "That is not something drawn in this file.");
            }

            (LineComment Comment, Versio.Core.Review.SemanticAnchor Anchor)? resolved = null;
            return await w.RunAsync(http, owner, repo, number, request, "anchor-comment", ReviewTags.PayloadHash(body, request.Path, request.From, request.Dom),
                precondition: async (who, s) =>
                {
                    var (refusal, found) = await w.AnchorPreconditionAsync(http, who, s, request.Path!, request.From, request.Dom, body, ct).ConfigureAwait(false);
                    resolved = found;
                    return refusal;
                },
                (who, s, key) => w.Writer.AddLineCommentAsync(who, owner, repo, number,
                    resolved!.Value.Comment with { Commit = s.HeadSha, Body = ReviewTags.WithAnchor(body, key, resolved.Value.Anchor) }, CancellationToken.None),
                (who, s, key, since) => w.Writer.FindTaggedCommentAsync(who, owner, repo, number, ReviewTags.OperationTag(key), since, ct),
                idempotent: false, ct).ConfigureAwait(false);
        });

        MapReviews(pulls);
        MapMeta(pulls);
        MapMerge(pulls);
        MapConversation(pulls);
        MapSuggestions(pulls);
        MapCreate(app);

        pulls.MapWrite("POST", "/threads/{threadId}/replies", async (string owner, string repo, int number, string threadId, CommentRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (Body(request.Body) is not { } body)
            {
                return Invalid("body", "A reply needs some text.");
            }

            return await w.RunAsync(http, owner, repo, number, request, "reply", ReviewTags.PayloadHash(threadId, body),
                precondition: (who, s) => w.OwnedAsync(who, s, threadId, ["PullRequestReviewThread"], ct),
                (who, s, key) => w.Writer.ReplyAsync(who, threadId, ReviewTags.WithOperation(body, key), CancellationToken.None),
                (who, s, key, since) => w.Writer.FindTaggedCommentAsync(who, owner, repo, number, ReviewTags.OperationTag(key), since, ct),
                idempotent: false, ct).ConfigureAwait(false);
        });

        pulls.MapWrite("POST", "/threads/{threadId}/resolve", async (string owner, string repo, int number, string threadId, ResolveRequest request, HttpContext http, Writes w, CancellationToken ct) =>
            await w.RunAsync(http, owner, repo, number, request, request.Resolved ? "resolve" : "unresolve", ReviewTags.PayloadHash(threadId, request.Resolved ? "1" : "0"),
                precondition: (who, s) => w.OwnedAsync(who, s, threadId, ["PullRequestReviewThread"], ct),
                async (who, s, key) => Done(await w.Writer.SetThreadResolvedAsync(who, threadId, request.Resolved, CancellationToken.None).ConfigureAwait(false), threadId),
                // A state read answers definitively: resolved as asked is done, otherwise not done.
                async (who, s, key, since) => Reconciled(await w.Writer.ThreadResolvedAsync(who, threadId, ct).ConfigureAwait(false), r => r == request.Resolved, threadId),
                idempotent: true, ct).ConfigureAwait(false));

        pulls.MapWrite("PATCH", "/comments/{commentId}", async (string owner, string repo, int number, string commentId, CommentRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (Body(request.Body) is not { } body)
            {
                return Invalid("body", "A comment needs some text; delete it instead.");
            }

            NodeOwner? target = null;
            Versio.Core.Review.SemanticAnchor? anchor = null;
            return await w.RunAsync(http, owner, repo, number, request, "edit", ReviewTags.PayloadHash(commentId, body),
                precondition: async (who, s) =>
                {
                    var (refusal, node) = await w.OwnerOfAsync(who, s, commentId, ["IssueComment", "PullRequestReviewComment"], ct).ConfigureAwait(false);
                    target = node;
                    if (refusal is not null)
                    {
                        return refusal;
                    }

                    // An edit changes the words, not what the comment is on. The body is rewritten
                    // whole, so the anchor the comment already carries is read here, from GitHub, and
                    // written again — never one the page sent. Without this an edited comment on a
                    // member or a network lost its anchor and fell off the drawing (found by review,
                    // 2026-10-20). Unreadable now: no edit, rather than one that drops the anchor.
                    var current = await w.Writer.CommentBodyAsync(who, commentId, ct).ConfigureAwait(false);
                    if (!current.Ok)
                    {
                        return ApiEndpoints.Refused(current.Failure);
                    }

                    anchor = ReviewTags.ReadAnchor(current.Value);
                    return null;
                },
                async (who, s, key) => Done(await w.Writer.EditCommentAsync(who, commentId, target!.Type,
                    anchor is null ? ReviewTags.WithOperation(body, key) : ReviewTags.WithAnchor(body, key, anchor), CancellationToken.None).ConfigureAwait(false), commentId),
                async (who, s, key, since) => Reconciled(await w.Writer.CommentBodyAsync(who, commentId, ct).ConfigureAwait(false), b => ReviewTags.Carries(b, key), commentId),
                idempotent: true, ct).ConfigureAwait(false);
        });

        pulls.MapWrite("POST", "/comments/{commentId}/delete", async (string owner, string repo, int number, string commentId, WriteRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            NodeOwner? target = null;
            return await w.RunAsync(http, owner, repo, number, request, "delete", ReviewTags.PayloadHash(commentId),
                precondition: async (who, s) =>
                {
                    var (refusal, node) = await w.OwnerOfAsync(who, s, commentId, ["IssueComment", "PullRequestReviewComment"], ct).ConfigureAwait(false);
                    target = node;
                    return refusal;
                },
                async (who, s, key) => Done(await w.Writer.DeleteCommentAsync(who, commentId, target!.Type, CancellationToken.None).ConfigureAwait(false), commentId),
                // Gone is done; still there is not done, and deleting is safe to repeat.
                async (who, s, key, since) => Reconciled(await w.Writer.CommentBodyAsync(who, commentId, ct).ConfigureAwait(false), b => b.Length == 0, commentId),
                idempotent: true, ct).ConfigureAwait(false);
        });

        // What a comment will look like, rendered by the same Core Markdown every reader sees it
        // through. Writes nothing; it is under MapWrite because it is a POST with a body, and only a
        // person who can write has a reason to preview.
        pulls.MapWrite("POST", "/markdown", (PreviewRequest request) =>
            Results.Ok(new { html = request.Body is { Length: > 0 and <= MaxBodyChars } b ? ApiEndpoints.MarkdownFragment(ReviewTags.Strip(b)) : "" }));
    }

    private static readonly string[] Events = ["APPROVE", "REQUEST_CHANGES", "COMMENT"];

    /// <summary>
    /// Reviews (docs/web/PLAN.md, *Pull request review*, C2): a one-shot review, the pending review's
    /// draft comments, its submission — on the snapshot's head, or as historical on its own older
    /// commit — and discarding it; requesting reviewers; marking files viewed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What an approval guarantees, and what it does not.</b> A review is bound to the commit it is
    /// CREATED on: a one-shot review is one REST call carrying <c>commit_id</c>, created and submitted
    /// together; a pending review is created with <c>commitOID</c>. Submitting takes no commit at all,
    /// which is why the commit is fixed at creation. What nothing in GitHub's API can guarantee is that
    /// the head did not move between our check and GitHub's submit; the check narrows that, the page
    /// re-reads the state after submitting and says when it moved, and branch protection's "dismiss
    /// stale approvals" does the rest. The plan says exactly this, and claims no more.
    /// </para>
    /// <para>
    /// <b>A pending review is resumed only if it is the viewer's and was created on the snapshot's
    /// head.</b> One on an older commit can be submitted only through the historical route, which names
    /// that commit and refuses any other, so a reviewer never approves a commit they did not see named.
    /// Moving its comments to a new review is deferred: GitHub allows one pending review per person per
    /// pull request, so a new one cannot be created beside it to copy into (PLAN.md, C2).
    /// </para>
    /// </remarks>
    private static void MapReviews(RouteGroupBuilder pulls)
    {
        pulls.MapWrite("POST", "/reviews", async (string owner, string repo, int number, ReviewRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (Event(request.Event, request.Body, oneShot: true) is { } invalid)
            {
                return invalid;
            }

            var body = request.Body ?? "";
            return await w.RunAsync(http, owner, repo, number, request, "review", ReviewTags.PayloadHash(request.Event, body),
                precondition: (who, s) => w.ReviewPreconditionAsync(who, s, ct),
                (who, s, key) => w.Writer.SubmitReviewAsync(who, owner, repo, number, s.HeadSha, request.Event!, ReviewTags.WithOperation(body, key), CancellationToken.None),
                (who, s, key, since) => w.Writer.FindTaggedReviewAsync(who, owner, repo, number, ReviewTags.OperationTag(key), ct),
                idempotent: false, ct).ConfigureAwait(false);
        });

        pulls.MapWrite("POST", "/reviews/pending/threads", async (string owner, string repo, int number, PendingThreadRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (Body(request.Body) is not { } body)
            {
                return Invalid("body", "A comment needs some text.");
            }

            if (!ApiEndpoints.ValidPath(request.Path) || (request.From is not null && !ApiEndpoints.ValidPath(request.From))
                || (request.Dom is not null && !AnchorDom().IsMatch(request.Dom))
                || (request.Line is { } l && (l < 1 || request.Side is not ("LEFT" or "RIGHT")))
                || (request.StartLine is { } st && (request.Line is not { } end || st < 1 || st > end)))
            {
                return Invalid("target", "That is not something in this file.");
            }

            (LineComment Comment, Versio.Core.Review.SemanticAnchor? Anchor, string? Review, string PullNode)? target = null;
            return await w.RunAsync(http, owner, repo, number, request, "pending-thread",
                ReviewTags.PayloadHash(body, request.Path, request.From, request.Dom, request.Line?.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Side, request.StartLine?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                precondition: async (who, s) =>
                {
                    var (refusal, found) = await w.PendingThreadPreconditionAsync(http, who, s, request, body, ct).ConfigureAwait(false);
                    target = found;
                    return refusal;
                },
                async (who, s, key) =>
                {
                    var (comment, anchor, review, pullNode) = target!.Value;
                    var reviewId = review;
                    if (reviewId is null)
                    {
                        // The review is created here, on the snapshot's head: the commit it will approve.
                        var started = await w.Writer.StartPendingReviewAsync(who, pullNode, s.HeadSha, CancellationToken.None).ConfigureAwait(false);
                        if (started.Outcome != WriteOutcome.Done)
                        {
                            return started;
                        }

                        reviewId = started.Value!;
                    }

                    var tagged = anchor is null ? ReviewTags.WithOperation(body, key) : ReviewTags.WithAnchor(body, key, anchor);
                    return await w.Writer.AddPendingThreadAsync(who, reviewId, comment with { Body = tagged }, CancellationToken.None).ConfigureAwait(false);
                },
                // Only the viewer sees their pending review: reconciled by its comments' tags.
                async (who, s, key, since) =>
                {
                    var pending = await w.Source.GetPendingReviewAsync(who, owner, repo, number, ct).ConfigureAwait(false);
                    if (!pending.Ok)
                    {
                        return pending.As<string>();
                    }

                    // Not among comments that were cut short is not "not there": unknown stays unknown.
                    var review = pending.Value!.Review;
                    return review?.Comments.FirstOrDefault(c => ReviewTags.Carries(c.Body, key)) is { } found
                        ? RepoResult<string>.Of(found.Id)
                        : review is { Truncated: true } ? RepoResult<string>.Fail(RepoFailure.Upstream) : RepoResult<string>.Of("");
                },
                idempotent: false, ct).ConfigureAwait(false);
        });

        pulls.MapWrite("POST", "/reviews/{reviewId}/submit", async (string owner, string repo, int number, string reviewId, ReviewRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (Event(request.Event, request.Body, oneShot: false) is { } invalid)
            {
                return invalid;
            }

            var body = request.Body ?? "";
            return await w.RunAsync(http, owner, repo, number, request, "submit", ReviewTags.PayloadHash(reviewId, request.Event, body),
                precondition: (who, s) => w.PendingPreconditionAsync(who, s, reviewId, commit: null, stale: true, ct),
                async (who, s, key) => Done(await w.Writer.SubmitPendingReviewAsync(who, reviewId, request.Event!, ReviewTags.WithOperation(body, key), CancellationToken.None).ConfigureAwait(false), reviewId),
                (who, s, key, since) => w.SubmittedAsync(who, reviewId, ct),
                idempotent: false, ct).ConfigureAwait(false);
        });

        // An older pending review, submitted as what it is: on the commit it was created on, which the
        // dialog named and this request repeats. Exempt from the snapshot's staleness rule because it
        // does not claim to describe the current head (C1's one exception).
        pulls.MapWrite("POST", "/reviews/{reviewId}/submit-historical", async (string owner, string repo, int number, string reviewId, HistoricalReviewRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (Event(request.Event, request.Body, oneShot: false) is { } invalid)
            {
                return invalid;
            }

            if (request.Commit is not { Length: 40 } commit || !commit.All(Uri.IsHexDigit))
            {
                return Invalid("commit", "Name the commit the review is on.");
            }

            var body = request.Body ?? "";
            return await w.RunAsync(http, owner, repo, number, request, "submit-historical", ReviewTags.PayloadHash(reviewId, request.Event, body, commit),
                precondition: (who, s) => w.PendingPreconditionAsync(who, s, reviewId, commit, stale: false, ct),
                async (who, s, key) => Done(await w.Writer.SubmitPendingReviewAsync(who, reviewId, request.Event!, ReviewTags.WithOperation(body, key), CancellationToken.None).ConfigureAwait(false), reviewId),
                (who, s, key, since) => w.SubmittedAsync(who, reviewId, ct),
                idempotent: false, ct).ConfigureAwait(false);
        });

        pulls.MapWrite("POST", "/reviews/{reviewId}/discard", async (string owner, string repo, int number, string reviewId, WriteRequest request, HttpContext http, Writes w, CancellationToken ct) =>
            await w.RunAsync(http, owner, repo, number, request, "discard", ReviewTags.PayloadHash(reviewId),
                precondition: (who, s) => w.PendingPreconditionAsync(who, s, reviewId, commit: "any", stale: false, ct),
                async (who, s, key) => Done(await w.Writer.DeletePendingReviewAsync(who, reviewId, CancellationToken.None).ConfigureAwait(false), reviewId),
                // Gone is done; still there is not, and deleting again is harmless.
                async (who, s, key, since) =>
                {
                    var node = await w.Writer.NodeOwnerAsync(who, reviewId, ct).ConfigureAwait(false);
                    return node.Ok ? RepoResult<string>.Of("") : node.Failure == RepoFailure.NotFound ? RepoResult<string>.Of(reviewId) : node.As<string>();
                },
                idempotent: true, ct).ConfigureAwait(false));

        pulls.MapWrite("POST", "/reviewers", async (string owner, string repo, int number, ReviewersRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            // People by login, teams by slug (FINISH.md, B7): both the same shape, both bounded. Asking
            // someone who already reviewed is GitHub's "re-request review", the same call.
            var change = new ReviewerChange(request.Add ?? [], request.Remove ?? [], request.AddTeams ?? [], request.RemoveTeams ?? []);
            if (change.Count is 0 or > 15 || change.Add.Concat(change.Remove).Concat(change.AddTeams).Concat(change.RemoveTeams).Any(l => !Login().IsMatch(l)))
            {
                return Invalid("reviewers", "Name up to fifteen people by their GitHub login, or teams by their slug.");
            }

            return await w.RunAsync(http, owner, repo, number, request, "reviewers",
                ReviewTags.PayloadHash(string.Join(',', change.Add), "|", string.Join(',', change.Remove), "|", string.Join(',', change.AddTeams), "|", string.Join(',', change.RemoveTeams)),
                precondition: null,
                async (who, s, key) => Done(await w.Writer.SetReviewersAsync(who, owner, repo, number, change, CancellationToken.None).ConfigureAwait(false), "reviewers"),
                // Safe to repeat: an uncertain change is simply made again.
                (who, s, key, since) => Task.FromResult(RepoResult<string>.Of("")),
                idempotent: true, ct).ConfigureAwait(false);
        });

        // Dismissing a review takes it out of the merge requirements with a reason everyone reads
        // (FINISH.md, B7). GitHub decides who may (write access); here only that it is a submitted
        // review of THIS pull request, and that the reason is said.
        pulls.MapWrite("POST", "/reviews/{reviewId}/dismiss", async (string owner, string repo, int number, string reviewId, CommentRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (Body(request.Body) is not { } message)
            {
                return Invalid("body", "Say why the review is dismissed.");
            }

            return await w.RunAsync(http, owner, repo, number, request, "dismiss", ReviewTags.PayloadHash(reviewId, message),
                precondition: async (who, s) =>
                {
                    var (refusal, node) = await w.OwnerOfAsync(who, s, reviewId, ["PullRequestReview"], ct).ConfigureAwait(false);
                    return refusal ?? (node!.State is "APPROVED" or "CHANGES_REQUESTED"
                        ? null
                        : Results.Json(new { error = "not-dismissable", message = "Only an approval or a request for changes can be dismissed." }, statusCode: 409));
                },
                async (who, s, key) => Done(await w.Writer.DismissReviewAsync(who, reviewId, message, CancellationToken.None).ConfigureAwait(false), reviewId),
                // Dismissed is a state: read back, it answers definitively.
                async (who, s, key, since) =>
                {
                    var node = await w.Writer.NodeOwnerAsync(who, reviewId, ct).ConfigureAwait(false);
                    return node.Ok ? RepoResult<string>.Of(node.Value!.State == "DISMISSED" ? reviewId : "") : node.As<string>();
                },
                idempotent: true, ct).ConfigureAwait(false);
        });

        // A submitted review's summary, rewritten by its author: the words only, never the state or
        // the commit. A pending review is not edited here — its submission is what writes its words.
        pulls.MapWrite("PATCH", "/reviews/{reviewId}", async (string owner, string repo, int number, string reviewId, CommentRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (request.Body is null || request.Body.Length > MaxBodyChars)
            {
                return Invalid("body", "That is too long to post.");
            }

            var body = request.Body;
            return await w.RunAsync(http, owner, repo, number, request, "edit-review", ReviewTags.PayloadHash(reviewId, body),
                precondition: async (who, s) =>
                {
                    var (refusal, node) = await w.OwnerOfAsync(who, s, reviewId, ["PullRequestReview"], ct).ConfigureAwait(false);
                    if (refusal is not null)
                    {
                        return refusal;
                    }

                    return node!.Author == who.Who && node.State != "PENDING"
                        ? null
                        : Results.Json(new { error = "not-your-review", message = "Only your own submitted review can be edited here." }, statusCode: 409);
                },
                async (who, s, key) => Done(await w.Writer.EditReviewAsync(who, reviewId, ReviewTags.WithOperation(body, key), CancellationToken.None).ConfigureAwait(false), reviewId),
                async (who, s, key, since) => Reconciled(await w.Writer.ReviewBodyAsync(who, reviewId, ct).ConfigureAwait(false), b => ReviewTags.Carries(b, key), reviewId),
                idempotent: true, ct).ConfigureAwait(false);
        });

        pulls.MapWrite("POST", "/viewed", async (string owner, string repo, int number, ViewedRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (!ApiEndpoints.ValidPath(request.Path))
            {
                return Invalid("path", "That is not a file of this pull request.");
            }

            string? pullNode = null;
            return await w.RunAsync(http, owner, repo, number, request, "viewed", ReviewTags.PayloadHash(request.Path, request.Viewed ? "1" : "0"),
                precondition: async (who, s) =>
                {
                    var summary = await w.Source.GetPullSummaryAsync(who, owner, repo, number, ct).ConfigureAwait(false);
                    pullNode = summary.Ok ? summary.Value!.NodeId : null;
                    return pullNode is { Length: > 0 } ? null : ApiEndpoints.Refused(summary.Ok ? RepoFailure.Upstream : summary.Failure);
                },
                async (who, s, key) => Done(await w.Writer.SetViewedAsync(who, pullNode!, request.Path!, request.Viewed, CancellationToken.None).ConfigureAwait(false), request.Path!),
                (who, s, key, since) => Task.FromResult(RepoResult<string>.Of("")),
                idempotent: true, ct).ConfigureAwait(false);
        });
    }

    /// <summary>A review event, and the body GitHub requires with it: a one-shot comment or request for changes says something.</summary>
    private static IResult? Event(string? reviewEvent, string? body, bool oneShot)
    {
        if (reviewEvent is null || !Events.Contains(reviewEvent))
        {
            return Invalid("event", "Choose Comment, Approve or Request changes.");
        }

        if (body is { Length: > MaxBodyChars })
        {
            return Invalid("body", "That is too long to post.");
        }

        return (reviewEvent == "REQUEST_CHANGES" || (oneShot && reviewEvent == "COMMENT")) && string.IsNullOrWhiteSpace(body)
            ? Invalid("body", "Say what should change.")
            : null;
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex Login();

    /// <summary>Registers a write route with the rules every write shares (S1, S4, S8, S9).</summary>
    public static RouteHandlerBuilder MapWrite(this RouteGroupBuilder group, string method, string pattern, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.MapMethods(pattern, [method], handler)
            .WithMetadata(new WriteEndpoint())
            .RequireRateLimiting(WebHost.WritePolicy);
    }

    internal const string AccessItem = "versio.write.access";

    /// <summary>
    /// The checks every write passes, as middleware right after routing, for every endpoint
    /// <see cref="MapWrite"/> marked.
    /// </summary>
    /// <remarks>
    /// <b>Middleware, not an endpoint filter</b>: filters run after the body is bound, so a form post
    /// got the framework's 415 instead of the forgery refusal, and with writes off a malformed body
    /// answered 400 — telling the world the route exists — before the kill switch's 404 could. Here
    /// nothing reads the body until every rule has passed (found by the first run of the S4 test).
    /// </remarks>
    public sealed class WriteGate(RequestDelegate next)
    {
        public async Task InvokeAsync(HttpContext http, IOptions<WebOptions> options, AccessResolver access, IVersioLogger log)
        {
            ArgumentNullException.ThrowIfNull(http);
            if (http.GetEndpoint()?.Metadata.GetMetadata<WriteEndpoint>() is null)
            {
                await next(http).ConfigureAwait(false);
                return;
            }

            if (await RefusalAsync(http, options, access, log).ConfigureAwait(false) is { } refused)
            {
                await refused.ExecuteAsync(http).ConfigureAwait(false);
                return;
            }

            await next(http).ConfigureAwait(false);
        }

        private static async Task<IResult?> RefusalAsync(HttpContext http, IOptions<WebOptions> options, AccessResolver access, IVersioLogger log)
        {
            var owner = http.GetRouteValue("owner") as string ?? "";
            var repo = http.GetRouteValue("repo") as string ?? "";

            // S9 first, as 404: with writes off for this repository, a write route does not exist.
            // Said in the log all the same (Step 10): a 404 the operator did not expect is the first
            // sign of a misconfigured repository list, and a burst of them is somebody probing.
            if (!ApiEndpoints.Valid(owner, repo) || !options.Value.Writable(owner, repo))
            {
                // Route values are percent-decoded, so an invalid name is not written into the log
                // as it came (a %0A would start a line of its own): the valid ones are named.
                log.Info(LogCategory.Web, $"write refused (S9: writes are off for {(ApiEndpoints.Valid(owner, repo) ? $"{owner}/{repo}" : "an invalid repository name")}) {http.Request.Method} {http.Request.Path}");
                return ApiEndpoints.NotFoundJson();
            }

            // S11: the merge tier is its own switch on top, and likewise does not exist without it.
            if (http.GetEndpoint()?.Metadata.GetMetadata<MergeEndpoint>() is not null && !options.Value.Writes.Merge)
            {
                log.Info(LogCategory.Web, $"write refused (S11: the merge tier is off) {http.Request.Method} {http.Request.Path}");
                return ApiEndpoints.NotFoundJson();
            }

            // S4: a header no cross-site request can send without a preflight this server never
            // answers; a JSON body, which a form cannot post; and the browser's own word on where the
            // request came from, when it gives it. The custom header alone stops a forged request;
            // the other two cost nothing and stop the next mistake.
            if (http.Request.Headers[AuthEndpoints.CsrfHeader] != "1"
                || !MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var type) || type.MediaType != "application/json"
                || !SameOrigin(http, options.Value.GitHub.PublicBaseUrl))
            {
                log.Warn(LogCategory.Web, $"write refused (S4: request forgery checks) {http.Request.Method} {http.Request.Path}");
                return Results.Json(new { error = "forbidden", message = "This request was refused." }, statusCode: 403);
            }

            // S8: bounded before anything reads the body.
            var max = Math.Max(1024, options.Value.Writes.MaxBodyBytes);
            if (http.Request.ContentLength > max)
            {
                log.Warn(LogCategory.Web, $"write refused (S8: body of {http.Request.ContentLength} bytes over {max}) {http.Request.Method} {http.Request.Path}");
                return Results.Json(new { error = "too-large", message = "That is too long to post." }, statusCode: 413);
            }

            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            {
                size.MaxRequestBodySize = max;
            }

            // S1: a person's own token, nothing else. Not the installation token, not anonymous, not
            // (when it exists) an Okta session without GitHub: a review must be in somebody's name.
            // The session is asked for FIRST, before access is resolved: resolving it for a request
            // with no session read the repository's visibility from GitHub under the App's token,
            // uncached for a private repository, on every anonymous probe that had the three S4
            // headers — a flood with no limiter in front of it, since the per-session limiter has
            // no session to count (review, 2026-10-24). With no session there is nobody to write as.
            if (await access.SessionAsync(http, http.RequestAborted).ConfigureAwait(false) is null)
            {
                log.Info(LogCategory.Web, $"write refused (S1: no session) {http.Request.Method} {http.Request.Path}");
                return Results.Json(new { error = "sign-in", message = "Sign in with GitHub to comment, so it is in your name." }, statusCode: 401);
            }

            var who = await access.ResolveAsync(http, owner, repo, http.RequestAborted).ConfigureAwait(false);
            if (!who.Ok)
            {
                return ApiEndpoints.Refused(who.Failure, http);
            }

            if (who.Value!.Kind != AccessKind.User)
            {
                log.Info(LogCategory.Web, $"write refused (S1: not a signed-in person) {http.Request.Method} {http.Request.Path}");
                return Results.Json(new { error = "sign-in", message = "Sign in with GitHub to comment, so it is in your name." }, statusCode: 401);
            }

            http.Items[AccessItem] = who.Value;
            return null;
        }

        /// <remarks>
        /// <c>Sec-Fetch-Site</c> is the browser saying, unforgeably, where the request came from; when
        /// it is absent (an old browser, a script), <c>Origin</c> must be this site's. With neither,
        /// the custom header is the wall.
        /// </remarks>
        private static bool SameOrigin(HttpContext http, string publicBaseUrl)
        {
            var site = http.Request.Headers["Sec-Fetch-Site"].ToString();
            if (site.Length > 0)
            {
                return site == "same-origin";
            }

            var origin = http.Request.Headers.Origin.ToString();
            if (origin.Length == 0)
            {
                return true;
            }

            var expected = publicBaseUrl.Length > 0 ? publicBaseUrl.TrimEnd('/') : $"{http.Request.Scheme}://{http.Request.Host}";
            return string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>What a write needs, injected once.</summary>
    public sealed partial class Writes(
        IRepoWriter writer, IRepoSource source, IOperationLedger ledger, ReviewSnapshots snapshots, PullReview review, IClock clock, IVersioLogger log,
        Versio.Web.Rendering.ViewService views, ApiEndpoints.Loader load)
    {
        public IRepoWriter Writer => writer;

        public IRepoSource Source => source;

        /// <summary>For the other parts of this class, and the branch routes' own log lines: a primary constructor's parameters are seen only by the part that declares them.</summary>
        internal IVersioLogger Log => log;

        private PullReview Review => review;

        private ApiEndpoints.Loader Load => load;

        /// <summary>A one-shot review's preconditions: the snapshot still the pull request's, and no pending review in the way.</summary>
        /// <remarks>GitHub keeps one pending review per person per pull request; a one-shot review beside it is refused there, and said here first.</remarks>
        internal async Task<IResult?> ReviewPreconditionAsync(RepoAccess who, ReviewSnapshot s, CancellationToken ct)
        {
            if (await StaleAsync(who, s, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var pending = await source.GetPendingReviewAsync(who, s.Owner, s.Repo, s.Number, ct).ConfigureAwait(false);
            if (!pending.Ok)
            {
                return ApiEndpoints.Refused(pending.Failure);
            }

            return pending.Value!.Review is null
                ? null
                : Results.Json(new { error = "pending-exists", message = "You have a review in progress. Submit or discard it." }, statusCode: 409);
        }

        /// <summary>
        /// A draft comment's preconditions: the snapshot current, the target valid (a line GitHub takes,
        /// or a drawn element resolved from the server's own rendering), and the viewer's pending review
        /// either absent (it will be created on the snapshot's head) or created on that head.
        /// </summary>
        internal async Task<(IResult? Refusal, (LineComment, Versio.Core.Review.SemanticAnchor?, string?, string)? Found)> PendingThreadPreconditionAsync(
            HttpContext http, RepoAccess who, ReviewSnapshot s, PendingThreadRequest request, string body, CancellationToken ct)
        {
            LineComment comment;
            Versio.Core.Review.SemanticAnchor? anchor = null;
            if (request.Dom is { } dom)
            {
                var (refusal, found) = await AnchorPreconditionAsync(http, who, s, request.Path!, request.From, dom, body, ct).ConfigureAwait(false);
                if (refusal is not null)
                {
                    return (refusal, null);
                }

                (comment, anchor) = found!.Value;
            }
            else
            {
                comment = new LineComment("", request.Path!, request.Line, request.Side ?? "RIGHT", request.StartLine, body);
                if (await LinePreconditionAsync(who, s, comment, ct).ConfigureAwait(false) is { } refused)
                {
                    return (refused, null);
                }
            }

            var summary = await source.GetPullSummaryAsync(who, s.Owner, s.Repo, s.Number, ct).ConfigureAwait(false);
            var pending = await source.GetPendingReviewAsync(who, s.Owner, s.Repo, s.Number, ct).ConfigureAwait(false);
            if (!summary.Ok || !pending.Ok)
            {
                return (ApiEndpoints.Refused(summary.Ok ? pending.Failure : summary.Failure), null);
            }

            if (pending.Value!.Review is { } existing && existing.Commit != s.HeadSha)
            {
                return (Results.Json(new { error = "pending-older", commit = existing.Commit, message = "Your review in progress is on an older commit. Submit it as it is, or discard it." }, statusCode: 409), null);
            }

            return (null, (comment, anchor, pending.Value.Review?.Id, summary.Value!.NodeId));
        }

        /// <summary>
        /// A pending review's preconditions for submitting or discarding it: it is a review of THIS pull
        /// request (S7), the viewer's own, still pending, and on the commit this route allows — the
        /// snapshot's head (<paramref name="commit"/> null), the named older commit (historical), or any
        /// ("any", discarding).
        /// </summary>
        internal async Task<IResult?> PendingPreconditionAsync(RepoAccess who, ReviewSnapshot s, string reviewId, string? commit, bool stale, CancellationToken ct)
        {
            if (stale && await StaleAsync(who, s, ct).ConfigureAwait(false) is { } moved)
            {
                return moved;
            }

            var (refusal, node) = await OwnerOfAsync(who, s, reviewId, ["PullRequestReview"], ct).ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }

            if (node!.Author != who.Who || node.State != "PENDING")
            {
                log.Warn(LogCategory.Web, $"review refused (C2: {reviewId} is not {who.Who}'s pending review)");
                return Results.Json(new { error = "not-your-pending-review", message = "That is not a review of yours in progress." }, statusCode: 409);
            }

            var wanted = commit ?? s.HeadSha;
            if (wanted != "any" && node.Commit != wanted)
            {
                return Results.Json(new { error = "pending-older", commit = node.Commit, message = "This review is on another commit than the one named. Reload, and check which commit you are approving." }, statusCode: 409);
            }

            return null;
        }

        /// <summary>A submitted review is no longer pending: that is the reconciliation's answer.</summary>
        internal async Task<RepoResult<string>> SubmittedAsync(RepoAccess who, string reviewId, CancellationToken ct)
        {
            var node = await writer.NodeOwnerAsync(who, reviewId, ct).ConfigureAwait(false);
            return node.Ok ? RepoResult<string>.Of(node.Value!.State is { } state && state != "PENDING" ? reviewId : "") : node.As<string>();
        }

        /// <summary>
        /// One write, start to finish: snapshot, key, reservation, preconditions, the call, the record.
        /// </summary>
        /// <param name="idempotent">
        /// True when doing it twice is harmless (resolve, edit to the same text, delete): an uncertain
        /// earlier attempt that reconciliation finds undone is simply done again. False for creating a
        /// comment: never sent twice; an uncertain one stays "may or may not have been posted" until
        /// reconciliation finds it, and a new attempt needs a new key — the person's deliberate choice.
        /// </param>
        internal async Task<IResult> RunAsync(
            HttpContext http, string owner, string repo, int number, WriteRequest request, string action, string payloadHash,
            Func<RepoAccess, ReviewSnapshot, Task<IResult?>>? precondition,
            Func<RepoAccess, ReviewSnapshot, string, Task<WriteResult<string>>> write,
            Func<RepoAccess, ReviewSnapshot, string, DateTimeOffset, Task<RepoResult<string>>> reconcile,
            bool idempotent, CancellationToken ct)
        {
            var who = (RepoAccess)http.Items[AccessItem]!;
            var token = http.Request.Headers[PullEndpoints.SnapshotHeader].ToString();
            var (snapshot, refused) = snapshots.Open(token, owner, repo, number, PullEndpoints.Viewer(who));
            if (snapshot is null && refused == SnapshotRefusal.Expired
                && snapshots.OpenExpired(token, owner, repo, number, PullEndpoints.Viewer(who)) is { } old)
            {
                return await SettleAsync(http, who, old, owner, repo, number, request, action, payloadHash,
                    (w, s, k, since) => reconcile(w, s!, k, since), ct).ConfigureAwait(false);
            }

            if (snapshot is null)
            {
                // Said in the log (S10): an expired token is ordinary, an invalid one is hand-made
                // or another viewer's, and either way the write did not happen.
                log.Info(LogCategory.Web, $"write {action} {who} {owner}/{repo}#{number} refused: snapshot {(refused == SnapshotRefusal.Expired ? "expired" : "invalid")}");
                return refused == SnapshotRefusal.Expired
                    ? Results.Json(new { error = "snapshot-expired", message = "This review was loaded too long ago. Reload the pull request." }, statusCode: 409)
                    : Results.Json(new { error = "snapshot-invalid", message = "This review's revision could not be read. Reload the pull request." }, statusCode: 400);
            }

            return await RunOpenedAsync(http, who, snapshot, owner, repo, number, request, action, payloadHash,
                precondition is null ? null : (w, s) => precondition(w, s!),
                (w, s, k) => write(w, s!, k),
                (w, s, k, since) => reconcile(w, s!, k, since),
                idempotent, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// A write about no existing pull request — opening one — so there is no snapshot to open:
        /// everything else is <see cref="RunAsync"/>'s, the key, the reservation, the preconditions,
        /// the call, the record and the reconciliation. The ledger keys it under pull request 0.
        /// </summary>
        internal Task<IResult> RunWithoutSnapshotAsync(
            HttpContext http, string owner, string repo, WriteRequest request, string action, string payloadHash,
            Func<RepoAccess, Task<IResult?>>? precondition,
            Func<RepoAccess, string, Task<WriteResult<string>>> write,
            Func<RepoAccess, string, DateTimeOffset, Task<RepoResult<string>>> reconcile,
            bool idempotent, CancellationToken ct) =>
            RunOpenedAsync(http, (RepoAccess)http.Items[AccessItem]!, null, owner, repo, 0, request, action, payloadHash,
                precondition is null ? null : (w, _) => precondition(w),
                (w, _, k) => write(w, k),
                (w, _, k, since) => reconcile(w, k, since),
                idempotent, ct);

        /// <summary>
        /// The revision an operation is about is part of what it IS. The same key under another
        /// snapshot is another operation and is refused as one ("op-mismatch"), never run: a retry of
        /// an approval that never reached the server, after a push and a reload, carried the new
        /// snapshot and would have approved a commit the person never saw (found by review,
        /// 2026-10-20). The page sends a retry with the snapshot it was first sent under.
        ///
        /// Every entry is bound: there is no bare-hash entry to accept. One was accepted for a while,
        /// for entries recorded before the binding, and then refused a takeover and given its own
        /// "operation-legacy" refusal (reviews, 2026-10-20 and 2026-10-21) — all for data that existed
        /// only on test machines, since this was never deployed with unbound entries. Removed
        /// 2026-10-22; a bare-hash entry is now simply another payload.
        /// </summary>
        private static string Bound(string payloadHash, ReviewSnapshot? snapshot) =>
            snapshot is null ? payloadHash : ReviewTags.PayloadHash(payloadHash, snapshot.HeadSha, snapshot.BaseSha);

        private string LedgerKey(RepoAccess who, string action, string owner, string repo, int number, string key) =>
            string.Join('|', PullEndpoints.Viewer(who), action, $"{owner}/{repo}".ToLowerInvariant(), number, key);

        /// <summary>
        /// A retry under an expired snapshot: it learns how its own operation ended, and starts
        /// nothing. Done is done; in flight is in flight; unknown or abandoned is reconciled (a read);
        /// anything else — nothing recorded, refused, a different payload — is "reload".
        /// </summary>
        private async Task<IResult> SettleAsync(
            HttpContext http, RepoAccess who, ReviewSnapshot old, string owner, string repo, int number, WriteRequest request, string action, string payloadHash,
            Func<RepoAccess, ReviewSnapshot?, string, DateTimeOffset, Task<RepoResult<string>>> reconcile, CancellationToken ct)
        {
            var expired = Results.Json(new { error = "snapshot-expired", message = "This review was loaded too long ago. Reload the pull request." }, statusCode: 409);
            // Every way out of here is in the log (Step 10): a settle is a write that may have
            // happened, and the one line that says how it was answered is what an investigation of
            // "was my comment posted?" has to go on.
            var check = ReviewTags.CheckKey(request.OpKey, clock.UtcNow);
            if (check != KeyCheck.Ok)
            {
                Audit(who, action, owner, repo, number, old, request.OpKey ?? "", $"expired snapshot, key {check.ToString().ToLowerInvariant()}: reload");
                return expired;
            }

            var key = request.OpKey!;
            var ledgerKey = LedgerKey(who, action, owner, repo, number, key);
            var entry = await ledger.FindAsync(ledgerKey, ct).ConfigureAwait(false);
            if (entry is null || entry.PayloadHash != Bound(payloadHash, old))
            {
                Audit(who, action, owner, repo, number, old, key, entry is null ? "expired snapshot, nothing recorded: reload" : "expired snapshot, different payload: reload");
                return expired;
            }

            switch (entry.State)
            {
                case OperationState.Succeeded:
                    Audit(who, action, owner, repo, number, old, key, "expired snapshot, settled done from the ledger");
                    return Results.Ok(new { outcome = "done", id = entry.Result });
                case OperationState.Pending when clock.UtcNow - entry.Reserved < AbandonedAfter:
                    Audit(who, action, owner, repo, number, old, key, "expired snapshot, still in flight");
                    return Results.Json(new { outcome = "pending", message = "Still being posted." }, statusCode: 202);
                case OperationState.Pending or OperationState.Unknown:
                    Audit(who, action, owner, repo, number, old, key, "expired snapshot, reconciling");
                    return await ReconcileAsync(who, old, key, entry, reconcile, ledgerKey, action, owner, repo, number).ConfigureAwait(false) ?? UnknownResult();
                default:
                    // Failed: the ledger knows nothing was written under this key.
                    Audit(who, action, owner, repo, number, old, key, "expired snapshot, settled as refused from the ledger");
                    Settled(http);
                    return expired;
            }
        }

        /// <summary>The header that says a refusal is this operation's recorded outcome — see <see cref="SettledHeader"/>.</summary>
        private static void Settled(HttpContext http) => http.Response.Headers[SettledHeader] = "1";

        /// <summary>
        /// Settled only when the ledger already held this key — a failed attempt taken over — so it
        /// knows every attempt there was. A FRESH reservation knows of none: on a first attempt the
        /// page needs no mark (it lets the key go anyway), and on a retry it means the ledger has
        /// forgotten the earlier attempt (a single-instance restart, a lost Redis) — which proves
        /// nothing about whether that one was written. Marking it settled let the page release a key
        /// whose first attempt may have posted (found in review, 2026-10-21).
        /// </summary>
        private static void SettledIfKnown(HttpContext http, Reservation reservation)
        {
            if (!reservation.Fresh)
            {
                Settled(http);
            }
        }

        private async Task<IResult> RunOpenedAsync(
            HttpContext http, RepoAccess who, ReviewSnapshot? snapshot, string owner, string repo, int number, WriteRequest request, string action, string payloadHash,
            Func<RepoAccess, ReviewSnapshot?, Task<IResult?>>? precondition,
            Func<RepoAccess, ReviewSnapshot?, string, Task<WriteResult<string>>> write,
            Func<RepoAccess, ReviewSnapshot?, string, DateTimeOffset, Task<RepoResult<string>>> reconcile,
            bool idempotent, CancellationToken ct)
        {
            payloadHash = Bound(payloadHash, snapshot);
            var now = clock.UtcNow;
            switch (ReviewTags.CheckKey(request.OpKey, now))
            {
                case KeyCheck.Malformed:
                    // A page never sends one: a malformed key is a hand-made request, worth a line.
                    log.Warn(LogCategory.Web, $"write {action} {who} {owner}/{repo}#{number} refused: malformed operation key");
                    return Invalid("op-key", "This request has no usable operation key.");
                case KeyCheck.Expired:
                    // Never re-run: the person submits again, deliberately, with a new key.
                    Audit(who, action, owner, repo, number, snapshot, request.OpKey!, "refused: operation key expired");
                    return Results.Json(new { error = "operation-expired", message = "This was submitted too long ago to retry. Check whether it was posted, then submit it again if not." }, statusCode: 422);
            }

            var key = request.OpKey!;
            var ledgerKey = LedgerKey(who, action, owner, repo, number, key);
            var reservation = await ledger.ReserveAsync(ledgerKey, payloadHash, now, ct).ConfigureAwait(false);
            if (!reservation.Fresh && reservation.Existing is { } existing)
            {
                if (existing.PayloadHash != payloadHash)
                {
                    Audit(who, action, owner, repo, number, snapshot, key, "refused: same key, different payload");
                    return Results.Json(new { error = "op-mismatch", message = "This retry does not match what was first submitted." }, statusCode: 422);
                }

                // A retry answered from the ledger is a write that did NOT go to GitHub again: said,
                // so a log reader counting posts against GitHub's own list finds them equal.
                switch (existing.State)
                {
                    case OperationState.Succeeded:
                        Audit(who, action, owner, repo, number, snapshot, key, "answered done from the ledger");
                        return Results.Ok(new { outcome = "done", id = existing.Result });
                    case OperationState.Pending when now - existing.Reserved < AbandonedAfter:
                        Audit(who, action, owner, repo, number, snapshot, key, "still in flight");
                        return Results.Json(new { outcome = "pending", message = "Still being posted." }, statusCode: 202);
                    case OperationState.Pending or OperationState.Unknown:
                        // Abandoned, or sent with no answer: find out before doing anything else.
                        Audit(who, action, owner, repo, number, snapshot, key, existing.State == OperationState.Pending ? "abandoned; reconciling" : "unknown earlier; reconciling");
                        var found = await ReconcileAsync(who, snapshot, key, existing, reconcile, ledgerKey, action, owner, repo, number).ConfigureAwait(false);
                        if (found is not null || !idempotent)
                        {
                            return found ?? UnknownResult();
                        }

                        break;
                }

                // Failed (nothing written), or an idempotent operation found undone: this request runs
                // it — if it is the one that takes the key over. Another retry may have read the same
                // entry; only one of them gets past here, the rest say "in progress".
                if (!await ledger.TryTakeOverAsync(ledgerKey, reservation, new OperationEntry(payloadHash, OperationState.Pending, null, now, now), ct).ConfigureAwait(false))
                {
                    Audit(who, action, owner, repo, number, snapshot, key, "another retry took the key over; still being posted");
                    return Results.Json(new { outcome = "pending", message = "Still being posted." }, statusCode: 202);
                }
            }

            if (precondition is not null && await precondition(who, snapshot).ConfigureAwait(false) is { } refusal)
            {
                await ledger.RecordAsync(ledgerKey, new OperationEntry(payloadHash, OperationState.Failed, null, now, clock.UtcNow), ct).ConfigureAwait(false);
                Audit(who, action, owner, repo, number, snapshot, key, "refused by a precondition");
                SettledIfKnown(http, reservation);
                return refusal;
            }

            // The call is not cancelled with the request: a person closing the tab must not turn a
            // write GitHub is already doing into an unknown one.
            var result = await write(who, snapshot, key).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case WriteOutcome.Done:
                    await ledger.RecordAsync(ledgerKey, new OperationEntry(payloadHash, OperationState.Succeeded, result.Value, now, clock.UtcNow), CancellationToken.None).ConfigureAwait(false);
                    Audit(who, action, owner, repo, number, snapshot, key, "done");
                    return Results.Ok(new { outcome = "done", id = result.Value });
                case WriteOutcome.Refused:
                    await ledger.RecordAsync(ledgerKey, new OperationEntry(payloadHash, OperationState.Failed, null, now, clock.UtcNow), CancellationToken.None).ConfigureAwait(false);
                    Audit(who, action, owner, repo, number, snapshot, key, $"refused by GitHub ({result.Failure})");
                    SettledIfKnown(http, reservation);
                    return RefusedByGitHub(result.Failure, result.Message, http);
                default:
                    var entry = new OperationEntry(payloadHash, OperationState.Unknown, null, now, clock.UtcNow);
                    await ledger.RecordAsync(ledgerKey, entry, CancellationToken.None).ConfigureAwait(false);
                    Audit(who, action, owner, repo, number, snapshot, key, "unknown; reconciling");
                    return await ReconcileAsync(who, snapshot, key, entry, reconcile, ledgerKey, action, owner, repo, number).ConfigureAwait(false) ?? UnknownResult();
            }
        }

        /// <summary>
        /// Looks for an uncertain write's effect. Found: recorded as succeeded and returned. Not found,
        /// or not knowable now: null, and the entry stays as it was.
        /// </summary>
        private async Task<IResult?> ReconcileAsync(
            RepoAccess who, ReviewSnapshot? snapshot, string key, OperationEntry entry,
            Func<RepoAccess, ReviewSnapshot?, string, DateTimeOffset, Task<RepoResult<string>>> reconcile,
            string ledgerKey, string action, string owner, string repo, int number)
        {
            var found = await reconcile(who, snapshot, key, entry.Reserved).ConfigureAwait(false);
            if (found.Ok && found.Value!.Length > 0)
            {
                await ledger.RecordAsync(ledgerKey, entry with { State = OperationState.Succeeded, Result = found.Value, Updated = clock.UtcNow }, CancellationToken.None).ConfigureAwait(false);
                Audit(who, action, owner, repo, number, snapshot, key, "reconciled: it was done");
                return Results.Ok(new { outcome = "done", id = found.Value, reconciled = true });
            }

            Audit(who, action, owner, repo, number, snapshot, key, found.Ok ? "reconciled: not found" : "reconciliation could not read GitHub");
            return null;
        }

        private static IResult UnknownResult() =>
            Results.Json(new { outcome = "unknown", message = "This may or may not have been posted. Refresh to check before posting it again." }, statusCode: 202);

        /// <summary>
        /// A comment on a drawn element's preconditions: the pull request still at this snapshot, the
        /// element really in the snapshot's drawing of that file, and where GitHub will take it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The drawing is rendered again here</b>, from the snapshot's SHAs, the same way the page
        /// was given it (rendered, unified — the anchors do not depend on the layout). It is cached,
        /// so this usually costs nothing. The page said only which element; what the element IS —
        /// its source layer, container, key, fingerprint, lines — comes from this rendering, so a
        /// page cannot place a comment on, or write a tag for, something it was not shown.
        /// </para>
        /// <para>
        /// <b>The comment goes on the element's own lines</b> that GitHub's diff shows, within one
        /// hunk, as a range ending on the last; when the diff shows none of them (an unchanged
        /// network, or a file with no patch) it goes on the whole file, and the tag still says which
        /// element. Never on a nearby line: on github.com that would read as a remark about whatever
        /// that line belongs to.
        /// </para>
        /// </remarks>
        internal async Task<(IResult? Refusal, (LineComment, Versio.Core.Review.SemanticAnchor)? Found)> AnchorPreconditionAsync(
            HttpContext http, RepoAccess who, ReviewSnapshot s, string path, string? from, string dom, string body, CancellationToken ct)
        {
            if (await StaleAsync(who, s, ct).ConfigureAwait(false) is { } stale)
            {
                return (stale, null);
            }

            var older = await load.ModelAsync(http, s.Owner, s.Repo, s.MergeBaseSha, ct).ConfigureAwait(false);
            var newer = older.Ok ? await load.ModelAsync(http, s.Owner, s.Repo, s.HeadSha, ct).ConfigureAwait(false) : older;
            if (!older.Ok || !newer.Ok)
            {
                return (ApiEndpoints.Refused(older.Ok ? newer.Failure : older.Failure), null);
            }

            var drawn = await views.CompareAsync(who, older.Value.Model, newer.Value.Model, path, Versio.Web.Rendering.PaneView.Rendered, false, ct, from).ConfigureAwait(false);
            // Exactly one element answers to the id. Two would be a renderer's defect, and a
            // refusal that says so beats the exception SingleOrDefault threw (a 500 with the
            // operation left pending in the ledger — review, 2026-10-24).
            var matches = drawn.Ok ? drawn.Value!.Anchors.Where(a => a.Dom == dom).Take(2).ToList() : [];
            if (matches.Count != 1)
            {
                log.Info(LogCategory.Web, matches.Count == 0
                    ? $"anchor comment refused: {dom} is not drawn in {s.Owner}/{s.Repo}#{s.Number} {path}"
                    : $"anchor comment refused: {dom} is drawn twice in {s.Owner}/{s.Repo}#{s.Number} {path}, which is a renderer's defect");
                return (Results.Json(new { error = "anchor-not-found", message = "That element is not in this version's drawing. Reload the file." }, statusCode: 422), null);
            }

            var anchor = matches[0];

            var side = anchor.Side == Versio.Core.Review.ReviewSide.New ? "RIGHT" : "LEFT";
            var semantic = new Versio.Core.Review.SemanticAnchor(anchor.Kind, anchor.SourcePath, anchor.Side,
                anchor.Side == Versio.Core.Review.ReviewSide.New ? s.HeadSha : s.MergeBaseSha, anchor.Container, anchor.Key, anchor.Fingerprint);
            var (patch, _) = await review.PatchForAsync(who, s, anchor.SourcePath, ct).ConfigureAwait(false);
            var at = patch is not null && anchor.Lines is { } span ? HunkLines.Parse(patch).Placement(side, span.Start, span.End) : null;
            var comment = at is { } place
                ? new LineComment("", anchor.SourcePath, place.Line, side, place.StartLine, body)
                : new LineComment("", anchor.SourcePath, null, side, null, body);
            return (null, (comment, semantic));
        }

        /// <summary>The pull request moved past the snapshot: 409, or the refusal that stopped the check.</summary>
        internal async Task<IResult?> StaleAsync(RepoAccess who, ReviewSnapshot s, CancellationToken ct)
        {
            var check = await review.CheckRevisionAsync(who, s, ct).ConfigureAwait(false);
            return check.Same ? null
                : check.Failure != RepoFailure.None ? ApiEndpoints.Refused(check.Failure)
                : Results.Json(new { error = "snapshot-stale", message = "This pull request changed after you opened it. Load the latest, then comment." }, statusCode: 409);
        }

        internal async Task<IResult?> LinePreconditionAsync(RepoAccess who, ReviewSnapshot s, LineComment comment, CancellationToken ct)
        {
            // Lines are positions in ONE revision. The comment is written against the snapshot's head
            // either way, but if the pull request moved, the reviewer is placing it on a drawing that
            // is no longer the pull request; they reload first. This narrows the race; GitHub's
            // commit_id is what pins the comment (C1, C2).
            if (await StaleAsync(who, s, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            if (comment.Line is not { } line)
            {
                return null;
            }

            var (patch, unavailable) = await review.PatchForAsync(who, s, comment.Path, ct).ConfigureAwait(false);
            if (patch is null)
            {
                return Results.Json(new { error = "inline-unavailable", reason = unavailable, message = "GitHub takes comments on this file only as a whole." }, statusCode: 422);
            }

            return HunkLines.Parse(patch).Accepts(comment.Side, line, comment.StartLine)
                ? null
                : Results.Json(new { error = "line-not-in-diff", message = "GitHub only takes comments on lines shown in its diff of this file." }, statusCode: 422);
        }

        /// <summary>S7: a node id names something in THIS pull request, or nothing happens.</summary>
        internal async Task<IResult?> OwnedAsync(RepoAccess who, ReviewSnapshot s, string nodeId, string[] types, CancellationToken ct) =>
            (await OwnerOfAsync(who, s, nodeId, types, ct).ConfigureAwait(false)).Refusal;

        /// <remarks>
        /// GraphQL node ids are global: a thread id from another repository resolves just as well, and
        /// the URL's repository — the one AccessResolver checked against the server's scope — says
        /// nothing about it. So the node is read first, and must belong to this repository and this
        /// pull request and be of the expected kind; otherwise "not found", exactly as for a node that
        /// does not exist, so nothing about it leaks.
        /// </remarks>
        internal async Task<(IResult? Refusal, NodeOwner? Node)> OwnerOfAsync(RepoAccess who, ReviewSnapshot s, string nodeId, string[] types, CancellationToken ct)
        {
            if (!NodeId().IsMatch(nodeId))
            {
                return (Results.NotFound(), null);
            }

            var node = await writer.NodeOwnerAsync(who, nodeId, ct).ConfigureAwait(false);
            if (!node.Ok || !types.Contains(node.Value!.Type)
                || !string.Equals(node.Value.Repository, $"{s.Owner}/{s.Repo}", StringComparison.OrdinalIgnoreCase) || node.Value.Number != s.Number)
            {
                log.Warn(LogCategory.Web, $"write refused (S7: node {nodeId} is not in {s.Owner}/{s.Repo}#{s.Number})");
                return (Results.Json(new { error = "not-found", message = "Not found, or not visible to you." }, statusCode: 404), null);
            }

            return (null, node.Value);
        }

        /// <summary>The audit line of every write (S10): who, what, where, at which revision, how it ended. Never a body, never a token.</summary>
        private void Audit(RepoAccess who, string action, string owner, string repo, int number, ReviewSnapshot? s, string key, string outcome) =>
            log.Info(LogCategory.Web, $"write {action} {who} {owner}/{repo}#{number} head {(s is null ? "-" : s.HeadSha[..8])} op {key[..Math.Min(key.Length, 13)]}… {outcome}");
    }

    /// <summary>A writer's bool result as the id-shaped result <see cref="Writes.RunAsync"/> records.</summary>
    private static WriteResult<string> Done(WriteResult<bool> result, string id) =>
        new(result.Outcome, result.Outcome == WriteOutcome.Done ? id : null, result.Failure, result.Message);

    /// <summary>A definitive state read as a reconciliation answer: the id when the state shows it done, empty when not.</summary>
    private static RepoResult<string> Reconciled<T>(RepoResult<T> read, Func<T, bool> done, string id) =>
        read.Ok ? RepoResult<string>.Of(done(read.Value!) ? id : "") : read.As<string>();

    private static string? Body(string? body) =>
        body is null || string.IsNullOrWhiteSpace(ReviewTags.Strip(body)) || body.Length > MaxBodyChars ? null : body;

    private static IResult Invalid(string field, string message) => Results.Json(new { error = "invalid", field, message }, statusCode: 400);

    /// <summary>GitHub's refusal, its own words spelled (S8): a 403 here is often the App missing a permission, and its message says which.</summary>
    private static IResult RefusedByGitHub(RepoFailure failure, string? message, HttpContext http) => failure switch
    {
        RepoFailure.Unauthorized or RepoFailure.SsoRequired or RepoFailure.RateLimited => ApiEndpoints.Refused(failure, http),
        RepoFailure.NotFound => Results.Json(new { error = "not-found", message = message is null ? "Not found, or not visible to you." : "GitHub: " + CodeHtml.Spelled(message) }, statusCode: 404),
        _ => Results.Json(new { error = "github-refused", message = "GitHub refused this" + (message is null ? "." : ": " + CodeHtml.Spelled(message)) }, statusCode: 422),
    };

    [GeneratedRegex("^[A-Za-z0-9_=-]{4,128}$")]
    private static partial Regex NodeId();

    /// <summary>The anchor ids renderers emit: a kind letter, a side, and an index or a number.</summary>
    [GeneratedRegex("^[rn]-(new|old)-[0-9]{1,6}$")]
    private static partial Regex AnchorDom();
}
