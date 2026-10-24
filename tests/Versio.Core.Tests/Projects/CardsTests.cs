using System.Text.RegularExpressions;
using Versio.Core.Diff;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// The rows of the task, device and project cards, decided once for both hosts, and the card as
/// HTML.
/// </summary>
public sealed class CardsTests
{
    private static string Value(IReadOnlyList<CardFact> facts, string label) => Assert.Single(facts, f => f.Label == label).Value;

    [Fact]
    public void A_task_card_says_what_its_catalog_records_and_dashes_what_it_does_not()
    {
        var bare = Cards.Task(new TaskRecord { Name = "T" });
        var full = Cards.Task(new TaskRecord
        {
            Name = "MainTask", Priority = "1", Interval = "t#20ms", IntervalUnit = "ms", CoreBinding = "-1",
            WatchdogEnabled = true, WatchdogTime = "t#100ms", WatchdogSensitivity = "1", Pous = ["PLC_PRG", "Simulation"],
        });

        Assert.Equal(["Priority", "Interval", "Core binding", "Watchdog", "Calls"], bare.Select(f => f.Label));
        Assert.Equal("—", Value(bare, "Priority"));
        Assert.Equal("—", Value(bare, "Core binding"));
        Assert.Equal("disabled", Value(bare, "Watchdog"));
        Assert.Equal("—", Value(bare, "Calls"));

        Assert.Equal("-1 (not bound to a core)", Value(full, "Core binding"));
        Assert.Equal("enabled · time t#100ms · sensitivity 1", Value(full, "Watchdog"));
        Assert.Equal("PLC_PRG, Simulation", Value(full, "Calls"));
    }

    [Fact]
    public void A_device_card_is_the_slot_the_value_or_the_connector_the_row_stands_for()
    {
        var catalog = new DeviceCatalog
        {
            Schema = 1,
            Name = "EtherCAT_Master",
            Connectors =
            [
                new DeviceConnector { ConnectorId = "1", Interface = "Common.Ethercat", ModuleType = "2", Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "1", ["B"] = "2" } },
            ],
        };
        var key = catalog.KeyedConnectors()[0].Key;

        var slot = Cards.Device(catalog, new ProjectTreeNode { Name = "0", Path = "D" + DeviceCatalog.SlotRowMarker + "0", Kind = ProjectNodeKind.Device });
        var parameter = Cards.Device(catalog, new ProjectTreeNode { Name = "A", Path = "D" + DeviceCatalog.ParamRowMarker + "A", Kind = ProjectNodeKind.Device, Detail = "1" });
        var connector = Cards.Device(catalog, new ProjectTreeNode { Name = "Common.Ethercat", Path = "D" + DeviceCatalog.ConnectorRowMarker + key, Kind = ProjectNodeKind.Device });
        var unknown = Cards.Device(catalog, new ProjectTreeNode { Name = "X", Path = "D" + DeviceCatalog.ConnectorRowMarker + "nowhere", Kind = ProjectNodeKind.Device });

