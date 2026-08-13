using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// The one tree the left panel shows: tracked projects with their objects underneath, and
/// every other changed file beside them.
/// </summary>
/// <remarks>
/// The rules that get the attention here are the ones about <i>coverage</i>. This builder
/// decides which changed files are represented as objects and which are shown as themselves,
/// and the failure mode worth guarding against is a file git reports as changed that appears
/// nowhere at all — indistinguishable, from the user's chair, from Versio having lost it.
/// </remarks>
public sealed class WorkspaceTreeBuilderTests
{
    private const string ProjectPath = "plc/Machine.project";
    private const string ExportPath = "plc/Machine";

    private static ProjectObject Obj(string name, string path, string guid, string type = "POU") =>
        new() { Name = name, Path = path, Guid = guid, Type = type, Form = "ST_DECL" };

    private static ProjectManifest Manifest(params ProjectObject[] objects) =>
        new() { Project = "Machine.project", Objects = objects };

    private static FileChange Change(string path, ChangeKind kind = ChangeKind.Modified) =>
        new() { Path = path, Staged = ChangeKind.Unmodified, Unstaged = kind };

    private static WorkspaceProject Project(
        ProjectManifest? manifest = null, string exportState = "in sync",
        bool needsAttention = false, bool busy = false) =>
        new()
        {
            ProjectPath = ProjectPath,
            ExportPath = ExportPath,
            Manifest = manifest,
            ExportState = exportState,
            NeedsAttention = needsAttention,
            Busy = busy,
        };

