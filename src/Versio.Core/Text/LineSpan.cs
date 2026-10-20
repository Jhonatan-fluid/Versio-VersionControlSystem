namespace Versio.Core.Text;

/// <summary>
/// The lines of a source file something drawn came from, from 1, both ends included.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where a drawn thing lives, not what it shows.</b> A member row's or a network's span is the
/// file lines its declaration or its XML occupies: what a review comment on it is pinned to, since
/// GitHub anchors comments to file lines (docs/web/PLAN.md, *Pull request review*, C3). It proves
/// nothing about which of those lines the drawing actually shows — a span that drew a visible change
/// can hold an ignored attribute on the same line — so it is never coverage evidence (C4).
/// </para>
/// <para>
/// Approximate at the end for XML: an element's span runs to the last line any of its descendants
/// starts on, because a reader reports where elements begin, not where they close.
/// </para>
/// </remarks>
public readonly record struct LineSpan(int Start, int End)
{
    public bool Contains(int line) => line >= Start && line <= End;

    public int Length => End - Start + 1;

    /// <summary>The same span moved down by <paramref name="lines"/>: a declaration read out of a larger file.</summary>
    public LineSpan Shift(int lines) => new(Start + lines, End + lines);
}
