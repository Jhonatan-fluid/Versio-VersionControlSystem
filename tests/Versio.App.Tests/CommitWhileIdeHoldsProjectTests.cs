using System.Diagnostics;
using System.IO;
using Versio.Core.Plc;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A commit while the IDE holds a changed directory project records nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found 2026-09-14, answering "can I commit while TIA Portal is open".</b> The commit gate
/// saw the project had changed and asked for an export; the export refused, because Portal
/// holds the project and a directory project cannot be exported from a copy; and that refusal
/// is deliberately a wait rather than a failure. The commit flow stopped only on a failure — so
/// it went on to stage the OLD mirror beside the new project and said "Committed.", which is
/// the one outcome the gate exists to prevent.
/// </para>
/// <para>
/// Driven through the shell with a real repository, the real TIA format and its real occupancy
/// marker, because the defect was in the wiring between them: every piece was right on its own.
/// </para>
/// </remarks>
public sealed class CommitWhileIdeHoldsProjectTests
{
    [Fact]
    public async Task A_commit_while_the_ide_holds_a_changed_project_commits_nothing()
    {
        using var repo = new TempRepo();
        var station = Path.Combine(repo.Path, "Station");
        Directory.CreateDirectory(Path.Combine(station, "System"));
        File.WriteAllText(Path.Combine(station, "Station.ap21"), "pointer");
        File.WriteAllText(Path.Combine(station, "System", "PEData.plf"), "program v1");

        var toolchain = new CapturingToolchain();
        using var shell = TestShell.Build(out var sink, toolchain: toolchain);
        await shell.OpenAsync(repo.Path);

        // Every wait reports the log and the shell's state AS IT FAILED. This test timed out
        // at ~33 s in three of nine full-suite runs on 2026-10-05, never alone, and the old
        // messages could not say which wait or why - one snapshotted the log before waiting.
        string State() =>
            $"busy={shell.Busy.IsBusy} exports={toolchain.Jobs.Count(j => j.Operation == PlcOperation.Export)} " +
            $"commit.canExecute={shell.CommitCommand.CanExecute(null)} commit.running={shell.CommitCommand.IsRunning}\nlog:\n" +
            string.Join("\n", sink.Snapshot().Select(e => $"{e.Level} {e.Category} {e.Message}"));

        // Opening adopts the project and, with a toolchain present, catches up with an export:
        // the state an engineer is in once Versio has seen the project.
        await Eventually.Until(
            () => toolchain.Jobs.Any(j => j.Operation == PlcOperation.Export),
            "the catch-up export ran", State);
        await Eventually.Until(() => !shell.Busy.IsBusy, "the catch-up export finished", State);

        // Portal opens the project - it writes Station.info beside the pointer - and saves.
        File.WriteAllText(Path.Combine(station, "Station.info"), "held by TIA Portal");
        File.WriteAllText(Path.Combine(station, "System", "PEData.plf"), "program v2, saved in Portal");
        await shell.RefreshAsync();

        var head = Head(repo);
        var exports = toolchain.Jobs.Count(j => j.Operation == PlcOperation.Export);

        shell.CommitMessage = "work saved in Portal";
        await Eventually.Press(shell.CommitCommand, "commit", State);

        // Nothing was committed: the mirror in that commit would not describe the project.
        Assert.Equal(head, Head(repo));

        // And it says why, in the export's own words - which name the IDE and what to close.
        Assert.Contains(shell.Notifications.Visible, n =>
            n.ClipboardText.Contains("Nothing was committed", StringComparison.Ordinal)
            && n.ClipboardText.Contains("open in the IDE", StringComparison.Ordinal)
            // ... and the file to delete if nobody has it, because this vendor's marker
            // carries no pid and can outlive the session that wrote it (2026-10-28).
            && n.ClipboardText.Contains("delete Station/Station.info", StringComparison.Ordinal));

        // The same refusal reaches the project panel, in the orchestrator's words, rather
        // than the panel saying only that changes have not been exported (2026-10-28). This
        // is the one test where a real refusal travels from the gate export to the banner;
        // WaitingOnIdePanelTests drives the banner from a remembered wait directly.
        Assert.Contains("cannot be exported yet", shell.Sync.StalenessSummary, StringComparison.Ordinal);
        Assert.Contains("open in the IDE", shell.Sync.StalenessSummary, StringComparison.Ordinal);

        // No export was run over a project the IDE holds.
        Assert.Equal(exports, toolchain.Jobs.Count(j => j.Operation == PlcOperation.Export));
    }

    private static string Head(TempRepo repo)
    {
        using var git = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "rev-parse HEAD",
            WorkingDirectory = repo.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return output.Trim();
    }
}
