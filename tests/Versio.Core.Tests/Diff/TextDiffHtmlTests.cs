using System.Text.RegularExpressions;
using Versio.Core.Diff;
using Xunit;

namespace Versio.Core.Tests.Diff;

/// <summary>
/// The Raw diff as HTML: nothing from the file reaches the page as markup, every code row says
/// which line and hunk it is, and unified and side by side show the same changes.
/// </summary>
public sealed class TextDiffHtmlTests
{
    private static readonly SyntaxLanguage St = LanguageCatalog.StructuredText;

    private const string Old = "PROGRAM Main\nVAR\n\tx : INT;\nEND_VAR\nx := 1;\ny := x + 2;\n";
    private const string New = "PROGRAM Main\nVAR\n\tx : INT;\nEND_VAR\nx := 5;\ny := x + 2;\nz := 3;\n";

    private static string Html(string? old, string? @new, TextDiffView view = TextDiffView.Unified) =>
        TextDiffHtml.Fragment(UnifiedDiffParser.Parse(UnifiedDiffWriter.Write(old, @new)), St, view);

    /// <summary>The changes a view claims: removed old line numbers and added new ones.</summary>
    private static (HashSet<int> Removed, HashSet<int> Added) Changes(string html) =>
        (Regex.Matches(html, "cd-removed\" data-old-line=\"(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet(),
         Regex.Matches(html, "cd-added\" data-line=\"(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet());

    [Fact]
    public void Markup_in_the_file_is_text_on_the_page()
    {
        var html = Html("a := 1;\n", "a := '<img src=x onerror=alert(1)>';\n");

        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_side_is_numbered_only_where_the_line_exists()
    {
        var html = Html(Old, New);

        Assert.Contains("<div class=\"cd-drow cd-removed\" data-old-line=\"5\" data-hunk=\"0\">", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"cd-drow cd-added\" data-line=\"5\" data-hunk=\"0\">", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"cd-drow\" data-old-line=\"4\" data-line=\"4\" data-hunk=\"0\">", html, StringComparison.Ordinal);
        Assert.Contains("data-added=\"2\" data-removed=\"1\" data-hunks=\"1\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_code_row_says_which_line_and_hunk_it_is()
    {
        foreach (var view in new[] { TextDiffView.Unified, TextDiffView.SideBySide })
        {
            var html = Html(Old, New, view);
            var rows = Regex.Matches(html, "<div class=\"cd-(?:drow|cell)(?: [^\"]*)?\"([^>]*)>").Where(m => m.Groups[1].Value.Length > 0).ToList();

            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Matches("data-(old-)?line=\"\\d+\"", r.Groups[1].Value));
            Assert.Contains("<div class=\"cd-hunk\" data-hunk=\"0\">", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Unified_and_side_by_side_show_the_same_changes()
    {
        // One diff source per view; the two must not disagree about a single line.
        var unified = Changes(Html(Old, New));
        var split = Changes(Html(Old, New, TextDiffView.SideBySide));

        Assert.NotEmpty(unified.Removed);
        Assert.NotEmpty(unified.Added);
        Assert.Equal(unified.Removed, split.Removed);
        Assert.Equal(unified.Added, split.Added);
    }

    [Fact]
    public void A_changed_word_is_marked_inside_its_token()
    {
        var html = Html("x := 1;\n", "x := 5;\n");

        Assert.Contains("<mark class=\"cd-em\">1</mark>", html, StringComparison.Ordinal);
        Assert.Contains("<mark class=\"cd-em\">5</mark>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Side_by_side_leaves_the_empty_side_untinted()
    {
        var html = Html("a := 1;\n", "a := 1;\nb := 2;\n", TextDiffView.SideBySide);

        Assert.Contains("<div class=\"cd-cell\"></div><span class=\"cd-rule\"></span><div class=\"cd-cell cd-added\" data-line=\"2\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void No_changes_and_binary_files_say_so()
    {
        Assert.Contains("<p class=\"cd-empty\">No changes.</p>", Html("a\n", "a\n"), StringComparison.Ordinal);
        Assert.Contains("Binary file", TextDiffHtml.Fragment(new DiffDocument { IsBinary = true }, St), StringComparison.Ordinal);
    }

    [Fact]
    public void A_hidden_character_in_a_diff_is_shown_and_announced()
    {
        var html = Html("ok := TRUE;\n", "ok := TRUE;" + (char)0x202E + "\n");

        Assert.DoesNotContain(((char)0x202E).ToString(), html, StringComparison.Ordinal);
        Assert.Contains("data-char=\"U+202E\"", html, StringComparison.Ordinal);
        Assert.Contains("data-hidden-chars=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("This file contains 1 hidden character ", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_git_patch_with_file_titles_draws_a_title_per_file()
    {
        const string patch = "diff --git a/A.st b/A.st\nindex 1..2 100644\n--- a/A.st\n+++ b/A.st\n@@ -1 +1 @@\n-a\n+b\n";

        var html = TextDiffHtml.Fragment(UnifiedDiffParser.Parse(patch), St);

        Assert.Contains("<div class=\"cd-file\" data-path=\"A.st\" data-change=\"modified\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_class_the_fragment_uses_is_in_the_stylesheet()
    {
        const string patch = "diff --git a/A.st b/A.st\nindex 1..2 100644\n--- a/A.st\n+++ b/A.st\n@@ -1 +1 @@\n-a\n+b\n";
        var html = Html(Old, New) + Html(Old, New, TextDiffView.SideBySide) + Html("a\n", "a\n")
                   + Html("x := 1;\n", "x := 5;" + (char)0x200B + "\n") + TextDiffHtml.Fragment(UnifiedDiffParser.Parse(patch), St);

        var used = Regex.Matches(html, "class=\"([^\"]+)\"").SelectMany(m => m.Groups[1].Value.Split(' ')).Distinct().ToList();
        Assert.All(used, c => Assert.Matches("\\." + Regex.Escape(c) + "[\\s,{:>.\\[]", CodePageAssets.Css));
    }
}
