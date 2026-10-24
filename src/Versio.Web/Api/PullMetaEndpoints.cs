using System.Globalization;
using Versio.Core.Diff;
using Versio.Core.Logging;
using Versio.Web.Review;
using Versio.Web.Sources;

namespace Versio.Web.Api;

/// <param name="Base">The branch it merges into.</param>
/// <param name="Head">A branch of the same repository: a fork's branch cannot be named here (PLAN.md, slice 5).</param>
public sealed record CreatePullRequest(string? OpKey, string? Base, string? Head, string? Title, string? Body, bool Draft) : WriteRequest(OpKey);

/// <summary>An edit of the title, the description or the base; each null to leave it.</summary>
/// <param name="ExpectTitle">The title the person edited from, as the page showed it: if it changed since, the edit is refused rather than overwriting someone else's.</param>
/// <param name="ExpectBody">Likewise the description.</param>
public sealed record EditPullRequest(string? OpKey, string? Title, string? Body, string? Base, string? ExpectTitle, string? ExpectBody) : WriteRequest(OpKey);

public sealed record OpenRequest(string? OpKey, bool Open) : WriteRequest(OpKey);

public sealed record DraftRequest(string? OpKey, bool Draft) : WriteRequest(OpKey);

/// <summary>Labels (by name) or assignees (by login) to add and remove.</summary>
public sealed record NamesRequest(string? OpKey, string[]? Add, string[]? Remove) : WriteRequest(OpKey);

/// <summary>
/// Opening pull requests and editing what they say about themselves (docs/web/PLAN.md, *Pull request
/// review*, slice 5): the title, the description and the base, closing and reopening, draft and
/// ready, labels and assignees. Every one through <see cref="MapWrite"/> and the operation ledger,
/// exactly as comments and reviews are.
/// </summary>
/// <remarks>
/// <para>
/// <b>Losing someone's words is the failure that matters, so an edit names what it replaces.</b>
/// GitHub's PATCH has no precondition: it overwrites whatever is there. The page sends the title and
/// description it edited from, and the server refuses (409) when either changed since — someone else
/// edited it meanwhile. That narrows the race to the moment between this read and GitHub's write; it
/// cannot close it, and does not claim to. A base change is held to the same rule: it must still be
/// the base the person was looking at.
/// </para>
/// <para>
/// <b>A base change makes the review's snapshot stale</b> (the base is part of it, C1), so the page
/// says "this pull request changed" and offers the reload, as for any push. Nothing about a base
/// change is reviewed against the old one.
/// </para>
/// <para>
/// <b>Opening one has no snapshot</b>, because there is no pull request yet; its uncertain outcome is
/// reconciled by finding the viewer's own pull request between those branches whose description
/// carries the operation tag, so a lost answer never opens a second one.
/// </para>
/// </remarks>
public static partial class PullWriteEndpoints
{
    /// <summary>GitHub's cap on a pull request title.</summary>
    internal const int MaxTitleChars = 256;

    /// <summary>At most this many names in one label or assignee change: a page has no reason to send more.</summary>
    private const int MaxNames = 20;

