using Versio.Core.Text;
using Xunit;

namespace Versio.Core.Tests.Text;

/// <summary>
/// Markdown as HTML for the browser-drawn Rendered view. The page runs script (mermaid), so
/// the tests that matter most are the ones proving a document cannot put markup into it.
/// </summary>
public sealed class MarkdownHtmlTests
{
    private static string Html(string markdown) => MarkdownHtml.Fragment(Markdown.Parse(markdown));

    [Fact]
    public void Raw_html_in_a_document_stays_text()
    {
        var html = Html("Hello <script>alert(1)</script> and <img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_inside_code_and_tables_stays_text()
    {
        var html = Html("`<b>`\n\n| a |\n|---|\n| <i>x</i> |\n\n```\n</pre><script>x</script>\n```");

        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<i>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_never_becomes_an_href()
    {
        // The browser must not be able to follow anything by itself: the page's click handler
        // passes data-href to the app, which decides. So even javascript: is inert here.
        var html = Html("[x](javascript:alert(1)) and [y](\"><script>z</script>)");

        Assert.DoesNotContain(" href=", html, StringComparison.Ordinal);
        Assert.Contains("data-href=\"javascript:alert(1)\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Mermaid_fences_become_mermaid_blocks_with_escaped_source()
    {
        var html = Html("```mermaid\nflowchart TB\n  a[\"x\"] --> b\n```");

        Assert.Contains("<pre class=\"mermaid\">flowchart TB\n  a[&quot;x&quot;] --&gt; b</pre>", html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Other_fences_are_code()
    {
        Assert.Contains("<pre><code>IF a THEN</code></pre>", Html("```st\nIF a THEN\n```"), StringComparison.Ordinal);
    }

    [Fact]
    public void Tables_keep_alignment_and_drop_an_empty_header()
    {
        var aligned = Html("| A | B |\n|:-:|--:|\n| 1 | 2 |");
        Assert.Contains("<th style=\"text-align:center\">A</th>", aligned, StringComparison.Ordinal);
        Assert.Contains("<td style=\"text-align:right\">2</td>", aligned, StringComparison.Ordinal);

        var keyValue = Html("| | |\n|---|---|\n| k | v |");
        Assert.DoesNotContain("<thead>", keyValue, StringComparison.Ordinal);
        Assert.Contains("<td>k</td>", keyValue, StringComparison.Ordinal);
    }

    [Fact]
    public void Styles_nest_in_a_fixed_order()
    {
        Assert.Contains("<del><strong>done</strong></del>", Html("~~**done**~~"), StringComparison.Ordinal);
    }

    [Fact]
    public void Tight_list_items_have_no_paragraph_and_ordered_lists_keep_their_start()
    {
        var html = Html("3. a\n4. b");
        Assert.Contains("<ol start=\"3\">", html, StringComparison.Ordinal);
        Assert.Contains("<li>a</li>", html, StringComparison.Ordinal);
    }
}
