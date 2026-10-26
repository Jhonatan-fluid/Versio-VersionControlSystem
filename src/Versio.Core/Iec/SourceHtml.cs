using System.Globalization;
using System.Text;
using Versio.Core.Diff;

namespace Versio.Core.Iec;

/// <summary>How a laid-out source file is drawn.</summary>
public enum SourceView
{
    /// <summary>
    /// One column: the file as it is, added lines tinted, a pure deletion marked on the line now
    /// standing where it was (<see cref="StSourceLayout.Unified"/>). Removed lines are side by side's.
    /// </summary>
    Unified,

    /// <summary>The older version on the left, the newer on the right, when anything is marked.</summary>
    SideBySide,
}

/// <summary>
/// A source file laid out by <see cref="StSourceLayout"/>, as an HTML fragment: the Rendered tab
/// of the desktop pane, for a browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>The layout is decided once, in <see cref="StSourceLayout"/>.</b> Which lines, which
/// numbers, where the Declaration and Implementation dividers sit and where a removed line is
/// drawn are all its answers; this only writes them as markup, as <c>DiffView</c> only draws
/// them in WPF. Two renderers of one layout cannot disagree about what a line's number is.
/// </para>
/// <para>
/// <b>Every row says which line it is, as a contract</b> (docs/web/PLAN.md Step 1):
/// <c>data-line</c> is the number the reader sees, <c>data-file-line</c> where it sits in the
/// file, and a removed line carries <c>data-old-line</c> instead. Links to a line use them now;
/// review comments and merge choices will attach to them later without taking the markup apart.
/// </para>
/// <para>
/// <b>Side by side only when something is marked</b>, as the desktop decides: an unchanged file
/// side by side is the same text twice at half the width.
/// </para>
/// <para>
/// <b>The same rows as the desktop's <c>DiffView</c>, not a reading of them.</b> Unified draws
/// <see cref="StSourceLayout.Unified"/>'s rows and a comparison folds with
/// <see cref="StSourceLayout.Fold(IReadOnlyList{SourceRow}, int)"/> unless the whole file is asked
/// for — the same calls in the same order. This drew removed lines struck in place and never folded
/// for a week after the desktop stopped, and the owner saw the two side by side on a real pull
/// request (2026-10-23): the viewer exists to show what the desktop shows.
/// </para>
/// </remarks>
public static class SourceHtml
{
    /// <summary>Rows per <c>.cd-block</c>; the one value is <see cref="CodeHtml.BlockRows"/>.</summary>
    public const int BlockRows = CodeHtml.BlockRows;

    /// <summary>The fragment for laid-out (and optionally <see cref="StSourceLayout.Mark"/>ed) rows.</summary>
    /// <param name="sourcePath">The file the rows are lines of, carried as <c>data-src-path</c>: a review thread on a file line is placed only in the block of its own file.</param>
    /// <param name="fullFile">
    /// The desktop pane's <i>Show full file</i>: false folds a comparison to git's context either
    /// side of each change. True by default, as the desktop control's is, so a caller that is not
    /// comparing draws everything.
    /// </param>
    public static string Fragment(IReadOnlyList<SourceRow> rows, SyntaxLanguage language, SourceView view = SourceView.Unified, string sourcePath = "", bool fullFile = true, Func<string, string?>? links = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(language);

        var marked = rows.Any(r => r is SourceRemovedLine or SourceCodeLine { Change: SourceChange.Added });
        var split = view == SourceView.SideBySide && marked;

        var body = new StringBuilder(rows.Count * 64);
        // DiffView.Rebuild's order exactly: pair, then fold; or fold, then leave removals out.
        var pairs = split ? StSourceLayout.SideBySide(rows) : null;
        var hidden = pairs is not null
            ? SplitRows(body, fullFile ? pairs : StSourceLayout.Fold(pairs), language, links)
            : UnifiedRows(body, marked ? StSourceLayout.Unified(fullFile ? rows : StSourceLayout.Fold(rows)) : rows, language, links);

        var html = new StringBuilder(body.Length + 512);
        html.Append("<div class=\"cd-page\" data-src-path=\"").Append(CodeHtml.A(sourcePath)).Append("\" data-view=\"").Append(split ? "side-by-side" : "unified")
            .Append("\" data-marked=\"").Append(marked ? "true" : "false")
            .Append("\" data-hidden-chars=\"").Append(hidden.ToString(CultureInfo.InvariantCulture))
            .Append("\">\n");

        // Above the code, worded once for every code renderer (CodeHtml.AppendNotice says why).
        CodeHtml.AppendNotice(html, hidden);
        html.Append("<div class=\"cd-rows\">\n").Append(body).Append("</div>\n</div>\n");
        return html.ToString();
    }

