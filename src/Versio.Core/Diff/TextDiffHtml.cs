using System.Globalization;
using System.Text;

namespace Versio.Core.Diff;

/// <summary>How a text diff is drawn.</summary>
public enum TextDiffView
{
    /// <summary>One column, both line numbers in the gutter, a marker per line.</summary>
    Unified,

    /// <summary>The old file on the left, the new on the right.</summary>
    SideBySide,
}

/// <summary>
/// A parsed unified diff as an HTML fragment: the desktop's Raw tab, for a browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>It draws what the parser and the fold decided, and decides nothing.</b> Line numbers come
/// from <see cref="UnifiedDiffParser"/>, pairing from <see cref="SideBySideDiff"/>, changed
/// words from <see cref="IntralineEmphasis"/>, all of which the desktop's <c>DiffView</c> already
/// draws. One diff source per view: unified and side by side of one document show the same
/// changed lines because they are built from the same lines.
/// </para>
/// <para>
/// <b>Every code row says where it is, as a contract</b> (docs/web/PLAN.md Step 1):
/// <c>data-old-line</c> and <c>data-line</c> for each side that has the line, and
/// <c>data-hunk</c> for the hunk it stands in. A link to a line uses them now; a review comment
/// or a per-hunk merge choice will attach to them later.
/// </para>
/// <para>
/// <b>An empty side keeps no tint</b>, for <c>DiffView</c>'s reason: a deletion has no
/// counterpart on the right, and tinting the void green would claim an addition that never
/// happened.
/// </para>
/// </remarks>
public static class TextDiffHtml
{
    /// <summary>The fragment for <paramref name="document"/>, highlighted as <paramref name="language"/>.</summary>
    /// <remarks>Changed words are found here if the caller has not already annotated the document.</remarks>
    public static string Fragment(DiffDocument document, SyntaxLanguage language, TextDiffView view = TextDiffView.Unified)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(language);

        var html = new StringBuilder(document.Lines.Count * 96);
        var split = view == TextDiffView.SideBySide;
        html.Append("<div class=\"cd-page cd-diff\" data-view=\"").Append(split ? "side-by-side" : "unified")
            .Append("\" data-added=\"").Append(N(document.Added))
            .Append("\" data-removed=\"").Append(N(document.Removed))
            .Append("\" data-hunks=\"").Append(N(document.Hunks));

        if (document.IsBinary || document.IsEmpty)
        {
            html.Append("\" data-hidden-chars=\"0\">\n<p class=\"cd-empty\">")
                .Append(document.IsBinary ? "Binary file: there is no text to compare." : "No changes.")
                .Append("</p>\n</div>\n");
            return html.ToString();
        }

        var annotated = document.Lines.Any(l => l.Emphasis.Count > 0) ? document : IntralineEmphasis.Annotate(document);
        var body = new StringBuilder(html.Capacity);
        var hidden = split
            ? SplitRows(body, SideBySideDiff.Build(annotated), language)
            : UnifiedRows(body, annotated.Lines, language);

