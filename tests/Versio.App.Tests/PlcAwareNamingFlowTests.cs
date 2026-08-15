using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Naming the changed files, entered from where the user enters it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every other test of this feature would pass with the wiring deleted.</b> The row knows how
/// to name itself and the index knows how to answer, and both are covered — but nothing in
/// either says that the shell ever asks. That is the failure mode this codebase has hit three
/// times: <c>GuardIntent.Clone</c>, <c>ResetForRepositoryChange</c> and <c>TreeRow.Reconcile</c>
/// each had a green suite and no caller. Deleting the two <c>DescribeRows</c> calls must turn
/// this file red, and nothing else.
/// </para>
/// <para>
/// Against a real repository with a real <c>repo.json</c> and a real manifest on disk, because
/// the thing under test is that two independently refreshed pieces — <c>git status</c> and the
/// parsed manifests — meet. A fake status would be a fixture agreeing with the code by
/// construction.
/// </para>
/// </remarks>
public sealed class PlcAwareNamingFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    /// <summary>
    /// A repository with one tracked project whose export tree is already on disk.
    /// </summary>
    /// <remarks>
    /// Written as files rather than through the adoption path: adopting a project runs a real
    /// toolchain probe, and this test is about naming rows, not about attaching.
    /// </remarks>
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

        // The .project itself, or the binding points at nothing and the scan treats it as an
        // orphan rather than as a tracked project.
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
            { "guid": "g2", "name": "Glob_Var", "type": "GVL", "form": "GVL_BOTH",
              "path": "App/Glob_Var.gvl.xml", "hash": "sha256:0", "bytes": 1 },
            { "guid": "g3", "name": "Library Manager", "type": "LIBRARY_MANAGER",
              "form": "LIBRARIES", "path": "App/Library Manager.libraries.yaml",
              "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [],
          "skipped": []
        }
        """);

        File.WriteAllText(Path.Combine(export, "FB_Motor.st"), "FUNCTION_BLOCK FB_Motor\n");
        File.WriteAllText(Path.Combine(export, "Glob_Var.gvl.st"), "VAR_GLOBAL\n");
        File.WriteAllText(Path.Combine(export, "Glob_Var.gvl.xml"), "<gvl />\n");
        File.WriteAllText(
            Path.Combine(export, "Library Manager.libraries.yaml"), "libraries: []\n");
    }

    [Fact]
    public async Task An_exported_file_reaches_the_panel_named_as_its_object()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        var row = shell.Unstaged.Single(r => r.Path == "plc/Machine/App/FB_Motor.st");

        // The whole chain: repo.json -> manifest -> ObjectIndex -> SyncViewModel.Objects ->
        // DescribeRows -> the row. Removing any link leaves this row showing "FB_Motor.st".
        Assert.True(row.IsObject);
        Assert.Equal("FB_Motor", row.Primary);
        Assert.Equal("POU", row.TypeLabel);
    }

    [Fact]
    public async Task A_file_the_manifest_does_not_describe_arrives_as_a_path()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);
        File.WriteAllText(Path.Combine(repo.Path, "notes.st"), "not a PLC object\n");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        var row = shell.Unstaged.Single(r => r.Path == "notes.st");

        Assert.False(row.IsObject);
        Assert.Equal("notes.st", row.Primary);
        Assert.Equal(string.Empty, row.TypeLabel);
    }

    [Fact]
    public async Task The_library_reference_row_says_the_edit_goes_nowhere()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        var library = shell.Unstaged.Single(
            r => r.Path == "plc/Machine/App/Library Manager.libraries.yaml");
        var pou = shell.Unstaged.Single(r => r.Path == "plc/Machine/App/FB_Motor.st");

        Assert.True(library.IsExportOnly);
        Assert.False(pou.IsExportOnly);
    }

    [Fact]
    public async Task The_summary_line_counts_objects_and_names_the_project()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        Assert.True(shell.HasChangeSummary);

        // One GVL, not two, though it is two files. And the .versio and repo files are counted
        // apart rather than folded in as objects.
        Assert.StartsWith(
            "1 GVL, 1 library manager and 1 POU changed in Machine",
            shell.ChangeSummaryLine);
    }

    [Fact]
    public async Task The_draft_fills_an_empty_box_with_object_names()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        Assert.True(shell.DraftCommitMessageCommand.CanExecute(null));
        shell.DraftCommitMessageCommand.Execute(null);

        Assert.Contains("FB_Motor", shell.CommitMessage);

        // And having filled it, it stops offering — the box now holds words somebody may edit.
        Assert.False(shell.DraftCommitMessageCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_row_that_appears_after_the_first_refresh_is_named_too()
    {
        // The naming pass runs on every refresh, not once at open. A project exported while
        // Versio is running produces rows that were not there when the index was first built.
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        repo.Git("add .");
        repo.Git("commit -qm exported");
        await shell.RefreshAsync();

        Assert.Empty(shell.Unstaged);

        File.WriteAllText(
            Path.Combine(repo.Path, "plc", "Machine", "App", "FB_Motor.st"),
            "FUNCTION_BLOCK FB_Motor // edited\n");

        await shell.RefreshAsync();

        var row = shell.Unstaged.Single(r => r.Path == "plc/Machine/App/FB_Motor.st");
        Assert.Equal("FB_Motor", row.Primary);
        Assert.Equal("1 POU changed in Machine", shell.ChangeSummaryLine);
    }
}
