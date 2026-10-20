using Versio.Core.Review;
using Versio.Web.Sources;

namespace Versio.Web.Review.GitHub;

/// <summary>Where one review thread goes on a drawing, and why.</summary>
/// <param name="Outcome">
/// exact or moved (placed on <paramref name="Dom"/> by its anchor tag); line (placed on the element
/// holding GitHub's current line); hint (no element holds the line, so the page may place it on a
/// drawn source line of <paramref name="HintPath"/>); or unplaced, with the reason in
/// <paramref name="Reason"/> (ambiguous, gone, disagrees, outdated, file).
/// </param>
public sealed record ThreadPlacement(string ThreadId, string Outcome, string? Dom, string? Reason, string? HintPath = null, string? HintSide = null, int? HintLine = null);

/// <summary>
/// Places a pull request's review threads on a rendered drawing (docs/web/PLAN.md, *Pull request
/// review*, C3): by the anchor Versio wrote into the comment when there is one, else by the file
/// line GitHub reports, and otherwise not at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>A tag never silently beats a line.</b> When GitHub still has a current line for the thread
/// and the tag found an element on the same file and side, that line must fall inside the element.
/// If it does not, one of them is wrong and nobody can say which: the thread is listed, not placed.
/// </para>
/// <para>
/// <b>An outdated thread</b> — GitHub has no current line, the code it was on changed — is placed
/// only by its tag, and a Moved placement is shown as moved. Without a tag it is listed with the
/// line it was written on.
/// </para>
/// </remarks>
public static class ThreadPlacements
{
    public static IReadOnlyList<ThreadPlacement> Place(
        IReadOnlyList<ReviewThread> threads, IReadOnlyList<RenderedAnchor> anchors, ReviewSnapshot snapshot, string shownPath, string? previousPath)
    {
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(snapshot);

        var paths = anchors.Select(a => a.SourcePath).Append(shownPath).ToHashSet(StringComparer.Ordinal);
        if (previousPath is not null)
        {
            paths.Add(previousPath);
        }

        string RevisionOf(ReviewSide side) => side == ReviewSide.New ? snapshot.HeadSha : snapshot.MergeBaseSha;
        string? Renamed(string path) => previousPath is not null && path == previousPath ? shownPath : null;

        var placed = new List<ThreadPlacement>();
        foreach (var thread in threads.Where(t => paths.Contains(t.Path)))
        {
            var side = thread.Side == "LEFT" ? ReviewSide.Old : ReviewSide.New;
            var tag = thread.Comments.Count > 0 ? ReviewTags.ReadAnchor(thread.Comments[0].Body) : null;
            if (tag is not null)
            {
                var match = AnchorMatcher.Match(tag, RevisionOf, anchors, Renamed);
                if (match.Anchor is not { } anchor)
                {
                    placed.Add(new ThreadPlacement(thread.Id, "unplaced", null, match.Outcome == AnchorOutcome.Ambiguous ? "ambiguous" : "gone"));
                    continue;
                }

                if (thread.Line is { } line && thread.SubjectType == "LINE" && anchor.Side == side
                    && (Renamed(thread.Path) ?? thread.Path) == anchor.SourcePath && anchor.Lines is { } span && !span.Contains(line))
                {
                    placed.Add(new ThreadPlacement(thread.Id, "unplaced", null, "disagrees"));
                    continue;
                }

                placed.Add(new ThreadPlacement(thread.Id, match.Outcome == AnchorOutcome.Moved ? "moved" : "exact", anchor.Dom, null));
                continue;
            }

            if (thread.SubjectType != "LINE")
            {
                placed.Add(new ThreadPlacement(thread.Id, "unplaced", null, "file"));
                continue;
            }

            if (thread.Line is not { } current)
            {
                placed.Add(new ThreadPlacement(thread.Id, "unplaced", null, "outdated"));
                continue;
            }

            var path = Renamed(thread.Path) ?? thread.Path;
            placed.Add(AnchorMatcher.ByLine(path, side, current, anchors) is { } holder
                ? new ThreadPlacement(thread.Id, "line", holder.Dom, null)
                : new ThreadPlacement(thread.Id, "hint", null, null, path, thread.Side, current));
        }

        return placed;
    }
}