    private static void MapCreate(WebApplication app)
    {
        var repos = app.MapGroup("/api/repos/{owner}/{repo}").RequireRateLimiting(WebHost.RatePolicy);
        // What a comment will look like, for the new pull request's description: the same Core
        // Markdown as the review page's preview, before there is a pull request to put it under.
        repos.MapWrite("POST", "/markdown", (PreviewRequest request) =>
            Results.Ok(new { html = request.Body is { Length: > 0 and <= MaxBodyChars } b ? ApiEndpoints.MarkdownFragment(ReviewTags.Strip(b)) : "" }));

        // A comment on a commit (Step 3b): no pull request, so no snapshot — the commit's sha is
        // the revision, fixed by nature, and the ledger keys it under pull request 0 with the sha
        // in the payload. Reconciled by the viewer's own tagged comment on that commit.
        repos.MapWrite("POST", "/commits/{sha}/comments", async (string owner, string repo, string sha, CommentRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (sha is not { Length: 40 } || !sha.All(Uri.IsHexDigit))
            {
                return Invalid("sha", "Name the commit by its full sha.");
            }

            if (Body(request.Body) is not { } body)
            {
                return Invalid("body", "A comment needs some text.");
            }

            return await w.RunWithoutSnapshotAsync(http, owner, repo, request, "commit-comment", ReviewTags.PayloadHash(sha, body),
                precondition: async who =>
                {
                    // The audit line below names no revision for a write without a snapshot (it says
                    // "#0 head -"); this one says which commit, so the two lines together answer
                    // "was my comment posted, and where" (S10).
                    w.Log.Info(LogCategory.Web, $"write commit-comment {who} {owner}/{repo} on commit {sha[..8]}");
                    var resolved = await w.Source.ResolveCommitAsync(who, owner, repo, sha, ct).ConfigureAwait(false);
                    return resolved.Ok ? null : resolved.Failure == RepoFailure.NotFound
                        ? Results.Json(new { error = "not-found", message = "Not found, or not visible to you." }, statusCode: 404)
                        : ApiEndpoints.Refused(resolved.Failure);
                },
                (who, key) => w.Writer.AddCommitCommentAsync(who, owner, repo, sha, ReviewTags.WithOperation(body, key), CancellationToken.None),
                (who, key, since) => w.Writer.FindTaggedCommitCommentAsync(who, owner, repo, sha, ReviewTags.OperationTag(key), ct),
                idempotent: false, ct).ConfigureAwait(false);
        });

        repos.MapWrite("POST", "/pulls", async (string owner, string repo, CreatePullRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            // The head may be a fork's, as GitHub names one (owner:branch) — Step 7; the base is always this repository's.
            if (Branch(request.Base) is not { } @base || Head(request.Head) is not { } head || @base == head)
            {
                return Invalid("branches", "Choose two different branches: the base one of this repository, the head one of this repository or a fork's as owner:branch.");
            }

            if (Title(request.Title) is not { } title)
            {
                return Invalid("title", "A pull request needs a title, of at most 256 characters.");
            }

            var body = request.Body ?? "";
            if (body.Length > MaxBodyChars)
            {
                return Invalid("body", "That description is too long to post.");
            }

            return await w.RunWithoutSnapshotAsync(http, owner, repo, request, "create", ReviewTags.PayloadHash(@base, head, title, body, request.Draft ? "1" : "0"),
                precondition: who => w.CreatePreconditionAsync(who, owner, repo, @base, head, ct),
                async (who, key) => Numbered(await w.Writer.CreatePullAsync(who, owner, repo, new NewPull(@base, head, title, ReviewTags.WithOperation(body, key), request.Draft), CancellationToken.None).ConfigureAwait(false)),
                async (who, key, since) =>
                {
                    var found = await w.Writer.FindCreatedPullAsync(who, owner, repo, head, @base, ReviewTags.OperationTag(key), ct).ConfigureAwait(false);
                    return found.Ok ? RepoResult<string>.Of(found.Value > 0 ? found.Value.ToString(CultureInfo.InvariantCulture) : "") : found.As<string>();
                },
                idempotent: false, ct).ConfigureAwait(false);
        });
    }

