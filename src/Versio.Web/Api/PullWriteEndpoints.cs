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
            return await w.RunAsync(http, owner, repo, number, request, "edit", ReviewTags.PayloadHash(commentId, body),
                precondition: async (who, s) =>
                {
                    var (refusal, node) = await w.OwnerOfAsync(who, s, commentId, ["IssueComment", "PullRequestReviewComment"], ct).ConfigureAwait(false);
                    target = node;
                    return refusal;
                },
                async (who, s, key) => Done(await w.Writer.EditCommentAsync(who, commentId, target!.Type, ReviewTags.WithOperation(body, key), CancellationToken.None).ConfigureAwait(false), commentId),
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
            if (!ApiEndpoints.Valid(owner, repo) || !options.Value.Writable(owner, repo))
            {
                return Results.NotFound();
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
                return Results.Json(new { error = "too-large", message = "That is too long to post." }, statusCode: 413);
            }

            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            {
                size.MaxRequestBodySize = max;
            }

            // S1: a person's own token, nothing else. Not the installation token, not anonymous, not
            // (when it exists) an Okta session without GitHub: a review must be in somebody's name.
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
    public sealed class Writes(IRepoWriter writer, IRepoSource source, IOperationLedger ledger, ReviewSnapshots snapshots, PullReview review, IClock clock, IVersioLogger log)
    {
        public IRepoWriter Writer => writer;

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
            var (snapshot, refused) = snapshots.Open(http.Request.Headers[PullEndpoints.SnapshotHeader].ToString(), owner, repo, number, PullEndpoints.Viewer(who));
            if (snapshot is null)
            {
                return refused == SnapshotRefusal.Expired
                    ? Results.Json(new { error = "snapshot-expired", message = "This review was loaded too long ago. Reload the pull request." }, statusCode: 409)
                    : Results.Json(new { error = "snapshot-invalid", message = "This review's revision could not be read. Reload the pull request." }, statusCode: 400);
            }

            var now = clock.UtcNow;
            switch (ReviewTags.CheckKey(request.OpKey, now))
            {
                case KeyCheck.Malformed:
                    return Invalid("op-key", "This request has no usable operation key.");
                case KeyCheck.Expired:
                    // Never re-run: the person submits again, deliberately, with a new key.
                    return Results.Json(new { error = "operation-expired", message = "This was submitted too long ago to retry. Check whether it was posted, then submit it again if not." }, statusCode: 422);
            }

            var key = request.OpKey!;
            var ledgerKey = string.Join('|', PullEndpoints.Viewer(who), action, $"{owner}/{repo}".ToLowerInvariant(), number, key);
            var reservation = await ledger.ReserveAsync(ledgerKey, payloadHash, now, ct).ConfigureAwait(false);
            if (!reservation.Fresh && reservation.Existing is { } existing)
            {
                if (existing.PayloadHash != payloadHash)
                {
                    Audit(who, action, owner, repo, number, snapshot, key, "refused: same key, different payload");
                    return Results.Json(new { error = "op-mismatch", message = "This retry does not match what was first submitted." }, statusCode: 422);
                }

                switch (existing.State)
                {
                    case OperationState.Succeeded:
                        return Results.Ok(new { outcome = "done", id = existing.Result });
                    case OperationState.Pending when now - existing.Reserved < AbandonedAfter:
                        return Results.Json(new { outcome = "pending", message = "Still being posted." }, statusCode: 202);
                    case OperationState.Pending or OperationState.Unknown:
                        // Abandoned, or sent with no answer: find out before doing anything else.
                        var found = await ReconcileAsync(who, snapshot, key, existing, reconcile, ledgerKey, action, owner, repo, number).ConfigureAwait(false);
                        if (found is not null || !idempotent)
                        {
                            return found ?? UnknownResult();
                        }

                        break;
                }

                // Failed (nothing written), or an idempotent operation found undone: this request runs it.
                await ledger.RecordAsync(ledgerKey, new OperationEntry(payloadHash, OperationState.Pending, null, now, now), ct).ConfigureAwait(false);
            }

            if (precondition is not null && await precondition(who, snapshot).ConfigureAwait(false) is { } refusal)
            {
                await ledger.RecordAsync(ledgerKey, new OperationEntry(payloadHash, OperationState.Failed, null, now, clock.UtcNow), ct).ConfigureAwait(false);
                Audit(who, action, owner, repo, number, snapshot, key, "refused by a precondition");
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
            RepoAccess who, ReviewSnapshot snapshot, string key, OperationEntry entry,
            Func<RepoAccess, ReviewSnapshot, string, DateTimeOffset, Task<RepoResult<string>>> reconcile,
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

        /// <summary>A line comment's preconditions: the pull request still at this snapshot, and a line GitHub will take.</summary>
        internal async Task<IResult?> LinePreconditionAsync(RepoAccess who, ReviewSnapshot s, LineComment comment, CancellationToken ct)
        {
            // Lines are positions in ONE revision. The comment is written against the snapshot's head
            // either way, but if the pull request moved, the reviewer is placing it on a drawing that
            // is no longer the pull request; they reload first. This narrows the race; GitHub's
            // commit_id is what pins the comment (C1, C2).
            var pull = await source.GetPullAsync(who, s.Owner, s.Repo, s.Number, ct).ConfigureAwait(false);
            var baseTip = pull.Ok ? await source.ResolveCommitAsync(who, s.Owner, s.Repo, pull.Value!.Pull.BaseRef, ct).ConfigureAwait(false) : null;
            if (!pull.Ok || baseTip is not { Ok: true } || pull.Value!.Pull.HeadSha != s.HeadSha || pull.Value.Pull.BaseRef != s.BaseRef || baseTip.Value != s.BaseSha)
            {
                return pull.Ok && baseTip is { Ok: true }
                    ? Results.Json(new { error = "snapshot-stale", message = "This pull request changed after you opened it. Load the latest, then comment." }, statusCode: 409)
                    : ApiEndpoints.Refused(pull.Ok ? baseTip!.Failure : pull.Failure);
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
        private void Audit(RepoAccess who, string action, string owner, string repo, int number, ReviewSnapshot s, string key, string outcome) =>
            log.Info(LogCategory.Web, $"write {action} {who} {owner}/{repo}#{number} head {s.HeadSha[..8]} op {key[..Math.Min(key.Length, 13)]}… {outcome}");
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
}
