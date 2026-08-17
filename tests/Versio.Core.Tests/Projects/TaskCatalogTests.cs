using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// The hand-written reader for <c>*.tasks.yaml</c>, against the shapes the emitter produces.
/// </summary>
/// <remarks>
/// The one structural difference from <see cref="LibraryCatalog"/> is the nested scalar list:
/// <c>pous</c> is written as dash lines deeper than the task's own keys, and a reader that
/// treated every dash as a new item would quietly turn one task into one task per POU — which
/// is why the dash-depth cases here are the ones that matter.
/// </remarks>
public sealed class TaskCatalogTests
{
    /// <summary>The committed fixture's shape, verbatim fields and all.</summary>
    private const string RealShape = """
        # Task configuration, exported by Versio.
        schema: 1
        vendor: codesys
        configuration: "Task Configuration"
        path: "Device/Plc Logic/Application/Task Configuration"
        taskCount: 2
        tasks:
          - name: MainTask
            priority: "1"
            interval: "t#20ms"
            intervalUnit: ms
            coreBinding: -2
            watchdogEnabled: false
            watchdogSensitivity: "1"
            pous:
              - PLC_PRG
              - Simulation
          - name: VISU_TASK
            priority: "31"
            interval: "100"
            intervalUnit: ms
            coreBinding: -1
            watchdogEnabled: false
            pous:
              - VisuElems.Visu_Prg
        """;

    [Fact]
    public void The_fixtures_own_shape_parses_completely()
    {
        var catalog = TaskCatalog.Parse(RealShape);

        Assert.True(catalog.IsValid);
        Assert.Equal("codesys", catalog.Vendor);
        Assert.Equal("Task Configuration", catalog.Configuration);
        Assert.Equal(2, catalog.Tasks.Count);

        var main = catalog.Tasks[0];
        Assert.Equal("MainTask", main.Name);
        Assert.Equal("1", main.Priority);
        Assert.Equal("t#20ms", main.Interval);
        Assert.Equal("ms", main.IntervalUnit);
        Assert.Equal("-2", main.CoreBinding);
        Assert.False(main.WatchdogEnabled);
        Assert.Equal("1", main.WatchdogSensitivity);
    }

    [Fact]
    public void A_pou_line_is_a_list_entry_and_not_a_new_task()
    {
        // The trap this parser exists to avoid: pous entries are dash lines, and a walk
        // that flushed the open task on every dash would read two tasks as five.
        var catalog = TaskCatalog.Parse(RealShape);

        Assert.Equal(["PLC_PRG", "Simulation"], catalog.Tasks[0].Pous);
        Assert.Equal(["VisuElems.Visu_Prg"], catalog.Tasks[1].Pous);
    }

    [Fact]
    public void Keys_after_the_pou_list_still_belong_to_their_task()
    {
        // The emitter writes pous last today; a reader must not depend on that. A key at
        // the task's own indent, after the list, closes the list and lands on the task.
        var catalog = TaskCatalog.Parse("""
            schema: 1
            tasks:
              - name: T
                pous:
                  - A
                priority: "5"
            """);

        var task = Assert.Single(catalog.Tasks);
        Assert.Equal(["A"], task.Pous);
        Assert.Equal("5", task.Priority);
    }

    [Fact]
    public void Values_are_kept_verbatim()
    {
        // "t#20ms" on one task and "100" on the other in the same file is what CODESYS
        // really stores. Normalising either would put a diff in front of somebody that
        // they did not make.
        var catalog = TaskCatalog.Parse(RealShape);

        Assert.Equal("t#20ms", catalog.Tasks[0].Interval);
        Assert.Equal("100", catalog.Tasks[1].Interval);
    }

    [Theory]
    [InlineData("t#20ms", "ms", "t#20ms")]
    [InlineData("100", "ms", "100 ms")]
    [InlineData("100", "", "100")]
    [InlineData("", "ms", "")]
    public void The_schedule_phrase_adds_the_unit_only_when_the_interval_lacks_one(
        string interval, string unit, string expected)
    {
        var task = new TaskRecord { Name = "T", Interval = interval, IntervalUnit = unit };

        Assert.Equal(expected, task.DisplaySchedule);
    }

    [Fact]
    public void A_task_with_no_name_is_dropped_not_invented()
    {
        var catalog = TaskCatalog.Parse("schema: 1\ntasks:\n  - priority: \"1\"\n");

        Assert.Empty(catalog.Tasks);
    }

    [Fact]
    public void A_file_with_no_schema_is_not_mistaken_for_an_empty_configuration()
    {
        Assert.False(TaskCatalog.Parse("tasks:\n  - name: T\n").IsValid);
        Assert.False(TaskCatalog.Parse(null).IsValid);
        Assert.False(TaskCatalog.Parse("").IsValid);
    }

    [Fact]
    public void Two_parses_of_one_document_are_content_equal()
    {
        // Record equality answers false here because each parse allocates a fresh Pous
        // list; ContentEquals exists for this pair. If this fails, every task row in the
        // tree shows as modified on every refresh.
        var a = TaskCatalog.Parse(RealShape).Tasks[0];
        var b = TaskCatalog.Parse(RealShape).Tasks[0];

        Assert.True(a.ContentEquals(b));
    }

    [Theory]
    [InlineData("    priority: \"1\"\n", "    priority: \"2\"\n")]
    [InlineData("    interval: \"t#20ms\"\n", "    interval: \"t#10ms\"\n")]
    [InlineData("    coreBinding: -2\n", "    coreBinding: -1\n")]
    [InlineData("    watchdogEnabled: false\n", "    watchdogEnabled: true\n")]
    [InlineData("    pous:\n      - A\n", "    pous:\n      - A\n      - B\n")]
    [InlineData("    pous:\n      - A\n      - B\n", "    pous:\n      - B\n      - A\n")]
    public void Any_recorded_difference_is_a_content_difference(string left, string right)
    {
        // Each case varies exactly one thing, so a comparison blind to one field fails
        // that field's case rather than passing on the strength of another difference.
        // The last case is a reorder: the POU list's order is the task's own, and a
        // reorder is a real change.
        var a = Assert.Single(
            TaskCatalog.Parse($"schema: 1\ntasks:\n  - name: T\n{left}").Tasks);
        var b = Assert.Single(
            TaskCatalog.Parse($"schema: 1\ntasks:\n  - name: T\n{right}").Tasks);

        Assert.False(a.ContentEquals(b));
    }
}
