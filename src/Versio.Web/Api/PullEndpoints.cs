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

/// <summary>One stretch of changed lines a drawing may not show: the reviewer reads it on Raw.</summary>
/// <param name="Start">The first line, from 1; zero for the whole file (one too large to read).</param>
public sealed record CoverageGapDto(string Path, string PathLabel, string Side, int Start, int End, string Reason);

/// <summary>A changed file's drawing in a review, with what the page must say about how far to trust it.</summary>
/// <param name="Coverage">
/// "complete" when every changed line of the object's files is one the drawing names as changed;
/// "partial" otherwise, with <paramref name="Gaps"/> saying where (docs/web/PLAN.md, C4); "raw" for
/// the text diff, which shows every changed line.
/// </param>
/// <param name="Placements">
/// Where each review thread on this file goes on a rendered drawing (docs/web/PLAN.md, C3), decided
/// here from the drawing's own anchors; empty on Raw, where the page places threads by the diff's
/// line markers, or when the threads could not be read.
/// </param>
public sealed record PullFileView(ViewResult View, string Coverage, IReadOnlyList<Versio.Web.Review.GitHub.ThreadPlacement> Placements, IReadOnlyList<CoverageGapDto> Gaps);

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
            PullReview review, ReviewSnapshots snapshots, IOptions<WebOptions> options, CancellationToken ct) =>
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
                html = ApiEndpoints.MarkdownFragment(ReviewTags.Strip(pull.Body)),
                // Whether to draw write controls at all: writes on for this repository (S9) and a
                // signed-in person (S1). The routes check both again; this only spares the page from
                // offering what would be refused.
                writable = options.Value.Writable(owner, repo) && who.Value!.Kind == AccessKind.User,
            });
        });

        MapLive(pulls);
        MapCoverage(pulls);

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

        // "from" is a renamed file's old path, as the files list reported it (from GitHub): the
        // drawing compares the object with itself across the rename rather than as a delete and an add.
        pulls.MapGet("/files/view", async (string owner, string repo, int number, string? path, string? from, string? view, string? layout,
            HttpContext http, AccessResolver access, ReviewSnapshots snapshots, ApiEndpoints.Loader load, ViewService views,
            IRepoSource source, IVersioLogger log, CancellationToken ct) =>
        {
            if (!ApiEndpoints.ValidPath(path) || (from is not null && !ApiEndpoints.ValidPath(from)))
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
            var result = await views.CompareAsync(models.Access!, models.MergeBase!, models.Head!, path!, pane, ApiEndpoints.SideBySide(layout), ct, from).ConfigureAwait(false);
            ApiEndpoints.Audit(log, models.Access!, models.Head!, path!, $"review #{number} {view}");
            if (!result.Ok)
            {
                return ApiEndpoints.Refused(result.Failure, http);
            }

            // Placements only for a drawing: Raw's rows are the diff's own lines, which the page
            // places by directly. A thread read that fails leaves every thread listed by the page,
            // never placed by a guess.
            IReadOnlyList<Versio.Web.Review.GitHub.ThreadPlacement> placements = [];
            if (pane == PaneView.Rendered)
            {
                var threads = await source.GetReviewThreadsAsync(opened.Access!, owner, repo, number, ct).ConfigureAwait(false);
                if (threads.Ok)
                {
                    placements = Versio.Web.Review.GitHub.ThreadPlacements.Place(threads.Value!.Threads, result.Value!.Anchors, opened.Snapshot!, result.Value.Path, from);
                }
            }

            return Results.Ok(new PullFileView(result.Value!, CoverageWord(pane, result.Value!.Coverage), placements, Gaps(result.Value!.Coverage)));
        });
    }

    /// <summary>
    /// "complete" only with evidence for every changed line; a drawing with none (a fallback to plain
    /// text, a binary) is partial, never complete by default.
    /// </summary>
    private static string CoverageWord(PaneView pane, Versio.Core.Review.ReviewCoverage? coverage) =>
        pane == PaneView.Raw ? "raw" : coverage is { Complete: true } ? "complete" : "partial";

    private static IReadOnlyList<CoverageGapDto> Gaps(Versio.Core.Review.ReviewCoverage? coverage) =>
        coverage?.Gaps.Select(g => new CoverageGapDto(g.Path, CodeHtml.Spelled(g.Path), g.Side == Versio.Core.Review.ReviewSide.New ? "new" : "old", g.Start, g.End, g.Reason)).ToList() ?? [];

    /// <summary>The most files whose drawings the coverage summary renders; the rest are reported unchecked.</summary>
    internal const int CoverageFileCap = 200;

    /// <summary>
    /// What the Approve dialog says before an approval: for each changed file, whether its drawing
    /// covers every changed line (C4). Renders every file's comparison (cached, so a file already
    /// opened costs nothing), up to <see cref="CoverageFileCap"/>; past it, files are "unchecked" and
    /// counted as such — never as covered.
    /// </summary>
    private static void MapCoverage(RouteGroupBuilder pulls)
    {
        pulls.MapGet("/coverage", async (string owner, string repo, int number, HttpContext http, AccessResolver access,
            ReviewSnapshots snapshots, ApiEndpoints.Loader load, PullReview review, ViewService views, CancellationToken ct) =>
        {
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

            var inventory = await review.InventoryAsync(opened.Access!, opened.Snapshot!, models.MergeBase!, models.Head!, ct).ConfigureAwait(false);
            var files = new List<object>(inventory.Files.Count);
            foreach (var (file, index) in inventory.Files.Select((f, i) => (f, i)))
            {
                if (index >= CoverageFileCap)
                {
                    files.Add(new { path = file.Path, coverage = "unchecked", gaps = 0 });
                    continue;
                }

                var drawn = await views.CompareAsync(models.Access!, models.MergeBase!, models.Head!, file.Path, PaneView.Rendered, false, ct, file.PreviousPath).ConfigureAwait(false);
                var coverage = drawn.Ok ? drawn.Value!.Coverage : null;
                files.Add(new { path = file.Path, coverage = drawn.Ok ? CoverageWord(PaneView.Rendered, coverage) : "unchecked", gaps = coverage?.Gaps.Count ?? 0 });
            }

            return Results.Ok(new { files });
        });
    }

    /// <summary>The live state, threads, conversation and commits: each against the snapshot it is read for.</summary>
    private static void MapLive(RouteGroupBuilder pulls)
    {
        // Polled every 30 s while the page is open. "stale" is the whole point: the pull request's
        // live head or base is no longer the snapshot's, so the page stops combining and offers a
        // reload. The checks are the SNAPSHOT head's, because that is the code under review.
        pulls.MapGet("/status", async (string owner, string repo, int number, HttpContext http, AccessResolver access,
            ReviewSnapshots snapshots, IRepoSource source, CancellationToken ct) =>
        {
            var opened = await OpenAsync(owner, repo, number, http, access, snapshots, ct).ConfigureAwait(false);
            if (opened.Refusal is { } refusal)
            {
                return refusal;
            }

            var s = opened.Snapshot!;
            var summary = await source.GetPullSummaryAsync(opened.Access!, owner, repo, number, ct).ConfigureAwait(false);
            var checks = await source.GetChecksAsync(opened.Access!, owner, repo, s.HeadSha, ct).ConfigureAwait(false);
            var live = summary.Ok ? summary.Value! : null;
            return Results.Ok(new
            {
                // Unknown is not "unchanged": without the live state, the page cannot vouch for the
                // snapshot either, and says so rather than staying quiet.
                stale = live is not null && (live.HeadSha != s.HeadSha || live.BaseRef != s.BaseRef || (live.BaseTipSha is { } tip && tip != s.BaseSha)),
                headMoved = live is not null && live.HeadSha != s.HeadSha,
                baseMoved = live is not null && (live.BaseRef != s.BaseRef || (live.BaseTipSha is { } t && t != s.BaseSha)),
                summaryError = summary.Ok ? null : Problem(summary.Failure),
                summary = live is null ? null : new
                {
                    state = live.State,
                    draft = live.Draft,
                    merged = live.Merged,
                    mergeable = live.Mergeable,
                    reviewDecision = live.ReviewDecision,
                    labels = live.Labels.Select(l => new { name = CodeHtml.Spelled(l.Name), color = l.Color }),
                    assignees = live.Assignees.Select(CodeHtml.Spelled),
                    reviewers = live.Reviewers.Select(r => new { login = CodeHtml.Spelled(r.Login), state = r.State }),
                },
                checksError = checks.Ok ? null : Problem(checks.Failure),
                checks = checks.Ok
                    ? checks.Value!.Select(c => new { name = CodeHtml.Spelled(c.Name), status = c.Status, conclusion = c.Conclusion, url = c.Url, source = c.Source })
                    : null,
            });
        });

        pulls.MapGet("/threads", async (string owner, string repo, int number, HttpContext http, AccessResolver access,
            ReviewSnapshots snapshots, IRepoSource source, CancellationToken ct) =>
        {
            var opened = await OpenAsync(owner, repo, number, http, access, snapshots, ct).ConfigureAwait(false);
            if (opened.Refusal is { } refusal)
            {
                return refusal;
            }

            var threads = await source.GetReviewThreadsAsync(opened.Access!, owner, repo, number, ct).ConfigureAwait(false);
            if (!threads.Ok)
            {
                return ApiEndpoints.Refused(threads.Failure, http);
            }

            return Results.Ok(new
            {
                truncated = threads.Value!.Truncated,
                threads = threads.Value.Threads.Select(t => new
                {
                    id = t.Id,
                    path = t.Path,
                    pathLabel = CodeHtml.Spelled(t.Path),
                    resolved = t.Resolved,
                    outdated = t.Outdated,
                    line = t.Line,
                    startLine = t.StartLine,
                    side = t.Side,
                    originalLine = t.OriginalLine,
                    subject = t.SubjectType,
                    truncated = t.Truncated,
                    canReply = t.CanReply,
                    canResolve = t.CanResolve,
                    canUnresolve = t.CanUnresolve,
                    comments = t.Comments.Select(c => new
                    {
                        id = c.Id,
                        author = CodeHtml.Spelled(c.Author),
                        when = c.When,
                        html = ApiEndpoints.MarkdownFragment(ReviewTags.Strip(c.Body)),
                        // The text to edit, tags removed: only ever a textarea's value on the page, never markup.
                        markdown = c.CanEdit ? ReviewTags.Strip(c.Body) : null,
                        commit = c.Commit,
                        originalCommit = c.OriginalCommit,
                        canEdit = c.CanEdit,
                        canDelete = c.CanDelete,
                    }),
                }),
            });
        });

        pulls.MapGet("/timeline", async (string owner, string repo, int number, HttpContext http, AccessResolver access,
            ReviewSnapshots snapshots, IRepoSource source, CancellationToken ct) =>
        {
            var opened = await OpenAsync(owner, repo, number, http, access, snapshots, ct).ConfigureAwait(false);
            if (opened.Refusal is { } refusal)
            {
                return refusal;
            }

            var timeline = await source.GetTimelineAsync(opened.Access!, owner, repo, number, ct).ConfigureAwait(false);
            return timeline.Ok
                ? Results.Ok(new
                {
                    truncated = timeline.Value!.Truncated,
                    items = timeline.Value.Items.Select(i => new
                    {
                        kind = i.Kind,
                        actor = CodeHtml.Spelled(i.Actor),
                        when = i.When,
                        // Comments and reviews are Markdown by anyone who can comment: rendered by
                        // Core and sanitised by the page, as every body is. A commit's headline is
                        // text, spelled.
                        html = i.Kind is "comment" or "review" && !string.IsNullOrEmpty(i.Body) ? ApiEndpoints.MarkdownFragment(ReviewTags.Strip(i.Body)) : null,
                        text = i.Kind == "commit" ? CodeHtml.Spelled(i.Body ?? "") : null,
                        state = i.State,
                        sha = i.Sha,
                        id = i.Id,
                        markdown = i.CanEdit ? ReviewTags.Strip(i.Body ?? "") : null,
                        canEdit = i.CanEdit,
                        canDelete = i.CanDelete,
                    }),
                })
                : ApiEndpoints.Refused(timeline.Failure, http);
        });

        pulls.MapGet("/commits", async (string owner, string repo, int number, HttpContext http, AccessResolver access,
            ReviewSnapshots snapshots, IRepoSource source, CancellationToken ct) =>
        {
            var opened = await OpenAsync(owner, repo, number, http, access, snapshots, ct).ConfigureAwait(false);
            if (opened.Refusal is { } refusal)
            {
                return refusal;
            }

            var commits = await source.GetPullCommitsAsync(opened.Access!, owner, repo, number, ct).ConfigureAwait(false);
            if (!commits.Ok)
            {
                return ApiEndpoints.Refused(commits.Failure, http);
            }

            // The list is the branch's live commits, oldest first. Those after the snapshot's head
            // arrived since the review loaded: marked, not hidden, so nobody mistakes them for
            // reviewed. With the head not in the list at all (a force-push replaced it), none can be
            // vouched for, and none is marked as in the snapshot.
            var list = commits.Value!.Commits;
            var head = list.ToList().FindIndex(c => c.Sha == opened.Snapshot!.HeadSha);
            return Results.Ok(new
            {
                truncated = commits.Value.Truncated,
                headFound = head >= 0,
                commits = list.Select((c, i) => new
                {
                    sha = c.Sha,
                    headline = CodeHtml.Spelled(c.Headline),
                    author = CodeHtml.Spelled(c.Author),
                    when = c.When,
                    inSnapshot = head >= 0 && i <= head,
                }),
            });
        });
    }

    /// <summary>A failure the page words in place, without failing the whole answer.</summary>
    private static string Problem(RepoFailure failure) => failure switch
    {
        RepoFailure.Unauthorized => "sign-in",
        RepoFailure.RateLimited => "rate-limit",
        RepoFailure.SsoRequired => "sso",
        RepoFailure.NotFound => "not-found",
        _ => "upstream",
    };

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
