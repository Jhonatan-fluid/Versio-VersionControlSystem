using System.IO;
using Versio.App.ViewModels;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// An object the last export could not write says so above its file and on its project's card,
/// not only in the log.
/// </summary>
/// <remarks>
/// The owner's refused OB, 2026-10-30: Portal would not export the block they had just edited,
/// the host kept the previous file and logged a warning, and everything on screen — the row,
/// the pane's "No changes right now", the card's "Warnings 1 — see the log" — read as a
/// project with nothing to show. The tree's rule for which warning counts is Core's; the row's
/// words are <see cref="TreeRowExportWarningTests"/>; this is the pane and the card.
/// </remarks>
public sealed class ExportRefusalPaneFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    private static void GivenATrackedProjectWithARefusedBlock(TempRepo repo)
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

        Directory.CreateDirectory(Path.Combine(repo.Path, "plc", "Machine", "App"));
        File.WriteAllBytes(
            Path.Combine(repo.Path, "plc", "Machine.project"), [0x00, 0x01, 0x02, 0x03]);
        File.WriteAllText(
            Path.Combine(repo.Path, "plc", "Machine", "App", "FB_Motor.st"),
            "FUNCTION_BLOCK FB_Motor\nEND_FUNCTION_BLOCK\n");
        File.WriteAllText(
            Path.Combine(repo.Path, "plc", "Machine", "App", "FB_Pump.st"),
            "FUNCTION_BLOCK FB_Pump\nEND_FUNCTION_BLOCK\n");

        // FB_Motor's file is the previous export: the exporter said so, in its own words.
        File.WriteAllText(Path.Combine(repo.Path, "plc", "Machine", "_manifest.json"), """
        {
          "schema": 4, "bindingId": "b1", "project": "Machine.project", "objectCount": 2,
          "objects": [
            { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_FULL",
              "path": "App/FB_Motor.st", "hash": "sha256:0", "bytes": 1 },
            { "guid": "g2", "name": "FB_Pump", "type": "POU", "form": "ST_FULL",
              "path": "App/FB_Pump.st", "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [
            { "code": "XML_EXPORT_FAILED", "path": "App/FB_Motor.st",
              "detail": "Error when calling method 'Export'.\r\n\r\nInconsistent blocks and PLC data types (UDT) cannot be exported." }
          ],
          "skipped": []
        }
        """);

        repo.Git("add .");
        repo.Git("commit -qm exported");

        // The PROJECT changed, so the Project panel lists it; the objects did not.
        File.WriteAllBytes(Path.Combine(repo.Path, "plc", "Machine.project"), [0x09, 0x09]);
    }

    [Fact]
    public async Task The_refused_object_says_so_above_its_file_and_the_next_file_does_not()
    {
        using var repo = new TempRepo();
        GivenATrackedProjectWithARefusedBlock(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = await Row(shell, "FB_Motor");
        await Eventually.Until(
            () => shell.PaneNote.Contains("Export refused", StringComparison.Ordinal),
            "the refusal reached the pane note");

        // One sentence, Portal's words with their line breaks collapsed, then the usual note.
        Assert.Contains(
            "Export refused: Error when calling method 'Export'. Inconsistent blocks and PLC data types (UDT) cannot be exported. Showing the previous export.",
            shell.PaneNote, StringComparison.Ordinal);
        Assert.Contains("No changes right now", shell.PaneNote, StringComparison.Ordinal);

        // Keyed by the file: the clean block beside it carries nothing of it.
        shell.Sync.SelectedNode = await Row(shell, "FB_Pump");
        await Eventually.Until(
            () => shell.PaneNote.Contains("No changes", StringComparison.Ordinal)
                  && !shell.PaneNote.Contains("Export refused", StringComparison.Ordinal),
            "the next file's note is its own");
    }

    [Fact]
    public async Task The_project_card_names_what_the_export_could_not_write()
    {
        using var repo = new TempRepo();
        GivenATrackedProjectWithARefusedBlock(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = await Row(shell, "Machine.project");

        var card = Assert.IsType<ProjectSummaryRender>(shell.RenderedContent);
        var warnings = Assert.Single(card.Facts, f => f.Label == "Warnings");
        Assert.Equal("1 — export refused: App/FB_Motor.st; see the log", warnings.Value);
    }

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

            if (Find(row.Children, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
