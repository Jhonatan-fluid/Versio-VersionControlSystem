using Versio.Core.Iec.Ladder;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// The one decision of what a row draws, arm by arm, in the desktop's order, with a stand-in
/// format that answers by file name: nothing here parses a vendor's bytes.
/// </summary>
/// <remarks>
/// <c>RenderChoiceParityTests</c> (in the desktop suite) runs both hosts over the real fixtures
/// and asserts they agree; this pins what the choice itself says, including the arms the
/// fixtures do not reach (a reference row whose catalog cannot be read, a binary primary file).
/// </remarks>
public sealed class RenderChoiceTests
{
    /// <summary>A vendor that reads by suffix: .tbl is a table, .ld a ladder, .lib a library manager, .dev a device, .tsk a task, .void holds nothing.</summary>
    private sealed class StandIn : IPlcProjectFormat
    {
        public PlcVendor Vendor => PlcVendor.Studio5000;

        public string DisplayName => "stand-in";

        public PlcCapabilities Capabilities => new() { TextualExport = true, TypicalStartupCost = TimeSpan.Zero };

        public IReadOnlyList<string> ProjectExtensions => [".standin"];

        public bool IsTemplate(string path) => false;

        public string ConventionalExportPath(string projectPath) => projectPath;

        public string? OccupancyMarkerFor(string projectPath) => null;

        public IReadOnlyList<string> TransientPatternsFor(string projectPath) => [];

        public ProjectOccupancy ParseOccupancy(string markerContents) => ProjectOccupancy.Free;

        public bool NeedsTextToPlan(string relativePath) => false;

        public ImportPlan PlanImport(ProjectManifest? manifest, IReadOnlyList<MirrorFile> mirror, IReadOnlyDictionary<string, string> departed) => new() { Files = [] };

        public ObjectTypeName? DisplayNameFor(string type) => null;

        public MemberTable? TryParseMemberTable(string relativePath, string content) =>
            relativePath.EndsWith(".tbl", StringComparison.Ordinal) ? Table(content) : null;

        public bool HoldsNoSettings(string relativePath, string content) => relativePath.EndsWith(".void", StringComparison.Ordinal);

        public LadderDocument? TryParseLadder(string relativePath, string content) =>
            relativePath.EndsWith(".ld", StringComparison.Ordinal) ? new LadderDocument(new LadderBody("B", []), LadderStyle.Default) : null;

        public LibraryCatalog? TryParseLibraryCatalog(string relativePath, string content) =>
            relativePath.EndsWith(".lib", StringComparison.Ordinal) ? new LibraryCatalog { Schema = 1, Manager = "M:" + content } : null;

        public TaskRecord? TryParseTask(string relativePath, string content) =>
            relativePath.EndsWith(".tsk", StringComparison.Ordinal) ? new TaskRecord { Name = content } : null;

        public DeviceCatalog? TryParseDeviceCatalog(string relativePath, string content) =>
            relativePath.EndsWith(".dev", StringComparison.Ordinal) ? new DeviceCatalog { Schema = 1, Name = "D:" + content } : null;

        // A body when the text says so; nothing for a file that has none.
        public (int Start, int End)? CodeBody(string relativePath, string content) =>
            content.Contains("BODY", StringComparison.Ordinal) ? (1, 2) : null;

        private static MemberTable Table(string content) => content switch
        {
            "interface" => new MemberTable { Name = "FB", IsInterface = true, Members = [new MemberRow { Name = "In", Path = "In" }] },
            "empty-interface" => new MemberTable { Name = "FB", IsInterface = true, Members = [] },
            "empty-type" => new MemberTable { Name = "ST", IsDataType = true, Members = [new MemberRow { Name = "VAR", Path = "VAR", IsSectionHeading = true }] },
            _ => new MemberTable { Name = "ST", IsDataType = true, Members = [new MemberRow { Name = "A", Path = "A" }] },
        };
    }

