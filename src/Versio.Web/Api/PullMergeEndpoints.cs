using Versio.Core.Logging;
using Versio.Web.Review;
using Versio.Web.Sources;

namespace Versio.Web.Api;

/// <summary>Marks a write as the merge tier (S11): it exists only with <c>Web:Writes:Merge</c> on.</summary>
public sealed class MergeEndpoint;

/// <param name="Method">MERGE, SQUASH or REBASE: one the repository allows.</param>
/// <param name="Title">The merge commit's title; null for GitHub's own.</param>
/// <param name="Message">The merge commit's message; null for GitHub's own.</param>
public sealed record MergeRequest(string? OpKey, string? Method, string? Title, string? Message) : WriteRequest(OpKey);

/// <param name="AcceptsLaterCommits">
/// The person's own yes to what auto-merge is: a merge of whatever the branch holds when the
/// requirements pass, which can include commits pushed after their review. Required to turn it on.
/// </param>
public sealed record AutoMergeRequest(string? OpKey, bool Enable, string? Method, bool AcceptsLaterCommits = false) : WriteRequest(OpKey);

/// <summary>
/// Merging (docs/web/PLAN.md, *Pull request review*, slice 6, S11): merge, squash or rebase;
/// bringing the branch up to date; auto-merge. A separate tier, <c>Web:Writes:Merge</c>, off by
/// default, because these are the only writes that need Contents write and the only ones that
/// change a branch.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is merged now is what was reviewed.</b> The merge carries the snapshot's head as
/// GitHub's atomic <c>sha</c> precondition, update-branch its <c>expected_head_sha</c>. The server's
/// own staleness check before each (head AND base, C1) narrows the window; GitHub's precondition
/// closes it.
/// </para>
/// <para>
/// <b>Auto-merge is a different authorization, and is asked for as one.</b> It is a later merge of
/// whatever the branch holds when the requirements pass. <c>expectedHeadOid</c> only makes GitHub
/// refuse to turn it on if the head already moved; afterwards GitHub turns it off for a push by
/// someone WITHOUT write access, not for one by someone with it, whose commits are merged too. So it
/// is not "a merge of this commit" — the first version said it was, on the page and here, and that
/// was wrong (found by review, 2026-10-20). Turning it on requires the person's explicit yes to
/// exactly that (<see cref="AutoMergeRequest.AcceptsLaterCommits"/>). The guard that does pin a
/// later commit to a fresh review is the repository's own: a required approval with "Dismiss stale
/// pull request approvals when new commits are pushed".
/// </para>
/// <para>
/// <b>No admin bypass, ever.</b> GitHub lets an administrator merge through the API past required
/// reviews and checks. Versio merges only when GitHub says the pull request can be merged by
/// anyone — <c>mergeStateStatus</c> CLEAN, UNSTABLE (only checks that are not required failed) or
/// HAS_HOOKS — and refuses BLOCKED, BEHIND, DIRTY, DRAFT and UNKNOWN for everybody, admins
/// included. When GitHub cannot say, nothing is merged.
/// </para>
/// <para>
/// <b>Branch deletion is not here, on purpose.</b> Deleting a ref takes no expected commit, so a
/// read-then-delete could delete work pushed in between. The repository's "Automatically delete
/// head branches" setting does it without that race.
/// </para>
/// </remarks>
public static partial class PullWriteEndpoints
{
    private static readonly string[] MergeMethods = ["MERGE", "SQUASH", "REBASE"];

    /// <summary>The states anyone may merge in: no protection is passed over, for an admin or anyone else.</summary>
    internal static readonly string[] MergeableStates = ["CLEAN", "UNSTABLE", "HAS_HOOKS"];

