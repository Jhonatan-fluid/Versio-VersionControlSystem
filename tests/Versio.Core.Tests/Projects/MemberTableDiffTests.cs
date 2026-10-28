using Versio.Core.Text;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// A member table marked with what changed since the last commit - the table, not the raw
/// diff, is where the owner wants to see a changed UDT (2026-10-05).
/// </summary>
public sealed class MemberTableDiffTests
{
    private static MemberRow Row(string name, string type = "DWord", string value = "", string section = "None") =>
        new() { Name = name, Path = name, Datatype = type, StartValue = value, Section = section };

    private static MemberRow Heading(string section) =>
        new() { Name = section, Path = section, Section = section, IsSectionHeading = true };

    private static MemberTable Table(params MemberRow[] rows) => new() { Name = "T", Members = rows };

    [Fact]
    public void Unchanged_tables_mark_nothing_and_say_nothing()
    {
        var same = Table(Row("Idle", value: "16#10"), Row("Held", value: "16#800"));

        var result = MemberTableDiff.Compare(same, same);

        Assert.All(result.Members, r => Assert.Equal(MemberChange.None, r.Change));
        Assert.False(result.HasChangeSummary);
    }

    [Fact]
    public void A_changed_value_is_modified_and_keeps_what_it_was()
    {
        var result = MemberTableDiff.Compare(
            Table(Row("Idle", value: "16#10"), Row("Held", value: "16#800")),
            Table(Row("Idle", value: "16#20"), Row("Held", value: "16#800")));

        var idle = result.Members[0];
        Assert.Equal(MemberChange.Modified, idle.Change);
        Assert.Contains("16#10", idle.Was, StringComparison.Ordinal);
        Assert.Equal("M", idle.ChangeGlyph);

        // Old → new in the cell that moved, and only there.
        Assert.Equal("16#10", idle.StartValueBefore);
        Assert.True(idle.StartValueChanged);
        Assert.False(idle.DatatypeChanged);
        Assert.Null(idle.CommentBefore);
        Assert.Equal(MemberChange.None, result.Members[1].Change);
        Assert.Equal("Since the last commit: 1 changed - marked below.", result.ChangeSummary);
    }

    [Fact]
    public void An_insertion_marks_one_row_not_every_row_under_it()
    {
        // The reason for matching by path: positionally, every row after the insert differs.
        var result = MemberTableDiff.Compare(
            Table(Row("A"), Row("B"), Row("C")),
            Table(Row("A"), Row("New"), Row("B"), Row("C")));

        Assert.Equal(
            [MemberChange.None, MemberChange.Added, MemberChange.None, MemberChange.None],
            result.Members.Select(r => r.Change));
    }

    [Fact]
    public void A_removed_row_comes_back_where_it_was_struck()
    {
        var result = MemberTableDiff.Compare(
            Table(Row("A"), Row("Gone", type: "Int"), Row("B")),
            Table(Row("A"), Row("B")));

        Assert.Equal(["A", "Gone", "B"], result.Members.Select(r => r.Name));
        var gone = result.Members[1];
        Assert.True(gone.IsRemoved);
        Assert.Contains("Int", gone.Was, StringComparison.Ordinal);
        Assert.Equal("Since the last commit: 1 removed - marked below.", result.ChangeSummary);
    }

    [Fact]
    public void A_value_that_appeared_shows_it_was_empty_and_markers_name_each_kind()
    {
        var result = MemberTableDiff.Compare(
            Table(Row("A"), Row("Gone")),
            Table(Row("A", value: "1"), Row("New")));

        var a = Assert.Single(result.Members, r => r.Name == "A");
        Assert.Equal("(empty)", a.StartValueBefore);

        Assert.Equal("A", Assert.Single(result.Members, r => r.Name == "New").ChangeGlyph);
        Assert.Equal("D", Assert.Single(result.Members, r => r.Name == "Gone").ChangeGlyph);
        Assert.All(result.Members.Where(r => r.Change != MemberChange.None),
            r => Assert.NotEmpty(r.ChangeLabel));
    }

