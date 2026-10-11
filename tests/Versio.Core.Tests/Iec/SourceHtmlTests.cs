using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Versio.Core.Diff;
using Versio.Core.Iec;
using Xunit;

namespace Versio.Core.Tests.Iec;

/// <summary>
/// The Rendered source as HTML, for the web viewer: the code is the file and only the file,
/// nothing from it reaches the page as markup, every row says which line it is, and a character
/// that hides what the code does is shown rather than passed on.
/// </summary>
public sealed partial class SourceHtmlTests
{
    private const string Declaration =
        "FUNCTION_BLOCK FB_Alarm\nVAR_INPUT\n\tCondition : BOOL;\nEND_VAR";

    private const string Implementation =
        "// Evaluate Inputs\nIF NOT Latch THEN\n\tCondition := FALSE;\nEND_IF";

    private static readonly SyntaxLanguage St = LanguageCatalog.StructuredText;

    private static string Html(string content, SourceView view = SourceView.Unified) =>
        SourceHtml.Fragment(StSourceLayout.Lay(content), St, view);

    private static string Marked(string committed, string current, SourceView view = SourceView.Unified) =>
        SourceHtml.Fragment(StSourceLayout.Mark(StSourceLayout.Lay(current), committed, current), St, view);

    /// <summary>The text a reader sees: every tag removed, every entity decoded.</summary>
    private static string Visible(string html) => WebUtility.HtmlDecode(Tags().Replace(html, ""));

    private static int Count(string html, string what) =>
        (html.Length - html.Replace(what, "", StringComparison.Ordinal).Length) / what.Length;