    private static void MapMerge(RouteGroupBuilder pulls)
    {
        pulls.MapWrite("POST", "/merge", async (string owner, string repo, int number, MergeRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (request.Method is null || !MergeMethods.Contains(request.Method))
            {
                return Invalid("method", "Choose merge, squash or rebase.");
            }

            if ((request.Title is { } t && (t.Length > MaxTitleChars || t.Any(char.IsControl))) || request.Message is { Length: > MaxBodyChars })
            {
                return Invalid("message", "That commit message is too long.");
            }

            return await w.RunAsync(http, owner, repo, number, request, "merge", ReviewTags.PayloadHash(request.Method, request.Title ?? "\0", request.Message ?? "\0"),
                precondition: (who, s) => w.MergePreconditionAsync(who, s, request.Method, autoMerge: false, ct),
                (who, s, key) => w.Writer.MergePullAsync(who, owner, repo, number, s.HeadSha, request.Method, request.Title, request.Message, CancellationToken.None),
                // Merged, and at the reviewed head: then this was the merge. Repeating it is safe
                // anyway — GitHub's sha precondition refuses a second one.
                async (who, s, key, since) =>
                {
                    var state = await w.Source.GetMergeStateAsync(who, owner, repo, number, ct).ConfigureAwait(false);
                    return state.Ok
                        ? RepoResult<string>.Of(state.Value!.Merged && state.Value.HeadSha == s.HeadSha ? state.Value.MergeCommit ?? "merged" : "")
                        : state.As<string>();
                },
                idempotent: true, ct).ConfigureAwait(false);
        }).WithMetadata(new MergeEndpoint());

        pulls.MapWrite("POST", "/update-branch", async (string owner, string repo, int number, WriteRequest request, HttpContext http, Writes w, CancellationToken ct) =>
            await w.RunAsync(http, owner, repo, number, request, "update-branch", ReviewTags.PayloadHash("update"),
                precondition: async (who, s) =>
                {
                    // The confirmation named a base: brought in only if it is still that base, at that
                    // commit. Without this, a base retargeted after the page opened was merged into the
                    // branch under a confirmation that named the old one (found by review, 2026-10-20).
                    // GitHub's expected_head_sha guards the head; this guards the base.
                    if (await w.StaleAsync(who, s, ct).ConfigureAwait(false) is { } stale)
                    {
                        return stale;
                    }

                    var (refusal, state) = await w.MergeStateAsync(who, s, ct).ConfigureAwait(false);
                    return refusal ?? (state!.State != "OPEN"
                        ? Results.Json(new { error = "not-open", message = "Only an open pull request's branch is brought up to date." }, statusCode: 409)
                        : null);
                },
                async (who, s, key) => Done(await w.Writer.UpdateBranchAsync(who, owner, repo, number, s.HeadSha, CancellationToken.None).ConfigureAwait(false), "updated"),
                // Done only when the head is GitHub's update: a merge commit whose parents are the
                // reviewed head and the base. "The head moved" alone also matches someone else's push
                // with GitHub refusing this update — reported as "updated" when it was not (found in
                // review, 2026-10-20). A merge of the base made by hand reads the same, and its effect
                // is the same: the branch is up to date with its base.
                async (who, s, key, since) =>
                {
                    var live = await w.Source.GetPullSummaryAsync(who, owner, repo, number, ct).ConfigureAwait(false);
                    if (!live.Ok)
                    {
                        return live.As<string>();
                    }

                    if (live.Value!.HeadSha == s.HeadSha)
                    {
                        return RepoResult<string>.Of("");
                    }

                    var parents = await w.Source.GetCommitParentsAsync(who, owner, repo, live.Value.HeadSha, ct).ConfigureAwait(false);
                    // Both parents: the reviewed head first, and the base second — the commit the
                    // precondition pinned (the snapshot's base tip), or the base's live tip should it
                    // have moved since. A merge of an unrelated commit onto the head has the same shape
                    // and is not this update (found by review, 2026-10-20).
                    return parents.Ok
                        ? RepoResult<string>.Of(parents.Value!.Count == 2 && parents.Value[0] == s.HeadSha
                            // The live tip only of the SAME base: after a retarget, a merge of the new
                            // base is not the update that was confirmed (found in review, 2026-10-21).
                            && (parents.Value[1] == s.BaseSha || (live.Value.BaseRef == s.BaseRef && parents.Value[1] == live.Value.BaseTipSha)) ? "updated" : "")
                        : parents.As<string>();
                },
                idempotent: true, ct).ConfigureAwait(false)).WithMetadata(new MergeEndpoint());

        pulls.MapWrite("POST", "/auto-merge", async (string owner, string repo, int number, AutoMergeRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (request.Enable && (request.Method is null || !MergeMethods.Contains(request.Method)))
            {
                return Invalid("method", "Choose merge, squash or rebase.");
            }

            if (request.Enable && !request.AcceptsLaterCommits)
            {
                return Invalid("consent", "Auto-merge can merge commits pushed after your review. Confirm that, or merge now instead.");
            }

            string? node = null;
            return await w.RunAsync(http, owner, repo, number, request, request.Enable ? "auto-merge-on" : "auto-merge-off", ReviewTags.PayloadHash(request.Enable ? request.Method! : "off"),
                precondition: async (who, s) =>
                {
                    if (request.Enable)
                    {
                        var refused = await w.MergePreconditionAsync(who, s, request.Method!, autoMerge: true, ct).ConfigureAwait(false);
                        if (refused is not null)
                        {
                            return refused;
                        }
                    }

                    var (refusal, state) = await w.MergeStateAsync(who, s, ct).ConfigureAwait(false);
                    node = state?.NodeId;
                    return refusal ?? (!request.Enable && !state!.ViewerCanDisableAutoMerge
                        ? Results.Json(new { error = "auto-merge-off", message = "Auto-merge is not on, or not yours to turn off." }, statusCode: 409)
                        : null);
                },
                async (who, s, key) => Done(request.Enable
                    ? await w.Writer.EnableAutoMergeAsync(who, node!, request.Method!, s.HeadSha, CancellationToken.None).ConfigureAwait(false)
                    : await w.Writer.DisableAutoMergeAsync(who, node!, CancellationToken.None).ConfigureAwait(false), "auto-merge"),
                async (who, s, key, since) =>
                {
                    var state = await w.Source.GetMergeStateAsync(who, owner, repo, number, ct).ConfigureAwait(false);
                    return state.Ok ? RepoResult<string>.Of((state.Value!.AutoMergeMethod is not null) == request.Enable ? "auto-merge" : "") : state.As<string>();
                },
                idempotent: true, ct).ConfigureAwait(false);
        }).WithMetadata(new MergeEndpoint());
    }