        Assert.Equal(["Slot", "Device"], slot.Select(f => f.Label));
        Assert.Equal("0", Value(slot, "Slot"));
        Assert.Equal("EtherCAT_Master", Value(slot, "Device"));
        Assert.Equal("1", Value(parameter, "Value"));
        Assert.Equal(["Device", "Connector id", "Module type", "Parameters"], connector.Select(f => f.Label));
        Assert.Equal("2", Value(connector, "Parameters"));
        Assert.Equal(["Device"], unknown.Select(f => f.Label));
    }

    private static ProjectManifest Manifest(params ProjectObject[] objects) => new()
    {
        Schema = 10,
        Vendor = PlcVendor.Codesys,
        Project = "Plant",
        ToolchainVersion = "CODESYS V3.5 SP22 Patch 4",
        Objects = objects,
        Warnings = [new ExportWarning { Code = "W1", Path = "x" }],
    };

    private static ProjectObject Obj(string path, string type, string guid = "") => new() { Path = path, Type = type, Guid = guid.Length > 0 ? guid : path, Name = path };

    [Fact]
    public void A_tracked_projects_card_says_who_wrote_the_mirror_what_is_in_it_and_what_it_left_out()
    {
        var project = new WorkspaceProject
        {
            ProjectPath = "plc/Plant.project",
            ExportPath = "plc/Plant",
            ExportState = "tracked",
            Manifest = Manifest(Obj("A.st", "POU", "a"), Obj("A.xml", "POU", "a"), Obj("B.st", "DUT", "b"), Obj("L.libraries.yaml", "LIBRARY_MANAGER", "l")),
            Libraries = [new LibrarySnapshot { Path = "plc/Plant/L.libraries.yaml", Current = new LibraryCatalog { Schema = 1, References = [new LibraryReference { Name = "Standard" }, new LibraryReference { Name = "Util" }] } }],
            Tasks = [new TaskSnapshot { Path = "t", Current = new TaskCatalog { Schema = 1, Tasks = [new TaskRecord { Name = "MainTask", Interval = "t#20ms" }] } }],
        };

        var facts = Cards.Project(project, fileCount: 0);

        Assert.Equal(["Vendor", "Exported by", "Sync state", "Mirror", "Objects", "Libraries", "Tasks", "Coverage", "Warnings"], facts.Select(f => f.Label));
        Assert.Equal("Codesys", Value(facts, "Vendor"));
        Assert.Equal("CODESYS V3.5 SP22 Patch 4", Value(facts, "Exported by"));
        Assert.Equal("4 exported file(s) · manifest schema 10", Value(facts, "Mirror"));
        // One entry per object: A.st and A.xml share a GUID and count once, so three kinds of one.
        Assert.Equal(3, Value(facts, "Objects").Split(", ").Length);
        Assert.All(Value(facts, "Objects").Split(", "), part => Assert.StartsWith("1 ", part, StringComparison.Ordinal));
        Assert.Equal("2 reference(s) in 1 manager(s)", Value(facts, "Libraries"));
        Assert.Equal("MainTask (t#20ms)", Value(facts, "Tasks"));
        Assert.StartsWith("complete", Value(facts, "Coverage"), StringComparison.Ordinal);
        Assert.Equal("1 — see the log", Value(facts, "Warnings"));
    }

    [Fact]
    public void A_mirror_never_exported_says_so_and_stops_and_a_mirror_without_a_recorded_toolchain_draws_no_row_for_it()
    {
        var never = Cards.Project(new WorkspaceProject { ProjectPath = "p.project", ExportPath = "p" }, 0);
        var older = Cards.Project(new WorkspaceProject { ProjectPath = "p.project", ExportPath = "p", Manifest = Manifest() with { ToolchainVersion = "", Warnings = [] } }, 0);

        Assert.Equal(["Vendor", "Sync state", "Mirror"], never.Select(f => f.Label));
        Assert.Equal("never exported", Value(never, "Mirror"));
        Assert.Equal("tracked", Value(never, "Sync state"));
        Assert.DoesNotContain(older, f => f.Label == "Exported by");
        Assert.DoesNotContain(older, f => f.Label == "Warnings");
    }

    [Fact]
    public void A_source_project_gets_its_own_rows_from_the_format_that_claimed_the_folder()
    {
        // The stand-in vendor's source-project extension, registered for the span of the test.
        var format = new SourceFormat();
        PlcFormats.Register(format);
        try
        {
            var facts = Cards.Project(new WorkspaceProject { ProjectPath = "plc/Plant.srcproj", ExportPath = "plc/Plant.srcproj", ExportState = "read from its files", Manifest = Manifest(Obj("A.st", "POU")) }, fileCount: 12);

            Assert.Equal(["Vendor", "Stored as", "Sync", "Project folder", "Objects"], facts.Select(f => f.Label));
            Assert.Equal("source stand-in", Value(facts, "Vendor"));
            Assert.Equal("12 file(s)", Value(facts, "Project folder"));
        }
        finally
        {
            PlcFormats.Unregister(format.Vendor);
        }
    }

    [Fact]
    public void A_card_as_html_escapes_and_marks_what_the_file_says_and_names_its_file()
    {
        var html = CardHtml.Fragment("task", "Main" + (char)0x202E, [new CardFact("Calls", "<b>PLC_PRG</b>"), new CardFact("Priority", "1")], "p/MainTask.tsk");

        Assert.StartsWith("<div class=\"pc-page\" data-kind=\"task\" data-src-path=\"p/MainTask.tsk\" data-hidden-chars=\"1\">", html, StringComparison.Ordinal);
        Assert.Contains("class=\"cd-notice\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(((char)0x202E).ToString(), html, StringComparison.Ordinal);
        Assert.Contains("<tr class=\"pc-fact\" data-fact=\"Calls\"><th class=\"pc-label\">Calls</th><td class=\"pc-value\">&lt;b&gt;PLC_PRG&lt;/b&gt;</td></tr>", html, StringComparison.Ordinal);

        var used = Regex.Matches(html, "class=\"([^\"]+)\"").SelectMany(m => m.Groups[1].Value.Split(' ')).Distinct().ToList();
        Assert.All(used, c => Assert.Matches("\\." + Regex.Escape(c) + "[\\s,{:>.\\[)]", CodePageAssets.Css));
    }

    private sealed class SourceFormat : IPlcProjectFormat
    {
        public PlcVendor Vendor => PlcVendor.Studio5000;

        public string DisplayName => "source stand-in";

        public PlcCapabilities Capabilities => new() { TextualExport = true, TypicalStartupCost = TimeSpan.Zero };

        public IReadOnlyList<string> ProjectExtensions => [".standin"];

        public IReadOnlyList<string> SourceProjectExtensions => [".srcproj"];

        public bool IsTemplate(string path) => false;

        public string ConventionalExportPath(string projectPath) => projectPath;

        public string? OccupancyMarkerFor(string projectPath) => null;

        public IReadOnlyList<string> TransientPatternsFor(string projectPath) => [];

        public ProjectOccupancy ParseOccupancy(string markerContents) => ProjectOccupancy.Free;

        public bool NeedsTextToPlan(string relativePath) => false;

        public ImportPlan PlanImport(ProjectManifest? manifest, IReadOnlyList<MirrorFile> mirror, IReadOnlyDictionary<string, string> departed) => new() { Files = [] };

        public ObjectTypeName? DisplayNameFor(string type) => null;
    }
}
