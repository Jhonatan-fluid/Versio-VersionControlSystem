using System.Text.RegularExpressions;
using Versio.Core.Iec;
using Versio.Core.Iec.Ladder;
using Versio.Core.Projects;
using Versio.Core.Review;
using Versio.Core.Text;
using Xunit;

namespace Versio.Core.Tests.Review;

/// <summary>
/// Review anchors (docs/web/PLAN.md, *Pull request review*, C3): where a drawn row or network came
/// from, what identifies it, and the rules that find it again — refusing whenever it could be wrong.
/// </summary>
public sealed partial class AnchorTests
{
    private static readonly Func<ReviewSide, string> AtHead = side => side == ReviewSide.New ? "head" : "base";

    private static RenderedAnchor Net(string dom, string key, string print, int start = 1, int end = 5, ReviewSide side = ReviewSide.New, string container = "FB_Pump") =>
        new(dom, "net", "src/FB_Pump.xml", side, container, key, print, new LineSpan(start, end));

    private static SemanticAnchor Tag(string key, string print, string revision = "head", ReviewSide side = ReviewSide.New, string container = "FB_Pump") =>
        new("net", "src/FB_Pump.xml", side, revision, container, key, print);

    // ---- the matcher --------------------------------------------------------------------------

    [Fact]
    public void Same_key_at_the_same_revision_is_exact()
    {
        var placed = AnchorMatcher.Match(Tag("#3", "P3"), AtHead, [Net("n-new-3", "#3", "P3"), Net("n-new-4", "#4", "P4")]);

        Assert.Equal((AnchorOutcome.Exact, "n-new-3"), (placed.Outcome, placed.Anchor?.Dom));
    }

    /// <summary>
    /// Every element of a file shares its revision: at a NEW revision the key alone is not enough,
    /// because network 3 after a deletion is another network. Same key and same content is.
    /// </summary>
    [Fact]
    public void At_a_later_revision_the_key_counts_only_with_the_same_content()
    {
        var candidates = new[] { Net("n-new-3", "#3", "OTHER"), Net("n-new-2", "#2", "P3") };

        var placed = AnchorMatcher.Match(Tag("#3", "P3", revision: "older-head"), AtHead, candidates);

        // Not the new network 3 (different content), but the one now numbered 2, which has it.
        Assert.Equal((AnchorOutcome.Moved, "n-new-2"), (placed.Outcome, placed.Anchor?.Dom));
    }

    [Fact]
    public void Same_id_with_different_content_is_gone_never_believed()
    {
        var placed = AnchorMatcher.Match(Tag("#3", "P3", revision: "older-head"), AtHead, [Net("n-new-3", "#3", "CHANGED")]);

        Assert.Equal(AnchorOutcome.Gone, placed.Outcome);
        Assert.Null(placed.Anchor);
    }

    /// <summary>Identical rungs are common: two with the comment's content are two candidates, and two candidates are never a pick.</summary>
    [Fact]
    public void Two_identical_networks_make_a_moved_comment_ambiguous()
    {
        var candidates = new[] { Net("n-new-2", "#2", "SAME"), Net("n-new-5", "#5", "SAME") };

        var placed = AnchorMatcher.Match(Tag("#3", "SAME", revision: "older-head"), AtHead, candidates);

        Assert.Equal(AnchorOutcome.Ambiguous, placed.Outcome);
        Assert.Null(placed.Anchor);
    }

    /// <summary>A fingerprint is looked for only in the same container: the same rung in another block is another rung.</summary>
    [Fact]
    public void A_moved_match_is_searched_only_inside_the_same_container_and_side()
    {
        var candidates = new[]
        {
            Net("n-new-1", "#1", "P3", container: "FB_Other"),
            Net("n-old-1", "#1", "P3", side: ReviewSide.Old),
        };

        Assert.Equal(AnchorOutcome.Gone, AnchorMatcher.Match(Tag("#3", "P3", revision: "older-head"), AtHead, candidates).Outcome);
    }

    [Fact]
    public void A_rename_is_followed_only_as_reported()
    {
        var moved = new RenderedAnchor("n-new-3", "net", "src/New.xml", ReviewSide.New, "FB_Pump", "#3", "P3", null);
        var tag = Tag("#3", "P3");

        Assert.Equal(AnchorOutcome.Gone, AnchorMatcher.Match(tag, AtHead, [moved]).Outcome);
        Assert.Equal(AnchorOutcome.Exact, AnchorMatcher.Match(tag, AtHead, [moved], p => p == "src/FB_Pump.xml" ? "src/New.xml" : null).Outcome);
    }