    private static readonly IReadOnlyList<IPlcProjectFormat> Formats = [new StandIn()];

    private static Func<string, string?> Reader(params (string Path, string? Text)[] files) =>
        path => files.FirstOrDefault(f => f.Path == path).Text;

    private static ProjectTreeNode Object(string name, params string[] files) =>
        new() { Name = name, Path = files.Length > 0 ? files[0] : name, Kind = ProjectNodeKind.Object, Files = files, TypeName = "Widget" };

    private static RenderChoice Decide(ProjectTreeNode node, Func<string, string?> read, string? primary = null) =>
        RenderChoice.Decide(node, read, primary, Formats);

    private const string LibrariesYaml = "schema: 1\nvendor: codesys\nmanager: \"Library Manager\"\nreferences:\n  - name: Standard\n";
    private const string TasksYaml = "schema: 1\nconfiguration: \"Task Configuration\"\ntasks:\n  - name: MainTask\n    priority: \"1\"\n";
    private const string DevicesYaml = "schema: 1\nname: Device\nconnectors: []\n";

    [Fact]
    public void A_project_row_is_its_card_whatever_its_files()
    {
        var node = new ProjectTreeNode { Name = "P", Path = "P.project", Kind = ProjectNodeKind.Project, Files = ["P.project"] };

        Assert.Equal(RenderKind.ProjectCard, Decide(node, Reader(("P.project", "\0binary"))).Kind);
        Assert.Equal(RenderKind.ProjectCard, Decide(node, Reader()).Kind);
    }

    [Fact]
    public void A_reference_row_opens_its_whole_catalog_read_back_by_its_suffix()
    {
        var node = new ProjectTreeNode { Name = "Standard", Path = "L.libraries.yaml::library::Standard", Kind = ProjectNodeKind.Library, Files = ["L.libraries.yaml"] };

        var choice = Decide(node, Reader(("L.libraries.yaml", LibrariesYaml)));

        Assert.Equal(RenderKind.LibraryCatalog, choice.Kind);
        Assert.Equal("L.libraries.yaml", choice.File);
        Assert.Equal(["L.libraries.yaml"], choice.Files);
        Assert.Equal("Library Manager", Assert.IsType<LibraryCatalog>(choice.Model).Manager);
        Assert.Equal("Library Manager", Assert.IsType<LibraryCatalog>(choice.Reparse!([LibrariesYaml])).Manager);
        Assert.Null(choice.Format);
    }

    [Fact]
    public void A_task_row_is_its_own_card_and_a_device_row_its_own()
    {
        var task = new ProjectTreeNode { Name = "MainTask", Path = "T.tasks.yaml::task::MainTask", Kind = ProjectNodeKind.Task, Files = ["T.tasks.yaml"] };
        var device = new ProjectTreeNode { Name = "Slot 1", Path = "D.devices.yaml::slot::1", Kind = ProjectNodeKind.Device, Files = ["D.devices.yaml"] };

        var taskChoice = Decide(task, Reader(("T.tasks.yaml", TasksYaml)));
        var deviceChoice = Decide(device, Reader(("D.devices.yaml", DevicesYaml)));

        Assert.Equal(RenderKind.TaskCard, taskChoice.Kind);
        Assert.Equal("MainTask", Assert.IsType<TaskRecord>(taskChoice.Model).Name);
        Assert.Equal(RenderKind.DeviceCard, deviceChoice.Kind);
        Assert.Equal("Device", Assert.IsType<DeviceCatalog>(deviceChoice.Model).Name);
    }

