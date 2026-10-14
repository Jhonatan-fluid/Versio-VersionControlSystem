using System.Text.RegularExpressions;
using Versio.Core.Iec.Ladder;
using Xunit;
using static Versio.Core.Tests.Iec.Ladder.LadderTestNetworks;

namespace Versio.Core.Tests.Iec.Ladder;

/// <summary>
/// The compared block as HTML: nothing from the file reaches the page as markup, every element
/// carries what a host needs to find it, and every jump lands on a row that exists.
/// </summary>
public sealed partial class LadderHtmlTests
{
    private static LadderNetwork Rung(string p, string a, string b, string coil, string title = "", string comment = "") =>
        FromTree(S(L(No(p + "a", a)), L(No(p + "b", b)), L(Coil(p + "c", coil))), 1, title, comment);

    private static LadderBody Block(params LadderNetwork[] networks) =>
        new("Main", networks.Select((n, i) => n with { Number = i + 1 }).ToList());

    private static string Html(LadderBody? old, LadderBody @new, LadderView view = LadderView.Merged) =>
        LadderHtml.Fragment(LadderDiff.Compare(old, @new), LadderStyle.Default, view);

    private static int Count(string html, string what) =>
        (html.Length - html.Replace(what, "", StringComparison.Ordinal).Length) / what.Length;

