using System.IO;
using Versio.App.Mvvm;
using Versio.App.ViewModels;
using Versio.Core.Logging;
using Versio.Core.Sync;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Reaching a saved copy from where the user reaches it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The orchestrator half is covered without a shell</b> — <c>BackupRestoreTests</c> asserts
/// that a restore puts the bytes back, keeps a copy of what it replaced, and refuses while the
/// IDE holds the project. None of that says the panel can get there, which is the half that has
/// been wrong four times in this codebase: <c>GuardIntent.Clone</c>,
/// <c>ResetForRepositoryChange</c>, <c>TreeRow.Reconcile</c> and the object index all had green
/// suites and no reachable caller.
/// </para>
/// <para>
/// So this drives a real repository with a real backup on disk and asserts the button appears,
/// the dialog is asked, and cancelling writes nothing.
/// </para>
/// </remarks>
public sealed class BackupRestoreFlowTests
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

        Directory.CreateDirectory(Path.Combine(repo.Path, "plc", "Machine", "App"));
        File.WriteAllBytes(
            Path.Combine(repo.Path, "plc", "Machine.project"), [0x00, 0x01, 0x02, 0x03]);
        File.WriteAllText(
            Path.Combine(repo.Path, "plc", "Machine", "App", "FB_Motor.st"), "FB\n");

        File.WriteAllText(Path.Combine(repo.Path, "plc", "Machine", "_manifest.json"), """
        {
          "schema": 4, "bindingId": "b1", "project": "Machine.project", "objectCount": 1,
          "objects": [
            { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_FULL",
              "path": "App/FB_Motor.st", "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [], "skipped": []
        }
        """);

        repo.Git("add .");
        repo.Git("commit -qm exported");
    }

    /// <summary>A copy in the folder, as though an earlier import had taken it.</summary>
    private static string GivenABackup(TempRepo repo, string stamp, byte[] content)
    {
        var folder = Path.Combine(repo.Path, ".versio", "local", "backups", "b1");
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, $"{stamp}-Machine.project");
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Select the project the way clicking its row does.</summary>
    private static void SelectTheProject(ShellViewModel shell) =>
        shell.Sync.NoteSelection("plc/Machine.project");

    [Fact]
    public async Task A_project_with_no_saved_copies_offers_nothing()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        SelectTheProject(shell);

        // The ordinary state: most projects have never had an import run. The button is
        // hidden rather than dead, so this is what keeps it off screen.
        Assert.False(shell.Sync.SelectedHasBackups);
        Assert.False(shell.Sync.RestoreBackupCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_saved_copy_makes_the_button_appear()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);
        GivenABackup(repo, "20260814-093000", [0x09]);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        SelectTheProject(shell);

        Assert.True(shell.Sync.SelectedHasBackups);
        Assert.True(shell.Sync.RestoreBackupCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_copies_reach_the_dialog_newest_first()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);
        GivenABackup(repo, "20260812-101500", [0x01]);
        GivenABackup(repo, "20260814-093000", [0x02]);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        SelectTheProject(shell);

        IReadOnlyList<ProjectBackup> offered = [];
        var project = string.Empty;

        shell.Asked().WhichBackup = (name, backups) =>
        {
            project = name;
            offered = backups;
            return null;
        };

        await Eventually.Press(shell.Sync.RestoreBackupCommand, "the restore button");
        await Eventually.Until(() => offered.Count > 0, "the dialog was offered the copies");

        Assert.Equal("Machine", project);
        Assert.Equal(2, offered.Count);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 14, 9, 30, 0, TimeSpan.Zero), offered[0].TakenUtc);

        // Repo-relative, because that is what ProjectBackup.Path promises and what the
        // orchestrator is handed back.
        Assert.StartsWith(".versio/local/backups/b1/", offered[0].Path);
    }

    [Fact]
    public async Task Cancelling_the_dialog_writes_nothing()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);
        GivenABackup(repo, "20260814-093000", [0x09, 0x09, 0x09]);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        SelectTheProject(shell);

        var project = Path.Combine(repo.Path, "plc", "Machine.project");
        var before = await File.ReadAllBytesAsync(project);

        var asked = false;
        shell.Asked().WhichBackup = (_, _) =>
        {
            asked = true;
            return null;
        };

        await Eventually.Press(shell.Sync.RestoreBackupCommand, "the restore button");
        await Eventually.Until(() => asked, "the dialog was asked");

        Assert.Equal(before, await File.ReadAllBytesAsync(project));
    }

    [Fact]
    public async Task An_unwired_dialog_restores_nothing()
    {
        // Null means cancel, and prompts nobody attached answer null. The wrong way round for
        // a wiring mistake would be restoring whichever copy happened to be first.
        using var repo = new TempRepo();
        GivenATrackedProject(repo);
        GivenABackup(repo, "20260814-093000", [0x09, 0x09, 0x09]);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        SelectTheProject(shell);

        var project = Path.Combine(repo.Path, "plc", "Machine.project");
        var before = await File.ReadAllBytesAsync(project);

        // Nothing attached, so the questions are NullPrompts' — which answer no.
        Assert.Same(NullPrompts.Instance, shell.Sync.Prompts);
        await Eventually.Press(shell.Sync.RestoreBackupCommand, "the restore button");

        Assert.Equal(before, await File.ReadAllBytesAsync(project));
    }

    [Fact]
    public async Task Choosing_a_copy_puts_it_back_and_keeps_the_one_it_replaced()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);
        GivenABackup(repo, "20260814-093000", [0xAA, 0xBB]);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        SelectTheProject(shell);

        var project = Path.Combine(repo.Path, "plc", "Machine.project");
        var replaced = await File.ReadAllBytesAsync(project);

        shell.Asked().WhichBackup = (_, backups) => backups[0];

        await Eventually.Press(shell.Sync.RestoreBackupCommand, "the restore button");
        await Eventually.Until(
            () => File.ReadAllBytes(project).SequenceEqual<byte>([0xAA, 0xBB]),
            "the project was restored");

        // And the state it replaced is still reachable, so going one copy too far is
        // recoverable rather than the last mistake somebody makes that afternoon.
        var folder = Path.Combine(repo.Path, ".versio", "local", "backups", "b1");
        Assert.Contains(
            Directory.GetFiles(folder),
            f => File.ReadAllBytes(f).SequenceEqual(replaced));
    }
}
