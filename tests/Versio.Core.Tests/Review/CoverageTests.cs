using Versio.Core.Iec;
using Versio.Core.Iec.Ladder;
using Versio.Core.Projects;
using Versio.Core.Review;
using Versio.Core.Text;
using Xunit;

namespace Versio.Core.Tests.Review;

/// <summary>
/// Coverage evidence (docs/web/PLAN.md, *Pull request review*, C4): a changed line counts as shown
/// when a renderer can name it or the parser declared it framing; everything else is a gap the
/// reviewer is sent to Raw for. The six rules of 2026-10-23 each have a test here, written so that
/// taking the rule out of <see cref="Coverage"/> fails it (the mutations are in FINDINGS).
/// </summary>
public sealed class CoverageTests
{
    private const string Before = "TYPE S :\nSTRUCT\n\tEnable : BOOL := TRUE; // switch\n\tLimit : INT := 5;\nEND_STRUCT\nEND_TYPE\n";

    private const string Block = "FUNCTION_BLOCK FB\nVAR\n\t// SCADA\n\tScada : INT;\n\n\tState : INT;\nEND_VAR\n";

    /// <summary>The table compared the way the web does it: a missing side is an empty table of the same kind.</summary>
    private static MemberTable Compared(string? before, string? after, string path)
    {
        var oldTable = before is null ? null : StDeclarationTable.FromSource(path, before);
        var newTable = after is null ? null : StDeclarationTable.FromSource(path, after);
        var baseline = oldTable ?? new MemberTable { Name = newTable!.Name, IsDataType = newTable.IsDataType, IsInterface = newTable.IsInterface };
        var current = newTable ?? baseline with { Members = [], Structural = [] };
        return MemberTableDiff.Compare(baseline, current);
    }

    private static ReviewCoverage Table(string? before, string? after, string path = "S.st")
    {
        var compared = Compared(before, after, path);
        return Coverage.Check([(path, before, after)],
            new Dictionary<string, IReadOnlyList<Coverage.Evidence>> { [path] = [Coverage.Table(compared, before, after)] }, _ => "no-evidence");
    }

    private static string[] Where(ReviewCoverage report) => report.Gaps.Select(g => $"{g.Side} {g.Start}-{g.End} {g.Reason}").ToArray();

    [Fact]
    public void A_changed_value_the_table_shows_is_covered_on_both_sides()
    {
        var report = Table(Before, Before.Replace(":= TRUE", ":= FALSE", StringComparison.Ordinal));

        Assert.Equal(2, report.Changed);
        Assert.True(report.Complete);
    }

    // ---- rule 1: a file entirely new or entirely deleted ---------------------------------------

    /// <summary>
    /// The symbol set case: a new file, every row A, and a doc comment the table never draws. There is
    /// nothing a reviewer could be missing, so the file is complete without reading its lines.
    /// </summary>
    [Fact]
    public void A_file_entirely_new_or_entirely_deleted_is_complete_when_something_drew_it()
    {
        const string documented = "/// What S is for.\n" + Before;

        var added = Table(null, documented);
        var deleted = Table(documented, null);

        Assert.Equal(7, added.Changed);
        Assert.True(added.Complete);
        Assert.True(deleted.Complete);
        // The same doc line with both versions present is a gap: the rule is about whole files.
        Assert.Equal(["New 1-1 not-shown"], Where(Table(Before, documented)));
    }

    [Fact]
    public void A_new_file_nothing_drew_is_still_reported_whole()
    {
        var report = Coverage.Check([("L.xml", null, "a\nb\n")], new Dictionary<string, IReadOnlyList<Coverage.Evidence>>(), _ => "not-drawn");

        Assert.Equal(["New 1-2 not-drawn"], Where(report));
    }

    // ---- rule 2: a changed row vouches for its span --------------------------------------------

    /// <summary>
    /// The plan's original case, decided the other way since the table draws pragmas as chips: a value
    /// changed beside an attribute added on the same line is one modified row, chip included.
    /// </summary>
    [Fact]
    public void A_row_drawn_as_changed_vouches_for_every_line_it_spans_chips_included()
    {
        var report = Table(Before, Before.Replace("\tEnable : BOOL := TRUE;", "\tEnable : BOOL := FALSE; {attribute 'hide'}", StringComparison.Ordinal));

        Assert.True(report.Complete);
    }