    [Fact]
    public void A_grafted_row_whose_catalog_cannot_be_read_draws_nothing_rather_than_inventing_a_view()
    {
        var task = new ProjectTreeNode { Name = "Other", Path = "T.tasks.yaml::task::Other", Kind = ProjectNodeKind.Task, Files = ["T.tasks.yaml"] };
        var unread = new ProjectTreeNode { Name = "Standard", Path = "x", Kind = ProjectNodeKind.Library, Files = ["L.libraries.yaml"] };
        var folder = new ProjectTreeNode { Name = "F", Path = "F", Kind = ProjectNodeKind.Folder };

        // The task named by the row is not in the file; the catalog cannot be read; a folder.
        Assert.Equal(RenderKind.Nothing, Decide(task, Reader(("T.tasks.yaml", TasksYaml))).Kind);
        Assert.Equal(RenderKind.Nothing, Decide(unread, Reader()).Kind);
        Assert.Equal(RenderKind.Nothing, Decide(folder, Reader()).Kind);
    }

    [Fact]
    public void An_objects_versio_catalog_wins_in_the_order_libraries_tasks_devices()
    {
        Assert.Equal(RenderKind.LibraryCatalog, Decide(Object("L", "L.libraries.yaml"), Reader(("L.libraries.yaml", LibrariesYaml))).Kind);
        Assert.Equal(RenderKind.TaskCatalog, Decide(Object("T", "T.xml", "T.tasks.yaml"), Reader(("T.tasks.yaml", TasksYaml), ("T.xml", "<x/>"))).Kind);
        Assert.Equal(RenderKind.DeviceCatalog, Decide(Object("D", "D.devices.yaml"), Reader(("D.devices.yaml", DevicesYaml))).Kind);

        // A catalog the YAML reader refuses is not a catalog: the object falls through to its file.
        Assert.Equal(RenderKind.Source, Decide(Object("L", "L.libraries.yaml"), Reader(("L.libraries.yaml", "not: yaml"))).Kind);
    }

    [Fact]
    public void A_vendors_own_catalog_files_are_read_by_its_format_and_read_back_by_it()
    {
        var libraries = Decide(Object("Libraries", "p/Libraries.lib"), Reader(("p/Libraries.lib", "one")));
        var device = Decide(Object("Device", "p/Device.dev"), Reader(("p/Device.dev", "d")));
        var task = Decide(Object("MainTask", "p/MainTask.tsk"), Reader(("p/MainTask.tsk", "MainTask")));

        Assert.Equal(RenderKind.LibraryCatalog, libraries.Kind);
        Assert.Same(Formats[0], libraries.Format);
        Assert.Equal("M:one", Assert.IsType<LibraryCatalog>(libraries.Model).Manager);
        Assert.Equal("M:two", Assert.IsType<LibraryCatalog>(libraries.Reparse!(["two"])).Manager);
        Assert.Null(libraries.Reparse!([null]));

        Assert.Equal(RenderKind.DeviceCatalog, device.Kind);
        Assert.Equal(RenderKind.TaskCard, task.Kind);
        Assert.Equal("MainTask", Assert.IsType<TaskRecord>(task.Model).Name);
    }

    [Fact]
    public void A_configuration_saved_as_one_file_per_task_is_the_table_of_its_children()
    {
        var node = Object("Task Configuration", "p/Task Configuration.cfg") with
        {
            Children = [Object("A", "p/A.tsk"), Object("B", "p/B.tsk"), Object("Not a task", "p/N.txt")],
        };
        var read = Reader(("p/Task Configuration.cfg", "<cfg/>"), ("p/A.tsk", "A"), ("p/B.tsk", "B"), ("p/N.txt", "n"));

        var choice = Decide(node, read);

        Assert.Equal(RenderKind.TaskCatalog, choice.Kind);
        Assert.Equal(["p/A.tsk", "p/B.tsk"], choice.Files);
        var catalog = Assert.IsType<TaskCatalog>(choice.Model);
        Assert.Equal("Task Configuration", catalog.Configuration);
        Assert.Equal(["A", "B"], catalog.Tasks.Select(t => t.Name));

        // The committed copies, one per file, in the same order; a file with no committed copy
        // (never committed) is left out rather than read as a task named "".
        var head = Assert.IsType<TaskCatalog>(choice.Reparse!(["A1", null]));
        Assert.Equal(["A1"], head.Tasks.Select(t => t.Name));
    }

