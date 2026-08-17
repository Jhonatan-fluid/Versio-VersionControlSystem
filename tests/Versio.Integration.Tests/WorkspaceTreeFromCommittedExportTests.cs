using Versio.Core.Bindings;
using Versio.Core.Git;
using Versio.Core.Projects;
using Versio.TestSupport;
using Xunit.Abstractions;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// The one-panel tree, built from the export this repository commits of its own fixture.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests build manifests by hand, which proves the rules and nothing about the
/// reading. This one uses the manifest CODESYS actually wrote — committed, so it needs no
/// CODESYS installation to run — and is therefore the test that would catch a renamed key, a
/// path separator arriving the other way round, or an object type nobody anticipated.
/// </para>
/// <para>
/// It is also the check that the panel and the repository agree about the same project. Versio
/// tracks its own fixture, so the tree these assertions describe is the tree somebody sees
/// when they open this repository in the app.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class WorkspaceTreeFromCommittedExportTests(ITestOutputHelper output)
{
    private const string ProjectPath = "fixtures/projects/RefrigeratorControl.project";
    private const string ExportPath = "fixtures/projects/RefrigeratorControl";

    private static string ManifestPath => Path.Combine(
        TestPaths.Root,
        ExportPath.Replace('/', Path.DirectorySeparatorChar),
        BindingConventions.ManifestFileName);

    private static WorkspaceProject Fixture(string state = "in sync") => new()
    {
        ProjectPath = ProjectPath,
        ExportPath = ExportPath,
        Manifest = ProjectManifest.Parse(File.ReadAllText(ManifestPath)),
        ExportState = state,
    };

    /// <summary>Keeps an unchanged project listed, which these tests are all about.</summary>
    /// <remarks>
    /// The panel now hides a project with a clean mirror. These assertions are about the shape
    /// the manifest produces, not about which projects get listed, so they ask for it
    /// explicitly - relying on the default would make every one of them assert against an
    /// empty list and pass for the wrong reason.
    /// </remarks>
    private static readonly ProjectTreeOptions KeepClean = new()
    {
        ChangedOnly = false,
        HideUnchangedProjects = false,
    };

    private static IEnumerable<ProjectTreeNode> Flatten(ProjectTreeNode node)
    {
        yield return node;
        foreach (var descendant in node.Children.SelectMany(Flatten))
        {
            yield return descendant;
        }
    }

    [SkippableFact]
    public void The_committed_export_builds_the_shape_CODESYS_structures_the_project_in()
    {
        Skip.If(!File.Exists(ManifestPath), "the fixture's export is not committed");

        var roots = WorkspaceTreeBuilder.Build([Fixture()], [], KeepClean);

        var project = Assert.Single(roots);
        Assert.Equal(ProjectNodeKind.Project, project.Kind);
        Assert.Equal("RefrigeratorControl.project", project.Name);

        var all = Flatten(project).ToList();
        foreach (var node in all)
        {
            output.WriteLine($"{new string(' ', 0)}{node.Kind,-8} {node.Name}  [{node.Type}]");
        }

        // The folder chain CODESYS itself shows, not paths on disk. If this ever flattens,
        // the panel has regressed to a file list with extra steps.
        Assert.Contains(all, n => n is { Kind: ProjectNodeKind.Folder, Name: "Device" });
        Assert.Contains(all, n => n is { Kind: ProjectNodeKind.Folder, Name: "Plc Logic" });
        Assert.Contains(all, n => n is { Kind: ProjectNodeKind.Folder, Name: "Application" });

        var objects = all.Where(n => n.Kind == ProjectNodeKind.Object).ToList();
        Assert.NotEmpty(objects);

        // Every object is a leaf with at least one file behind it, and none of them was
        // mistaken for a folder - the failure that used to follow from a missing GUID.
        Assert.All(objects, o =>
        {
            Assert.False(o.IsFolder);
            Assert.NotEmpty(o.Files);
        });
    }

    [SkippableFact]
    public void One_object_with_two_layers_is_still_one_row()
    {
        // The fixture's GVL exports as both .gvl.st and .gvl.xml. Two rows would invite the
        // question of which one is the real object, and this is the manifest that proves the
        // grouping works on data CODESYS produced rather than on data a test wrote.
        Skip.If(!File.Exists(ManifestPath), "the fixture's export is not committed");

        var roots = WorkspaceTreeBuilder.Build([Fixture()], [], KeepClean);

        var layered = Flatten(roots[0])
            .Where(n => n.Kind == ProjectNodeKind.Object && n.Files.Count > 1)
            .ToList();

        foreach (var node in layered)
        {
            output.WriteLine($"{node.Name}: {string.Join(", ", node.Files)}");
        }

        var gvl = Assert.Single(layered, n => n.Name == "Glob_Var");
        Assert.Contains(gvl.Files, f => f.EndsWith(".gvl.st", StringComparison.Ordinal));
        Assert.Contains(gvl.Files, f => f.EndsWith(".gvl.xml", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void A_change_to_an_exported_file_lands_on_the_object_that_owns_it()
    {
        // The whole claim of folding the two panels together: an edit to a path git reports
        // shows up as the CODESYS object it belongs to, in one place, once.
        Skip.If(!File.Exists(ManifestPath), "the fixture's export is not committed");

        const string edited = ExportPath + "/Device/Plc Logic/Application/Simulation.st";

        var roots = WorkspaceTreeBuilder.Build([Fixture()],
        [
            new FileChange
            {
                Path = edited,
                Staged = ChangeKind.Unmodified,
                Unstaged = ChangeKind.Modified,
            },
        ]);

        var project = Assert.Single(roots);
        var changed = Flatten(project)
            .Where(n => n.Kind == ProjectNodeKind.Object && n.Status != ChangeKind.Unmodified)
            .ToList();

        var simulation = Assert.Single(changed);
        Assert.Equal("Simulation", simulation.Name);
        Assert.Equal(edited, simulation.Files.Single());

        // And it rolls all the way up, so the change is never hidden behind a collapsed node.
        Assert.Equal(1, project.ChangedDescendants);
        Assert.Equal(ChangeKind.Modified, project.RollUp);

        // Nothing appears as a loose file row: the object row is the only place it is.
        Assert.DoesNotContain(roots, n => n.Kind == ProjectNodeKind.File);
    }

    [SkippableFact]
    public void The_manifest_itself_appears_under_the_project_rather_than_vanishing()
    {
        // _manifest.json changes on every export and describes the other files rather than
        // appearing among them. A file git reports as changed, in a directory Versio owns,
        // showing up nowhere at all is indistinguishable from Versio having lost it.
        Skip.If(!File.Exists(ManifestPath), "the fixture's export is not committed");

        var roots = WorkspaceTreeBuilder.Build([Fixture()],
        [
            new FileChange
            {
                Path = ExportPath + "/_manifest.json",
                Staged = ChangeKind.Unmodified,
                Unstaged = ChangeKind.Modified,
            },
        ]);

        var project = Assert.Single(roots);
        var stray = Assert.Single(project.Children, c => c.Kind == ProjectNodeKind.File);

        Assert.Equal("_manifest.json", stray.Name);
        Assert.Equal(ChangeKind.Modified, stray.Status);
    }

    [SkippableFact]
    public void The_project_row_reports_its_export_and_offers_no_diff()
    {
        // A .project is a binary compound document; there is nothing to compare. The row says
        // what its export did instead, which is the whole premise of the tool stated in the
        // one place somebody is most likely to click first.
        Skip.If(!File.Exists(ManifestPath), "the fixture's export is not committed");

        var roots = WorkspaceTreeBuilder.Build([Fixture("exported · 11 files")], [], KeepClean);

        var project = Assert.Single(roots);
        Assert.Equal("exported · 11 files", project.Detail);
        Assert.Equal(ProjectPath, Assert.Single(project.Files));
    }

    [SkippableFact]
    public void The_committed_export_carries_its_library_references_and_reports_no_gaps()
    {
        // Runs without CODESYS, because the manifest is committed. That matters: the machine
        // this is checked on is not necessarily the machine CODESYS is installed on, and a
        // coverage regression that only a CODESYS box could notice would be noticed late.
        //
        // This test used to assert the opposite — two LIBRARY_MANAGER gaps — and it was right
        // to. The gap is closed, and the assertion inverted with it rather than being deleted:
        // what it guards now is that the mirror this repository dogfoods really does carry the
        // library references, and would say so if it stopped.
        Skip.If(!File.Exists(ManifestPath), "the fixture's export is not committed");

        var manifest = ProjectManifest.Parse(File.ReadAllText(ManifestPath));
        var report = ExportCoverage.Of(manifest);

        output.WriteLine(report.Describe());
        foreach (var skipped in manifest.Skipped)
        {
            output.WriteLine($"{skipped.Reason,-18} {skipped.Type,-22} {skipped.Path}");
        }

        Assert.Equal(ExportCoverage.CurrentSchema, manifest.Schema);
        Assert.Equal(CoverageVerdict.CompleteByDesign, report.Verdict);
        Assert.Empty(report.Gaps);

        // The fixture has two library managers — one on the project and one under the
        // application — and both now reach the mirror, one file each.
        //
        // FOUR one-way files, not three: the task configuration's native XML joined them when
        // its .tasks.yaml became the importable half, and the GVL's .gvl.xml joined them on
        // 2026-08-16 when G12 showed it was overwriting the .gvl.st beside it.
        //
        // This assertion read 2 while the first of those was already true of the running
        // exporter, because the committed mirror it reads had not been regenerated — a stale
        // fixture makes a true assertion protect a dead specification, and nothing about it
        // looks stale from here. The mirror was regenerated with the G12 fix for exactly that
        // reason.
        Assert.Equal(4, report.ExportOnlyFiles);
        Assert.Equal(2, manifest.Objects.Count(o => o.Type == "LIBRARY_MANAGER"));

        // The two that are not library managers, named rather than left as arithmetic — and
        // they are the two halves of the same shape, which is why they are asserted together.
        Assert.Single(manifest.Objects,
            o => o.Form == ExportCoverage.TaskNativeForm
                 && o.Path == "Device/Plc Logic/Application/Task Configuration.xml");

        Assert.Single(manifest.Objects,
            o => o.Form == ExportCoverage.GvlNativeForm
                 && o.Path == "Device/Plc Logic/Application/Glob_Var.gvl.xml");

        // The deliberate skips are still present and still NOT counted as gaps. If that
        // distinction ever collapses, this repository's own panel starts crying wolf about a
        // visualisation manager that is skipped on purpose.
        Assert.Equal(4, report.DeliberateSkips);
        Assert.DoesNotContain(report.Gaps, g => g.Reason != ExportCoverage.GapReason);
    }

    [SkippableFact]
    public void The_committed_library_file_parses_with_the_reader_that_ships_beside_it()
    {
        // Also without CODESYS. The exporter and the reader are two hand-written halves of one
        // file format that never meet in the same process, and the committed export is the
        // only place they can be checked against each other for free.
        var path = Path.Combine(
            TestPaths.Root,
            ExportPath.Replace('/', Path.DirectorySeparatorChar),
            "Library Manager.libraries.yaml");

        Skip.If(!File.Exists(ManifestPath), "the fixture's export is not committed");
        Assert.True(File.Exists(path), "the committed export has no library file");

        var catalog = LibraryCatalog.Parse(File.ReadAllText(path));

        Assert.True(catalog.IsValid);
        Assert.Equal("codesys", catalog.Vendor);
        Assert.NotEmpty(catalog.References);
        Assert.All(catalog.References, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
    }

    [SkippableFact]
    public void The_committed_catalogs_hang_their_references_under_both_managers()
    {
        // The unit tests hand the builder catalogs a test wrote; this hands it the two
        // CODESYS actually produced — including the quoting that exists because library
        // names really contain commas and parentheses — and checks the full path from
        // committed bytes to rows. The fixture has two managers on purpose: a snapshot
        // joined to the wrong one would put the application's 21 references under the
        // project's manager, and no single-manager test can notice that.
        Skip.If(!File.Exists(ManifestPath), "the fixture's export is not committed");

        var manifest = ProjectManifest.Parse(File.ReadAllText(ManifestPath));
        var snapshots = manifest.Objects
            .Where(o => o.Form == ExportCoverage.LibrariesForm)
            .Select(o => new LibrarySnapshot
            {
                Path = $"{ExportPath}/{o.Path}",
                Current = LibraryCatalog.Parse(File.ReadAllText(Path.Combine(
                    TestPaths.Root,
                    ExportPath.Replace('/', Path.DirectorySeparatorChar),
                    o.Path.Replace('/', Path.DirectorySeparatorChar)))),
            })
            .ToList();

        Assert.Equal(2, snapshots.Count);

        var roots = WorkspaceTreeBuilder.Build(
            [Fixture() with { Libraries = snapshots }], [], KeepClean);

        var managers = Flatten(Assert.Single(roots))
            .Where(n => n.Type == "LIBRARY_MANAGER")
            .ToList();

        Assert.Equal(2, managers.Count);
        foreach (var manager in managers)
        {
            var snapshot = snapshots.Single(s =>
                string.Equals(s.Path, manager.Path, StringComparison.OrdinalIgnoreCase));

            output.WriteLine($"{manager.Path}: {manager.Children.Count} references");
            Assert.Equal(snapshot.Current.References.Count, manager.Children.Count);
            Assert.All(manager.Children, c => Assert.Equal(ProjectNodeKind.Library, c.Kind));

            // Chosen before system — the three somebody picked must not be lost among the
            // eighteen the visualisation plug-in pulled in.
            var firstSystem = manager.Children.ToList()
                .FindIndex(c => c.Detail.EndsWith("· system", StringComparison.Ordinal));
            var lastChosen = manager.Children.ToList()
                .FindLastIndex(c => !c.Detail.EndsWith("· system", StringComparison.Ordinal));
            if (firstSystem >= 0 && lastChosen >= 0)
            {
                Assert.True(lastChosen < firstSystem,
                    "a chosen library is sorted among the system ones");
            }
        }

        // And one known row, with the pin a reviewer would look for.
        var application = managers.Single(m => m.Path.Contains("/Application/"));
        var standard = application.Children.Single(c => c.Name == "Standard");
        Assert.StartsWith("Standard, 3.5.", standard.Detail);
    }

    [SkippableFact]
    public void The_committed_task_catalog_hangs_its_tasks_under_the_configuration()
    {
        // The two halves of one file format never meet in the same process — versio_tasks.py
        // writes, TaskCatalog reads — and the committed export is where they are checked
        // against each other for free. The configuration is also the tree's one two-file
        // leaf, so this is the join proven against the real pair rather than a manifest a
        // test wrote.
        Skip.If(!File.Exists(ManifestPath), "the fixture's export is not committed");

        var manifest = ProjectManifest.Parse(File.ReadAllText(ManifestPath));
        var record = manifest.Objects.Single(o => o.Form == ExportCoverage.TaskBothForm);

        var catalog = TaskCatalog.Parse(File.ReadAllText(Path.Combine(
            TestPaths.Root,
            ExportPath.Replace('/', Path.DirectorySeparatorChar),
            record.Path.Replace('/', Path.DirectorySeparatorChar))));

        Assert.True(catalog.IsValid);
        Assert.Equal("codesys", catalog.Vendor);

        var roots = WorkspaceTreeBuilder.Build(
            [Fixture() with
            {
                Tasks =
                [
                    new TaskSnapshot
                    {
                        Path = $"{ExportPath}/{record.Path}",
                        Current = catalog,
                    },
                ],
            }],
            [], KeepClean);

        var configuration = Flatten(Assert.Single(roots))
            .First(n => n.Name == "Task Configuration");

        foreach (var task in configuration.Children)
        {
            output.WriteLine($"{task.Name}  [{task.Detail}]");
        }

        // Two files, one node, and the catalog's tasks beneath it — the fixture's two
        // real tasks with their real verbatim schedules.
        Assert.Equal(2, configuration.Files.Count);
        Assert.Equal(catalog.Tasks.Count, configuration.Children.Count);
        Assert.Contains(configuration.Children,
            t => t.Name == "MainTask" && t.Detail == "t#20ms · prio 1");
        Assert.Contains(configuration.Children,
            t => t.Name == "VISU_TASK" && t.Detail == "100 ms · prio 31");
    }

    [SkippableFact]
    public void The_coverage_report_is_committed_beside_the_manifest()
    {
        // It is a markdown file in the repository on purpose: a coverage regression then
        // arrives as a reviewable diff in a pull request rather than as a number in a panel
        // nobody had open.
        var path = Path.Combine(
            TestPaths.Root,
            ExportPath.Replace('/', Path.DirectorySeparatorChar),
            "_coverage.md");

        Skip.If(!File.Exists(ManifestPath), "the fixture's export is not committed");

        Assert.True(File.Exists(path), "the committed export has no coverage report");

        var text = File.ReadAllText(path);
        Assert.Contains("LIBRARY_MANAGER", text, StringComparison.Ordinal);

        // In the one-way section now, not the gap section. The committed report is the
        // evidence a reviewer sees, so which section a type appears under is the assertion
        // worth making — "the word appears somewhere" would survive the gap coming back.
        Assert.Contains("Exported for review, not applied back", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Not exported, and that is a gap", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
    }
}