    private static void MapMeta(RouteGroupBuilder pulls)
    {
        pulls.MapWrite("POST", "/edit", async (string owner, string repo, int number, EditPullRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            if (request.Title is null && request.Body is null && request.Base is null)
            {
                return Invalid("edit", "Nothing to change.");
            }

            string? title = null;
            if (request.Title is not null && (title = Title(request.Title)) is null)
            {
                return Invalid("title", "A pull request needs a title, of at most 256 characters.");
            }

            if (request.Body is { Length: > MaxBodyChars })
            {
                return Invalid("body", "That description is too long to post.");
            }

            string? @base = null;
            if (request.Base is not null && (@base = Branch(request.Base)) is null)
            {
                return Invalid("base", "That is not a branch of this repository.");
            }

            // The text it replaces, required: an edit that does not say what it is replacing could
            // overwrite anything.
            if ((title is not null && request.ExpectTitle is null) || (request.Body is not null && request.ExpectBody is null))
            {
                return Invalid("expect", "Say what is being replaced. Reload the pull request.");
            }

            var edit = new PullEdit(title, request.Body, @base);
            return await w.RunAsync(http, owner, repo, number, request, "edit-pull", ReviewTags.PayloadHash(title ?? "\0", request.Body ?? "\0", @base ?? "\0"),
                precondition: (who, s) => w.EditPreconditionAsync(who, s, request, edit, ct),
                async (who, s, key) => Done(await w.Writer.EditPullAsync(who, owner, repo, number, edit, CancellationToken.None).ConfigureAwait(false), "edit"),
                async (who, s, key, since) => Reconciled(await w.Source.GetPullSummaryAsync(who, owner, repo, number, ct).ConfigureAwait(false), live =>
                    (edit.Title is null || live.Title == edit.Title)
                    && (edit.Body is null || Same(live.Body, edit.Body))
                    && (edit.Base is null || live.BaseRef == edit.Base), "edit"),
                idempotent: true, ct).ConfigureAwait(false);
        });

        pulls.MapWrite("POST", "/state", async (string owner, string repo, int number, OpenRequest request, HttpContext http, Writes w, CancellationToken ct) =>
            await w.RunAsync(http, owner, repo, number, request, request.Open ? "reopen" : "close", ReviewTags.PayloadHash(request.Open ? "open" : "closed"),
                precondition: async (who, s) =>
                {
                    var (refusal, live) = await w.LiveAsync(who, s, ct).ConfigureAwait(false);
                    return refusal ?? (live!.Merged
                        ? Results.Json(new { error = "merged", message = "This pull request is merged: it cannot be closed or reopened." }, statusCode: 409)
                        : null);
                },
                async (who, s, key) => Done(await w.Writer.SetPullOpenAsync(who, owner, repo, number, request.Open, CancellationToken.None).ConfigureAwait(false), "state"),
                async (who, s, key, since) => Reconciled(await w.Source.GetPullSummaryAsync(who, owner, repo, number, ct).ConfigureAwait(false),
                    live => live.State == (request.Open ? "OPEN" : "CLOSED"), "state"),
                idempotent: true, ct).ConfigureAwait(false));

        pulls.MapWrite("POST", "/draft", async (string owner, string repo, int number, DraftRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            string? node = null;
            return await w.RunAsync(http, owner, repo, number, request, request.Draft ? "to-draft" : "ready", ReviewTags.PayloadHash(request.Draft ? "draft" : "ready"),
                precondition: async (who, s) =>
                {
                    var (refusal, live) = await w.LiveAsync(who, s, ct).ConfigureAwait(false);
                    node = live?.NodeId;
                    return refusal ?? (live!.State != "OPEN" || node is not { Length: > 0 }
                        ? Results.Json(new { error = "not-open", message = "Only an open pull request can be made a draft or ready." }, statusCode: 409)
                        : null);
                },
                async (who, s, key) => Done(await w.Writer.SetDraftAsync(who, node!, request.Draft, CancellationToken.None).ConfigureAwait(false), "draft"),
                async (who, s, key, since) => Reconciled(await w.Source.GetPullSummaryAsync(who, owner, repo, number, ct).ConfigureAwait(false),
                    live => live.Draft == request.Draft, "draft"),
                idempotent: true, ct).ConfigureAwait(false);
        });

        pulls.MapWrite("POST", "/labels", async (string owner, string repo, int number, NamesRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            var (add, remove) = (request.Add ?? [], request.Remove ?? []);
            if (add.Length + remove.Length is 0 or > MaxNames || add.Concat(remove).Any(n => n.Length is 0 or > 50 || n.Any(char.IsControl)))
            {
                return Invalid("labels", "Name up to twenty labels.");
            }

            return await w.RunAsync(http, owner, repo, number, request, "labels", ReviewTags.PayloadHash(string.Join('\n', add), "|", string.Join('\n', remove)),
                precondition: (who, s) => w.LabelsPreconditionAsync(who, s, add, ct),
                async (who, s, key) => Done(await w.Writer.SetLabelsAsync(who, owner, repo, number, add, remove, CancellationToken.None).ConfigureAwait(false), "labels"),
                async (who, s, key, since) => Reconciled(await w.Source.GetPullSummaryAsync(who, owner, repo, number, ct).ConfigureAwait(false),
                    live => add.All(a => live.Labels.Any(l => l.Name == a)) && !remove.Any(r => live.Labels.Any(l => l.Name == r)), "labels"),
                idempotent: true, ct).ConfigureAwait(false);
        });

        pulls.MapWrite("POST", "/assignees", async (string owner, string repo, int number, NamesRequest request, HttpContext http, Writes w, CancellationToken ct) =>
        {
            var (add, remove) = (request.Add ?? [], request.Remove ?? []);
            if (add.Length + remove.Length is 0 or > 10 || add.Concat(remove).Any(l => !Login().IsMatch(l)))
            {
                return Invalid("assignees", "Name up to ten people by their GitHub login.");
            }

            return await w.RunAsync(http, owner, repo, number, request, "assignees", ReviewTags.PayloadHash(string.Join(',', add), "|", string.Join(',', remove)),
                precondition: (who, s) => w.AssigneesPreconditionAsync(who, s, add, ct),
                async (who, s, key) => Done(await w.Writer.SetAssigneesAsync(who, owner, repo, number, add, remove, CancellationToken.None).ConfigureAwait(false), "assignees"),
                async (who, s, key, since) => Reconciled(await w.Source.GetPullSummaryAsync(who, owner, repo, number, ct).ConfigureAwait(false),
                    live => add.All(a => live.Assignees.Contains(a, StringComparer.OrdinalIgnoreCase)) && !remove.Any(r => live.Assignees.Contains(r, StringComparer.OrdinalIgnoreCase)), "assignees"),
                idempotent: true, ct).ConfigureAwait(false);
        });
    }

