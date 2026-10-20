using Versio.Core.Iec;
using Versio.Core.Projects;
using Versio.Core.Review;
using Xunit;

namespace Versio.Core.Tests.Review;

/// <summary>
/// Coverage evidence (docs/web/PLAN.md, *Pull request review*, C4): a changed line counts as shown
/// only when a renderer can name it; everything else is a gap the reviewer is sent to Raw for.
/// </summary>
public sealed class CoverageTests
{
    private const string Before = "TYPE S :\nSTRUCT\n\tEnable : BOOL := TRUE; // switch\n\tLimit : INT := 5;\nEND_STRUCT\nEND_TYPE\n";

    private static ReviewCoverage Table(string before, string after, string path = "S.st")
    {
        var compared = MemberTableDiff.Compare(StDeclarationTable.FromSource(path, before)!, StDeclarationTable.FromSource(path, after)!);
        return Coverage.Check([(path, before, after)],
            new Dictionary<string, IReadOnlyList<Coverage.Evidence>> { [path] = [Coverage.Table(compared, before, after)] }, _ => "no-evidence");
    }

    [Fact]
    public void A_changed_value_the_table_shows_is_covered_on_both_sides()
    {
        var report = Table(Before, Before.Replace(":= TRUE", ":= FALSE", StringComparison.Ordinal));

        Assert.Equal(2, report.Changed);
        Assert.True(report.Complete);
    }

    /// <summary>
    /// The plan's own case: on ONE line, a change the table shows (the value) and one it does not (a
    /// pragma). Being inside a drawn, changed row proves nothing about the pragma, so the line is a gap.
    /// </summary>
    [Fact]
    public void A_visible_and_an_invisible_change_on_the_same_line_leave_the_line_uncovered()
    {
        var report = Table(Before, Before.Replace("\tEnable : BOOL := TRUE;", "\tEnable : BOOL := FALSE; {attribute 'hide'}", StringComparison.Ordinal));

        Assert.False(report.Complete);
        Assert.Contains(report.Gaps, g => g.Side == ReviewSide.New && g.Start == 3 && g.Reason == "not-shown");
    }

    [Fact]
    public void A_change_outside_every_row_is_a_gap_even_inside_the_declaration()
    {
        // The STRUCT keyword's line is no member's: the table draws nothing for it.
        var report = Table(Before, Before.Replace("STRUCT\n", "STRUCT // packed\n", StringComparison.Ordinal));

        Assert.Contains(report.Gaps, g => g.Start == 2);
    }

    /// <summary>A reformatted line whose row reads unchanged is not shown as a change, so it is not covered.</summary>
    [Fact]
    public void Spacing_changed_under_an_unchanged_row_is_a_gap()
    {
        var report = Table(Before, Before.Replace("\tLimit : INT := 5;", "\tLimit   : INT := 5;", StringComparison.Ordinal));

        Assert.Equal([4, 4], report.Gaps.Where(g => g.Side == ReviewSide.New).Select(g => new[] { g.Start, g.End }).Single());
    }

    [Fact]
    public void A_drawn_source_vouches_for_its_added_and_struck_lines_and_not_for_a_header_it_leaves_out()
    {
        // A body drawn from the second line on: the first is a header the layout does not draw.
        var rows = StSourceLayout.Mark(StSourceLayout.Body("H\na\nc\n", 1, 3), "h\na\nb\n", "H\na\nc\n");
        var report = Coverage.Check([("F.st", "h\na\nb\n", "H\na\nc\n")],
            new Dictionary<string, IReadOnlyList<Coverage.Evidence>> { ["F.st"] = [Coverage.Source(rows)] }, _ => "no-evidence");

        // "c" is drawn added and both old lines are drawn struck; the new header "H" is drawn nowhere.
        Assert.Equal(["New 1-1"], report.Gaps.Select(g => $"{g.Side} {g.Start}-{g.End}"));
    }

    [Fact]
    public void A_file_with_no_evidence_is_reported_whole_with_the_reason_given()
    {
        var report = Coverage.Check([("L.xml", "a\nb\nc\nd\n", "a\nB\nC\nd\n")], new Dictionary<string, IReadOnlyList<Coverage.Evidence>>(), p => "no-evidence");

        // Consecutive lines fold into one gap per side.
        Assert.Equal(2, report.Gaps.Count);
        Assert.All(report.Gaps, g => Assert.Equal(("no-evidence", 2, 3), (g.Reason, g.Start, g.End)));
    }
}