        html.Append("\" data-hidden-chars=\"").Append(N(hidden)).Append("\">\n");
        CodeHtml.AppendNotice(html, hidden);
        html.Append("<div class=\"cd-rows\">\n").Append(body).Append("</div>\n</div>\n");
        return html.ToString();
    }

    private static int UnifiedRows(StringBuilder html, IReadOnlyList<DiffLine> lines, SyntaxLanguage language)
    {
        var hidden = 0;
        var blocks = new CodeHtml.Blocks(html);
        foreach (var line in lines)
        {
            blocks.Next();
            if (Spanning(html, line, ref hidden))
            {
                continue;
            }

            html.Append("<div class=\"cd-drow").Append(Tint(line)).Append('"');
            Position(html, old: true, line.OldLine);
            Position(html, old: false, line.NewLine);
            Hunk(html, line);
            html.Append("><span class=\"cd-num\">").Append(Num(line.OldLine))
                .Append("</span><span class=\"cd-num\">").Append(Num(line.NewLine))
                .Append("</span><span class=\"cd-marker\">").Append(MarkerText(line))
                .Append("</span><code class=\"cd-code\">");
            hidden += CodeHtml.AppendLine(html, line.Text, language, line.Emphasis);
            html.Append("</code></div>\n");
        }

        blocks.End();
        return hidden;
    }

    private static int SplitRows(StringBuilder html, IReadOnlyList<SideBySideRow> rows, SyntaxLanguage language)
    {
        var hidden = 0;
        var blocks = new CodeHtml.Blocks(html);
        foreach (var row in rows)
        {
            blocks.Next();
            if (row.Spanning is { } spanning && Spanning(html, spanning, ref hidden))
            {
                continue;
            }

            html.Append("<div class=\"cd-split\"");
            Hunk(html, row.Left ?? row.Right);
            html.Append('>');
            hidden += Cell(html, row.Left, old: true, language);
            html.Append("<span class=\"cd-rule\"></span>");
            hidden += Cell(html, row.Right, old: false, language);
            html.Append("</div>\n");
        }

        blocks.End();
        return hidden;
    }

    private static int Cell(StringBuilder html, DiffLine? line, bool old, SyntaxLanguage language)
    {
        if (line is null)
        {
            html.Append("<div class=\"cd-cell\"></div>");
            return 0;
        }

        var number = old ? line.OldLine : line.NewLine;
        html.Append("<div class=\"cd-cell").Append(Tint(line)).Append('"');
        Position(html, old, number);
        html.Append("><span class=\"cd-num\">").Append(Num(number)).Append("</span><code class=\"cd-code\">");
        var hidden = CodeHtml.AppendLine(html, line.Text, language, line.Emphasis);
        html.Append("</code></div>");
        return hidden;
    }

    /// <summary>A line that spans the view: a hunk header, a file title, a dimmed header line.</summary>
    private static bool Spanning(StringBuilder html, DiffLine line, ref int hidden)
    {
        switch (line.Kind)
        {
            case DiffLineKind.HunkHeader:
                html.Append("<div class=\"cd-hunk\"");
                Hunk(html, line);
                html.Append("><code class=\"cd-code\">");
                hidden += CodeHtml.AppendLine(html, line.Text, SyntaxLanguage.PlainText);
                html.Append("</code></div>\n");
                return true;
            case DiffLineKind.FileTitle when line.Title is { } title:
                html.Append("<div class=\"cd-file\" data-path=\"").Append(CodeHtml.E(title.Path))
                    .Append("\" data-change=\"").Append(title.Change.ToString().ToLowerInvariant())
                    .Append("\"><span class=\"cd-file-path\">");
                hidden += CodeHtml.AppendLine(html, title.OldPath is null ? title.Path : $"{title.OldPath} -> {title.Path}", SyntaxLanguage.PlainText);
                html.Append("</span> <span class=\"cd-file-count\">+").Append(N(title.Added))
                    .Append(" &#8722;").Append(N(title.Removed)).Append("</span></div>\n");
                return true;
            case DiffLineKind.FileTitle or DiffLineKind.FileHeader or DiffLineKind.NoNewline:
                html.Append("<div class=\"cd-meta\"><code class=\"cd-code\">");
                hidden += CodeHtml.AppendLine(html, line.Text, SyntaxLanguage.PlainText);
                html.Append("</code></div>\n");
                return true;
            default:
                return false;
        }
    }

    /// <remarks>
    /// The parser leaves an added line without an old number and a removed one without a new
    /// number, so the number's presence is the whole test. A check on the line's kind as well was
    /// written first and removed: a mutation showed it could never change the output.
    /// </remarks>
    private static void Position(StringBuilder html, bool old, int? number)
    {
        if (number is not { } n)
        {
            return;
        }

        html.Append(old ? " data-old-line=\"" : " data-line=\"").Append(N(n)).Append('"');
    }

    private static void Hunk(StringBuilder html, DiffLine? line)
    {
        if (line is { HunkIndex: >= 0 })
        {
            html.Append(" data-hunk=\"").Append(N(line.HunkIndex)).Append('"');
        }
    }

    private static string Tint(DiffLine line) => line.Kind switch
    {
        DiffLineKind.Added => " cd-added",
        DiffLineKind.Removed => " cd-removed",
        _ => "",
    };

    private static string MarkerText(DiffLine line) => line.Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "&#8722;",
        _ => "",
    };

    private static string Num(int? value) => value is { } v ? N(v) : "";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
