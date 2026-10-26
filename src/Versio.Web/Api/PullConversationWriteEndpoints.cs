using Versio.Core.Logging;
using Versio.Web.Review;
using Versio.Web.Sources;

namespace Versio.Web.Api;

/// <param name="Subject">The node reacted to: a comment, a review, or the pull request itself (its description).</param>
/// <param name="Content">THUMBS_UP, THUMBS_DOWN, LAUGH, HOORAY, CONFUSED, HEART, ROCKET or EYES.</param>
/// <param name="On">True adds the viewer's reaction; false takes it away.</param>
public sealed record ReactionRequest(string? OpKey, string? Subject, string? Content, bool On) : WriteRequest(OpKey);

/// <param name="Classifier">Why the comment is hidden (SPAM, ABUSE, OFF_TOPIC, OUTDATED, DUPLICATE, RESOLVED); null shows it again.</param>
public sealed record MinimizeRequest(string? OpKey, string? Classifier) : WriteRequest(OpKey);

/// <param name="Reason">OFF_TOPIC, TOO_HEATED, RESOLVED or SPAM; null for no stated reason.</param>
public sealed record LockRequest(string? OpKey, bool Lock, string? Reason) : WriteRequest(OpKey);

/// <param name="Number">The milestone's number; null clears it.</param>
public sealed record MilestoneRequest(string? OpKey, int? Number) : WriteRequest(OpKey);

/// <param name="State">SUBSCRIBED, UNSUBSCRIBED or IGNORED: the viewer's own notifications, nobody else's.</param>
public sealed record SubscriptionRequest(string? OpKey, string? State) : WriteRequest(OpKey);

/// <summary>
/// The conversation's other writes (docs/web/FINISH.md, Step 5): reactions, hiding a comment, locking
/// the conversation, the milestone, the viewer's notifications. Every one through <see cref="MapWrite"/>
/// and the operation ledger, as every write is; every one safe to repeat, so an uncertain outcome is
/// read back and, if undone, done again.
/// </summary>
/// <remarks>
/// <para>
/// <b>A reaction's subject must be in this pull request</b> (S7, as for a reply): GraphQL node ids
/// are global, and a reaction on a comment of some other repository would go through just as well.
/// The subject is read first and must be a comment, a review or the pull request of this URL.
/// </para>
/// <para>
/// <b>A milestone is put on, never made</b>, as labels are: it must be one the repository has, or
/// GitHub's own refusal would be the first anyone hears of a typo.
/// </para>
/// </remarks>
public static partial class PullWriteEndpoints
{
    private static readonly string[] ReactionWords = ["THUMBS_UP", "THUMBS_DOWN", "LAUGH", "HOORAY", "CONFUSED", "HEART", "ROCKET", "EYES"];

    private static readonly string[] MinimizeWords = ["SPAM", "ABUSE", "OFF_TOPIC", "OUTDATED", "DUPLICATE", "RESOLVED"];

    private static readonly string[] LockWords = ["OFF_TOPIC", "TOO_HEATED", "RESOLVED", "SPAM"];

    private static readonly string[] SubscriptionWords = ["SUBSCRIBED", "UNSUBSCRIBED", "IGNORED"];

    /// <summary>What may carry a reaction: the pull request's own body, a conversation comment, a review's summary, a line comment.</summary>
    private static readonly string[] Reactable = ["PullRequest", "IssueComment", "PullRequestReview", "PullRequestReviewComment"];

