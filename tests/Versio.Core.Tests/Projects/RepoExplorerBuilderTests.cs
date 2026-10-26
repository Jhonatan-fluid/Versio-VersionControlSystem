using Versio.Core.Git;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// The repo explorer: a folder tree of the clone, with each tracked PLC project grafted in
/// as the same object tree the Project panel already builds.
/// </summary>
public sealed class RepoExplorerBuilderTests
{
    private const string ProjectPath = "fixtures/projects/Foo.project";
    private const string ExportPath = "fixtures/projects/Foo";

    private static ProjectObject Obj(string name, string path, string guid) =>
        new() { Name = name, Path = path, Guid = guid, Type = "POU", Form = "ST_DECL" };

    private static WorkspaceProject Project(ProjectManifest? manifest, string state = "tracked") =>
        new()
        {
            ProjectPath = ProjectPath,
            ExportPath = ExportPath,
            Manifest = manifest,
            ExportState = state,
        };

    private static IReadOnlyList<ProjectTreeNode> Build(
        IReadOnlyList<string> files,
        WorkspaceProject? project = null,
        string search = "") =>
        RepoExplorerBuilder.Build(
            files,
            project is null ? [] : [project],
            new ProjectTreeOptions
            {
                ChangedOnly = false,
                HideUnchangedProjects = false,
                Search = search,
            });

    private static IEnumerable<ProjectTreeNode> Flatten(ProjectTreeNode node)
    {
        yield return node;
        foreach (var descendant in node.Children.SelectMany(Flatten))
        {
            yield return descendant;
        }
    }

    private static IEnumerable<ProjectTreeNode> Flatten(IEnumerable<ProjectTreeNode> roots) =>
        roots.SelectMany(Flatten);

    private static FileChange Change(string path, ChangeKind kind) =>
        new() { Path = path, Staged = kind, Unstaged = ChangeKind.Unmodified };

    /// <summary>
    /// A comparison marks the listing as the Project panel marks a working-tree diff (FINISH.md
    /// Step 8): each changed file carries its state, a folder rolls its descendants up with a
    /// count, a deleted file is listed struck rather than dropped, and a plain listing marks nothing.
    /// </summary>
    [Fact]
    public void A_comparison_marks_files_rolls_folders_up_and_lists_a_deleted_file()
    {
        var files = new[] { "docs/README.md", "src/Pump.st", "src/Valve.st" };
        var changes = new[] { Change("src/Pump.st", ChangeKind.Modified), Change("src/Old.st", ChangeKind.Deleted), Change("docs/New.md", ChangeKind.Added) };

        var marked = RepoExplorerBuilder.Build(files, [], new ProjectTreeOptions { ChangedOnly = false, HideUnchangedProjects = false }, changes: changes);
        var plain = RepoExplorerBuilder.Build(files, [], new ProjectTreeOptions { ChangedOnly = false, HideUnchangedProjects = false });
        var changedOnly = RepoExplorerBuilder.Build(files, [], new ProjectTreeOptions { ChangedOnly = true, HideUnchangedProjects = true }, changes: changes);

        var src = Flatten(marked).Single(n => n.Path == "src");
        Assert.Equal((ChangeKind.Modified, 2, ChangeKind.Deleted), (src.Children.Single(c => c.Name == "Pump.st").Status, src.ChangedDescendants, src.RollUp));
        Assert.Equal(ChangeKind.Deleted, src.Children.Single(c => c.Name == "Old.st").Status);
        Assert.Equal(ChangeKind.Unmodified, src.Children.Single(c => c.Name == "Valve.st").Status);
        Assert.Equal(ChangeKind.Added, Flatten(marked).Single(n => n.Path == "docs/New.md").Status);
        Assert.All(Flatten(plain), n => Assert.Equal((ChangeKind.Unmodified, 0), (n.Status, n.ChangedDescendants)));
        // Changed only: the unchanged files are gone, the folders that hold a change stay.
        Assert.Equal(["docs/New.md", "src/Old.st", "src/Pump.st"], Flatten(changedOnly).Where(n => n.Kind == ProjectNodeKind.File).Select(n => n.Path).Order());
    }

