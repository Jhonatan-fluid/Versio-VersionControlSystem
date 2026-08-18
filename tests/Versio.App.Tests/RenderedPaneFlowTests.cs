using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The pane's Rendered half: tables for catalogs, a card for the project, content for clean
/// files — and the default tab following the selection.
/// </summary>
/// <remarks>
/// Driven through <c>SelectedNode</c>, the same door a click enters by. The templates are
/// XAML; what these pin is the dispatch — which selection produces which content type, and
/// that a clean file finally renders as itself instead of a shrug.
/// </remarks>
public sealed class RenderedPaneFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    private static void GivenATrackedProject(TempRepo repo)
    {
        var versio = Path.Combine(repo.Path, ".versio");
        Directory.CreateDirectory(versio);

        File.WriteAllText(Path.Combine(versio, "repo.json"), """
        {
          "schema": 1,
          "bindings": [
            {
              "id": "b1",
              "projectPath": "plc/Machine.project",
              "exportPath": "plc/Machine",
              "enabled": true
            }
          ]
        }
        """);

        var export = Path.Combine(repo.Path, "plc", "Machine", "App");
        Directory.CreateDirectory(export);

        File.WriteAllBytes(
            Path.Combine(repo.Path, "plc", "Machine.project"), [0x00, 0x01, 0x02, 0x03]);

        File.WriteAllText(Path.Combine(repo.Path, "plc", "Machine", "_manifest.json"), """
        {
          "schema": 4,
          "bindingId": "b1",
          "project": "Machine.project",
          "objectCount": 4,
          "objects": [
            { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_FULL",
              "path": "App/FB_Motor.st", "hash": "sha256:0", "bytes": 1 },
            { "guid": "g3", "name": "Library Manager", "type": "LIBRARY_MANAGER",
              "form": "LIBRARIES", "path": "App/Library Manager.libraries.yaml",
              "hash": "sha256:0", "bytes": 1 },
            { "guid": "g4", "name": "Task Configuration", "type": "TASK_CONFIGURATION",
              "form": "TASK_BOTH", "path": "App/Task Configuration.tasks.yaml",
              "hash": "sha256:0", "bytes": 1 },
            { "guid": "g4", "name": "Task Configuration", "type": "TASK_CONFIGURATION",
              "form": "TASK_NATIVE", "path": "App/Task Configuration.xml",
              "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [],
          "skipped": []
        }
        """);

        File.WriteAllText(Path.Combine(export, "FB_Motor.st"),
            "FUNCTION_BLOCK FB_Motor\nVAR speed : INT; END_VAR\nEND_FUNCTION_BLOCK\n");
        File.WriteAllText(Path.Combine(export, "Library Manager.libraries.yaml"), """
            schema: 1
            vendor: codesys
            manager: "Library Manager"
            path: "App/Library Manager"
            referenceCount: 1
            references:
              - name: "#Standard"
                placeholder: Standard
                defaultResolution: "Standard, * (System)"
            """ + "\n");
        File.WriteAllText(Path.Combine(export, "Task Configuration.tasks.yaml"), """
            schema: 1
            vendor: codesys
            configuration: "Task Configuration"
            path: "App/Task Configuration"
            taskCount: 1
            tasks:
              - name: MainTask
                priority: "1"
                interval: "t#20ms"
                pous:
                  - PLC_PRG
            """ + "\n");
        File.WriteAllText(Path.Combine(export, "Task Configuration.xml"), "<tasks />\n");
    }

    /// <summary>
    /// The row by name, waiting for it the way a person looking at the panel would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It waited for nothing until 2026-08-17, and that is the only difference.</b>
    /// <c>A_clean_file_shows_itself_and_says_nothing_changed</c> failed twice in a row in the
    /// full suite while passing alone every time, and then went green five runs running —
    /// including one deliberately loaded with two other suites — so the message was never
    /// captured and the failing assertion is genuinely not known. What is known: both red runs
    /// were the slow ones (62 s and 66 s against 54–60 s green), which is the signature of a
    /// race rather than a wrong expectation.
    /// </para>
    /// <para>
    /// This test is the only one in the class that <i>commits</i> before opening — it has to,
    /// because a clean file is what it is about — so it is the only one whose tree is rebuilt
    /// against a changed git status while the test is reading it. Reaching into
    /// <c>shell.Sync.Tree</c> the instant <c>OpenAsync</c> returns assumed that rebuild had
    /// already landed.
    /// </para>
    /// <para>
    /// So the lookup waits, bounded. It cannot hide a row that never appears — the deadline
    /// still fails the test, naming the row — and it is what the panel does anyway.
    /// </para>
    /// </remarks>
    private static async Task<TreeRow> Row(ShellViewModel shell, string name)
    {
        TreeRow? found = null;

        await Eventually.Until(
            () => (found = Find(shell.Sync.Tree, name)) is not null,
            $"a row named '{name}' appeared in the tree");

        return found!;
    }

    private static TreeRow? Find(IEnumerable<TreeRow> rows, string name)
    {
        foreach (var row in rows)
        {
            if (row.Name == name)
            {
                return row;
            }

            if (Search(row, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static TreeRow? Search(TreeRow row, string name)
    {
        foreach (var child in row.Children)
        {
            if (child.Name == name)
            {
                return child;
            }

            if (Search(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public async Task The_library_manager_renders_as_its_catalog()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = await Row(shell, "Library Manager");

        var catalog = Assert.IsType<LibraryCatalog>(shell.RenderedContent);
        Assert.Equal("Standard", Assert.Single(catalog.References).Placeholder);
    }

    [Fact]
    public async Task A_task_row_renders_as_that_tasks_facts()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = await Row(shell, "MainTask");

        var detail = Assert.IsType<TaskDetailRender>(shell.RenderedContent);
        Assert.Equal("MainTask", detail.Task.Name);
        Assert.Contains(detail.Facts, f => f is { Label: "Calls", Value: "PLC_PRG" });
    }

    [Fact]
    public async Task The_task_configuration_renders_as_its_table()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = await Row(shell, "Task Configuration");

        var catalog = Assert.IsType<TaskCatalog>(shell.RenderedContent);
        Assert.Equal("MainTask", Assert.Single(catalog.Tasks).Name);
    }

    [Fact]
    public async Task A_clean_file_shows_itself_and_says_nothing_changed()
    {
        // The correction the owner asked for by name: a clean object used to render as a
        // shrug, and the click's question was "what is in this file", not "what changed".
        using var repo = new TempRepo();
        GivenATrackedProject(repo);
        repo.Git("add .");
        repo.Git("commit -qm exported");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = await Row(shell, "FB_Motor");

        var source = Assert.IsType<SourceRender>(shell.RenderedContent);
        Assert.True(source.NoChanges);
        Assert.Contains("FUNCTION_BLOCK FB_Motor", source.PseudoDiff);

        // Clean defaults to Rendered; the raw half carries the full file too, with the
        // note in the header rather than the text.
        Assert.True(shell.ShowRendered);
        await Eventually.Until(
            () => shell.PaneNote.Contains("No changes"), "the pane note arrived");
        Assert.Contains("FUNCTION_BLOCK FB_Motor", shell.SelectedDiff);
    }

    [Fact]
    public async Task A_changed_file_defaults_to_the_raw_diff()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);
        repo.Git("add .");
        repo.Git("commit -qm exported");
        File.WriteAllText(
            Path.Combine(repo.Path, "plc", "Machine", "App", "FB_Motor.st"),
            "FUNCTION_BLOCK FB_Motor // edited\n");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = await Row(shell, "FB_Motor");

        // The rendered view exists — the user can flip to it — but the diff is what
        // review is, so it opens underneath.
        Assert.True(shell.HasRenderedView);
        Assert.False(shell.ShowRendered);
    }

    [Fact]
    public async Task The_project_row_renders_as_the_card()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = await Row(shell, "Machine.project");

        var card = Assert.IsType<ProjectSummaryRender>(shell.RenderedContent);
        Assert.True(shell.ShowRendered);
        Assert.Contains(card.Facts, f => f.Label == "Vendor");
        Assert.Contains(card.Facts, f => f.Label == "Objects" && f.Value.Contains("POU"));
        Assert.Contains(card.Facts, f => f.Label == "Libraries" && f.Value.Contains("1 reference"));
        Assert.Contains(card.Facts, f => f.Label == "Tasks" && f.Value.Contains("MainTask"));
    }
}
