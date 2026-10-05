using Versio.Core.Text;
using Xunit;

namespace Versio.Core.Tests.Text;

/// <summary>
/// The Markdown reader behind the diff pane's Rendered tab.
/// </summary>
/// <remarks>
/// Cases are taken from what this repository's own documents do - hard-wrapped numbered lists,
/// pipe tables with code in their cells, identifiers full of underscores - because those are
/// the documents it will be pointed at first.
/// </remarks>
public sealed class MarkdownTests
{
    private static string Text(IReadOnlyList<MdRun> runs) => string.Concat(runs.Select(r => r.Text));

    [Fact]
    public void Headings_atx_and_setext()
    {
        var blocks = Markdown.Parse("# One\n\n## Two ##\n\nThree\n=====\n\nFour\n---\n");

        Assert.Collection(blocks,
            b => Assert.Equal((1, "One"), (((MdHeading)b).Level, Text(((MdHeading)b).Runs))),
            b => Assert.Equal((2, "Two"), (((MdHeading)b).Level, Text(((MdHeading)b).Runs))),
            b => Assert.Equal((1, "Three"), (((MdHeading)b).Level, Text(((MdHeading)b).Runs))),
            b => Assert.Equal((2, "Four"), (((MdHeading)b).Level, Text(((MdHeading)b).Runs))));
    }

    [Fact]
    public void A_rule_after_a_blank_line_is_a_rule_not_a_heading()
    {
        var blocks = Markdown.Parse("Text.\n\n---\n\nMore.");

        Assert.Collection(blocks,
            b => Assert.IsType<MdParagraph>(b),
            b => Assert.IsType<MdRule>(b),
            b => Assert.IsType<MdParagraph>(b));
    }

    [Fact]
    public void Hard_wrapped_lines_join_into_one_paragraph()
    {
        var paragraph = Assert.IsType<MdParagraph>(
            Assert.Single(Markdown.Parse("A PLC project in git\nis the program somebody\nmeant to ship.")));

        Assert.Equal("A PLC project in git is the program somebody meant to ship.", Text(paragraph.Runs));
    }

    [Fact]
    public void Inline_styles()
    {
        var runs = Markdown.Inline("**bold** and *em* and `code` and ~~gone~~ and ***both***");

        Assert.Contains(runs, r => r is { Text: "bold", Style: MdStyle.Strong });
        Assert.Contains(runs, r => r is { Text: "em", Style: MdStyle.Emphasis });
        Assert.Contains(runs, r => r is { Text: "code", Style: MdStyle.Code });
        Assert.Contains(runs, r => r is { Text: "gone", Style: MdStyle.Strike });
        Assert.Contains(runs, r => r is { Text: "both", Style: MdStyle.Strong | MdStyle.Emphasis });
    }

    [Fact]
    public void Styles_nest()
    {
        var runs = Markdown.Inline("~~**Run `tests`.**~~ done");

        Assert.Contains(runs, r => r is { Text: "Run ", Style: MdStyle.Strike | MdStyle.Strong });
        Assert.Contains(runs, r => r.Text == "tests"
                                   && r.Style == (MdStyle.Strike | MdStyle.Strong | MdStyle.Code));
        Assert.Equal("Run tests. done", Text(runs));
    }

    [Fact]
    public void Underscores_inside_identifiers_are_not_emphasis()
    {
        // FB_Motor_Speed is a name. Reading it as F B <em>Motor</em> Speed is the classic
        // Markdown bug, and a PLC repository is made of these.
        var runs = Markdown.Inline("Call FB_Motor_Speed and _really_ mean it.");

        Assert.Contains(runs, r => r.Text.Contains("FB_Motor_Speed", StringComparison.Ordinal)
                                   && r.Style == MdStyle.None);
        Assert.Contains(runs, r => r is { Text: "really", Style: MdStyle.Emphasis });
    }

    [Fact]
    public void A_lone_star_between_spaces_is_arithmetic()
    {
        Assert.Equal([new MdRun("a * b * c", MdStyle.None)], Markdown.Inline("a * b * c"));
    }

    [Fact]
    public void Code_spans_hide_delimiters_and_keep_double_backtick_content()
    {
        var runs = Markdown.Inline("``a ` b`` then `*not em*`");

        Assert.Contains(runs, r => r is { Text: "a ` b", Style: MdStyle.Code });
        Assert.Contains(runs, r => r is { Text: "*not em*", Style: MdStyle.Code });
    }

    [Fact]
    public void Links_carry_their_target_and_their_styled_label()
    {
        var runs = Markdown.Inline("Read [`../STATUS.md`](../STATUS.md) and <https://example.com>.");

        Assert.Contains(runs, r => r is { Text: "../STATUS.md", Style: MdStyle.Code, Link: "../STATUS.md" });
        Assert.Contains(runs, r => r is { Text: "https://example.com", Link: "https://example.com" });
    }

    [Fact]
    public void Escapes_are_literal()
    {
        Assert.Equal("*not em* and [no link]", Text(Markdown.Inline(@"\*not em\* and \[no link\]")));
    }

    [Fact]
    public void Unclosed_delimiters_stay_as_text()
    {
        Assert.Equal("**open and `tick", Text(Markdown.Inline("**open and `tick")));
    }

