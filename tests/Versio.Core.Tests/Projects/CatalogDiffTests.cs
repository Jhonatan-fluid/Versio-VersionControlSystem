using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// The library, task and device tables compared with the last commit.
/// </summary>
/// <remarks>
/// The case that started it is the first test: six visualisation libraries moved from 4.9 to
/// 4.10, the tree marked six rows, and the Rendered table drew the new file with nothing
/// marked (owner, 2026-10-10). Rows are matched by the tree's own keys and each row's own
/// <c>ContentEquals</c>, so these also pin that the two can never disagree.
/// </remarks>
public sealed class CatalogDiffTests
{
    private static LibraryReference Lib(string name, string resolution, bool system = true) => new()
    {
        Name = name,
        Namespace = name.TrimStart('#'),
        DefaultResolution = resolution,
        SystemLibrary = system,
    };

    private static LibraryCatalog Libraries(params LibraryReference[] references) =>
        new() { Schema = 1, Manager = "Library Manager", References = references };

    [Fact]
    public void Rows_sharing_a_name_are_paired_by_occurrence_so_an_unchanged_file_marks_nothing()
    {
        // FsLibrary.fbslib, 2026-10-23: four references whose names the reader had left empty.
        // Paired by name alone, the second, third and fourth were each compared with the first,
        // and a tree with no changes drew "3 changed".
        var head = Libraries(Lib("", "A"), Lib("", "B"), Lib("", "C"), Lib("#Standard", "S"));
        var same = Libraries(Lib("", "A"), Lib("", "B"), Lib("", "C"), Lib("#Standard", "S"));
        var moved = Libraries(Lib("", "A"), Lib("", "B2"), Lib("", "C"), Lib("#Standard", "S"));

        var unchanged = CatalogDiff.Compare(head, same);
        Assert.Equal("", unchanged.ChangeSummary);
        Assert.All(unchanged.References, r => Assert.Equal(MemberChange.None, r.Mark.Change));

        // A real change to one of them is still that one row.
        var marked = CatalogDiff.Compare(head, moved);
        Assert.Equal(
            [MemberChange.None, MemberChange.Modified, MemberChange.None, MemberChange.None],
            marked.References.Select(r => r.Mark.Change));
    }

    [Fact]
    public void A_moved_resolution_is_marked_with_what_it_was()
    {
        var head = Libraries(
            Lib("#Standard", "Standard, 3.5.22.0 (System)", system: false),
            Lib("#System_VisuElemMeter", "VisuElemMeter, 4.9.0.0 (System)"));
        var now = Libraries(
            Lib("#Standard", "Standard, 3.5.22.0 (System)", system: false),
            Lib("#System_VisuElemMeter", "VisuElemMeter, 4.10.0.0 (System)"));

        var marked = CatalogDiff.Compare(head, now);

        Assert.Equal("Since the last commit: 1 changed - marked below.", marked.ChangeSummary);
        Assert.Equal(MemberChange.None, marked.References[0].Mark.Change);

        var meter = marked.References[1];
        Assert.Equal(MemberChange.Modified, meter.Mark.Change);
        Assert.Equal("M", meter.Mark.Glyph);
        Assert.Equal("VisuElemMeter, 4.9.0.0 (System)", meter.ResolutionBefore);
        Assert.True(meter.ResolutionChanged);
        Assert.False(meter.NamespaceChanged);
        Assert.False(meter.KindChanged);
        Assert.Contains("resolution: VisuElemMeter, 4.9.0.0 (System) → VisuElemMeter, 4.10.0.0 (System)", meter.Mark.Was);
    }

    [Fact]
    public void An_added_reference_is_marked_and_a_deleted_one_comes_back_where_it_was()
    {
        var head = Libraries(
            Lib("#A", "A, 1 (System)"),
            Lib("#Gone", "Gone, 1 (System)"),
            Lib("#C", "C, 1 (System)"));
        var now = Libraries(
            Lib("#A", "A, 1 (System)"),
            Lib("#C", "C, 1 (System)"),
            Lib("#New", "New, 1 (System)"));

        var marked = CatalogDiff.Compare(head, now);

        Assert.Equal(["#A", "#Gone", "#C", "#New"], marked.References.Select(r => r.Name));
        Assert.True(marked.References[1].Mark.IsRemoved);
        Assert.True(marked.References[3].Mark.IsAdded);
        Assert.Equal("Since the last commit: 1 added, 1 removed - marked below.", marked.ChangeSummary);
    }

    [Fact]
    public void A_deleted_first_row_stays_at_the_top()
    {
        var marked = CatalogDiff.Compare(
            Libraries(Lib("#First", "x"), Lib("#B", "y")),
            Libraries(Lib("#B", "y")));

        Assert.Equal(["#First", "#B"], marked.References.Select(r => r.Name));
        Assert.True(marked.References[0].Mark.IsRemoved);
    }

