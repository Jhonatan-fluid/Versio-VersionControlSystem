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
}