    /// <summary>A comment line above a declaration is the row's comment cell, so its span starts there.</summary>
    [Fact]
    public void A_rows_comment_line_above_it_is_in_its_span_and_vouched_for_when_the_row_changes()
    {
        var report = Table(Block, Block.Replace("// SCADA", "// SCADA tags", StringComparison.Ordinal), "FB.st");

        Assert.True(report.Complete);
    }

    /// <summary>A pragma the parser does not read as an attribute leaves the row unchanged and the line a gap.</summary>
    [Fact]
    public void A_change_under_an_unchanged_row_that_is_not_spacing_is_a_gap()
    {
        var report = Table(Before, Before.Replace("\tLimit : INT := 5;", "\tLimit : INT := 5; {warning 'check'}", StringComparison.Ordinal));

        Assert.Equal(["New 4-4 not-shown"], Where(report));
    }

    /// <summary>
    /// The owner's FB_Alarm: twelve declarations re-aligned, twelve rows reading unchanged, twelve
    /// warnings. The cells are the same cells, so spacing under an unchanged row is vouched for —
    /// and so is a comment moved from above the declaration to the end of its line (GVL_Logger).
    /// </summary>
    [Fact]
    public void Spacing_changed_or_a_comment_moved_under_an_unchanged_row_is_covered()
    {
        var spaced = Table(Before, Before.Replace("\tLimit : INT := 5;", "\tLimit   :   INT := 5;", StringComparison.Ordinal));
        Assert.Equal(2, spaced.Changed);
        Assert.True(spaced.Complete);

        const string above = "TYPE S :\nSTRUCT\n\t// the cap\n\tLimit : INT := 5;\nEND_STRUCT\nEND_TYPE\n";
        const string trailing = "TYPE S :\nSTRUCT\n\tLimit : INT := 5; // the cap\nEND_STRUCT\nEND_TYPE\n";
        Assert.True(Table(above, trailing).Complete);
        Assert.True(Table(trailing, above).Complete);
    }

    // ---- rule 3: the declaration's framing -----------------------------------------------------

    /// <summary>MoveHome: a method header that gained PUBLIC, a VAR_IN_OUT block opened, a blank line removed — framing, all of it.</summary>
    [Fact]
    public void The_declarations_framing_is_vouched_for_by_the_table()
    {
        const string method = "METHOD MoveHome\nVAR_INPUT\n\tx : INT;\nEND_VAR\n";
        Assert.True(Table(method, method.Replace("METHOD MoveHome", "METHOD PUBLIC MoveHome", StringComparison.Ordinal), "M.st").Complete);

        var moved = Table(Block, "FUNCTION_BLOCK FB\nVAR_IN_OUT\n\t// SCADA\n\tScada : INT;\nEND_VAR\nVAR\n\tState : INT;\nEND_VAR\n", "FB.st");
        Assert.True(moved.Complete);
    }

    [Fact]
    public void A_header_that_says_more_than_its_keywords_is_a_gap_and_the_table_says_so()
    {
        var after = Block.Replace("FUNCTION_BLOCK FB", "FUNCTION_BLOCK FB EXTENDS FB_Base", StringComparison.Ordinal);

        Assert.Equal(["New 1-1 not-shown"], Where(Table(Block, after, "FB.st")));
        Assert.StartsWith("1 declaration change is not shown", Compared(Block, after, "FB.st").UnshownChange, StringComparison.Ordinal);
    }

    [Fact]
    public void A_keyword_line_that_carries_a_comment_is_not_framing()
    {
        // The STRUCT keyword's line is framing until it says something: a comment there is drawn nowhere.
        var report = Table(Before, Before.Replace("STRUCT\n", "STRUCT // packed\n", StringComparison.Ordinal));

        Assert.Equal(["New 2-2 not-shown"], Where(report));
    }

    // ---- rule 4: one edit, one gap ---------------------------------------------------------------

    [Fact]
    public void A_line_edited_in_place_is_one_gap_named_as_it_is_now()
    {
        var report = Coverage.Check([("L.xml", "a\nb\nc\nd\n", "a\nB\nC\nd\n")], new Dictionary<string, IReadOnlyList<Coverage.Evidence>>(), p => "no-evidence");

        Assert.Equal(4, report.Changed);
        // Consecutive lines fold into one gap, and the older side of the same edit is not listed again.
        Assert.Equal(["New 2-3 no-evidence"], Where(report));
    }