    [Fact]
    public void A_removed_first_row_is_shown_first()
    {
        var result = MemberTableDiff.Compare(Table(Row("First"), Row("A")), Table(Row("A")));
        Assert.Equal(["First", "A"], result.Members.Select(r => r.Name));
        Assert.True(result.Members[0].IsRemoved);
    }

    [Fact]
    public void The_same_name_in_another_section_is_another_member()
    {
        // An interface can move "X" from Input to InOut: that is a removal and an addition,
        // not "unchanged" because the names match.
        var result = MemberTableDiff.Compare(
            Table(Heading("Input"), Row("X", section: "Input"), Heading("InOut")),
            Table(Heading("Input"), Heading("InOut"), Row("X", section: "InOut")));

        Assert.Contains(result.Members, r => r is { Name: "X", Section: "Input", IsRemoved: true });
        Assert.Contains(result.Members, r => r is { Name: "X", Section: "InOut", IsAdded: true });
        Assert.All(result.Members.Where(r => r.IsSectionHeading), h => Assert.Equal(MemberChange.None, h.Change));
    }

    private const string Alarm = """
        {attribute 'linkalways'}
        {attribute 'symbol' := 'none'}
        FUNCTION_BLOCK FB_Alarm
        VAR
        	// SCADA
        	{attribute 'symbol' := 'read'}
        	Scada		: ST_AlarmScada;
        	State		: INT;
        END_VAR
        """;

    private static MemberTable Compared(string before, string after) =>
        MemberTableDiff.Compare(Versio.Core.Iec.StDeclarationTable.Parse(before, "x")!, Versio.Core.Iec.StDeclarationTable.Parse(after, "x")!);

    [Fact]
    public void An_added_object_attribute_is_marked_and_nothing_else_is()
    {
        // F_UpdateMeta's case: one attribute above the header, nothing else changed.
        var result = Compared(Alarm.Replace("{attribute 'linkalways'}\n", "", StringComparison.Ordinal), Alarm);

        var linkalways = Assert.Single(result.Attributes, a => a.Name == "linkalways");
        Assert.True(linkalways.IsAdded);
        Assert.Equal(MemberChange.None, Assert.Single(result.Attributes, a => a.Name == "symbol").Change);
        Assert.All(result.Members, r => Assert.Equal(MemberChange.None, r.Change));
        Assert.False(result.HasUnshownChange);
    }

    [Fact]
    public void A_variable_whose_only_change_is_an_attribute_is_a_modified_row()
    {
        var result = Compared(Alarm, Alarm.Replace("'read'", "'readwrite'", StringComparison.Ordinal));

        var scada = Assert.Single(result.Members, r => r.Name == "Scada");
        Assert.Equal(MemberChange.Modified, scada.Change);
        var symbol = Assert.Single(scada.Attributes);
        Assert.True(symbol.IsModified);
        Assert.Equal("read", symbol.ValueBefore);
        Assert.Equal("symbol = readwrite", symbol.Text);
        Assert.Contains("symbol = read", scada.Was, StringComparison.Ordinal);
        Assert.Equal(MemberChange.None, Assert.Single(result.Members, r => r.Name == "State").Change);
        Assert.Equal("Since the last commit: 1 changed - marked below.", result.ChangeSummary);
        Assert.False(result.HasUnshownChange);
    }

    [Fact]
    public void A_removed_attribute_comes_back_where_it_was_and_struck()
    {
        var result = MemberTableDiff.Compare(
            Table() with { Attributes = [new() { Name = "a" }, new() { Name = "gone" }, new() { Name = "b" }] },
            Table() with { Attributes = [new() { Name = "a" }, new() { Name = "b" }, new() { Name = "new" }] });

        Assert.Equal(["a", "gone", "b", "new"], result.Attributes.Select(a => a.Name).ToList());
        Assert.Equal([MemberChange.None, MemberChange.Removed, MemberChange.None, MemberChange.Added], result.Attributes.Select(a => a.Change).ToList());
    }