    private static void MapConversation(RouteGroupBuilder pulls)
    {
        pulls.MapWrite("POST", "/reactions", async (string owner, string repo, int number, ReactionRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (request.Content is null || !ReactionWords.Contains(request.Content) || request.Subject is null)
            {
                return Invalid("reaction", "Choose one of GitHub's reactions.");
            }

            var (subject, content) = (request.Subject, request.Content);
            return await w.RunAsync(http, owner, repo, number, request, request.On ? "react" : "unreact", ReviewTags.PayloadHash(subject, content, request.On ? "1" : "0"),
                precondition: (who, s) => w.OwnedAsync(who, s, subject, Reactable, ct),
                async (who, s, key) => Done(await w.Writer.SetReactionAsync(who, subject, content, request.On, CancellationToken.None).ConfigureAwait(false), subject),
                // The viewer's reaction is there or it is not: read back, that settles it.
                async (who, s, key, since) => Reconciled(await w.Writer.ViewerReactedAsync(who, subject, content, ct).ConfigureAwait(false), r => r == request.On, subject),
                idempotent: true, ct).ConfigureAwait(false);
        });

        pulls.MapWrite("POST", "/comments/{commentId}/minimize", async (string owner, string repo, int number, string commentId, MinimizeRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (request.Classifier is { } c && !MinimizeWords.Contains(c))
            {
                return Invalid("classifier", "Say why the comment is hidden: spam, abuse, off topic, outdated, duplicate or resolved.");
            }

            var hide = request.Classifier is not null;
            return await w.RunAsync(http, owner, repo, number, request, hide ? "minimize" : "unminimize", ReviewTags.PayloadHash(commentId, request.Classifier ?? "\0"),
                precondition: (who, s) => w.OwnedAsync(who, s, commentId, ["IssueComment", "PullRequestReviewComment"], ct),
                async (who, s, key) => Done(await w.Writer.SetMinimizedAsync(who, commentId, request.Classifier, CancellationToken.None).ConfigureAwait(false), commentId),
                async (who, s, key, since) => Reconciled(await w.Writer.CommentMinimizedAsync(who, commentId, ct).ConfigureAwait(false), m => m == hide, commentId),
                idempotent: true, ct).ConfigureAwait(false);
        });

        pulls.MapWrite("POST", "/lock", async (string owner, string repo, int number, LockRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (request.Reason is { } reason && !LockWords.Contains(reason))
            {
                return Invalid("reason", "A lock's reason is off topic, too heated, resolved or spam.");
            }

            string? node = null;
            return await w.RunAsync(http, owner, repo, number, request, request.Lock ? "lock" : "unlock", ReviewTags.PayloadHash(request.Lock ? "lock" : "unlock", request.Reason ?? "\0"),
                precondition: async (who, s) =>
                {
                    var (refusal, live) = await w.LiveAsync(who, s, ct).ConfigureAwait(false);
                    node = live?.NodeId;
                    return refusal ?? (node is { Length: > 0 } ? null : ApiEndpoints.Refused(RepoFailure.Upstream));
                },
                async (who, s, key) => Done(await w.Writer.SetLockedAsync(who, node!, request.Lock, request.Reason, CancellationToken.None).ConfigureAwait(false), "lock"),
                async (who, s, key, since) => Reconciled(await w.Source.GetPullSummaryAsync(who, owner, repo, number, ct).ConfigureAwait(false), live => live.Locked == request.Lock, "lock"),
                idempotent: true, ct).ConfigureAwait(false);
        });

        pulls.MapWrite("POST", "/milestone", async (string owner, string repo, int number, MilestoneRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (request.Number is < 1)
            {
                return Invalid("milestone", "That is not a milestone of this repository.");
            }

            return await w.RunAsync(http, owner, repo, number, request, "milestone", ReviewTags.PayloadHash(request.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "\0"),
                precondition: (who, s) => w.MilestonePreconditionAsync(who, s, request.Number, ct),
                async (who, s, key) => Done(await w.Writer.SetMilestoneAsync(who, owner, repo, number, request.Number, CancellationToken.None).ConfigureAwait(false), "milestone"),
                async (who, s, key, since) => Reconciled(await w.Source.GetPullSummaryAsync(who, owner, repo, number, ct).ConfigureAwait(false), live => live.MilestoneNumber == request.Number, "milestone"),
                idempotent: true, ct).ConfigureAwait(false);
        });

        pulls.MapWrite("POST", "/subscription", async (string owner, string repo, int number, SubscriptionRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (request.State is null || !SubscriptionWords.Contains(request.State))
            {
                return Invalid("state", "Subscribe, unsubscribe or ignore.");
            }

            var state = request.State;
            string? node = null;
            return await w.RunAsync(http, owner, repo, number, request, "subscription", ReviewTags.PayloadHash(state),
                precondition: async (who, s) =>
                {
                    var (refusal, live) = await w.LiveAsync(who, s, ct).ConfigureAwait(false);
                    node = live?.NodeId;
                    return refusal ?? (node is { Length: > 0 } ? null : ApiEndpoints.Refused(RepoFailure.Upstream));
                },
                async (who, s, key) => Done(await w.Writer.SetSubscriptionAsync(who, node!, state, CancellationToken.None).ConfigureAwait(false), "subscription"),
                async (who, s, key, since) => Reconciled(await w.Source.GetPullSummaryAsync(who, owner, repo, number, ct).ConfigureAwait(false), live => live.ViewerSubscription == state, "subscription"),
                idempotent: true, ct).ConfigureAwait(false);
        });
    }

    public sealed partial class Writes
    {
        /// <summary>A milestone put on must exist here: GitHub answers 422 for one that does not, but only after the page said it worked for a moment.</summary>
        internal async Task<IResult?> MilestonePreconditionAsync(RepoAccess who, ReviewSnapshot s, int? number, CancellationToken ct)
        {
            if (number is not { } wanted)
            {
                return null;
            }

            var milestones = await Source.GetMilestonesAsync(who, s.Owner, s.Repo, ct).ConfigureAwait(false);
            if (!milestones.Ok)
            {
                return ApiEndpoints.Refused(milestones.Failure);
            }

            if (milestones.Value!.Truncated)
            {
                // A cut list cannot say a milestone does not exist: GitHub decides then (review, 2026-10-24).
                Log.Info(LogCategory.Web, $"open milestones of {s.Owner}/{s.Repo} are more than {GitHubRepoSource.ListPages * 100}: the write is not gated on the list");
                return null;
            }

            if (milestones.Value.Milestones.All(m => m.Number != wanted))
            {
                Log.Info(LogCategory.Web, $"milestone refused: {s.Owner}/{s.Repo} has no open milestone {wanted}");
                return Results.Json(new { error = "milestone-not-found", message = "This repository has no open milestone by that number." }, statusCode: 422);
            }

            return null;
        }
    }
}