    [Fact]
    public void A_pure_deletion_is_named_on_the_older_side()
    {
        var report = Coverage.Check([("L.xml", "a\nb\nc\n", "a\nc\n")], new Dictionary<string, IReadOnlyList<Coverage.Evidence>>(), p => "no-evidence");

        Assert.Equal(["Old 2-2 no-evidence"], Where(report));
    }

    /// <summary>When the newer lines of an edit are all shown and an older one is not, that one is still said.</summary>
    [Fact]
    public void An_edit_whose_newer_lines_are_shown_still_names_an_older_line_that_is_not()
    {
        Coverage.Evidence newOnly = (side, _) => side == ReviewSide.New;
        var report = Coverage.Check([("L.xml", "a\nb\nc\n", "a\nB\nc\n")], new Dictionary<string, IReadOnlyList<Coverage.Evidence>> { ["L.xml"] = [newOnly] }, p => "no-evidence");

        Assert.Equal(["Old 2-2 not-shown"], Where(report));
    }

    [Fact]
    public void A_drawn_source_vouches_for_its_added_and_struck_lines_and_not_for_a_header_it_leaves_out()
    {
        // A body drawn from the second line on: the first is a header the layout does not draw.
        var rows = StSourceLayout.Mark(StSourceLayout.Body("H\na\nc\n", 1, 3), "h\na\nb\n", "H\na\nc\n");
        var report = Coverage.Check([("F.st", "h\na\nb\n", "H\na\nc\n")],
            new Dictionary<string, IReadOnlyList<Coverage.Evidence>> { ["F.st"] = [Coverage.Source(rows)] }, _ => "no-evidence");

        // "c" is drawn added and "b" struck; the header changed too, and its edit is named as it is now.
        Assert.Equal(["New 1-1 not-shown"], Where(report));
    }

    [Fact]
    public void A_changed_attribute_drawn_on_its_row_or_over_the_table_is_covered()
    {
        const string before = "{attribute 'symbol' := 'none'}\nTYPE S :\nSTRUCT\n\t{attribute 'symbol' := 'read'}\n\tScada : INT;\nEND_STRUCT\nEND_TYPE\n";

        Assert.True(Table(before, before.Replace("'read'", "'readwrite'", StringComparison.Ordinal)).Complete);
        Assert.True(Table(before, before.Replace("'none'", "'readwrite'", StringComparison.Ordinal)).Complete);
        Assert.True(Table(before, "{attribute 'linkalways'}\n" + before).Complete);
    }

    // ---- rule 5: an XML table's framing -------------------------------------------------------

    private const string Xml = "<Block>\n  <Members>\n    <Member Name=\"a\" Type=\"Int\" Retain=\"false\" />\n    <Member Name=\"b\" Type=\"Int\" />\n  </Members>\n  <Number>5</Number>\n</Block>\n";

    /// <summary>A table read from XML the way a vendor pack builds one: rows on their lines, the markup between them declared framing.</summary>
    private static MemberTable XmlTable(string xml)
    {
        var lines = xml.Split('\n');
        var rows = lines.Select((text, i) => (text, line: i + 1)).Where(l => l.text.Contains("<Member ", StringComparison.Ordinal))
            .Select(l => new MemberRow
            {
                Path = l.text.Split('"')[1],
                Name = l.text.Split('"')[1],
                Datatype = l.text.Split('"')[3],
                Lines = new LineSpan(l.line, l.line),
            }).ToList();
        return new MemberTable { Name = "B", Members = rows, Structural = MarkupLines.Structural(xml, rows.Select(r => r.Lines!.Value)) };
    }

    private static ReviewCoverage XmlCompared(string before, string after)
    {
        var compared = MemberTableDiff.Compare(XmlTable(before), XmlTable(after));
        return Coverage.Check([("B.xml", before, after)],
            new Dictionary<string, IReadOnlyList<Coverage.Evidence>> { ["B.xml"] = [Coverage.Table(compared, before, after)] }, _ => "no-evidence");
    }

    [Fact]
    public void Markup_between_the_rows_of_an_xml_table_is_framing_on_both_sides()
    {
        // A wrapper re-indented and a blank line added: changed lines, and nothing a cell could show.
        var after = Xml.Replace("  </Members>\n", "</Members>\n\n", StringComparison.Ordinal);

        var report = XmlCompared(Xml, after);

        Assert.Equal(3, report.Changed);
        Assert.True(report.Complete);
    }