    [Fact]
    public void A_line_falls_in_the_smallest_element_holding_it_and_a_tie_places_nothing()
    {
        var wide = Net("n-new-1", "#1", "A", 1, 40);
        var narrow = Net("n-new-2", "#2", "B", 10, 20);
        var twin = Net("n-new-3", "#3", "C", 10, 20);

        Assert.Equal("n-new-2", AnchorMatcher.ByLine("src/FB_Pump.xml", ReviewSide.New, 15, [wide, narrow])?.Dom);
        Assert.Equal("n-new-1", AnchorMatcher.ByLine("src/FB_Pump.xml", ReviewSide.New, 30, [wide, narrow])?.Dom);
        Assert.Null(AnchorMatcher.ByLine("src/FB_Pump.xml", ReviewSide.New, 15, [narrow, twin]));
        Assert.Null(AnchorMatcher.ByLine("src/FB_Pump.xml", ReviewSide.Old, 15, [narrow]));
    }

    // ---- where rows come from ------------------------------------------------------------------

    /// <summary>Rows name the file lines they were declared on, shifted by where the declaration starts.</summary>
    [Fact]
    public void Declaration_rows_carry_their_file_lines_including_a_declaration_over_several_lines()
    {
        var declaration = "FUNCTION_BLOCK FB_Pump\nVAR_INPUT\n\tStart : BOOL;\n\tLimit : INT\n\t\t:= 10;\nEND_VAR\n";

        var table = StDeclarationTable.Parse(declaration, "x", firstLine: 7)!;

        var rows = table.Members.Where(m => !m.IsSectionHeading).ToDictionary(m => m.Path);
        Assert.Equal(new LineSpan(9, 9), rows["Start"].Lines);
        Assert.Equal(new LineSpan(10, 11), rows["Limit"].Lines);
    }

    [Fact]
    public void A_comparison_keeps_each_rows_lines_on_both_sides_and_a_removed_row_only_its_old_ones()
    {
        var old = StDeclarationTable.Parse("TYPE S : STRUCT\n\tA : INT;\n\tB : INT;\n\tC : INT;\nEND_STRUCT\nEND_TYPE\n", "S")!;
        var @new = StDeclarationTable.Parse("TYPE S : STRUCT\n\tX : INT;\n\tA : INT := 1;\n\tY : INT;\n\tC : INT;\nEND_STRUCT\nEND_TYPE\n", "S")!;

        var rows = MemberTableDiff.Compare(old, @new).Members.ToDictionary(m => m.Path);

        Assert.Equal((new LineSpan(3, 3), new LineSpan(2, 2)), (rows["A"].Lines!.Value, rows["A"].WasLines!.Value));
        Assert.Equal((null, new LineSpan(3, 3)), (rows["B"].Lines, rows["B"].WasLines!.Value));
        Assert.Equal((new LineSpan(2, 2), (LineSpan?)null), (rows["X"].Lines!.Value, rows["X"].WasLines));
        // Unchanged, and moved down a line by the additions above it: each side keeps its own line,
        // because a comment on the older side names the older one.
        Assert.Equal(MemberChange.None, rows["C"].Change);
        Assert.Equal((new LineSpan(5, 5), new LineSpan(4, 4)), (rows["C"].Lines!.Value, rows["C"].WasLines!.Value));
    }

    // ---- the markers contract: the anchor list and the markup agree ----------------------------

    [GeneratedRegex("data-anchors=\"([^\"]*)\"")]
    private static partial Regex AnchorsAttribute();

    private static HashSet<string> DomIds(string html) =>
        AnchorsAttribute().Matches(html).SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet();

