using System.IO;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A project the IDE holds says so in the panel, not only in the log.
/// </summary>
/// <remarks>
/// Seen 2026-10-28 on the first open of a repository (FINDINGS, same date): the only
/// project was adopted, its first export was refused because TIA Portal held it, and the
/// panel said "1 project has changes that have not been exported" over a row reading
/// "not exported" — both true, and neither the answer to the owner's first question, which
/// was why nothing was exporting. The refusal's own sentence was in the log and nowhere
/// else. The orchestrator's refusal is remembered per binding anyway, for the retry when
/// the IDE lets go; this proves the panel reads it back.
/// </remarks>
public sealed class WaitingOnIdePanelTests
{
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

        File.WriteAllBytes(
            Path.Combine(repo.Path, "plc", "Machine.project"), [0x00, 0x01, 0x02, 0x03]);
    }

    private const string Refusal =
        "Machine.project is open in the IDE (CODESYS 3.5: engineer on FS-WORKSTATION, " +
        "since 2026-10-26 16:21:45Z), so it cannot be exported until it is closed. " +
        "Close it and the export will run.";

    [Fact]
    public async Task A_project_waiting_on_the_IDE_shows_the_refusal_in_the_panel()
    {
        using var repo = new TempRepo();
        Directory.CreateDirectory(Path.Combine(repo.Path, "plc"));
        GivenATrackedProject(repo);

        // No toolchain, so the project stays behind: the open cannot export it, which is
        // the state a refused export leaves as well.
        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);
        Assert.True(shell.Sync.HasUnexportedChanges,
            $"precondition: the project is behind. summary: '{shell.Sync.StalenessSummary}'");

        // The production path records the wait inside the gate export, which then
        // recomputes the banner; a top-bar refresh alone does not, so this drives the
        // recomputation the export would have.
        shell.Sync.RememberWaitingOnIde("b1", Refusal);
        await shell.Sync.RefreshStalenessAsync();

        // The reason, in the banner, in the orchestrator's own words.
        Assert.Contains("cannot be exported yet", shell.Sync.StalenessSummary, StringComparison.Ordinal);
        Assert.Contains("open in the IDE", shell.Sync.StalenessSummary, StringComparison.Ordinal);
        Assert.Contains("since 2026-10-26", shell.Sync.StalenessSummary, StringComparison.Ordinal);

        // Said once: the same project is not also counted as "changes not exported".
        Assert.DoesNotContain("have not been exported", shell.Sync.StalenessSummary,
            StringComparison.Ordinal);

        // Still behind as far as the commit gate and the git panel are concerned.
        Assert.True(shell.Sync.HasUnexportedChanges);

        var row = Assert.Single(shell.Sync.Projects, p => p.BindingId == "b1");
        Assert.Equal("waiting for the IDE", row.State);
    }
}