    [Fact]
    public void A_header_modifier_gained_or_swapped_is_marked_as_the_attribute_chips_are()
    {
        static MemberTable With(params string[] modifiers) =>
            Table() with { Modifiers = modifiers.Select(m => new MemberAttribute { Name = m, IsModifier = true, Lines = new LineSpan(1, 1) }).ToList() };

        // FB_Template.Reset, 2026-10-28: METHOD Reset became METHOD PUBLIC Reset and the table
        // drew nothing for it. One green chip now, carrying the header line for a review comment.
        var gained = MemberTableDiff.Compare(With(), With("PUBLIC"));
        var chip = Assert.Single(gained.Modifiers);
        Assert.Equal(("PUBLIC", MemberChange.Added, true), (chip.Name, chip.Change, chip.IsModifier));
        Assert.Equal(new LineSpan(1, 1), chip.Lines);
        Assert.False(gained.HasChangeSummary);
        Assert.False(gained.HasUnshownChange);

        // By name, as attributes: PRIVATE become PUBLIC is PUBLIC gained and PRIVATE struck, in the
        // header's own words, rather than one chip "changed from".
        var swapped = MemberTableDiff.Compare(With("PRIVATE"), With("PUBLIC"));
        Assert.Equal(["PRIVATE", "PUBLIC"], swapped.Modifiers.Select(m => m.Name).ToList());
        Assert.Equal([MemberChange.Removed, MemberChange.Added], swapped.Modifiers.Select(m => m.Change).ToList());
        Assert.Null(swapped.Modifiers[0].Lines);
        Assert.Equal(new LineSpan(1, 1), swapped.Modifiers[0].WasLines);

        Assert.All(MemberTableDiff.Compare(With("PUBLIC"), With("PUBLIC")).Modifiers, m => Assert.Equal(MemberChange.None, m.Change));
    }

    [Fact]
    public void A_changed_line_the_table_does_not_draw_is_said_with_where_it_is()
    {
        var result = Compared(Alarm, Alarm.Replace("FUNCTION_BLOCK FB_Alarm", "FUNCTION_BLOCK FB_Alarm EXTENDS FB_Base", StringComparison.Ordinal));

        Assert.Equal("1 declaration change is not shown in this table (line 3) - the difference is on Raw.", result.UnshownChange);
    }

    [Fact]
    public void A_line_only_taken_away_is_named_on_the_older_side()
    {
        var result = Compared("/// doc\n" + Alarm, Alarm);

        Assert.Equal("1 declaration change is not shown in this table (line 1 before) - the difference is on Raw.", result.UnshownChange);
    }

    [Fact]
    public void A_change_the_table_draws_or_an_unshown_line_merely_moved_says_nothing()
    {
        // A new member above a doc comment's block moves nothing it does not draw; a new value is drawn.
        var doc = "/// doc\n" + Alarm;
        Assert.False(Compared(doc, doc.Replace("\tState\t\t: INT;", "\tExtra : BOOL;\n\tState\t\t: INT := 1;", StringComparison.Ordinal)).HasUnshownChange);
        Assert.False(Compared("METHOD M : INT\nVAR\nEND_VAR", "METHOD M : DINT\nVAR\nEND_VAR").HasUnshownChange);
        Assert.False(Compared(doc, doc.Replace("/// doc", "///   doc", StringComparison.Ordinal)).HasUnshownChange);
    }

    [Fact]
    public void An_edit_inside_a_members_multi_line_comment_is_drawn_so_it_says_nothing()
    {
        // A block comment's text lands on the line that closes it; the lines it opened on are
        // that comment's too, and the row draws all of it.
        const string before = "FUNCTION_BLOCK B\nVAR\n\t(* first\n\t   middle\n\t   last *)\n\tx : INT;\nEND_VAR";

        var result = Compared(before, before.Replace("middle", "changed", StringComparison.Ordinal));

        Assert.True(Assert.Single(result.Members, r => r.Name == "x").IsModified);
        Assert.False(result.HasUnshownChange);
    }
}
