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
                // The merge tier on top (S11): whether to draw the merge box at all.
                canMerge = options.Value.Writable(owner, repo) && options.Value.Writes.Merge && who.Value!.Kind == AccessKind.User,
            });
        });

        MapLive(pulls);
        MapCoverage(pulls);
        MapRepoLevel(app);
        MapMergeState(pulls);

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
                    // GitHub's "+12 −3" per file, counted from the patch it gave; null without one
                    // (binary, too large, past its list), which the page draws as no count rather than 0.
                    additions = f.Patch is { } p1 ? PatchStats(p1).Added : (int?)null,
                    deletions = f.Patch is { } p2 ? PatchStats(p2).Removed : (int?)null,
                }),
            });
        });

        // "from" is a renamed file's old path, as the files list reported it (from GitHub): the
        // drawing compares the object with itself across the rename rather than as a delete and an add.
        pulls.MapGet("/files/view", async (string owner, string repo, int number, string? path, string? from, string? view, string? layout, string? full,
            HttpContext http, AccessResolver access, ReviewSnapshots snapshots, ApiEndpoints.Loader load, ViewService views,
            PullReview review, IVersioLogger log, CancellationToken ct) =>
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
            var result = await views.CompareAsync(models.Access!, models.MergeBase!, models.Head!, path!, pane, ApiEndpoints.SideBySide(layout), ct, from, ApiEndpoints.FullFile(full)).ConfigureAwait(false);
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
                var threads = await review.ThreadsAsync(opened.Access!, opened.Snapshot!, ct).ConfigureAwait(false);
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

    /// <summary>
    /// The merge box's read: the methods allowed, whether anyone could merge now and why not, and
    /// auto-merge. Read with the viewer's own credential; with the merge tier off it says only that.
    /// </summary>
    private static void MapMergeState(RouteGroupBuilder pulls)
    {
        pulls.MapGet("/merge-state", async (string owner, string repo, int number, HttpContext http, AccessResolver access,
            ReviewSnapshots snapshots, IRepoSource source, IOptions<WebOptions> options, CancellationToken ct) =>
        {
            var opened = await OpenAsync(owner, repo, number, http, access, snapshots, ct).ConfigureAwait(false);
            if (opened.Refusal is { } refusal)
            {
                return refusal;
            }

            if (!options.Value.Writable(owner, repo) || !options.Value.Writes.Merge)
            {
                return Results.Ok(new { tier = false });
            }

            var state = await source.GetMergeStateAsync(opened.Access!, owner, repo, number, ct).ConfigureAwait(false);
            if (!state.Ok)
            {
                // Failing closed: no merge is offered when GitHub cannot say whether one is allowed.
                return Results.Ok(new { tier = true, error = Problem(state.Failure) });
            }

            var m = state.Value!;
            var mergeable = !m.Merged && m.State == "OPEN" && PullWriteEndpoints.MergeableStates.Contains(m.MergeStateStatus);
            return Results.Ok(new
            {
                tier = true,
                state = m.State,
                merged = m.Merged,
                headSha = m.HeadSha,
                mergeStateStatus = m.MergeStateStatus,
                mergeable,
                blocked = mergeable || m.Merged ? null : PullWriteEndpoints.MergeBlocked(m.MergeStateStatus),
                methods = m.Methods,
                autoMergeAllowed = m.AutoMergeAllowed && m.ViewerCanEnableAutoMerge,
                autoMerge = m.AutoMergeMethod is null ? null : new { method = m.AutoMergeMethod, by = CodeHtml.Spelled(m.AutoMergeBy ?? "") },
                canDisableAutoMerge = m.ViewerCanDisableAutoMerge,
                mergeCommit = m.MergeCommit,
            });
        });
    }

    /// <summary>Where GitHub looks for a pull request template, in its order; the name is matched without case, as GitHub does.</summary>
    private static readonly string[] TemplateFolders = [".github", "", "docs"];

    /// <summary>
    /// What opening a pull request needs to read: the repository's template at the base branch, and
    /// the labels a pull request can be given. Neither writes, so neither is under MapWrite.
    /// </summary>
    private static void MapRepoLevel(WebApplication app)
    {
        var repos = app.MapGroup("/api/repos/{owner}/{repo}").RequireRateLimiting(WebHost.RatePolicy);

        repos.MapGet("/pulls/template", async (string owner, string repo, string? @base, HttpContext http, ApiEndpoints.Loader load, IRepoSource source,
            IOptions<WebOptions> options, CancellationToken ct) =>
        {
            if (@base is null || !ApiEndpoints.ValidRef(@base))
            {
                return Results.NotFound();
            }

            var model = await load.ModelAsync(http, owner, repo, @base, ct).ConfigureAwait(false);
            if (!model.Ok)
            {
                return ApiEndpoints.Refused(model.Failure, http);
            }

            var (access, m) = model.Value!;
            var entry = TemplateFolders
                .Select(folder => m.Files.Values.FirstOrDefault(f => string.Equals(f.Path, folder.Length == 0 ? "pull_request_template.md" : $"{folder}/pull_request_template.md", StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(f => f is not null);
            if (entry is null)
            {
                return Results.Ok(new { markdown = (string?)null, path = (string?)null });
            }

            // A template is a page of text; anything larger is not one, and is not put in a form.
            var bytes = await source.GetBlobAsync(access, owner, repo, entry.BlobSha, Math.Min(options.Value.MaxBlobBytes, 64 * 1024), ct).ConfigureAwait(false);
            return bytes.Ok
                ? Results.Ok(new { markdown = System.Text.Encoding.UTF8.GetString(bytes.Value!), path = CodeHtml.Spelled(entry.Path) })
                : Results.Ok(new { markdown = (string?)null, path = (string?)null });
        });

        repos.MapGet("/labels", async (string owner, string repo, HttpContext http, AccessResolver access, IRepoSource source, CancellationToken ct) =>
        {
            if (!ApiEndpoints.Valid(owner, repo))
            {
                return Results.NotFound();
            }

            var who = await access.ResolveAsync(http, owner, repo, ct).ConfigureAwait(false);
            if (!who.Ok)
            {
                return ApiEndpoints.Refused(who.Failure, http);
            }

            var labels = await source.GetLabelsAsync(who.Value!, owner, repo, ct).ConfigureAwait(false);
            return labels.Ok
                // value is the name as GitHub has it, sent back to add or remove; label is what is shown.
                ? Results.Ok(new { labels = labels.Value!.Select(l => new { value = l.Name, label = CodeHtml.Spelled(l.Name), color = l.Color }) })
                : ApiEndpoints.Refused(labels.Failure, http);
        });

        // The open milestones, for the sidebar's picker (Step 5): the number is what a write names.
        repos.MapGet("/milestones", async (string owner, string repo, HttpContext http, AccessResolver access, IRepoSource source, CancellationToken ct) =>
        {
            if (!ApiEndpoints.Valid(owner, repo))
            {
                return Results.NotFound();
            }

            var who = await access.ResolveAsync(http, owner, repo, ct).ConfigureAwait(false);
            if (!who.Ok)
            {
                return ApiEndpoints.Refused(who.Failure, http);
            }

            var milestones = await source.GetMilestonesAsync(who.Value!, owner, repo, ct).ConfigureAwait(false);
            return milestones.Ok
                ? Results.Ok(new { milestones = milestones.Value!.Select(m => new { number = m.Number, title = CodeHtml.Spelled(m.Title) }) })
                : ApiEndpoints.Refused(milestones.Failure, http);
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
                    labels = live.Labels.Select(l => new { name = CodeHtml.Spelled(l.Name), value = l.Name, color = l.Color }),
                    assignees = live.Assignees.Select(CodeHtml.Spelled),
                    reviewers = live.Reviewers.Select(r => new { login = CodeHtml.Spelled(r.Login), state = r.State, isTeam = r.IsTeam, avatar = r.AvatarUrl, slug = r.Slug }),
                    // The pull request's own node: what a reaction on its description names (Step 5).
                    nodeId = live.NodeId,
                    milestoneNumber = live.MilestoneNumber,
                    viewerSubscription = live.ViewerSubscription,
                    canReact = live.ViewerCanReact,
                    // GitHub refuses an author's approval: the page offers it to nobody else.
                    viewerIsAuthor = live.ViewerIsAuthor,
                    viewerLastReviewCommit = live.ViewerLastReviewCommit,
                    // The title and description as they are now, so an edit made anywhere shows without
                    // a reload; the raw text is what an edit starts from and names as replaced.
                    title = CodeHtml.Spelled(live.Title),
                    titleText = live.Title,
                    markdown = ReviewTags.Strip(live.Body),
                    html = ApiEndpoints.MarkdownFragment(ReviewTags.Strip(live.Body)),
                    viewerCanUpdate = live.ViewerCanUpdate,
                    // What GitHub's header and sidebar say (docs/web/FINISH.md, B1): the counts, the
                    // author, the dates, the lock, the milestone, a fork's repository, the reactions.
                    additions = live.Additions,
                    deletions = live.Deletions,
                    changedFiles = live.ChangedFiles,
                    commits = live.CommitCount,
                    comments = live.CommentCount,
                    createdAt = live.CreatedAt,
                    closedAt = live.ClosedAt,
                    mergedAt = live.MergedAt,
                    mergedBy = live.MergedBy is { } by ? CodeHtml.Spelled(by) : null,
                    author = CodeHtml.Spelled(live.Author),
                    authorAvatar = live.AuthorAvatar,
                    authorAssociation = live.AuthorAssociation,
                    locked = live.Locked,
                    lockReason = live.LockReason,
                    milestone = live.Milestone is { } m ? CodeHtml.Spelled(m) : null,
                    headRepository = live.HeadRepository is { } hr ? CodeHtml.Spelled(hr) : null,
                    reactions = live.Reactions.Select(Reaction),
                    checkRollup = live.CheckRollup,
                    requiredChecks = live.RequiredChecks.Select(CodeHtml.Spelled),
                },
                checksError = checks.Ok ? null : Problem(checks.Failure),
                checks = checks.Ok
                    ? checks.Value!.Checks.Select(c => new
                    {
                        name = CodeHtml.Spelled(c.Name),
                        status = c.Status,
                        conclusion = c.Conclusion,
                        url = c.Url,
                        source = c.Source,
                        id = c.Id,
                        startedAt = c.StartedAt,
                        completedAt = c.CompletedAt,
                        app = c.App is { } app ? CodeHtml.Spelled(app) : null,
                        suite = c.Suite,
                        title = c.Title is { } t ? CodeHtml.Spelled(t) : null,
                        // A run's summary is Markdown written by the CI (S6: untrusted): rendered by
                        // Core and sanitised by the page, as every body is.
                        summary = c.Summary is { } sm ? ApiEndpoints.MarkdownFragment(sm) : null,
                        annotations = c.AnnotationCount,
                        description = c.Description is { } d ? CodeHtml.Spelled(d) : null,
                        // Required by branch protection, as GitHub marks the LIVE head's checks; the
                        // list here is the snapshot head's, so a renamed check reads as not required.
                        required = live is not null && live.RequiredChecks.Contains(c.Name, StringComparer.Ordinal),
                    })
                    : null,
                // More checks than were read: the page says the list is cut, so a failure past the
                // bound is never hidden behind a list of passes.
                checksTruncated = checks.Ok && checks.Value!.Truncated,
            });
        });

        // The viewer's pending review: what the review bar shows, and what decides whether "Submit"
        // means this review or a historical one (C2). Visible only to its author, so it is read with
        // the viewer's own credential and never cached across people.
        pulls.MapGet("/reviews/pending", async (string owner, string repo, int number, HttpContext http, AccessResolver access,
            ReviewSnapshots snapshots, IRepoSource source, CancellationToken ct) =>
        {
            var opened = await OpenAsync(owner, repo, number, http, access, snapshots, ct).ConfigureAwait(false);
            if (opened.Refusal is { } refusal)
            {
                return refusal;
            }

            var pending = await source.GetPendingReviewAsync(opened.Access!, owner, repo, number, ct).ConfigureAwait(false);
            if (!pending.Ok)
            {
                return ApiEndpoints.Refused(pending.Failure, http);
            }

            var review = pending.Value!.Review;
            return Results.Ok(new
            {
                review = review is null ? null : new
                {
                    id = review.Id,
                    commit = review.Commit,
                    onSnapshotHead = review.Commit == opened.Snapshot!.HeadSha,
                    // More comments than were read: the page says so rather than showing part as all.
                    truncated = review.Truncated,
                    comments = review.Comments.Select(c => new
                    {
                        id = c.Id,
                        path = c.Path,
                        pathLabel = CodeHtml.Spelled(c.Path),
                        html = ApiEndpoints.MarkdownFragment(ReviewTags.Strip(c.Body)),
                        // The draft's own words and place, so the review panel can show and edit each
                        // one (Step 5), not only name its file.
                        markdown = ReviewTags.Strip(c.Body),
                        line = c.Line,
                        startLine = c.StartLine,
                    }),
                },
            });
        });

        pulls.MapGet("/viewed", async (string owner, string repo, int number, HttpContext http, AccessResolver access,
            ReviewSnapshots snapshots, IRepoSource source, CancellationToken ct) =>
        {
            var opened = await OpenAsync(owner, repo, number, http, access, snapshots, ct).ConfigureAwait(false);
            if (opened.Refusal is { } refusal)
            {
                return refusal;
            }

            var viewed = await source.GetViewedAsync(opened.Access!, owner, repo, number, ct).ConfigureAwait(false);
            return viewed.Ok ? Results.Ok(new { paths = viewed.Value }) : ApiEndpoints.Refused(viewed.Failure, http);
        });

        pulls.MapGet("/threads", async (string owner, string repo, int number, HttpContext http, AccessResolver access,
            ReviewSnapshots snapshots, PullReview review, CancellationToken ct) =>
        {
            var opened = await OpenAsync(owner, repo, number, http, access, snapshots, ct).ConfigureAwait(false);
            if (opened.Refusal is { } refusal)
            {
                return refusal;
            }

            var threads = await review.ThreadsAsync(opened.Access!, opened.Snapshot!, ct).ConfigureAwait(false);
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
                        reviewId = c.ReviewId,
                        // The hunk is text the page draws in a code block: spelled line by line, so a
                        // hidden character shows while the newlines that make it a hunk stay.
                        diffHunk = c.DiffHunk is { } hunk ? SpelledLines(hunk) : null,
                        association = c.AuthorAssociation,
                        avatar = c.AvatarUrl,
                        editedAt = c.EditedAt,
                        url = c.Url,
                        reactions = c.Reactions.Select(Reaction),
                        pending = c.Pending,
                        minimized = c.Minimized,
                        minimizedReason = c.MinimizedReason,
                        // The suggested change the comment carries, as text the page draws as a
                        // diff and offers to apply (Step 5); spelled line by line like a hunk.
                        suggestion = Suggestions.Read(c.Body) is { } suggestion ? SpelledLines(suggestion) : null,
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
                        // The event's own words (FINISH.md, B3): what it named, what was there before,
                        // what it pointed at, who the author is to the repository, the picture, the
                        // edit mark, the permalink, the reactions. Everything a person typed is spelled.
                        target = i.Target is { } target ? CodeHtml.Spelled(target) : null,
                        targetColor = i.TargetColor,
                        previous = i.Previous is { } previous ? CodeHtml.Spelled(previous) : null,
                        reference = i.Reference is { } r ? new { number = r.Number, title = CodeHtml.Spelled(r.Title), repository = CodeHtml.Spelled(r.Repository), url = r.Url, isPull = r.IsPull, state = r.State } : null,
                        message = i.Message is { } message ? CodeHtml.Spelled(message) : null,
                        association = i.AuthorAssociation,
                        avatar = i.AvatarUrl,
                        editedAt = i.EditedAt,
                        url = i.Url,
                        reactions = i.Reactions.Select(Reaction),
                        verified = i.Verified,
                        minimized = i.Minimized,
                        minimizedReason = i.MinimizedReason,
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
                    login = c.AuthorLogin is { } login ? CodeHtml.Spelled(login) : null,
                    avatar = c.AuthorAvatar,
                    committer = c.Committer is { } committer ? CodeHtml.Spelled(committer) : null,
                    body = SpelledLines(c.Body),
                    verified = c.Verified,
                    parents = c.ParentCount,
                    url = c.Url,
                }),
            });
        });
    }

    /// <summary>
    /// Lines added and removed in a unified patch as GitHub writes one: hunks only, no file header,
    /// so every '+' and '-' line counts except the "\ No newline at end of file" marker.
    /// </summary>
    internal static (int Added, int Removed) PatchStats(string patch)
    {
        int added = 0, removed = 0;
        foreach (var raw in patch.Split('\n'))
        {
            if (raw.StartsWith("+", StringComparison.Ordinal) && !raw.StartsWith("+++", StringComparison.Ordinal))
            {
                added++;
            }
            else if (raw.StartsWith("-", StringComparison.Ordinal) && !raw.StartsWith("---", StringComparison.Ordinal))
            {
                removed++;
            }
        }

        return (added, removed);
    }

    /// <summary>
    /// Text of several lines, each spelled (security rule 9) with the newlines between them kept:
    /// a commit body or a diff hunk spelled whole read as one line of <c>U+000A</c> markers (seen
    /// in the preview, 2026-10-23).
    /// </summary>
    internal static string SpelledLines(string text) => string.Join("\n", text.Split('\n').Select(CodeHtml.Spelled));

    /// <summary>A reaction group as the page draws it: GitHub's content word, the count, whether the viewer is in it.</summary>
    private static object Reaction(ReactionGroup g) => new { content = g.Content, count = g.Count, viewerHasReacted = g.ViewerHasReacted };

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
