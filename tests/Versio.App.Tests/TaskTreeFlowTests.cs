using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Task rows, entered from where the user enters them — the same seam
/// <see cref="LibraryTreeFlowTests"/> guards, for the second catalog type.
/// </summary>
/// <remarks>
/// Not redundant with the library flow: the shell reads task catalogs through a different
/// form match (<c>TASK_BOTH</c>), the baseline prefetch matches a different suffix, and the
/// tree joins through a two-file leaf. Deleting any of those three leaves the library tests
/// green — each has failed here first, by mutation, before this file was committed.
/// </remarks>
public sealed class TaskTreeFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    private const string Catalog = """
        schema: 1
        vendor: codesys
        configuration: "Task Configuration"
        path: "App/Task Configuration"
        taskCount: 2
        tasks:
          - name: MainTask
            priority: "1"
            interval: "t#20ms"
            intervalUnit: ms
            coreBinding: -2
            pous:
              - PLC_PRG
          - name: VISU_TASK
            priority: "31"
            interval: "100"
            intervalUnit: ms
        """;

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

        File.WriteAllText(Path.Combine(export, "FB_Motor.st"), "FUNCTION_BLOCK FB_Motor\n");
        File.WriteAllText(
            Path.Combine(export, "Task Configuration.tasks.yaml"), Catalog + "\n");
        File.WriteAllText(Path.Combine(export, "Task Configuration.xml"), "<tasks />\n");
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
    public async Task The_tasks_reach_the_panel_as_rows_under_their_configuration()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        var configuration = FindRow(shell.Sync.Tree, "Task Configuration");
        Assert.Equal(["MainTask", "VISU_TASK"],
            configuration.Children.Select(c => c.Name));
        Assert.Equal("t#20ms · prio 1", configuration.Children[0].Annotation);
    }

    [Fact]
    public async Task A_priority_edit_in_the_working_tree_marks_that_task()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);
        repo.Git("add .");
        repo.Git("commit -qm exported");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        File.WriteAllText(
            Path.Combine(repo.Path, "plc", "Machine", "App", "Task Configuration.tasks.yaml"),
            Catalog.Replace("priority: \"1\"", "priority: \"2\"") + "\n");
        await shell.RefreshAsync();

        var configuration = FindRow(shell.Sync.Tree, "Task Configuration");
        Assert.Equal(ChangeKind.Modified,
            configuration.Children.Single(c => c.Name == "MainTask").Node.Status);
        Assert.Equal(ChangeKind.Unmodified,
            configuration.Children.Single(c => c.Name == "VISU_TASK").Node.Status);
    }
}