    /// <summary>
    /// Every anchor the renderer lists is in its markup exactly once and nothing else is: a page
    /// placing a thread by an id the markup lacks would drop it silently, and a duplicated id would
    /// place one thread twice.
    /// </summary>
    [Fact]
    public void A_compared_tables_anchors_and_its_markup_name_the_same_rows()
    {
        var old = StDeclarationTable.Parse("TYPE S : STRUCT\n\tA : INT;\n\tB : INT;\nEND_STRUCT\nEND_TYPE\n", "S")!;
        var @new = StDeclarationTable.Parse("TYPE S : STRUCT\n\tX : INT;\n\tA : INT := 1;\nEND_STRUCT\nEND_TYPE\n", "S")!;
        var compared = MemberTableDiff.Compare(old, @new) with { SourcePath = "src/S.st" };

        var anchors = MemberTableHtml.Anchors(compared, compared: true);
        var html = MemberTableHtml.Fragment(compared, compared: true);

        Assert.Equal(anchors.Select(a => a.Dom).ToHashSet(), DomIds(html));
        Assert.Equal(anchors.Count, anchors.Select(a => a.Dom).Distinct().Count());
        Assert.Contains("data-src-path=\"src/S.st\"", html, StringComparison.Ordinal);
        // A on both sides, B only old (removed), X only new (added).
        Assert.Equal([ReviewSide.Old, ReviewSide.New], anchors.Where(a => a.Key == "A").Select(a => a.Side).Order());
        Assert.Equal([ReviewSide.Old], anchors.Where(a => a.Key == "B").Select(a => a.Side));
        Assert.Equal([ReviewSide.New], anchors.Where(a => a.Key == "X").Select(a => a.Side));
        // The older side's fingerprint is what the row WAS: A had no value then.
        var a = anchors.Where(x => x.Key == "A").ToDictionary(x => x.Side);
        Assert.NotEqual(a[ReviewSide.New].Fingerprint, a[ReviewSide.Old].Fingerprint);
    }

    private static LadderNetwork Network(int number, string operand, int line, string title = "") => new()
    {
        Number = number,
        Title = title,
        Lines = new LineSpan(line, line + 9),
        Nodes = [new LdLeftRail("rail"), new LdContact("c", operand, LdContactKind.NormallyOpen), new LdCoil("q", "Out", LdCoilKind.Normal)],
        Nets =
        [
            new LdNet([new LdPinRef("rail", LdPins.Out), new LdPinRef("c", LdPins.In)]),
            new LdNet([new LdPinRef("c", LdPins.Out), new LdPinRef("q", LdPins.In)]),
        ],
    };

    /// <summary>The same contract for ladder, in both layouts: merged draws an unchanged network once, for both sides.</summary>
    [Theory]
    [InlineData(LadderView.Merged)]
    [InlineData(LadderView.SideBySide)]
    public void A_compared_ladders_anchors_and_its_markup_name_the_same_networks(LadderView view)
    {
        var old = new LadderBody("FB_Pump", [Network(1, "Start", 10), Network(2, "Stop", 20), Network(3, "Gone", 30)]);
        var @new = new LadderBody("FB_Pump", [Network(1, "Start", 10, "Run"), Network(2, "StopChanged", 20), Network(3, "Added", 30)]);
        var comparison = LadderDiff.Compare(old, @new);

        var anchors = LadderHtml.Anchors(comparison, "src/FB_Pump.xml");
        var html = LadderHtml.Fragment(comparison, LadderStyle.Default, view, sourcePath: "src/FB_Pump.xml");

        Assert.Equal(anchors.Select(x => x.Dom).ToHashSet(), DomIds(html));
        Assert.Equal(anchors.Count, anchors.Select(x => x.Dom).Distinct().Count());
        Assert.All(anchors, x => Assert.Equal("src/FB_Pump.xml", x.SourcePath));
        // A titled network is keyed by its title, an untitled one by its number.
        Assert.Contains(anchors, x => x.Key == "t:Run" && x.Side == ReviewSide.New);
        Assert.Contains(anchors, x => x.Key == "#2" && x.Side == ReviewSide.Old && x.Lines == new LineSpan(20, 29));
    }

    /// <summary>A network's fingerprint follows its content, not its number: renumbered by a deletion, it is still recognised.</summary>
    [Fact]
    public void A_networks_fingerprint_survives_renumbering_and_changes_with_its_content()
    {
        var before = LadderHtml.Anchors(LadderDiff.Compare(null, new LadderBody("B", [Network(3, "Start", 1)])), "p").Single();
        var after = LadderHtml.Anchors(LadderDiff.Compare(null, new LadderBody("B", [Network(2, "Start", 1)])), "p").Single();
        var edited = LadderHtml.Anchors(LadderDiff.Compare(null, new LadderBody("B", [Network(2, "Stop", 1)])), "p").Single();

        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.NotEqual(after.Fingerprint, edited.Fingerprint);
        Assert.NotEqual(before.Key, after.Key);
    }
}