    [Fact]
    public void A_value_an_xml_table_does_not_draw_stays_a_gap()
    {
        Assert.Equal(["New 6-6 not-shown"], Where(XmlCompared(Xml, Xml.Replace("<Number>5</Number>", "<Number>6</Number>", StringComparison.Ordinal))));
        // An attribute on a row's own line that the table has no column for, the row reading unchanged.
        Assert.Equal(["New 3-3 not-shown"], Where(XmlCompared(Xml, Xml.Replace("Retain=\"false\"", "Retain=\"true\"", StringComparison.Ordinal))));
        // The same line with a change the row does draw is the row's to vouch for, attribute and all.
        Assert.True(XmlCompared(Xml, Xml.Replace("Type=\"Int\" Retain=\"false\"", "Type=\"Real\" Retain=\"true\"", StringComparison.Ordinal)).Complete);
    }

    // ---- rule 6: a ladder's evidence -------------------------------------------------------------

    private static LadderNetwork Network(int number, string operand, int start, int end) => new()
    {
        Number = number,
        // A kept title is what makes one renamed operand read as the same network edited.
        Title = "Net " + number,
        Nodes = [new LdLeftRail("r"), new LdContact("c", operand, LdContactKind.NormallyOpen), new LdCoil("q", "Q" + number, LdCoilKind.Normal)],
        Nets = [new LdNet([new LdPinRef("r", LdPins.Out), new LdPinRef("c", LdPins.In)]), new LdNet([new LdPinRef("c", LdPins.Out), new LdPinRef("q", LdPins.In)])],
        Lines = new LineSpan(start, end),
    };

    /// <summary>Forty numbered lines, so a change can be put inside or outside a network's span.</summary>
    private static string Lines(params (int Line, string Text)[] edits)
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"line {i}").ToArray();
        foreach (var (line, text) in edits)
        {
            lines[line - 1] = text;
        }

        return string.Join('\n', lines) + "\n";
    }

    [Fact]
    public void A_ladder_vouches_for_every_networks_span_and_its_declared_framing_and_for_nothing_else()
    {
        var old = new LadderBody("L", [Network(1, "A", 10, 20), Network(2, "B", 21, 30)]);
        var @new = new LadderBody("L", [Network(1, "A", 10, 20), Network(2, "C", 21, 30)]);
        var comparison = LadderDiff.Compare(old, @new);
        Assert.Equal([LadderChange.Same, LadderChange.Changed], comparison.Rows.Select(r => r.Change));

        // Line 15 inside the unchanged network, 25 inside the changed one, 33 declared framing on
        // both sides (it was edited in place, so the older line needs vouching for too), 5 neither.
        var before = Lines();
        var after = Lines((5, "setting"), (15, "renumbered id"), (25, "C"), (33, "</Wrapper>"));
        var framing = new[] { new LineSpan(31, 35) };
        var report = Coverage.Check([("L.xml", before, after)],
            new Dictionary<string, IReadOnlyList<Coverage.Evidence>> { ["L.xml"] = [Coverage.Ladder(comparison, framing, framing)] }, _ => "no-evidence");

        Assert.Equal(8, report.Changed);
        Assert.Equal(["New 5-5 not-shown"], Where(report));
    }

    [Fact]
    public void A_removed_network_vouches_for_its_lines_on_the_older_side_only()
    {
        var old = new LadderBody("L", [Network(1, "A", 10, 20), Network(2, "B", 21, 30)]);
        var @new = new LadderBody("L", [Network(1, "A", 10, 20)]);
        var comparison = LadderDiff.Compare(old, @new);

        var evidence = Coverage.Ladder(comparison, [], []);

        Assert.True(evidence(ReviewSide.Old, 25));
        Assert.False(evidence(ReviewSide.New, 25));
    }

    [Fact]
    public void Structural_evidence_answers_for_each_side_from_its_own_spans()
    {
        var evidence = Coverage.Structural([new LineSpan(1, 2)], [new LineSpan(5, 6)]);

        Assert.True(evidence(ReviewSide.Old, 2));
        Assert.False(evidence(ReviewSide.Old, 5));
        Assert.True(evidence(ReviewSide.New, 5));
        Assert.False(evidence(ReviewSide.New, 2));
    }
}