    [Fact]
    public void A_table_comes_from_any_layer_and_an_interface_carries_its_code_body()
    {
        var node = Object("FB", "p/FB.st", "p/FB.tbl");
        var read = Reader(("p/FB.st", "DECL\nBODY\nEND"), ("p/FB.tbl", "interface"));

        var choice = Decide(node, read);

        Assert.Equal(RenderKind.MemberTable, choice.Kind);
        Assert.Equal("p/FB.tbl", choice.File);
        Assert.Equal(CodeUnder.Body, choice.Code);
        Assert.Equal("p/FB.st", choice.CodeFile);
        Assert.Equal((1, 2), choice.Body);
        Assert.IsType<MemberTable>(choice.Reparse!(["interface"]));
    }

    [Fact]
    public void An_interface_over_a_ladder_carries_the_ladder_and_over_a_file_with_no_body_the_whole_file()
    {
        var ladder = Decide(Object("OB", "p/OB.ld", "p/OB.tbl"), Reader(("p/OB.ld", "<ld/>"), ("p/OB.tbl", "interface")));
        var whole = Decide(Object("FB", "p/FB.xml", "p/FB.tbl"), Reader(("p/FB.xml", "<xml/>"), ("p/FB.tbl", "interface")));

        Assert.Equal(CodeUnder.Ladder, ladder.Code);
        Assert.NotNull(ladder.CodeLadder);
        Assert.Equal("p/OB.ld", ladder.CodeFile);

        // The desktop has always drawn the whole file under such a table; the web drew the table
        // with nothing under it until this was one rule.
        Assert.Equal(CodeUnder.WholeFile, whole.Code);
        Assert.Equal("p/FB.xml", whole.CodeFile);
        Assert.Null(whole.Body);
    }

    [Fact]
    public void A_data_type_carries_no_code_and_a_binary_or_unreadable_primary_file_none_either()
    {
        Assert.Equal(CodeUnder.None, Decide(Object("ST", "p/ST.tbl"), Reader(("p/ST.tbl", "type"))).Code);
        Assert.Equal(CodeUnder.None, Decide(Object("FB", "p/FB.st", "p/FB.tbl"), Reader(("p/FB.st", "BODY\0"), ("p/FB.tbl", "interface"))).Code);
        Assert.Equal(CodeUnder.None, Decide(Object("FB", "p/FB.st", "p/FB.tbl"), Reader(("p/FB.tbl", "interface"))).Code);
    }

    [Fact]
    public void The_husk_rule_drops_an_empty_type_to_its_source_but_keeps_an_empty_interface()
    {
        // A table with nothing in it is a husk (2026-10-14); an interface with no variables is
        // drawn as every other POU is, an empty table over its code (the owner, 2026-10-23).
        var husk = Decide(Object("ST", "p/ST.tbl"), Reader(("p/ST.tbl", "empty-type")));
        var pou = Decide(Object("Main", "p/Main.st", "p/Main.tbl"), Reader(("p/Main.st", "BODY"), ("p/Main.tbl", "empty-interface")));

        Assert.Equal(RenderKind.Source, husk.Kind);
        Assert.Equal(RenderKind.MemberTable, pou.Kind);
        Assert.Equal(CodeUnder.Body, pou.Code);
    }

    [Fact]
    public void A_ladder_with_no_table_is_the_ladder_from_any_layer()
    {
        var choice = Decide(Object("OB1", "p/OB1.txt", "p/OB1.ld"), Reader(("p/OB1.txt", "t"), ("p/OB1.ld", "<ld/>")));

        Assert.Equal(RenderKind.Ladder, choice.Kind);
        Assert.Equal("p/OB1.ld", choice.File);
        Assert.IsType<LadderDocument>(choice.Model);
        Assert.IsType<LadderDocument>(choice.Reparse!(["<ld/>"]));
    }