    [Fact]
    public void Merged_is_the_default_and_a_changed_network_is_one_rung()
    {
        var html = Html(Block(Rung("o", "A", "B", "C")), Block(Rung("n", "A", "X", "C")));

        Assert.Contains("data-view=\"merged\"", html, StringComparison.Ordinal);
        Assert.Contains("ld-one", html, StringComparison.Ordinal);
        Assert.Equal(1, Count(html, "<svg "));
        Assert.Contains("data-key=\"n:nb\" data-change=\"modified\"", html, StringComparison.Ordinal);
        Assert.Contains(">was B<", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Merged_a_replaced_network_is_two_rungs_in_one_row_never_one_invented_rung()
    {
        var html = Html(Block(Rung("o", "A", "B", "C")), Block(Rung("n", "P", "Q", "R")));

        var row = Section(html, "replaced");
        Assert.Equal(2, Count(row, "<svg "));
        Assert.Contains("ld-old ld-c-removed", row, StringComparison.Ordinal);
        Assert.Contains("ld-new ld-c-added", row, StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_speaks_the_hosts_words_and_the_desktops_by_default()
    {
        var same = Block(Rung("o", "A", "B", "C"));
        var unchanged = LadderDiff.Compare(same, same);
        var added = LadderDiff.Compare(null, same);
        var web = new LadderWording("", "Not in the older version: every {0} is new.");

        Assert.Contains("No changes since the last commit.", LadderHtml.Fragment(unchanged, LadderStyle.Default), StringComparison.Ordinal);
        Assert.DoesNotContain("ld-note", LadderHtml.Fragment(unchanged, LadderStyle.Default, wording: web), StringComparison.Ordinal);
        Assert.Contains("No committed copy to compare with: every network is new.", LadderHtml.Fragment(added, LadderStyle.Default), StringComparison.Ordinal);
        Assert.Contains("Not in the older version: every network is new.", LadderHtml.Fragment(added, LadderStyle.Default, wording: web), StringComparison.Ordinal);
    }

    [Fact]
    public void Side_by_side_is_two_columns_when_asked()
    {
        var html = Html(Block(Rung("o", "A", "B", "C")), Block(Rung("n", "A", "X", "C")), LadderView.SideBySide);

        Assert.Contains("data-view=\"side-by-side\"", html, StringComparison.Ordinal);
        Assert.Contains("ld-two", html, StringComparison.Ordinal);
        Assert.Equal(2, Count(html, "<svg "));
    }

    [Fact]
    public void Merged_a_retitled_network_says_what_the_title_was()
    {
        var html = Html(
            Block(Rung("o", "A", "B", "C", "Motor", "old note")),
            Block(Rung("n", "A", "B", "C", "Main motor", "new note")));

        Assert.Contains("<span class=\"ld-was\">was Motor</span>", html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"ld-comment ld-was-text\">old note</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Titles_comments_and_tag_names_stay_text()
    {
        var html = Html(null, Block(Rung("x", "\"<img src=x onerror=alert(1)>\"", "B&C", "Out",
            title: "<script>alert(1)</script>", comment: "a < b && c")));

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("a &lt; b &amp;&amp; c", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_element_carries_its_key_and_its_change()
    {
        var html = Html(Block(Rung("o", "A", "B", "C")), Block(Rung("n", "A", "X", "C")), LadderView.SideBySide);

        var groups = ElementGroup().Matches(html).Select(m => m.Value).ToList();
        Assert.Equal(6, groups.Count);
        Assert.All(groups, g => Assert.Matches("data-key=\"[on][abc]\" data-change=\"(same|modified|added|removed)\"", g));
        Assert.Contains(groups, g => g.Contains("data-key=\"nb\" data-change=\"modified\"", StringComparison.Ordinal));
    }

    [Fact]
    public void An_added_network_has_a_hatched_gap_on_the_old_side()
    {
        var html = Html(Block(Rung("a", "A", "B", "C")), Block(Rung("a", "A", "B", "C"), Rung("b", "D", "E", "F")), LadderView.SideBySide);

        var row = Section(html, "added");
        Assert.Contains("ld-old ld-gap", row, StringComparison.Ordinal);
        Assert.Contains("ld-new ld-c-added", row, StringComparison.Ordinal);
        Assert.Contains("+ Added", row, StringComparison.Ordinal);
    }

    [Fact]
    public void A_replaced_network_shows_removed_on_the_left_and_added_on_the_right_on_one_row()
    {
        var html = Html(Block(Rung("o", "A", "B", "C")), Block(Rung("n", "P", "Q", "R")));

        var row = Section(html, "replaced");
        Assert.Contains("ld-old ld-c-removed", row, StringComparison.Ordinal);
        Assert.Contains("ld-new ld-c-added", row, StringComparison.Ordinal);
        Assert.DoesNotContain("Changed", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_jump_lands_on_a_row_that_exists()
    {
        var a = Rung("a", "A1", "A2", "A3");
        var b = Rung("b", "B1", "B2", "B3");
        var c = Rung("c", "C1", "C2", "C3");
        var html = Html(Block(a, b, c), Block(Rung("x", "X1", "X2", "X3"), c, a, b));

        var ids = RowId().Matches(html).Select(m => m.Groups[1].Value).ToHashSet();
        var jumps = Jump().Matches(html).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(jumps);
        Assert.All(jumps, j => Assert.Contains(j, ids));
        Assert.Contains("moved to network", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comment_edited_under_the_same_title_marks_the_comment_not_the_title()
    {
        // Seen in the first gallery: one header flag lit the title when only the comment changed.
        var html = Html(
            Block(Rung("o", "A", "B", "C", "Motor", "old note")),
            Block(Rung("n", "A", "B", "C", "Motor", "new note")));

        Assert.Contains("<p class=\"ld-comment ld-text-changed\">new note", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ld-title ld-text-changed", html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unchanged_block_is_one_column_and_says_so()
    {
        var block = Block(Rung("a", "A", "B", "C"));

        var html = Html(block, block);

        Assert.Contains("ld-one", html, StringComparison.Ordinal);
        Assert.Contains("No changes since the last commit.", html, StringComparison.Ordinal);
        Assert.Contains("data-changes-only=\"false\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_network_scales_to_its_column_but_not_below_sixty_percent()
    {
        var html = Html(null, Block(Rung("a", "A", "B", "C")));

        // The floor is CSS: the width the layout measured, as a variable the stylesheet reads.
        Assert.Matches("<svg class=\"ld-net\"[^>]*viewBox=\"0 0 [0-9.]+ [0-9.]+\"[^>]*style=\"--w:[0-9.]+px\"", html);
        Assert.Contains("min-width: calc(var(--w) * 0.6)", LadderPageAssets.Css, StringComparison.Ordinal);
        Assert.Contains("@container (max-width: 900px)", LadderPageAssets.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gallery_of_the_named_shapes_is_written_for_a_human_to_look_at()
    {
        // Not an assertion on looks, which a test cannot make. A page beside the test output
        // with every case drawn, to open after a layout change. See docs/vendors/tia/DRIVING.md.
        var a = Rung("a", "Start", "Stop", "Motor", "Motor control", "Seal-in rung");
        var b = FromTree(S(P(L(No("p1", "Auto")), L(No("p2", "Manual"))), L(Timer("t", "\"Delay_DB\"")),
            P(S(L(Nc("q", "Fault")), L(Coil("r", "Run"))), L(Coil("s", "Lamp", LdCoilKind.Set)))), 2, "Branches");
        var c = Rung("c", new string('x', 60), "B", "C", "Long names");
        var html = Html(Block(a, b, c), Block(Rung("n", "Start", "Stop_Ok", "Motor", "Motor control"), c, b));

        var page = $"<!doctype html><meta charset=utf-8><style>{LadderPageAssets.Css}</style>{html}<script>{LadderPageAssets.Script}</script>";
        var path = Path.Combine(AppContext.BaseDirectory, "ladder-gallery.html");
        File.WriteAllText(path, page);

        Assert.True(new FileInfo(path).Length > 1000);
    }

    private static string Section(string html, string change)
    {
        var start = html.IndexOf($"data-change=\"{change}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no {change} row");
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    [GeneratedRegex("<g class=\"ld-el ld-k-(?:contact|coil)[^\"]*\"[^>]*>")]
    private static partial Regex ElementGroup();

    [GeneratedRegex("<section [^>]*id=\"([^\"]+)\"")]
    private static partial Regex RowId();

    [GeneratedRegex("data-jump=\"([^\"]+)\"")]
    private static partial Regex Jump();
}