    [Fact]
    public void Markup_in_the_file_is_text_on_the_page()
    {
        var html = Html("x := '<script>alert(1)</script>'; (* a & b \"q\" *)");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("a &amp; b &quot;q&quot;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_character_of_every_line_is_drawn()
    {
        // Gaps between tokenizer spans are drawn as plain text, so the code on the page is the
        // file. Checked line by line against what was laid out.
        var file = StTextCodec.Join(Declaration, Implementation + "\nx := 16#FF; s := 'it''s'; {attribute 'x'}");
        var rows = StSourceLayout.Lay(file);
        var html = SourceHtml.Fragment(rows, St);

        foreach (var line in rows.OfType<SourceCodeLine>().Where(l => l.Text.Length > 0))
        {
            var row = Regex.Match(html, $"data-file-line=\"{line.FileLine}\"><span class=\"cd-num\">\\d+</span><code class=\"cd-code\">(.*?)</code>");
            Assert.True(row.Success, $"no row for file line {line.FileLine}");
            Assert.Equal(line.Text, Visible(row.Groups[1].Value));
        }
    }

    [Fact]
    public void Keywords_comments_and_strings_carry_their_class()
    {
        var html = Html("IF x THEN (* note *) s := 'text'; END_IF");

        Assert.Contains("<span class=\"cd-t-keyword\">IF</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"cd-t-comment\">(* note *)</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"cd-t-string\">&#39;text&#39;</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Dividers_and_section_numbers_are_the_layouts()
    {
        var html = Html(StTextCodec.Join(Declaration, Implementation));

        Assert.Contains("data-divider=\"Declaration\"", html, StringComparison.Ordinal);
        Assert.Contains("data-divider=\"Implementation\"", html, StringComparison.Ordinal);

        // The implementation restarts at 1 behind its divider, as the IDE numbers it.
        var afterDivider = html[html.IndexOf("data-divider=\"Implementation\"", StringComparison.Ordinal)..];
        Assert.Matches("^[^>]*>.*?\\n<div class=\"cd-row\" data-line=\"1\" ", afterDivider);
    }

    [Fact]
    public void Every_code_row_says_which_line_it_is()
    {
        // The contract a link, a comment or a merge choice attaches to.
        var html = Marked(StTextCodec.Join(Declaration, Implementation),
            StTextCodec.Join(Declaration, Implementation.Replace("FALSE", "TRUE", StringComparison.Ordinal)));

        var rows = Regex.Matches(html, "<div class=\"cd-row(?: [^\"]*)?\"([^>]*)>");
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Matches("data-(old-)?line=\"\\d+\"", r.Groups[1].Value));
    }

    [Fact]
    public void A_changed_line_is_drawn_removed_then_added()
    {
        var html = Marked("a := 1;\nb := 2;\n", "a := 1;\nb := 3;\n");

        Assert.Contains("data-marked=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"cd-row cd-removed\" data-old-line=\"2\"", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"cd-row cd-added\" data-line=\"2\"", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("cd-removed", StringComparison.Ordinal) < html.IndexOf("cd-added", StringComparison.Ordinal));
        Assert.Contains("<del>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Side_by_side_only_when_something_is_marked()
    {
        var unchanged = Html("a := 1;\n", SourceView.SideBySide);
        var changed = Marked("a := 1;\n", "a := 2;\n", SourceView.SideBySide);

        Assert.Contains("data-view=\"unified\"", unchanged, StringComparison.Ordinal);
        Assert.DoesNotContain("cd-split", unchanged, StringComparison.Ordinal);
        Assert.Contains("data-view=\"side-by-side\"", changed, StringComparison.Ordinal);
        Assert.Contains("<div class=\"cd-cell cd-removed\" data-old-line=\"1\">", changed, StringComparison.Ordinal);
        Assert.Contains("<div class=\"cd-cell cd-added\" data-line=\"1\">", changed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_direction_override_is_shown_as_a_marker_and_never_passed_on()
    {
        // Trojan Source: the override makes the comment appear to close before the check.
        var html = Html("IF isAdmin THEN (* \u202E } \u2066if (isAdmin)\u2069 \u2066 begin admins only *)");

        Assert.DoesNotContain("\u202E", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2066", html, StringComparison.Ordinal);
        Assert.Contains("data-char=\"U+202E\"", html, StringComparison.Ordinal);
        Assert.Contains("data-hidden-chars=\"4\"", html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"cd-notice\">This file contains 4 hidden or look-alike characters", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Zero_width_and_control_characters_are_shown_too_and_a_tab_is_not()
    {
        var html = Html("a\u200B := 1;\tb := \u0007;");

        Assert.Contains("data-char=\"U+200B\"", html, StringComparison.Ordinal);
        Assert.Contains("data-char=\"U+0007\"", html, StringComparison.Ordinal);
        Assert.Contains("\t", html, StringComparison.Ordinal);
        Assert.Contains("data-hidden-chars=\"2\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_mixing_latin_and_cyrillic_is_marked_and_announced()
    {
        // "Compressor" with a Cyrillic first letter: it reads the same and is another variable.
        var lookalike = (char)0x0421 + "ompressor";
        var html = Html($"IF {lookalike} THEN x := 1; END_IF");

        Assert.Contains("<span class=\"cd-confusable\" data-scripts=\"Latin and Cyrillic\"", html, StringComparison.Ordinal);
        Assert.Contains(lookalike, html, StringComparison.Ordinal);
        Assert.Contains("data-hidden-chars=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("This file contains 1 hidden or look-alike character ", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Honest_non_latin_text_is_not_marked()
    {
        // A name wholly in one script, and Russian beside English in a comment or a string, are
        // ordinary; only a name that mixes scripts is the attack.
        var russian = new string([(char)0x0414, (char)0x0430, (char)0x0442, (char)0x0447, (char)0x0438, (char)0x043A]);
        var html = Html($"{russian} := 1; (* {russian} sensor *) s := '{russian} ok';");

        Assert.DoesNotContain("cd-confusable", html, StringComparison.Ordinal);
        Assert.Contains("data-hidden-chars=\"0\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_file_says_nothing_about_hidden_characters()
    {
        var html = Html(StTextCodec.Join(Declaration, Implementation));

        Assert.Contains("data-hidden-chars=\"0\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("cd-notice", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_files_come_in_blocks_the_browser_can_skip()
    {
        var file = string.Join('\n', Enumerable.Range(1, (SourceHtml.BlockRows * 2) + 1).Select(i => $"x{i} := {i};"));

        var html = Html(file);

        Assert.Equal(3, Count(html, "<div class=\"cd-block\">"));
        Assert.Equal(Count(html, "<div class=\"cd-block\">") + 2, Count(html, "</div>\n") - Count(html, "<div class=\"cd-row\" "));
    }

    [Fact]
    public void Every_class_the_fragment_uses_is_in_the_stylesheet()
    {
        // The class names are a contract with CodePageAssets; a rename that missed one side
        // would be a silent unstyled page.
        var html = new StringBuilder()
            .Append(Html(StTextCodec.Join(Declaration, Implementation) + "\n'\u202E' 16#1 {pragma}"))
            .Append(Marked("a := 1;\n", "a := 2;\n"))
            .Append(Marked("a := 1;\n", "a := 2;\n", SourceView.SideBySide))
            .ToString();

        var used = Regex.Matches(html, "class=\"([^\"]+)\"").SelectMany(m => m.Groups[1].Value.Split(' ')).Distinct().ToList();
        Assert.NotEmpty(used);
        Assert.All(used, c => Assert.Contains("." + c, CodePageAssets.Css, StringComparison.Ordinal));
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();
}