    [Fact]
    public void Then_the_empty_object_note_then_markdown_then_the_page_then_the_source()
    {
        Assert.Equal(RenderKind.EmptyObject, Decide(Object("Plc Logic", "p/Plc Logic.void"), Reader(("p/Plc Logic.void", "<empty/>"))).Kind);
        Assert.Equal(RenderKind.Markdown, Decide(Object("Readme", "docs/README.md"), Reader(("docs/README.md", "# Hi"))).Kind);
        Assert.Equal(RenderKind.Markdown, Decide(Object("Notes", "docs/n.markdown"), Reader(("docs/n.markdown", "# Hi"))).Kind);
        Assert.Equal(RenderKind.WebPage, Decide(Object("Site", "docs/index.html"), Reader(("docs/index.html", "<html/>"))).Kind);
        Assert.Equal(RenderKind.WebPage, Decide(Object("Site", "docs/index.HTM"), Reader(("docs/index.HTM", "<html/>"))).Kind);
        Assert.Equal(RenderKind.Source, Decide(Object("Main", "p/Main.st"), Reader(("p/Main.st", "PROGRAM Main"))).Kind);
    }

    [Fact]
    public void A_binary_or_missing_primary_file_draws_nothing()
    {
        Assert.Equal(RenderKind.Nothing, Decide(Object("Img", "p/logo.png"), Reader(("p/logo.png", "\u0089PNG\0"))).Kind);
        Assert.Equal(RenderKind.Nothing, Decide(Object("Gone", "p/gone.st"), Reader()).Kind);
        Assert.Equal(RenderKind.Nothing, Decide(Object("None") with { Files = [] }, Reader()).Kind);

        // A binary file named like a document is still binary.
        Assert.Equal(RenderKind.Nothing, Decide(Object("Doc", "p/doc.md"), Reader(("p/doc.md", "x\0y"))).Kind);
    }

    [Fact]
    public void The_text_arms_draw_the_layer_the_host_asks_for()
    {
        var node = Object("Glob_Var", "p/Glob_Var.st", "p/Glob_Var.xml") with { TypeName = "GVL" };
        var read = Reader(("p/Glob_Var.st", "VAR_GLOBAL END_VAR"), ("p/Glob_Var.xml", "<gvl/>"));

        var asked = Decide(node, read, "p/Glob_Var.xml");

        Assert.Equal(RenderKind.Source, asked.Kind);
        Assert.Equal("p/Glob_Var.xml", asked.File);
    }

    [Fact]
    public void A_vendors_native_file_drawn_as_source_says_why_and_an_objects_own_text_does_not()
    {
        var native = Decide(Object("Live_Visu", "p/Live_Visu.vis.xml") with { TypeName = "Visualization" }, Reader(("p/Live_Visu.vis.xml", "<visu/>")));
        var text = Decide(Object("Main", "p/Main.st"), Reader(("p/Main.st", "PROGRAM Main")));
        var untyped = Decide(Object("Thing", "p/thing.xml") with { TypeName = null }, Reader(("p/thing.xml", "<t/>")));
        var plain = Decide(new ProjectTreeNode { Name = "x.xml", Path = "tools/x.xml", Kind = ProjectNodeKind.File, Files = ["tools/x.xml"] }, Reader(("tools/x.xml", "<t/>")));

        Assert.Equal("Versio has no drawing for this Visualization yet; this is its file as the vendor saved it.", native.Notice);
        Assert.Null(text.Notice);
        Assert.Equal("Versio has no drawing for this object yet; this is its file as the vendor saved it.", untyped.Notice);

        // A file that is not an object has no drawing to be missing.
        Assert.Null(plain.Notice);
    }
}