    [Fact]
    public void A_parameter_with_no_column_is_named_on_the_hover()
    {
        // Util's IBLOCKSIZE is real project state on the fixture: changing it changes what the
        // machine does, and the table has no column for parameters.
        var head = Libraries(Lib("#Util", "Util, 3.5.22.0 (System)") with
        {
            Parameters = new Dictionary<string, string> { ["IBLOCKSIZE"] = "22800" },
        });
        var now = Libraries(Lib("#Util", "Util, 3.5.22.0 (System)") with
        {
            Parameters = new Dictionary<string, string> { ["IBLOCKSIZE"] = "30000" },
        });

        var util = Assert.Single(CatalogDiff.Compare(head, now).References);

        Assert.True(util.Mark.IsModified);
        Assert.False(util.ResolutionChanged);
        Assert.Contains("parameter IBLOCKSIZE: 22800 → 30000", util.Mark.Was);
    }

    [Fact]
    public void No_committed_copy_means_every_row_is_new()
    {
        var marked = CatalogDiff.Compare(null, Libraries(Lib("#A", "a"), Lib("#B", "b")));

        Assert.All(marked.References, r => Assert.True(r.Mark.IsAdded));
        Assert.Equal("Since the last commit: 2 added - marked below.", marked.ChangeSummary);
    }

    [Fact]
    public void An_unchanged_catalog_marks_nothing_and_says_nothing()
    {
        var head = Libraries(Lib("#A", "a"));
        var marked = CatalogDiff.Compare(head, Libraries(Lib("#A", "a")));

        Assert.False(marked.HasChangeSummary);
        Assert.Equal(MemberChange.None, Assert.Single(marked.References).Mark.Change);
    }

    [Fact]
    public void A_mark_is_never_part_of_what_a_row_records()
    {
        // The tree compares rows by ContentEquals. A mark leaking into it would call a row
        // changed because a pane had looked at it.
        var plain = Lib("#A", "a");
        var marked = plain with { Mark = new RowMark { Change = MemberChange.Modified, Was = "x" } };

        Assert.True(plain.ContentEquals(marked));
        Assert.True(marked.ContentEquals(plain));

        var task = new TaskRecord { Name = "T" };
        Assert.True(task.ContentEquals(task with { Mark = new RowMark { Change = MemberChange.Added } }));

        var connector = new DeviceConnector { ConnectorId = "1" };
        Assert.True(connector.ContentEquals(connector with { Mark = new RowMark { Change = MemberChange.Removed } }));
    }

    [Fact]
    public void A_task_shows_its_interval_and_calls_old_to_new()
    {
        var head = new TaskCatalog
        {
            Schema = 1,
            Tasks = [new TaskRecord { Name = "MainTask", Interval = "t#20ms", Priority = "1", Pous = ["PLC_PRG"] }],
        };
        var now = head with
        {
            Tasks = [new TaskRecord { Name = "MainTask", Interval = "t#10ms", Priority = "1", Pous = ["PLC_PRG", "Signals"] }],
        };

        var task = Assert.Single(CatalogDiff.Compare(head, now).Tasks);

        Assert.Equal("t#20ms", task.ScheduleBefore);
        Assert.Equal("PLC_PRG", task.CallsBefore);
        Assert.False(task.PriorityChanged);
        Assert.False(task.WatchdogChanged);
    }

    [Fact]
    public void A_connector_is_matched_by_its_key_and_a_changed_parameter_is_on_the_hover()
    {
        var head = new DeviceCatalog
        {
            Schema = 1,
            Connectors = [new DeviceConnector
            {
                ConnectorId = "1",
                Interface = "EtherCAT",
                Parameters = new Dictionary<string, string> { ["CycleTime"] = "4000" },
            }],
        };
        var now = head with
        {
            Connectors = [new DeviceConnector
            {
                ConnectorId = "1",
                Interface = "EtherCAT",
                Parameters = new Dictionary<string, string> { ["CycleTime"] = "2000" },
            }],
        };

        var connector = Assert.Single(CatalogDiff.Compare(head, now).Connectors);

        Assert.True(connector.Mark.IsModified);
        Assert.False(connector.ParameterCountChanged);
        Assert.Contains("parameter CycleTime: 4000 → 2000", connector.Mark.Was);
    }

    [Fact]
    public void A_changed_empty_slot_is_said_since_it_has_no_row()
    {
        var head = new DeviceCatalog
        {
            Schema = 1,
            Slots = new Dictionary<string, string> { ["0"] = "<Empty>", ["1"] = "<Empty>" },
        };
        var now = head with { Slots = new Dictionary<string, string> { ["0"] = "<Empty>" } };

        Assert.Equal(
            "Since the last commit: its empty slots changed - the tree lists them.",
            CatalogDiff.Compare(head, now).ChangeSummary);
    }
}
