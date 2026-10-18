using Microsoft.Extensions.Options;
using Versio.Core.Diff;
using Versio.Core.Logging;
using Versio.Core.Projects;
using Versio.Web.Auth;
using Versio.Web.Rendering;
using Versio.Web.Repo;
using Versio.Web.Review;
using Versio.Web.Sources;

namespace Versio.Web.Api;

/// <summary>A changed file's drawing in a review, with what the page must say about how far to trust it.</summary>
/// <param name="Coverage">
/// "partial" for every rendered view until its renderer reports field-level evidence
/// (docs/web/PLAN.md, C4); "raw" for the text diff, which shows every changed line.
/// </param>
public sealed record PullFileView(ViewResult View, string Coverage);

/// <summary>
/// Pull request review, read side (docs/web/PLAN.md, *Pull request review*, slice 1). Every route
/// but <c>snapshot</c> takes the review snapshot in <see cref="SnapshotHeader"/> and answers from
/// its SHAs, so every answer the page combines describes one revision (C1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The snapshot rides in a header, not the query.</b> It is a few hundred characters, and a
/// header keeps it out of URLs, browser history and any log that records a query string.
/// </para>
/// <para>
/// <b>Access first, snapshot second, on every request.</b> The snapshot proves only that this server
/// made it; <see cref="AccessResolver.ResolveAsync"/> runs before it is opened, exactly as for every
/// other route, and the snapshot must name this repository, pull request and viewer.
/// </para>
/// </remarks>
public static class PullEndpoints
{
    public const string SnapshotHeader = "X-Versio-Snapshot";

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var pulls = app.MapGroup("/api/repos/{owner}/{repo}/pulls/{number:int}").RequireRateLimiting(WebHost.RatePolicy);

        // The review's first call: resolve the pull request to one revision and seal it.
        pulls.MapGet("/snapshot", async (string owner, string repo, int number, HttpContext http, AccessResolver access,
            PullReview review, ReviewSnapshots snapshots, CancellationToken ct) =>
        {
            if (!ApiEndpoints.Valid(owner, repo) || !ValidNumber(number))
            {
                return Results.NotFound();
            }

            var who = await access.ResolveAsync(http, owner, repo, ct).ConfigureAwait(false);
            if (!who.Ok)
            {
                return ApiEndpoints.Refused(who.Failure, http);
            }

            var resolved = await review.ResolveAsync(who.Value!, owner, repo, number, Viewer(who.Value!), ct).ConfigureAwait(false);
            if (!resolved.Ok)
            {
                return ApiEndpoints.Refused(resolved.Failure, http);
            }

            var (s, pull) = resolved.Value;
            var p = pull.Pull;
            return Results.Ok(new
            {
                token = snapshots.Seal(s),
                number = p.Number,
                title = CodeHtml.Spelled(p.Title),
                author = CodeHtml.Spelled(p.Author),
                when = p.Updated,
                draft = p.Draft,
                baseRef = s.BaseRef,
                baseLabel = CodeHtml.Spelled(s.BaseRef),
                headLabel = CodeHtml.Spelled(p.HeadRef),
                baseSha = s.BaseSha,
                mergeBaseSha = s.MergeBaseSha,
                headSha = s.HeadSha,
                issuedAt = s.IssuedAt,
                html = ApiEndpoints.MarkdownFragment(pull.Body),
            });
        });

        pulls.MapGet("/files", async (string owner, string repo, int number, HttpContext http, AccessResolver access,
            ReviewSnapshots snapshots, ApiEndpoints.Loader load, PullReview review, IOptions<WebOptions> options, CancellationToken ct) =>
        {
            var opened = await OpenAsync(owner, repo, number, http, access, snapshots, ct).ConfigureAwait(false);
            if (opened.Refusal is { } refusal)
            {
                return refusal;
            }

            var s = opened.Snapshot!;
            var models = await ModelsAsync(http, load, s, ct).ConfigureAwait(false);
            if (models.Refusal is { } failed)
            {
                return failed;
            }

            var (mergeBase, head) = (models.MergeBase!, models.Head!);
            var inventory = await review.InventoryAsync(opened.Access!, s, mergeBase, head, ct).ConfigureAwait(false);

            // Capped (security rule 8), as the compare route is, and said: the page reports how many
            // were left out, so a cut list never passes for the whole change.
            var cap = Math.Max(1, options.Value.MaxComparedFiles);
            var shown = inventory.Files.Take(cap).ToList();
            return Results.Ok(new
            {
                patchSource = inventory.PatchSource,
                githubListCut = inventory.GitHubListCut,
                total = inventory.Files.Count,
                truncated = inventory.Files.Count > shown.Count,
                files = shown.Select(f => new
                {
                    path = f.Path,
                    label = CodeHtml.Spelled(f.Path),
                    change = f.Change,
                    previousPath = f.PreviousPath,
                    previousLabel = f.PreviousPath is null ? null : CodeHtml.Spelled(f.PreviousPath),
                    @object = (head.NodeFor(f.Path) ?? mergeBase.NodeFor(f.Path)) is { Kind: ProjectNodeKind.Object } n ? CodeHtml.Spelled(n.Name) : null,
                    inline = f.InlineUnavailable is null,
                    inlineUnavailable = f.InlineUnavailable,
                }),
            });
        });