    [Fact]
    public void Fenced_code_is_verbatim_with_its_language()
    {
        var code = Assert.IsType<MdCode>(Assert.Single(
            Markdown.Parse("```st\nIF a THEN\n    **b** := 1;\nEND_IF\n```")));

        Assert.Equal("st", code.Language);
        Assert.Equal("IF a THEN\n    **b** := 1;\nEND_IF", code.Text);
    }

    [Fact]
    public void An_unclosed_fence_runs_to_the_end_rather_than_throwing()
    {
        var code = Assert.IsType<MdCode>(Assert.Single(Markdown.Parse("```\nnever closed")));
        Assert.Equal("never closed", code.Text);
    }

    [Fact]
    public void Tables_with_alignment_and_pipes_in_code()
    {
        var table = Assert.IsType<MdTable>(Assert.Single(Markdown.Parse(
            "| Name | Count |\n|:---|---:|\n| `a | b` | 3 |\n| short |")));

        Assert.Equal(["Name", "Count"], table.Header.Select(Text));
        Assert.Equal([MdAlign.Left, MdAlign.Right], table.Align);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("a | b", Text(table.Rows[0][0]));

        // A short row is padded to the header's width.
        Assert.Equal(2, table.Rows[1].Count);
        Assert.Equal(string.Empty, Text(table.Rows[1][1]));
    }

    [Fact]
    public void A_table_with_an_empty_header_still_parses()
    {
        // docs/outpost/PLAN.md opens with exactly this: a key/value table with no header.
        var table = Assert.IsType<MdTable>(Assert.Single(Markdown.Parse(
            "| | |\n|---|---|\n| Document | The design |")));

        Assert.Single(table.Rows);
        Assert.Equal("Document", Text(table.Rows[0][0]));
    }

    [Fact]
    public void Ordered_list_with_hard_wrapped_items_and_a_start_number()
    {
        var list = Assert.IsType<MdList>(Assert.Single(Markdown.Parse(
            "3. First item\n   wraps here.\n4. Second item\nlazily wraps.\n")));

        Assert.True(list.Ordered);
        Assert.Equal(3, list.Start);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal("First item wraps here.",
            Text(Assert.IsType<MdParagraph>(Assert.Single(list.Items[0])).Runs));
        Assert.Equal("Second item lazily wraps.",
            Text(Assert.IsType<MdParagraph>(Assert.Single(list.Items[1])).Runs));
    }

    [Fact]
    public void Nested_bullets()
    {
        var list = Assert.IsType<MdList>(Assert.Single(Markdown.Parse(
            "- outer\n  - inner one\n  - inner two\n- second outer")));

        Assert.False(list.Ordered);
        Assert.Equal(2, list.Items.Count);
        var inner = Assert.IsType<MdList>(list.Items[0][1]);
        Assert.Equal(2, inner.Items.Count);
    }

    [Fact]
    public void Blank_lines_between_items_keep_one_list()
    {
        var list = Assert.IsType<MdList>(Assert.Single(Markdown.Parse("- a\n\n- b\n\n- c")));
        Assert.Equal(3, list.Items.Count);
    }

    [Fact]
    public void A_number_at_the_start_of_a_wrapped_line_does_not_start_a_list()
    {
        // Only "1." may interrupt a paragraph, so a sentence wrapped before a year stays prose.
        var paragraph = Assert.IsType<MdParagraph>(Assert.Single(Markdown.Parse(
            "This was measured in\n2026. It held.")));

        Assert.Equal("This was measured in 2026. It held.", Text(paragraph.Runs));
    }

    [Fact]
    public void Quotes_parse_their_own_blocks()
    {
        var quote = Assert.IsType<MdQuote>(Assert.Single(Markdown.Parse("> # Title\n> body\n> - item")));

        Assert.Collection(quote.Blocks,
            b => Assert.IsType<MdHeading>(b),
            b => Assert.IsType<MdParagraph>(b),
            b => Assert.IsType<MdList>(b));
    }

    [Fact]
    public void Html_comments_are_not_shown()
    {
        var blocks = Markdown.Parse("<!-- a note\nto the editor -->\nVisible.");
        Assert.Equal("Visible.", Text(Assert.IsType<MdParagraph>(Assert.Single(blocks)).Runs));
    }

    [Fact]
    public void Crlf_input_parses_the_same()
    {
        Assert.Equal(
            Markdown.Parse("# A\n\n- b\n- c\n").Count,
            Markdown.Parse("# A\r\n\r\n- b\r\n- c\r\n").Count);
    }

    [Fact]
    public void This_repository_s_own_documents_parse_without_throwing()
    {
        // The real corpus, not a sample: whatever is in docs/ today. A parser that throws on
        // a document leaves the pane on a crash instead of a page.
        var docs = FindDocs();
        Assert.True(docs.Length > 5, "docs/ was not found from the test directory");

        foreach (var path in docs)
        {
            var blocks = Markdown.Parse(System.IO.File.ReadAllText(path));
            Assert.NotEmpty(blocks);
        }
    }

    private static string[] FindDocs()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "docs")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? []
            : System.IO.Directory.GetFiles(System.IO.Path.Combine(dir.FullName, "docs"), "*.md",
                System.IO.SearchOption.AllDirectories);
    }
}