    /// <summary>What a refused merge state means, in words: why nobody merges now.</summary>
    internal static string MergeBlocked(string status) => status switch
    {
        "BLOCKED" => "Required reviews or checks have not passed. Versio does not merge past them, for anyone.",
        "BEHIND" => "The branch must be brought up to date with its base first.",
        "DIRTY" => "The branch has conflicts with its base.",
        "DRAFT" => "This is a draft. Mark it ready for review first.",
        _ => "GitHub cannot say yet whether this can be merged. Try again in a moment.",
    };

    public sealed partial class Writes
    {
        /// <summary>The merge state, read for a precondition; a refusal when it cannot be read — failing closed.</summary>
        internal async Task<(IResult? Refusal, MergeState? State)> MergeStateAsync(RepoAccess who, ReviewSnapshot s, CancellationToken ct)
        {
            var state = await Source.GetMergeStateAsync(who, s.Owner, s.Repo, s.Number, ct).ConfigureAwait(false);
            return state.Ok ? (null, state.Value) : (ApiEndpoints.Refused(state.Failure), null);
        }

        /// <summary>
        /// A merge, now or later, needs: the snapshot still the pull request's (head and base), the
        /// pull request open, the method allowed here, and — for a merge now — a state anyone could
        /// merge in. Auto-merge waits for that state by design, so it needs the repository to allow it.
        /// </summary>
        internal async Task<IResult?> MergePreconditionAsync(RepoAccess who, ReviewSnapshot s, string method, bool autoMerge, CancellationToken ct)
        {
            if (await StaleAsync(who, s, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var (refusal, state) = await MergeStateAsync(who, s, ct).ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }

            if (state!.Merged || state.State != "OPEN")
            {
                return Results.Json(new { error = "not-open", message = "This pull request is not open." }, statusCode: 409);
            }

            if (!state.Methods.Contains(method))
            {
                return Results.Json(new { error = "method-not-allowed", message = "This repository does not allow that kind of merge." }, statusCode: 422);
            }

            if (autoMerge)
            {
                return state.AutoMergeAllowed && state.ViewerCanEnableAutoMerge
                    ? null
                    : Results.Json(new { error = "auto-merge-unavailable", message = "Auto-merge is not allowed here, or not for you." }, statusCode: 422);
            }

            if (!MergeableStates.Contains(state.MergeStateStatus))
            {
                Log.Info(LogCategory.Web, $"merge refused: {s.Owner}/{s.Repo}#{s.Number} is {state.MergeStateStatus}");
                return Results.Json(new { error = "not-mergeable", state = state.MergeStateStatus, message = MergeBlocked(state.MergeStateStatus) }, statusCode: 409);
            }

            return null;
        }
    }
}