        pulls.MapGet("/files/view", async (string owner, string repo, int number, string? path, string? view, string? layout,
            HttpContext http, AccessResolver access, ReviewSnapshots snapshots, ApiEndpoints.Loader load, ViewService views,
            IVersioLogger log, CancellationToken ct) =>
        {
            if (!ApiEndpoints.ValidPath(path))
            {
                return Results.NotFound();
            }

            var opened = await OpenAsync(owner, repo, number, http, access, snapshots, ct).ConfigureAwait(false);
            if (opened.Refusal is { } refusal)
            {
                return refusal;
            }

            var models = await ModelsAsync(http, load, opened.Snapshot!, ct).ConfigureAwait(false);
            if (models.Refusal is { } failed)
            {
                return failed;
            }

            var pane = ApiEndpoints.Pane(view);
            var result = await views.CompareAsync(models.Access!, models.MergeBase!, models.Head!, path!, pane, ApiEndpoints.SideBySide(layout), ct).ConfigureAwait(false);
            ApiEndpoints.Audit(log, models.Access!, models.Head!, path!, $"review #{number} {view}");
            return result.Ok
                ? Results.Ok(new PullFileView(result.Value!, pane == PaneView.Raw ? "raw" : "partial"))
                : ApiEndpoints.Refused(result.Failure, http);
        });
    }

    /// <summary>Who a snapshot is sealed for: a signed-in person by login, everyone else alike.</summary>
    /// <remarks>
    /// Anonymous readers share one viewer name on purpose: a snapshot grants nothing, and every
    /// request is re-authorised anyway; binding it to a person matters for the writes, which only a
    /// signed-in person can make.
    /// </remarks>
    internal static string Viewer(RepoAccess access) => access.Kind == AccessKind.User ? "user:" + access.Who : "anonymous";

    private static bool ValidNumber(int number) => number is > 0 and <= 100_000_000;

    private sealed record Opened(RepoAccess? Access, ReviewSnapshot? Snapshot, IResult? Refusal);

    /// <summary>The credential, then the snapshot: in that order, every time.</summary>
    private static async Task<Opened> OpenAsync(string owner, string repo, int number, HttpContext http, AccessResolver access, ReviewSnapshots snapshots, CancellationToken ct)
    {
        if (!ApiEndpoints.Valid(owner, repo) || !ValidNumber(number))
        {
            return new Opened(null, null, Results.NotFound());
        }

        var who = await access.ResolveAsync(http, owner, repo, ct).ConfigureAwait(false);
        if (!who.Ok)
        {
            return new Opened(null, null, ApiEndpoints.Refused(who.Failure, http));
        }

        var (snapshot, refused) = snapshots.Open(http.Request.Headers[SnapshotHeader].ToString(), owner, repo, number, Viewer(who.Value!));
        return refused switch
        {
            SnapshotRefusal.None => new Opened(who.Value, snapshot, null),
            SnapshotRefusal.Expired => new Opened(null, null, Results.Json(new { error = "snapshot-expired", message = "This review was loaded too long ago. Reload the pull request." }, statusCode: 409)),
            _ => new Opened(null, null, Results.Json(new { error = "snapshot-invalid", message = "This review's revision could not be read. Reload the pull request." }, statusCode: 400)),
        };
    }

    private sealed record Models(RepoAccess? Access, RepoModel? MergeBase, RepoModel? Head, IResult? Refusal);

    /// <summary>The snapshot's two commits as models, each loaded by SHA with this request's credential.</summary>
    private static async Task<Models> ModelsAsync(HttpContext http, ApiEndpoints.Loader load, ReviewSnapshot s, CancellationToken ct)
    {
        var older = await load.ModelAsync(http, s.Owner, s.Repo, s.MergeBaseSha, ct).ConfigureAwait(false);
        if (!older.Ok)
        {
            return new Models(null, null, null, ApiEndpoints.Refused(older.Failure, http));
        }

        var newer = await load.ModelAsync(http, s.Owner, s.Repo, s.HeadSha, ct).ConfigureAwait(false);
        return newer.Ok
            ? new Models(newer.Value.Access, older.Value.Model, newer.Value.Model, null)
            : new Models(null, null, null, ApiEndpoints.Refused(newer.Failure, http));
    }
}
