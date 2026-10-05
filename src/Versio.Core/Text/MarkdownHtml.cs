using System.Net;
using System.Text;

namespace Versio.Core.Text;

/// <summary>
/// Parsed Markdown as an HTML fragment, for the pane's browser-drawn Rendered view.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why HTML at all, when MarkdownView draws a FlowDocument.</b> Two things the owner saw on
/// 2026-10-04 that a FlowDocument cannot do well: a GFM table whose last column took almost no
/// width, so every row stood hundreds of pixels tall (FlowDocument tables have no auto layout),
/// and Mermaid diagrams, which only exist once a browser runs mermaid.js. A browser lays tables
/// out by content and draws the diagrams, so the Rendered view is a page now and the
/// FlowDocument is its fallback for a machine without the WebView2 runtime.
/// </para>
/// <para>
/// <b>Everything from the document is escaped. No exceptions.</b> The parser already treats raw
/// HTML as text; this keeps it text. The page runs script - mermaid needs it - so the one thing
/// that must never happen is a document's own markup reaching the page as markup. Link targets
/// go into a <c>data-href</c> attribute, never <c>href</c>, so not even a <c>javascript:</c>
/// URL can be followed by the browser; the page's own click handler hands the raw target to
/// the app, which decides.
/// </para>
/// <para>
/// A fenced block whose language is <c>mermaid</c> becomes <c>&lt;pre class="mermaid"&gt;</c>
/// with its source escaped, which is the form mermaid reads (it takes the element's text, so
/// escaping changes nothing it sees).
/// </para>
/// </remarks>
public static class MarkdownHtml
{
    /// <summary>The fragment for a whole document's blocks.</summary>
    public static string Fragment(IReadOnlyList<MdBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var html = new StringBuilder();
        foreach (var block in blocks)
        {
            Block(html, block);
        }

        return html.ToString();
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static void Block(StringBuilder html, MdBlock block)
    {
        switch (block)
        {
            case MdHeading h:
                html.Append("<h").Append(h.Level).Append('>');
                Runs(html, h.Runs);
                html.Append("</h").Append(h.Level).Append(">\n");
                break;

            case MdParagraph p:
                html.Append("<p>");
                Runs(html, p.Runs);
                html.Append("</p>\n");
                break;

            case MdCode { Language: var lang } c when lang.Equals("mermaid", StringComparison.OrdinalIgnoreCase):
                html.Append("<pre class=\"mermaid\">").Append(E(c.Text)).Append("</pre>\n");
                break;

            case MdCode c:
                html.Append("<pre><code>").Append(E(c.Text)).Append("</code></pre>\n");
                break;

            case MdQuote q:
                html.Append("<blockquote>\n");
                foreach (var inner in q.Blocks)
                {
                    Block(html, inner);
                }

                html.Append("</blockquote>\n");
                break;

            case MdList l:
                html.Append(l.Ordered
                    ? (l.Start == 1 ? "<ol>\n" : $"<ol start=\"{l.Start}\">\n")
                    : "<ul>\n");
                foreach (var item in l.Items)
                {
                    html.Append("<li>");

                    // A single paragraph item is tight: no <p>, so the list keeps the spacing
                    // of a list rather than of separate paragraphs.
                    if (item is [MdParagraph only])
                    {
                        Runs(html, only.Runs);
                    }
                    else
                    {
                        foreach (var inner in item)
                        {
                            Block(html, inner);
                        }
                    }

                    html.Append("</li>\n");
                }

                html.Append(l.Ordered ? "</ol>\n" : "</ul>\n");
                break;

            case MdTable t:
                Table(html, t);
                break;

            case MdRule:
                html.Append("<hr>\n");
                break;
        }
    }

    private static void Table(StringBuilder html, MdTable table)
    {
        html.Append("<table>\n");

        // "| | |" - a key/value table - has no header to show, and an empty header strip
        // reads as a rendering fault.
        if (table.Header.Any(h => h.Count > 0 && h.Any(r => r.Text.Length > 0)))
        {
            html.Append("<thead><tr>");
            for (var c = 0; c < table.Header.Count; c++)
            {
                html.Append("<th").Append(Align(table, c)).Append('>');
                Runs(html, table.Header[c]);
                html.Append("</th>");
            }

            html.Append("</tr></thead>\n");
        }

        html.Append("<tbody>\n");
        foreach (var row in table.Rows)
        {
            html.Append("<tr>");
            for (var c = 0; c < row.Count; c++)
            {
                html.Append("<td").Append(Align(table, c)).Append('>');
                Runs(html, row[c]);
                html.Append("</td>");
            }

            html.Append("</tr>\n");
        }

        html.Append("</tbody></table>\n");
    }

    private static string Align(MdTable table, int column) =>
        (column < table.Align.Count ? table.Align[column] : MdAlign.None) switch
        {
            MdAlign.Center => " style=\"text-align:center\"",
            MdAlign.Right => " style=\"text-align:right\"",
            _ => string.Empty,
        };

    private static void Runs(StringBuilder html, IReadOnlyList<MdRun> runs)
    {
        string? openLink = null;

        foreach (var run in runs)
        {
            if (run.Link != openLink)
            {
                if (openLink is not null)
                {
                    html.Append("</a>");
                }

                if (run.Link is not null)
                {
                    html.Append("<a data-href=\"").Append(E(run.Link))
                        .Append("\" title=\"").Append(E(run.Link)).Append("\">");
                }

                openLink = run.Link;
            }

            Styled(html, run);
        }

        if (openLink is not null)
        {
            html.Append("</a>");
        }
    }

    private static void Styled(StringBuilder html, MdRun run)
    {
        var open = new StringBuilder();
        var close = new StringBuilder();

        void Wrap(MdStyle flag, string tag)
        {
            if (run.Style.HasFlag(flag))
            {
                open.Append('<').Append(tag).Append('>');
                close.Insert(0, $"</{tag}>");
            }
        }

        Wrap(MdStyle.Strike, "del");
        Wrap(MdStyle.Strong, "strong");
        Wrap(MdStyle.Emphasis, "em");
        Wrap(MdStyle.Code, "code");

        html.Append(open)
            .Append(E(run.Text).Replace("\n", "<br>", StringComparison.Ordinal))
            .Append(close);
    }
}