    /// <summary>A rename across folders is listed at both places: struck where it was, marked where it is (review, 2026-10-24).</summary>
    [Fact]
    public void A_rename_is_struck_at_its_old_path_and_marked_at_its_new_one()
    {
        var files = new[] { "src/New.st", "src/Pump.st" };
        var moved = new FileChange { Path = "src/New.st", Staged = ChangeKind.Renamed, Unstaged = ChangeKind.Unmodified, OriginalPath = "lib/Old.st" };

        var marked = RepoExplorerBuilder.Build(files, [], new ProjectTreeOptions { ChangedOnly = false, HideUnchangedProjects = false }, changes: [moved]);

        Assert.Equal(ChangeKind.Renamed, Flatten(marked).Single(n => n.Path == "src/New.st").Status);
        Assert.Equal(ChangeKind.Deleted, Flatten(marked).Single(n => n.Path == "lib/Old.st").Status);
    }

    [Fact]
    public void A_bound_project_is_a_project_node_not_a_file_named_project()
    {
        var roots = Build(
            [
                ProjectPath,
                ExportPath + "/Device/App/Simulation.st",
                "src/tools/foo.py",
            ],
            Project(new ProjectManifest
            {
                Project = "Foo.project",
                Objects = [Obj("Simulation", "Device/App/Simulation.st", "g1")],
            }));

        var project = Assert.Single(Flatten(roots), n => n.Kind == ProjectNodeKind.Project);
        Assert.Equal("Foo.project", project.Name);
        Assert.Equal(ProjectPath, project.Path);

        var simulation = Assert.Single(Flatten(project), n => n.Kind == ProjectNodeKind.Object);
        Assert.Equal("Simulation", simulation.Name);

        Assert.DoesNotContain(Flatten(roots), n =>
            n.Kind == ProjectNodeKind.File && n.Path == ProjectPath);
    }

    [Fact]
    public void Without_a_binding_the_export_is_ordinary_files()
    {
        // The mutation check for the graft: if BuildProjectNode is never called, this is
        // how Simulation.st appears — and The_export_tree_is_not_a_second_folder_of_st_files
        // goes red. This one staying green while that one does is the pair.
        var roots = Build(
            [ProjectPath, ExportPath + "/Device/App/Simulation.st"]);

        Assert.Contains(Flatten(roots), n =>
            n.Kind == ProjectNodeKind.File &&
            n.Path == ExportPath + "/Device/App/Simulation.st");
        Assert.DoesNotContain(Flatten(roots), n => n.Kind == ProjectNodeKind.Project);
    }