    private static int UnifiedRows(StringBuilder html, IReadOnlyList<SourceRow> rows, SyntaxLanguage language, Func<string, string?>? links)
    {
        var hidden = 0;
        var block = new CodeHtml.Blocks(html);
        // The rows are the file in order, so a block comment's state carries from line to line
        // (the second line of a (* … *) is a comment, never a link); a fold hides lines, and
        // after one the state is what the stateless tokenizer assumes: code.
        var state = new SyntaxTokenizer.TokenState();
        foreach (var row in rows)
        {
            block.Next();
            switch (row)
            {
                case SourceDivider divider:
                    Divider(html, divider);
                    break;
                case SourceFold fold:
                    Fold(html, fold);
                    state.Reset();
                    break;
                case SourceCodeLine line:
                    html.Append("<div class=\"cd-row").Append(line.Change == SourceChange.Added ? " cd-added" : "")
                        .Append(line.RemovedNear is { } gap ? gap.After ? " cd-removed-below" : " cd-removed-above" : "")
                        .Append("\" data-line=\"").Append(N(line.Number))
                        .Append("\" data-file-line=\"").Append(N(line.FileLine)).Append('"');
                    if (line.OldFileLine is { } was)
                    {
                        html.Append(" data-old-file-line=\"").Append(N(was)).Append('"');
                    }

                    if (line.RemovedNear is { } removed)
                    {
                        // The desktop's tooltip, word for word.
                        html.Append(" title=\"").Append(N(removed.Count)).Append(removed.Count == 1 ? " line" : " lines")
                            .Append(" removed ").Append(removed.After ? "below" : "above")
                            .Append(" since the last commit. Side by side shows them.\"");
                    }

                    html.Append("><span class=\"cd-num\">").Append(N(line.Number))
                        .Append("</span><code class=\"cd-code\">");
                    hidden += CodeHtml.AppendLine(html, line.Text, language, links: links, state: state);
                    html.Append("</code></div>\n");
                    break;
            }
        }

        block.End();
        return hidden;
    }

    private static int SplitRows(StringBuilder html, IReadOnlyList<SourceSplitRow> rows, SyntaxLanguage language, Func<string, string?>? links)
    {
        var hidden = 0;
        var block = new CodeHtml.Blocks(html);
        // One comment state per side: each column is its own file in order (see UnifiedRows).
        var left = new SyntaxTokenizer.TokenState();
        var right = new SyntaxTokenizer.TokenState();
        foreach (var row in rows)
        {
            block.Next();
            if (row.Divider is { } divider)
            {
                Divider(html, divider);
                continue;
            }

            if (row.Fold is { } fold)
            {
                Fold(html, fold);
                left.Reset();
                right.Reset();
                continue;
            }

            html.Append("<div class=\"cd-split\">");
            hidden += Cell(html, row.Left, old: true, language, links, left);
            html.Append("<span class=\"cd-rule\"></span>");
            hidden += Cell(html, row.Right, old: false, language, links, right);
            html.Append("</div>\n");
        }

        block.End();
        return hidden;
    }

    private static int Cell(StringBuilder html, SourceSplitCell? cell, bool old, SyntaxLanguage language, Func<string, string?>? links, SyntaxTokenizer.TokenState state)
    {
        if (cell is null)
        {
            // An empty side keeps no tint: a deletion has no counterpart on the right, and
            // tinting the void green would claim an addition that never happened (DiffView).
            html.Append("<div class=\"cd-cell\"></div>");
            return 0;
        }

        var kind = cell.Kind switch
        {
            SourceSide.Added => " cd-added",
            SourceSide.Removed => " cd-removed",
            _ => "",
        };
        html.Append("<div class=\"cd-cell").Append(kind).Append("\" ")
            .Append(old ? "data-old-line" : "data-line").Append("=\"").Append(N(cell.Number)).Append('"');
        if (cell.FileLine > 0)
        {
            // The section numbers above restart at every divider; the file line is what a review
            // comment names (docs/web/PLAN.md, C3).
            html.Append(old ? " data-old-file-line=\"" : " data-file-line=\"").Append(N(cell.FileLine)).Append('"');
        }

        html.Append("><span class=\"cd-num\">").Append(N(cell.Number)).Append("</span><code class=\"cd-code\">");
        var hidden = CodeHtml.AppendLine(html, cell.Text, language, links: links, state: state);
        html.Append("</code></div>");
        return hidden;
    }

    private static void Divider(StringBuilder html, SourceDivider divider) =>
        html.Append("<div class=\"cd-divider\" data-divider=\"").Append(CodeHtml.E(divider.Label))
            .Append("\"><span>").Append(CodeHtml.E(divider.Label)).Append("</span></div>\n");

    /// <summary>Unchanged lines folded away: a quiet row saying how many, as the desktop's does.</summary>
    private static void Fold(StringBuilder html, SourceFold fold) =>
        html.Append("<div class=\"cd-fold\" data-folded=\"").Append(N(fold.Hidden))
            .Append("\" title=\"Tick Show full file to see every line\"><span class=\"cd-num\">&#8943;</span><span>")
            .Append(N(fold.Hidden)).Append(fold.Hidden == 1 ? " unchanged line" : " unchanged lines").Append("</span></div>\n");

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
