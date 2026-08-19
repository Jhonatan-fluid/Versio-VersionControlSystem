using Versio.Core.Git;
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
}