    /// <summary>A branch name this repository could have: a valid ref, and never <c>owner:branch</c> (a fork's).</summary>
    private static string? Branch(string? name) =>
        name is { Length: > 0 } && ApiEndpoints.ValidRef(name) && !name.Contains(':', StringComparison.Ordinal) && !name.StartsWith('-') ? name : null;

    /// <summary>A head branch: this repository's, or a fork's as <c>owner:branch</c> with a login before the colon.</summary>
    private static string? Head(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return Branch(name);
        }

        return Login().IsMatch(name[..colon]) && Branch(name[(colon + 1)..]) is not null ? name : null;
    }

    private static string? Title(string? title) =>
        title?.Trim() is { Length: > 0 and <= MaxTitleChars } t && !t.Any(char.IsControl) ? t : null;

    /// <summary>Two descriptions are the same text: Versio's own tags and line endings aside.</summary>
    internal static bool Same(string a, string b) =>
        ReviewTags.Strip(a).ReplaceLineEndings("\n").TrimEnd() == ReviewTags.Strip(b).ReplaceLineEndings("\n").TrimEnd();

    private static WriteResult<string> Numbered(WriteResult<int> result) =>
        new(result.Outcome, result.Outcome == WriteOutcome.Done ? result.Value.ToString(CultureInfo.InvariantCulture) : null, result.Failure, result.Message);

    public sealed partial class Writes
    {
        /// <summary>
        /// Both branches exist here — a fork's head cannot be looked up in this repository, so GitHub
        /// checks that one. GitHub refuses the rest (no commits between them, one already open) in its own words.
        /// </summary>
        internal async Task<IResult?> CreatePreconditionAsync(RepoAccess who, string owner, string repo, string @base, string head, CancellationToken ct)
        {
            foreach (var branch in head.Contains(':', StringComparison.Ordinal) ? [@base] : new[] { @base, head })
            {
                var resolved = await Source.ResolveCommitAsync(who, owner, repo, branch, ct).ConfigureAwait(false);
                if (!resolved.Ok)
                {
                    return resolved.Failure == RepoFailure.NotFound
                        ? Results.Json(new { error = "branch-not-found", message = $"There is no branch {CodeHtml.Spelled(branch)} here." }, statusCode: 422)
                        : ApiEndpoints.Refused(resolved.Failure);
                }
            }

            return null;
        }

        /// <summary>The pull request's live state, read for a precondition; a refusal when it cannot be read.</summary>
        internal async Task<(IResult? Refusal, PullSummary? Live)> LiveAsync(RepoAccess who, ReviewSnapshot s, CancellationToken ct)
        {
            var live = await Source.GetPullSummaryAsync(who, s.Owner, s.Repo, s.Number, ct).ConfigureAwait(false);
            return live.Ok ? (null, live.Value) : (ApiEndpoints.Refused(live.Failure), null);
        }

        /// <summary>
        /// An edit replaces only what the person saw: the title and description they edited from, and
        /// the base the snapshot was taken against, must all still be the pull request's.
        /// </summary>
        internal async Task<IResult?> EditPreconditionAsync(RepoAccess who, ReviewSnapshot s, EditPullRequest request, PullEdit edit, CancellationToken ct)
        {
            var (refusal, live) = await LiveAsync(who, s, ct).ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }

            var changed = (edit.Title is not null && live!.Title != request.ExpectTitle)
                          || (edit.Body is not null && !Same(live!.Body, request.ExpectBody!))
                          || (edit.Base is not null && live!.BaseRef != s.BaseRef);
            if (changed)
            {
                Log.Info(LogCategory.Web, $"edit refused: {s.Owner}/{s.Repo}#{s.Number} changed since it was opened for editing");
                return Results.Json(new { error = "changed-since", message = "Someone changed this pull request after you started editing. Your text is kept; reload to see theirs." }, statusCode: 409);
            }

            if (edit.Base is { } @base)
            {
                var resolved = await Source.ResolveCommitAsync(who, s.Owner, s.Repo, @base, ct).ConfigureAwait(false);
                if (!resolved.Ok)
                {
                    return resolved.Failure == RepoFailure.NotFound
                        ? Results.Json(new { error = "branch-not-found", message = $"There is no branch {CodeHtml.Spelled(@base)} here." }, statusCode: 422)
                        : ApiEndpoints.Refused(resolved.Failure);
                }
            }

            return null;
        }

        /// <summary>
        /// Labels are put on, never made: each one added must already exist in the repository.
        /// GitHub would create a missing one silently, which is a repository-wide change nobody asked for.
        /// </summary>
        internal async Task<IResult?> LabelsPreconditionAsync(RepoAccess who, ReviewSnapshot s, IReadOnlyList<string> add, CancellationToken ct)
        {
            if (add.Count == 0)
            {
                return null;
            }

            var labels = await Source.GetLabelsAsync(who, s.Owner, s.Repo, ct).ConfigureAwait(false);
            if (!labels.Ok)
            {
                return ApiEndpoints.Refused(labels.Failure);
            }

            // A cut list cannot say a label does not exist: GitHub decides then (review, 2026-10-24).
            if (labels.Value!.Truncated)
            {
                Log.Info(LogCategory.Web, $"labels of {s.Owner}/{s.Repo} are more than {GitHubRepoSource.ListPages * 100}: the write is not gated on the list");
                return null;
            }

            var missing = add.FirstOrDefault(a => labels.Value.Labels.All(l => l.Name != a));
            return missing is null
                ? null
                : Results.Json(new { error = "label-not-found", message = $"This repository has no label {CodeHtml.Spelled(missing)}." }, statusCode: 422);
        }

        /// <summary>GitHub drops an assignee who cannot be assigned and answers success: asked first, so the page is not told it worked.</summary>
        internal async Task<IResult?> AssigneesPreconditionAsync(RepoAccess who, ReviewSnapshot s, IReadOnlyList<string> add, CancellationToken ct)
        {
            foreach (var login in add)
            {
                var can = await Source.CanAssignAsync(who, s.Owner, s.Repo, login, ct).ConfigureAwait(false);
                if (!can.Ok)
                {
                    return ApiEndpoints.Refused(can.Failure);
                }

                if (!can.Value)
                {
                    return Results.Json(new { error = "not-assignable", message = $"{CodeHtml.Spelled(login)} cannot be assigned in this repository." }, statusCode: 422);
                }
            }

            return null;
        }
    }
}
