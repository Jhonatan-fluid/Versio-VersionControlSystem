using System.IO;
using Versio.App.ViewModels;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Expansion state surviving the window, not only the refresh.
/// </summary>
/// <remarks>
/// The in-memory set already survives a rebuild; this is the other half of the plan's
/// "persisted per repo": <c>.versio/local/tree.json</c>, written when the repository
/// detaches — the one funnel that switching, closing and disposing all pass through — and
/// read before the first tree build, because rows take their expansion at construction.
/// </remarks>
public sealed class TreeExpansionPersistenceTests
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
          "objectCount": 1,
          "objects": [
            { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_FULL",
              "path": "App/FB_Motor.st", "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [],
          "skipped": []
        }
        """);

        File.WriteAllText(Path.Combine(export, "FB_Motor.st"), "FUNCTION_BLOCK FB_Motor\n");
    }

    private static TreeRow ProjectRow(ShellViewModel shell) =>
        shell.Sync.Tree.First(r => r.IsProject);

    [Fact]
    public async Task A_row_closed_in_one_session_is_still_closed_in_the_next()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using (var first = Shell())
        {
            await first.OpenAsync(repo.Path);

            var project = ProjectRow(first);
            Assert.True(project.IsExpanded);

            // The user's click, through the same property the view sets.
            project.IsExpanded = false;
        }

        Assert.True(File.Exists(
            Path.Combine(repo.Path, ".versio", "local", "tree.json")),
            "closing the shell did not write the expansion state");

        using var second = Shell();
        await second.OpenAsync(repo.Path);

        // The default for a project row is open — only the persisted state can say closed.
        Assert.False(ProjectRow(second).IsExpanded);
    }

    [Fact]
    public async Task An_unreadable_state_file_means_the_defaults()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        var state = Path.Combine(repo.Path, ".versio", "local", "tree.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        File.WriteAllText(state, "{ torn");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        // Expansion state is the one kind of state that is always safe to lose; a file that
        // cannot be read must cost the defaults, never the open.
        Assert.True(ProjectRow(shell).IsExpanded);
    }
}