    /// <summary>Builds with unchanged projects kept, for the tests about tree shape.</summary>
    /// <remarks>
    /// Explicit rather than relying on the default, because the default is now to hide them: a
    /// test asserting the shape of a project row would otherwise assert against an empty list
    /// and pass for the wrong reason.
    /// </remarks>
    private static IReadOnlyList<ProjectTreeNode> Build(
        WorkspaceProject project, IReadOnlyList<FileChange> changes, string search = "") =>
        WorkspaceTreeBuilder.Build([project], changes,
            new ProjectTreeOptions
            {
                ChangedOnly = false,
                Search = search,
                HideUnchangedProjects = false,
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
    public void The_project_file_is_a_row_of_its_own_carrying_its_export_state()
    {
        var roots = Build(
            Project(Manifest(Obj("Main", "App/Main.st", "g1")), exportState: "exported 11 files"),
            []);

        var project = Assert.Single(roots);
        Assert.Equal(ProjectNodeKind.Project, project.Kind);
        Assert.Equal("Machine.project", project.Name);
        Assert.Equal(ProjectPath, project.Path);

        // A .project is a binary compound document; there is no diff to show, so the row
        // reports the export instead. That sentence is the whole content of the row.
        Assert.Equal("exported 11 files", project.Detail);
    }

    [Fact]
    public void A_changed_project_file_says_so_on_its_own_row()
    {
        // The one row in the panel that never opens a diff is also the row most likely to
        // have changed. Silence here would be the single place a change was invisible.
        var roots = Build(Project(Manifest(Obj("Main", "App/Main.st", "g1"))),
                          [Change(ProjectPath)]);

        Assert.Equal(ChangeKind.Modified, Assert.Single(roots).Status);
    }

    [Fact]
    public void The_objects_hang_under_the_project_rather_than_beside_it()
    {
        var roots = Build(
            Project(Manifest(Obj("Main", "Device/App/Main.st", "g1"))),
            [Change("plc/Machine/Device/App/Main.st")]);

        var project = Assert.Single(roots);
        var main = Assert.Single(Flatten(project), n => n.Kind == ProjectNodeKind.Object);

        Assert.Equal("Main", main.Name);
        Assert.Equal(ChangeKind.Modified, main.Status);

        // And nothing is duplicated as a loose file row: the whole point of one tree is that
        // an edit has one place to be.
        Assert.DoesNotContain(roots, n => n.Kind == ProjectNodeKind.File);
    }

    [Fact]
    public void The_tree_is_full_by_default_not_filtered_to_what_changed()
    {
        // As a secondary view the tree answered "what did I change". As the only view it has
        // to answer "where in the machine am I", which needs the quiet objects present.
        var roots = WorkspaceTreeBuilder.Build(
            [Project(Manifest(
                Obj("Touched", "App/Touched.st", "g1"),
                Obj("Quiet", "App/Quiet.st", "g2")))],
            [Change("plc/Machine/App/Touched.st")]);

        var names = Flatten(roots).Select(n => n.Name).ToList();
        Assert.Contains("Touched", names);
        Assert.Contains("Quiet", names);
    }

    [Fact]
    public void A_changed_file_the_manifest_does_not_describe_still_appears_under_its_project()
    {
        // _manifest.json is the everyday case: it changes on every export and describes the
        // other files rather than appearing among them.
        var roots = Build(
            Project(Manifest(Obj("Main", "App/Main.st", "g1"))),
            [Change("plc/Machine/_manifest.json")]);

        var project = Assert.Single(roots);
        var stray = Assert.Single(project.Children, c => c.Kind == ProjectNodeKind.File);

        Assert.Equal("_manifest.json", stray.Name);
        Assert.Equal("plc/Machine/_manifest.json", stray.Path);
    }

    [Fact]
    public void A_changed_file_outside_every_export_tree_stays_a_row_of_its_own()
    {
        var roots = Build(
            Project(Manifest(Obj("Main", "App/Main.st", "g1"))),
            [Change("docs/README.md"), Change("plc/Machine/App/Main.st")]);

        var loose = Assert.Single(roots, n => n.Kind == ProjectNodeKind.File);

        // Named by its full path: a flat row has no folder above it to give it context.
        Assert.Equal("docs/README.md", loose.Name);
        Assert.Equal("docs/README.md", loose.Path);
    }

    [Fact]
    public void Every_changed_path_is_represented_exactly_once()
    {
        // The property that matters, stated directly: no change is dropped, and none appears
        // twice. Both failures read as a broken panel rather than as a wrong number.
        string[] paths =
        [
            ProjectPath,
            "plc/Machine/App/Main.st",
            "plc/Machine/_manifest.json",
            "docs/README.md",
        ];

        var roots = Build(
            Project(Manifest(Obj("Main", "App/Main.st", "g1"))),
            [.. paths.Select(p => Change(p))]);

        var represented = Flatten(roots)
            .SelectMany(n => n.Files)
            .ToList();

        Assert.Equal(
            paths.OrderBy(p => p, StringComparer.Ordinal),
            represented.Where(paths.Contains).OrderBy(p => p, StringComparer.Ordinal));

        Assert.Equal(represented.Count, represented.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_project_rolls_up_everything_beneath_it_including_the_strays()
    {
        var roots = Build(
            Project(Manifest(
                Obj("Main", "App/Main.st", "g1"),
                Obj("Gone", "App/Gone.st", "g2"))),
            [
                Change("plc/Machine/App/Main.st"),
                Change("plc/Machine/App/Gone.st", ChangeKind.Deleted),
                Change("plc/Machine/_manifest.json"),
            ]);

        var project = Assert.Single(roots);

        Assert.Equal(3, project.ChangedDescendants);

        // A deletion outranks an edit, and the project row is where somebody looks first.
        Assert.Equal(ChangeKind.Deleted, project.RollUp);
    }

    [Fact]
    public void A_project_that_has_never_been_exported_is_still_a_row()
    {
        // Tracked with no manifest is an ordinary state — it is what every project looks like
        // between adoption and its first export. An empty panel would read as a broken scan.
        var roots = Build(Project(manifest: null, exportState: "never exported"), []);

        var project = Assert.Single(roots);
        Assert.Equal("never exported", project.Detail);
        Assert.Empty(project.Children);
    }

    [Fact]
    public void Projects_come_before_the_loose_files()
    {
        var roots = WorkspaceTreeBuilder.Build(
            [
                Project(Manifest(Obj("Main", "App/Main.st", "g1")), needsAttention: true),
                new WorkspaceProject
                {
                    ProjectPath = "plc/Another.project",
                    ExportPath = "plc/Another",
                    NeedsAttention = true,
                },
            ],
            [Change("a-sorts-first.txt")]);

        Assert.Equal(
            [ProjectNodeKind.Project, ProjectNodeKind.Project, ProjectNodeKind.File],
            roots.Select(n => n.Kind));

        // And the projects are ordered by path, not by the order the config listed them.
        Assert.Equal("Another.project", roots[0].Name);
    }

    // --- which projects are listed at all ---------------------------------------

    [Fact]
    public void A_project_with_nothing_changed_is_not_listed()
    {
        // The panel answers "what changed". A project whose mirror is clean has nothing to
        // review, and listing it anyway turns the view into a directory listing that happens
        // to mark changes.
        var roots = WorkspaceTreeBuilder.Build(
            [Project(Manifest(Obj("Quiet", "App/Quiet.st", "g1")))], []);

        Assert.Empty(roots);
    }

    [Fact]
    public void A_project_with_a_change_anywhere_under_it_is_listed()
    {
        var roots = WorkspaceTreeBuilder.Build(
            [Project(Manifest(
                Obj("Touched", "App/Touched.st", "g1"),
                Obj("Quiet", "App/Quiet.st", "g2")))],
            [Change("plc/Machine/App/Touched.st")]);

        var project = Assert.Single(roots);

        // And it still carries the whole tree, quiet objects included: once a project is on
        // screen the question becomes "where in the machine", which needs its neighbours.
        Assert.Contains(Flatten(project), n => n.Name == "Quiet");
    }

    [Fact]
    public void A_changed_project_file_alone_is_enough_to_list_it()
    {
        // The .project moved but the mirror has not caught up - exactly the moment somebody
        // wants to see it.
        var roots = WorkspaceTreeBuilder.Build(
            [Project(Manifest(Obj("Quiet", "App/Quiet.st", "g1")))],
            [Change(ProjectPath)]);

        Assert.Single(roots);
    }

    [Fact]
    public void A_project_needing_attention_is_listed_however_quiet_the_working_tree_is()
    {
        // Never exported, mirror behind, lock held, failed. Git reports nothing about any of
        // those - there is no changed file to report - so hiding on git's say-so would hide
        // precisely the projects that need a person.
        var roots = WorkspaceTreeBuilder.Build(
            [Project(manifest: null, exportState: "never exported", needsAttention: true)], []);

        Assert.Equal("never exported", Assert.Single(roots).Detail);
    }

    [Fact]
    public void A_search_reaches_into_a_project_that_has_no_changes()
    {
        // The box searches the project, not the diff. Quietly restricting it to changed
        // objects would be a search that lies about what the project contains.
        var roots = WorkspaceTreeBuilder.Build(
            [Project(Manifest(
                Obj("Pump", "App/Pump.st", "g1"),
                Obj("Valve", "App/Valve.st", "g2")))],
            [],
            new ProjectTreeOptions { ChangedOnly = false, Search = "pump" });

        var project = Assert.Single(roots);
        var found = Assert.Single(Flatten(project), n => n.Kind == ProjectNodeKind.Object);
        Assert.Equal("Pump", found.Name);
    }

    [Fact]
    public void A_search_matching_nothing_in_a_clean_project_still_hides_it()
    {
        var roots = WorkspaceTreeBuilder.Build(
            [Project(Manifest(Obj("Pump", "App/Pump.st", "g1")))],
            [],
            new ProjectTreeOptions { ChangedOnly = false, Search = "matches-nothing" });

        Assert.Empty(roots);
    }

    [Fact]
    public void Search_filters_the_loose_files_as_well_as_the_objects()
    {
        // Two lists filtered by one box, where only one of them narrowed, is worse than no
        // filter at all: the box would look broken on whichever half ignored it.
        var roots = Build(
            Project(Manifest(
                Obj("Pump", "App/Pump.st", "g1"),
                Obj("Valve", "App/Valve.st", "g2"))),
            [Change("docs/pump-notes.md"), Change("docs/README.md")],
            search: "pump");

        Assert.Equal(["docs/pump-notes.md"],
                     roots.Where(n => n.Kind == ProjectNodeKind.File).Select(n => n.Name));

        var objects = Flatten(roots).Where(n => n.Kind == ProjectNodeKind.Object).ToList();
        Assert.Equal("Pump", Assert.Single(objects).Name);
    }

    [Fact]
    public void An_object_is_never_mistaken_for_a_folder_when_the_manifest_omits_its_guid()
    {
        // "Folder" used to mean "has no GUID". A manifest record without one therefore drew
        // as an empty folder, which is a plausible-looking way to lose an object entirely.
        var roots = Build(
            Project(Manifest(new ProjectObject
            {
                Name = "Orphan",
                Path = "App/Orphan.st",
                Type = "POU",
            })),
            []);

        var orphan = Assert.Single(Flatten(roots), n => n.Name == "Orphan");
        Assert.Equal(ProjectNodeKind.Object, orphan.Kind);
        Assert.False(orphan.IsFolder);
    }

    [Fact]
    public void A_project_beside_its_own_export_directory_does_not_swallow_itself()
    {
        // plc/Machine.project sits next to plc/Machine, and a careless prefix test matches
        // one against the other. Getting this wrong is the same class of bug as the fake
        // filesystem whose EnumerateFiles was a bare prefix match.
        var roots = Build(Project(Manifest(Obj("Main", "App/Main.st", "g1"))),
                          [Change(ProjectPath)]);

        var project = Assert.Single(roots);
        Assert.Equal(ProjectPath, project.Path);
        Assert.DoesNotContain(project.Children, c => c.Path == ProjectPath);
    }

    // --- a run in flight ------------------------------------------------------

    [Fact]
    public void A_project_with_a_run_in_flight_says_so_on_its_node()
    {
        // The row is rebuilt from this snapshot on every refresh, so "something is happening
        // to this project" has to travel in the snapshot. Held beside it in a view model, it
        // would be lost the moment the tree was rebuilt - which is exactly when a long export
        // causes a rebuild.
        var node = Build(Project(exportState: "exporting…", busy: true), []).Single();

        Assert.True(node.Busy);
        Assert.Equal("exporting…", node.Detail);
    }

    [Fact]
    public void A_project_with_nothing_running_is_not_busy()
    {
        Assert.False(Build(Project(), []).Single().Busy);
    }

    [Fact]
    public void Busy_is_a_flag_rather_than_a_reading_of_the_state_text()
    {
        // Deliberately contradictory: the wording is for a person and is free to change, so
        // nothing may infer the flag from it. A test that matched on "exporting" would pass
        // here and break the first time somebody rephrased the sentence.
        var node = Build(Project(exportState: "in sync", busy: true), []).Single();

        Assert.True(node.Busy);
    }
}
