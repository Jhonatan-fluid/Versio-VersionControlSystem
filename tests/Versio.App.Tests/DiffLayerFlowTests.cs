using System.IO;
using Versio.App.ViewModels;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The <c>ST | native</c> strip in the diff pane's header, driven from where the user
/// drives it: a real repository, the shell's own tree, and the chips' own commands.
/// </summary>
/// <remarks>
/// The tree folds an object's layers into one row on purpose — a GVL is one row over
/// <c>.gvl.st</c> and <c>.gvl.xml</c> — and until this strip existed the second layer was
/// reachable by nothing. The rule under test is ownership: the strip follows the SHOWN
/// path, not the tree selection, so it survives switching between its own layers and
/// clears for any other diff, without every caller into the pane having to know it exists.
/// </remarks>
public sealed class DiffLayerFlowTests
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
          "objectCount": 3,
          "objects": [
            { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_FULL",
              "path": "App/FB_Motor.st", "hash": "sha256:0", "bytes": 1 },
            { "guid": "g2", "name": "Glob_Var", "type": "GVL", "form": "GVL_BOTH",
              "path": "App/Glob_Var.gvl.st", "hash": "sha256:0", "bytes": 1 },
            { "guid": "g2", "name": "Glob_Var", "type": "GVL", "form": "GVL_NATIVE",
              "path": "App/Glob_Var.gvl.xml", "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [],
          "skipped": []
        }
        """);

        File.WriteAllText(Path.Combine(export, "FB_Motor.st"), "FUNCTION_BLOCK FB_Motor\n");
        File.WriteAllText(Path.Combine(export, "Glob_Var.gvl.st"), "VAR_GLOBAL\n");
        File.WriteAllText(Path.Combine(export, "Glob_Var.gvl.xml"), "<gvl />\n");
    }

    private static TreeRow FindRow(IEnumerable<TreeRow> rows, string name)
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

        throw new InvalidOperationException($"no row named '{name}' in the tree");
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
    public async Task A_two_layer_object_offers_the_strip_with_the_shown_layer_active()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = FindRow(shell.Sync.Tree, "Glob_Var");

        Assert.True(shell.HasDiffLayers);
        Assert.Equal(["ST", "native"], shell.DiffLayers.Select(l => l.Label));

        // The readable layer opens by default, and the strip says so.
        Assert.True(shell.DiffLayers.Single(l => l.Label == "ST").IsActive);
        Assert.EndsWith(".gvl.st", shell.SelectedDiffPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clicking_the_native_chip_shows_the_native_layer()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = FindRow(shell.Sync.Tree, "Glob_Var");
        shell.DiffLayers.Single(l => l.Label == "native").ShowCommand.Execute(null);

        Assert.EndsWith(".gvl.xml", shell.SelectedDiffPath, StringComparison.Ordinal);

        // The strip survived its own switch, with the mark moved — this is the "follows
        // the shown path" half of the ownership rule.
        Assert.Equal(["ST", "native"], shell.DiffLayers.Select(l => l.Label));
        Assert.True(shell.DiffLayers.Single(l => l.Label == "native").IsActive);
        Assert.False(shell.DiffLayers.Single(l => l.Label == "ST").IsActive);
    }

    [Fact]
    public async Task A_single_layer_object_offers_no_strip()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = FindRow(shell.Sync.Tree, "FB_Motor");

        Assert.False(shell.HasDiffLayers);
        Assert.Empty(shell.DiffLayers);
    }

    [Fact]
    public async Task Showing_an_unrelated_diff_clears_the_strip()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = FindRow(shell.Sync.Tree, "Glob_Var");
        Assert.True(shell.HasDiffLayers);

        // Another object's diff, reached through the same tree: not one of the strip's
        // layers, so the strip must go rather than offering Glob_Var's files under
        // FB_Motor's diff.
        shell.Sync.SelectedNode = FindRow(shell.Sync.Tree, "FB_Motor");

        Assert.False(shell.HasDiffLayers);
    }

    [Fact]
    public async Task A_diff_opened_from_the_changed_files_list_clears_the_strip()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = FindRow(shell.Sync.Tree, "Glob_Var");
        Assert.True(shell.HasDiffLayers);

        // The other route into the pane — no tree row, so nothing re-offers layers, and
        // only ShowPathAsync's own reconcile can take the stale strip down. This is the
        // half of the ownership rule the tree-driven tests cannot reach.
        shell.SelectedUnstaged = shell.Unstaged.Single(r => r.Path == "plc/Machine.project");

        Assert.False(shell.HasDiffLayers);
    }

    [Fact]
    public async Task Selecting_the_project_row_clears_the_strip()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = FindRow(shell.Sync.Tree, "Glob_Var");
        Assert.True(shell.HasDiffLayers);

        shell.Sync.SelectedNode = FindRow(shell.Sync.Tree, "Machine.project");

        // The project row shows a report, not a diff; chips offering to switch a report's
        // "layers" would be furniture from the previous selection.
        Assert.False(shell.HasDiffLayers);
    }
}
