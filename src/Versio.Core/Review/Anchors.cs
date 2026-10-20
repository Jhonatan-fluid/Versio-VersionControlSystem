using System.Security.Cryptography;
using System.Text;
using Versio.Core.Text;

namespace Versio.Core.Review;

/// <summary>Which version of a comparison something belongs to.</summary>
public enum ReviewSide
{
    /// <summary>The older version: where the changes are measured from.</summary>
    Old,

    /// <summary>The newer version: the one under review.</summary>
    New,
}

/// <summary>
/// One thing a renderer drew that a review comment can sit on: a member row or a network, on one
/// side of a comparison (docs/web/PLAN.md, *Pull request review*, C3).
/// </summary>
/// <param name="Dom">
/// The id the fragment carries for it, in a <c>data-anchors</c> list: one drawn element can stand
/// for both sides (an unchanged row, a merged network), so an element lists every anchor it is.
/// </param>
/// <param name="Kind">row or net.</param>
/// <param name="SourcePath">
/// The file the element was parsed from — the layer, not necessarily the file on screen: a block's
/// table is drawn from the <c>.xml</c> beside the <c>.st</c> being looked at.
/// </param>
/// <param name="Container">The object it belongs to: the table's or the block's name.</param>
/// <param name="Key">Its identity inside the container: a member path; a network's title, or its number when it has none.</param>
/// <param name="Fingerprint">A hash of what it shows, so a renumbered network is recognised and a reused id is not believed.</param>
/// <param name="Lines">Its file lines on <paramref name="Side"/>, or null when the parser did not know them.</param>
public sealed record RenderedAnchor(
    string Dom,
    string Kind,
    string SourcePath,
    ReviewSide Side,
    string Container,
    string Key,
    string Fingerprint,
    LineSpan? Lines);

/// <summary>
/// What a review comment was placed on, as Versio wrote it into the comment: everything needed to
/// find the same element again in a later revision, and to tell when it cannot be.
/// </summary>
/// <param name="Revision">The commit of <paramref name="Side"/> when the comment was written.</param>
public sealed record SemanticAnchor(
    string Kind,
    string SourcePath,
    ReviewSide Side,
    string Revision,
    string Container,
    string Key,
    string Fingerprint);

/// <summary>How well an anchor found its element again.</summary>
public enum AnchorOutcome
{
    /// <summary>The same element: same file, side, container and key, and the same revision or the same content.</summary>
    Exact,

    /// <summary>The same content under another key, the only such element in its container: a network renumbered by a deletion.</summary>
    Moved,

    /// <summary>More than one element could be it. Never a guess: the comment is listed, not placed.</summary>
    Ambiguous,

    /// <summary>Nothing it could be.</summary>
    Gone,
}

public sealed record AnchorPlacement(AnchorOutcome Outcome, RenderedAnchor? Anchor);

/// <summary>
/// Finds the element a review comment belongs on, by the anchor Versio wrote into it or by the file
/// line GitHub reports (docs/web/PLAN.md, *Pull request review*, C3). Pure, and host-neutral: what
/// a GitHub line or tag is lives in the web project (C6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Wrong placement is worse than none.</b> A comment drawn on the wrong rung reads as a remark
/// about logic it never saw. So every rule here refuses rather than picks: two candidates are
/// Ambiguous, a key without its content at a new revision is not Exact, a fingerprint is looked for
/// only inside the same container, and anything uncertain goes to the unplaced list with the
/// revision it was written on.
/// </para>
/// <para>
/// <b>Why not the revision alone.</b> Every element of a file shares its revision; "same revision"
/// says nothing about which element. And why not the key alone: a network numbered 3 after a
/// deletion is not the network 3 the comment was about. The key plus either the very revision it
/// was written on, or the same content, is what identifies the element.
/// </para>
/// </remarks>
public static class AnchorMatcher
{
    /// <param name="revisionOf">The commit each side of the comparison now shows.</param>
    /// <param name="renamed">A rename GitHub reported, old path to new; only such renames are followed.</param>
    public static AnchorPlacement Match(
        SemanticAnchor anchor,
        Func<ReviewSide, string> revisionOf,
        IReadOnlyList<RenderedAnchor> candidates,
        Func<string, string?>? renamed = null)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(revisionOf);
        ArgumentNullException.ThrowIfNull(candidates);

        var path = renamed?.Invoke(anchor.SourcePath) ?? anchor.SourcePath;
        var pool = candidates
            .Where(c => c.Kind == anchor.Kind && c.SourcePath == path && c.Side == anchor.Side && c.Container == anchor.Container)
            .ToList();

        var sameRevision = revisionOf(anchor.Side) == anchor.Revision;
        var exact = pool.Where(c => c.Key == anchor.Key && (sameRevision || c.Fingerprint == anchor.Fingerprint)).ToList();
        if (exact.Count > 0)
        {
            return exact.Count == 1 ? new AnchorPlacement(AnchorOutcome.Exact, exact[0]) : new AnchorPlacement(AnchorOutcome.Ambiguous, null);
        }

        var moved = pool.Where(c => c.Key != anchor.Key && c.Fingerprint == anchor.Fingerprint).ToList();
        return moved.Count switch
        {
            1 => new AnchorPlacement(AnchorOutcome.Moved, moved[0]),
            > 1 => new AnchorPlacement(AnchorOutcome.Ambiguous, null),
            _ => new AnchorPlacement(AnchorOutcome.Gone, null),
        };
    }

    /// <summary>
    /// The element a file line falls in, on that side: the smallest span holding it, or null when
    /// none does or two of the same size do.
    /// </summary>
    public static RenderedAnchor? ByLine(string path, ReviewSide side, int line, IReadOnlyList<RenderedAnchor> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var holding = candidates
            .Where(c => c.SourcePath == path && c.Side == side && c.Lines is { } span && span.Contains(line))
            .OrderBy(c => c.Lines!.Value.Length)
            .ToList();
        return holding.Count switch
        {
            0 => null,
            1 => holding[0],
            _ => holding[0].Lines!.Value.Length < holding[1].Lines!.Value.Length ? holding[0] : null,
        };
    }

    /// <summary>A short, stable hash of what an element shows.</summary>
    public static string Print(params string?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts.Select(p => p ?? "\u0000")))))[..16];
}