    [Fact]
    public void The_export_tree_is_not_a_second_folder_of_st_files()
    {
        // Dropping the graft would leak Simulation.st as fixtures/projects/Foo/... — the
        // same file in two places, which is the defect this builder exists to prevent.
        var roots = Build(
            [
                ProjectPath,
                ExportPath + "/Device/App/Simulation.st",
                ExportPath + "/_manifest.json",
                "README.md",
            ],
            Project(new ProjectManifest
            {
                Project = "Foo.project",
                Objects = [Obj("Simulation", "Device/App/Simulation.st", "g1")],
            }));

        Assert.DoesNotContain(Flatten(roots), n =>
            n.Kind == ProjectNodeKind.File &&
            n.Path.StartsWith(ExportPath + "/", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(Flatten(roots), n =>
            n.Kind == ProjectNodeKind.Folder && n.Path == ExportPath);
    }

    [Fact]
    public void A_python_file_nests_as_folders_and_carries_no_change_mark()
    {
        var roots = Build(["src/tools/foo.py", "README.md"]);

        var readme = Assert.Single(roots, n => n.Name == "README.md");
        Assert.Equal(ProjectNodeKind.File, readme.Kind);
        Assert.Equal(ChangeKind.Unmodified, readme.Status);

        var src = Assert.Single(roots, n => n.Name == "src");
        Assert.Equal(ProjectNodeKind.Folder, src.Kind);

        var tools = Assert.Single(src.Children);
        Assert.Equal("tools", tools.Name);

        var py = Assert.Single(tools.Children);
        Assert.Equal("foo.py", py.Name);
        Assert.Equal("src/tools/foo.py", py.Path);
        Assert.Equal(ChangeKind.Unmodified, py.Status);
        Assert.Equal(ChangeKind.Unmodified, src.Status);
    }

    [Fact]
    public void A_never_exported_project_still_appears()
    {
        var roots = Build([ProjectPath, "README.md"], Project(manifest: null));

        var project = Assert.Single(Flatten(roots), n => n.Kind == ProjectNodeKind.Project);
        Assert.DoesNotContain(project.Children, c => c.Kind == ProjectNodeKind.Object);
        Assert.Equal("README.md", Assert.Single(roots, n => n.Kind == ProjectNodeKind.File).Name);
    }

    [Fact]
    public void Search_keeps_a_matching_object_and_drops_an_unrelated_file()
    {
        var roots = Build(
            [
                ProjectPath,
                ExportPath + "/Device/App/Simulation.st",
                "src/tools/foo.py",
            ],
            Project(new ProjectManifest
            {
                Project = "Foo.project",
                Objects = [Obj("Simulation", "Device/App/Simulation.st", "g1")],
            }),
            search: "Simulation");

        Assert.Contains(Flatten(roots), n => n.Kind == ProjectNodeKind.Object && n.Name == "Simulation");
        Assert.DoesNotContain(Flatten(roots), n => n.Name == "foo.py");
    }

    [Fact]
    public void Every_node_is_unmodified()
    {
        var roots = Build(
            [ProjectPath, "src/a.py"],
            Project(new ProjectManifest
            {
                Project = "Foo.project",
                Objects = [Obj("Simulation", "Device/App/Simulation.st", "g1")],
            }));

        Assert.All(Flatten(roots), n => Assert.Equal(ChangeKind.Unmodified, n.Status));
    }

    // --- the toolchain pill ------------------------------------------------------------

    private static IReadOnlyList<ProjectTreeNode> BuildLabelled(
        IReadOnlyList<string> files,
        params WorkspaceProject[] projects) =>
        RepoExplorerBuilder.Build(
            files,
            projects,
            new ProjectTreeOptions { ChangedOnly = false, HideUnchangedProjects = false },
            path => path.EndsWith(".dirproj", StringComparison.OrdinalIgnoreCase)
                ? DirectoryFormat.Instance
                : path.EndsWith(".fileproj", StringComparison.OrdinalIgnoreCase)
                    ? FileFormat.Instance
                    : null);

    private static ProjectTreeNode At(IReadOnlyList<ProjectTreeNode> roots, string path) =>
        Assert.Single(Flatten(roots), n => n.Path == path);

    [Fact]
    public void A_directory_project_labels_the_folder_it_is_not_its_pointer_or_parent()
    {
        var roots = BuildLabelled(
        [
            "plc/Station/Station.dirproj",
            "plc/Station/System/PEData.plf",
            "plc/readme.md",
        ]);

        Assert.Equal("Dir V21", At(roots, "plc/Station").Toolchain);
        Assert.Equal(string.Empty, At(roots, "plc/Station/Station.dirproj").Toolchain);
        Assert.Equal(string.Empty, At(roots, "plc").Toolchain);
        Assert.Equal(string.Empty, At(roots, "plc/readme.md").Toolchain);
    }

    [Fact]
    public void A_one_file_project_labels_its_own_row_and_leaves_the_folder_alone()
    {
        // The folder holding a single-file project is often just where projects are kept;
        // a pill on it would say the whole folder is one vendor's project.
        var roots = BuildLabelled(["plc/Machine.fileproj", "plc/notes.txt"]);

        Assert.Equal("File tool", At(roots, "plc/Machine.fileproj").Toolchain);
        Assert.Equal(string.Empty, At(roots, "plc").Toolchain);
    }

    [Fact]
    public void A_tracked_project_grafted_into_the_tree_keeps_its_pill()
    {
        var tracked = new WorkspaceProject
        {
            ProjectPath = "plc/Machine.fileproj",
            ExportPath = "plc/Machine",
            ExportState = "in sync",
        };

        var roots = BuildLabelled(["plc/Machine.fileproj", "plc/Machine/A.st"], tracked);

        var project = Assert.Single(Flatten(roots), n => n.Kind == ProjectNodeKind.Project);
        Assert.Equal("File tool", project.Toolchain);
    }

    [Fact]
    public void A_tracked_directory_project_says_its_state_on_the_folder_that_wears_the_pill()
    {
        // Seen in the running window: the CODESYS rows said "in sync" and the TIA and TwinCAT
        // folders beside them said nothing, because the state was on the collapsed pointer row.
        var tracked = new WorkspaceProject
        {
            ProjectPath = "plc/Station/Station.dirproj",
            ExportPath = "plc/Station/Station",
            ExportState = "in sync",
        };

        var roots = BuildLabelled(
            ["plc/Station/Station.dirproj", "plc/Station/System/PEData.plf", "plc/Other/x.txt"],
            tracked);

        Assert.Equal("in sync", At(roots, "plc/Station").Detail);
        Assert.Equal("in sync", At(roots, "plc/Station/Station.dirproj").Detail);
        Assert.Equal(string.Empty, At(roots, "plc").Detail);
        Assert.Equal(string.Empty, At(roots, "plc/Other").Detail);
    }

    [Fact]
    public void An_untracked_directory_project_has_a_pill_and_no_state()
    {
        var roots = BuildLabelled(["plc/Station/Station.dirproj"]);

        Assert.Equal("Dir V21", At(roots, "plc/Station").Toolchain);
        Assert.Equal(string.Empty, At(roots, "plc/Station").Detail);
    }

    [Fact]
    public void A_template_beside_a_project_adds_no_pill_of_its_own()
    {
        var roots = BuildLabelled(["plc/Machine_template.fileproj"]);

        Assert.All(Flatten(roots), n => Assert.Equal(string.Empty, n.Toolchain));
    }

    [Fact]
    public void Two_projects_in_one_folder_show_both_labels_once_each()
    {
        var roots = BuildLabelled(
        [
            "plc/Station/Station.dirproj",
            "plc/Station/Old.dirproj",
            "plc/Station/Other.dirproj",
        ]);

        Assert.Equal("Dir V21", At(roots, "plc/Station").Toolchain);

        var mixed = BuildLabelled(["plc/Station/A.dirproj", "plc/Station/B_v20.dirproj"]);
        Assert.Equal("Dir V20 · Dir V21", At(mixed, "plc/Station").Toolchain);
    }

    private abstract class StandInFormat : IPlcProjectFormat
    {
        public abstract PlcVendor Vendor { get; }
        public abstract string DisplayName { get; }
        public PlcCapabilities Capabilities => new();
        public abstract IReadOnlyList<string> ProjectExtensions { get; }
        public bool IsTemplate(string path) =>
            path.Contains("_template", StringComparison.OrdinalIgnoreCase);
        public string ConventionalExportPath(string projectPath) => projectPath + ".export";
        public string? OccupancyMarkerFor(string projectPath) => null;
        public IReadOnlyList<string> TransientPatternsFor(string projectPath) => [];
        public ProjectOccupancy ParseOccupancy(string markerContents) => ProjectOccupancy.Free;
        public bool NeedsTextToPlan(string relativePath) => false;
        public ImportPlan PlanImport(
            ProjectManifest? manifest,
            IReadOnlyList<MirrorFile> mirror,
            IReadOnlyDictionary<string, string> departed) =>
            new() { Files = [] };
        public ObjectTypeName? DisplayNameFor(string type) => null;
    }

    /// <summary>A project that is the folder around its pointer, versioned by name.</summary>
    private sealed class DirectoryFormat : StandInFormat, IPlcProjectFormat
    {
        public static readonly DirectoryFormat Instance = new();

        public override PlcVendor Vendor => new("DirStandIn");
        public override string DisplayName => "Dir";
        public override IReadOnlyList<string> ProjectExtensions => [".dirproj"];

        public string PayloadPathFor(string projectPath) =>
            System.IO.Path.GetDirectoryName(projectPath) ?? projectPath;

        public string ProjectLabelFor(string projectPath) =>
            projectPath.Contains("_v20", StringComparison.OrdinalIgnoreCase) ? "Dir V20" : "Dir V21";
    }

    /// <summary>A one-file project that keeps every default — the label is its name.</summary>
    private sealed class FileFormat : StandInFormat
    {
        public static readonly FileFormat Instance = new();

        public override PlcVendor Vendor => new("FileStandIn");
        public override string DisplayName => "File tool";
        public override IReadOnlyList<string> ProjectExtensions => [".fileproj"];
    }
}
