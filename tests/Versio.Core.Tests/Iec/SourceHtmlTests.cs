using System.Globalization;
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

    /// <summary>
    /// Unified is the file as it is, as the desktop draws it: an edited line is its new text alone,
    /// tinted. The old text is side by side's. The web drew it struck above the new one until the
    /// owner put the two hosts side by side on a real pull request (2026-10-23).
    /// </summary>
    [Fact]
    public void A_changed_line_in_unified_is_the_new_line_alone()
    {
        var html = Marked("a := 1;\nb := 2;\n", "a := 1;\nb := 3;\n");

        Assert.Contains("data-marked=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"cd-row cd-added\" data-line=\"2\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("cd-removed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<del>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pure_deletion_is_marked_on_the_line_now_in_its_place_with_the_desktops_words()
    {
        var above = Marked("a := 1;\nb := 2;\nc := 3;\n", "a := 1;\nc := 3;\n");
        var below = Marked("a := 1;\nb := 2;\n", "a := 1;\n");

        Assert.Contains("<div class=\"cd-row cd-removed-above\" data-line=\"2\"", above, StringComparison.Ordinal);
        Assert.Contains("title=\"1 line removed above since the last commit. Side by side shows them.\"", above, StringComparison.Ordinal);
        Assert.Contains("<div class=\"cd-row cd-removed-below\" data-line=\"1\"", below, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comparison_folds_unless_the_whole_file_is_asked_for()
    {
        var before = string.Concat(Enumerable.Range(1, 20).Select(i => $"x{i} := {i};\n"));
        var after = before.Replace("x10 := 10;", "x10 := 11;", StringComparison.Ordinal);
        var rows = StSourceLayout.Mark(StSourceLayout.Lay(after), before, after);

        var folded = SourceHtml.Fragment(rows, SyntaxLanguage.PlainText, fullFile: false);
        var whole = SourceHtml.Fragment(rows, SyntaxLanguage.PlainText, fullFile: true);
        var split = SourceHtml.Fragment(rows, SyntaxLanguage.PlainText, SourceView.SideBySide, fullFile: false);

        // Git's three lines either side of line 10: 1-6 and 14-20 fold.
        Assert.Contains("<div class=\"cd-fold\" data-folded=\"6\"", folded, StringComparison.Ordinal);
        Assert.Contains("<div class=\"cd-fold\" data-folded=\"7\"", folded, StringComparison.Ordinal);
        Assert.DoesNotContain("cd-fold", whole, StringComparison.Ordinal);
        Assert.Contains("cd-fold", split, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rows themselves, which both hosts draw: no removed line, and the pure deletion's mark on
    /// the line after it. The page alone cannot show a removed line left in (it has no way to draw
    /// one any more), so this is pinned where the rule lives.
    /// </summary>
    [Fact]
    public void Unified_rows_hold_no_removed_line_and_mark_the_line_after_a_pure_deletion()
    {
        var rows = StSourceLayout.Unified(StSourceLayout.Mark(StSourceLayout.Lay("a := 1;\nc := 3;\n"), "a := 1;\nb := 2;\nc := 3;\n", "a := 1;\nc := 3;\n"));

        Assert.DoesNotContain(rows, r => r is SourceRemovedLine);
        var after = Assert.Single(rows.OfType<SourceCodeLine>(), l => l.RemovedNear is not null);
        Assert.Equal(("c := 3;", new SourceRemoval(1, false)), (after.Text, after.RemovedNear));
    }

    /// <summary>
    /// The page's rows are the desktop's rows: the file lines a unified, folded comparison draws are
    /// exactly those of <see cref="StSourceLayout.Unified"/> over <see cref="StSourceLayout.Fold(IReadOnlyList{SourceRow}, int)"/>,
    /// which is what <c>DiffView</c> sets as its items. A renderer that reads the rows its own way
    /// fails here.
    /// </summary>
    [Fact]
    public void A_unified_comparison_draws_the_rows_the_desktop_draws()
    {
        var before = string.Concat(Enumerable.Range(1, 30).Select(i => $"x{i} := {i};\n"));
        var after = before.Replace("x5 := 5;\n", "", StringComparison.Ordinal).Replace("x20 := 20;", "x20 := 0;", StringComparison.Ordinal);
        var rows = StSourceLayout.Mark(StSourceLayout.Lay(after), before, after);

        var html = SourceHtml.Fragment(rows, SyntaxLanguage.PlainText, fullFile: false);
        var drawn = Regex.Matches(html, "data-file-line=\"(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        var desktop = StSourceLayout.Unified(StSourceLayout.Fold(rows)).OfType<SourceCodeLine>().Select(l => l.FileLine);

        Assert.Equal(desktop, drawn);
    }

    [Fact]
    public void Side_by_side_only_when_something_is_marked()
    {
        var unchanged = Html("a := 1;\n", SourceView.SideBySide);
        var changed = Marked("a := 1;\n", "a := 2;\n", SourceView.SideBySide);

        Assert.Contains("data-view=\"unified\"", unchanged, StringComparison.Ordinal);
        Assert.DoesNotContain("cd-split", unchanged, StringComparison.Ordinal);
        Assert.Contains("data-view=\"side-by-side\"", changed, StringComparison.Ordinal);
        // Each cell names its own version's file line too, beside the IDE's section number: that is
        // what a review comment on it is pinned to (docs/web/PLAN.md, *Pull request review*, C3).
        Assert.Contains("<div class=\"cd-cell cd-removed\" data-old-line=\"1\" data-old-file-line=\"1\">", changed, StringComparison.Ordinal);
        Assert.Contains("<div class=\"cd-cell cd-added\" data-line=\"1\" data-file-line=\"1\">", changed, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unchanged line of a comparison names where it sat in the older file as well: a review
    /// comment on the older side of an unchanged line belongs on THAT line, and the two differ as
    /// soon as anything above it was added or removed.
    /// </summary>
    [Fact]
    public void An_unchanged_line_carries_its_older_file_line_on_both_layouts()
    {
        var unified = Marked("a := 1;\nb := 2;\n", "x := 0;\na := 1;\nb := 2;\n");
        var split = Marked("a := 1;\nb := 2;\n", "x := 0;\na := 1;\nb := 2;\n", SourceView.SideBySide);

        Assert.Contains("data-file-line=\"2\" data-old-file-line=\"1\"", unified, StringComparison.Ordinal);
        Assert.Contains("data-old-line=\"1\" data-old-file-line=\"1\"", split, StringComparison.Ordinal);
        Assert.Contains("data-line=\"2\" data-file-line=\"2\"", split, StringComparison.Ordinal);
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
