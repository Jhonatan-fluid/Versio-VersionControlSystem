using System.Globalization;
using System.Text;
using Versio.Core.Diff;

namespace Versio.Core.Iec;

/// <summary>How a laid-out source file is drawn.</summary>
public enum SourceView
{
    /// <summary>One column: added lines tinted, removed lines struck where they stood.</summary>
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
/// </remarks>
public static class SourceHtml
{
    /// <summary>Rows per <c>.cd-block</c>, the unit the browser may skip laying out.</summary>
    public const int BlockRows = 256;

    /// <summary>The fragment for laid-out (and optionally <see cref="StSourceLayout.Mark"/>ed) rows.</summary>
    public static string Fragment(IReadOnlyList<SourceRow> rows, SyntaxLanguage language, SourceView view = SourceView.Unified)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(language);

        var marked = rows.Any(r => r is SourceRemovedLine or SourceCodeLine { Change: SourceChange.Added });
        var split = view == SourceView.SideBySide && marked;

        var body = new StringBuilder(rows.Count * 64);
        var hidden = split
            ? SplitRows(body, StSourceLayout.SideBySide(rows), language)
            : UnifiedRows(body, rows, language);

        var html = new StringBuilder(body.Length + 512);
        html.Append("<div class=\"cd-page\" data-view=\"").Append(split ? "side-by-side" : "unified")
            .Append("\" data-marked=\"").Append(marked ? "true" : "false")
            .Append("\" data-hidden-chars=\"").Append(hidden.ToString(CultureInfo.InvariantCulture))
            .Append("\">\n");

        if (hidden > 0)
        {
            // Above the code, not only inside it: one marker in a long file is easy to scroll
            // past, and the whole point is that the reader is told before trusting what they read.
            html.Append("<p class=\"cd-notice\">This file contains ")
                .Append(hidden.ToString(CultureInfo.InvariantCulture))
                .Append(hidden == 1 ? " hidden character" : " hidden characters")
                .Append(" that can make code read differently from how it compiles. Each is shown as a U+ marker.</p>\n");
        }

        html.Append("<div class=\"cd-rows\">\n").Append(body).Append("</div>\n</div>\n");
        return html.ToString();
    }

    private static int UnifiedRows(StringBuilder html, IReadOnlyList<SourceRow> rows, SyntaxLanguage language)
    {
        var hidden = 0;
        var block = new BlockWriter(html);
        foreach (var row in rows)
        {
            block.Next();
            switch (row)
            {
                case SourceDivider divider:
                    Divider(html, divider);
                    break;
                case SourceRemovedLine gone:
                    html.Append("<div class=\"cd-row cd-removed\" data-old-line=\"").Append(N(gone.OldFileLine))
                        .Append("\" title=\"Removed (was line ").Append(N(gone.OldFileLine))
                        .Append(")\"><span class=\"cd-num\">&#8722;</span><code class=\"cd-code\"><del>");
                    hidden += CodeHtml.AppendLine(html, gone.Text, language);
                    html.Append("</del></code></div>\n");
                    break;
                case SourceCodeLine line:
                    html.Append("<div class=\"cd-row").Append(line.Change == SourceChange.Added ? " cd-added" : "")
                        .Append("\" data-line=\"").Append(N(line.Number))
                        .Append("\" data-file-line=\"").Append(N(line.FileLine))
                        .Append("\"><span class=\"cd-num\">").Append(N(line.Number))
                        .Append("</span><code class=\"cd-code\">");
                    hidden += CodeHtml.AppendLine(html, line.Text, language);
                    html.Append("</code></div>\n");
                    break;
            }
        }

        block.End();
        return hidden;
    }

    private static int SplitRows(StringBuilder html, IReadOnlyList<SourceSplitRow> rows, SyntaxLanguage language)
    {
        var hidden = 0;
        var block = new BlockWriter(html);
        foreach (var row in rows)
        {
            block.Next();
            if (row.Divider is { } divider)
            {
                Divider(html, divider);
                continue;
            }

            html.Append("<div class=\"cd-split\">");
            hidden += Cell(html, row.Left, old: true, language);
            html.Append("<span class=\"cd-rule\"></span>");
            hidden += Cell(html, row.Right, old: false, language);
            html.Append("</div>\n");
        }

        block.End();
        return hidden;
    }

    private static int Cell(StringBuilder html, SourceSplitCell? cell, bool old, SyntaxLanguage language)
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
            .Append(old ? "data-old-line" : "data-line").Append("=\"").Append(N(cell.Number))
            .Append("\"><span class=\"cd-num\">").Append(N(cell.Number)).Append("</span><code class=\"cd-code\">");
        var hidden = CodeHtml.AppendLine(html, cell.Text, language);
        html.Append("</code></div>");
        return hidden;
    }

    private static void Divider(StringBuilder html, SourceDivider divider) =>
        html.Append("<div class=\"cd-divider\" data-divider=\"").Append(CodeHtml.E(divider.Label))
            .Append("\"><span>").Append(CodeHtml.E(divider.Label)).Append("</span></div>\n");

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Opens a new <c>.cd-block</c> every <see cref="BlockRows"/> rows.</summary>
    private sealed class BlockWriter(StringBuilder html)
    {
        private int _rows;

        public void Next()
        {
            if (_rows % BlockRows == 0)
            {
                if (_rows > 0)
                {
                    html.Append("</div>\n");
                }

                html.Append("<div class=\"cd-block\">\n");
            }

            _rows++;
        }

        public void End()
        {
            if (_rows > 0)
            {
                html.Append("</div>\n");
            }
        }
    }
}
