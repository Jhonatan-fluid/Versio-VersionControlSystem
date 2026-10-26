using System.IO;
using Versio.App.Mvvm;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Guarding;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Versio.Infrastructure.Watching;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// An import is detected automatically and applied only when somebody says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this closes was a lie rather than a crash.</b>
/// <c>RequireImportConfirmation</c> has been <c>true</c> since it shipped, the orchestrator
/// consulted it on every import, and the only caller answered <c>Confirmed = true</c> with the
/// comment <i>"there is nobody to ask"</i>. So the gate was switched on, reached, and always
/// passed — for the one direction that writes a binary file nobody can diff afterwards.
/// </para>
/// <para>
/// <b>Driven through <c>SyncSettledChangeAsync</c>, which is where the answer is given.</b> A
/// test that called the orchestrator directly would prove the gate — which
/// <c>AdoptAndExportTests</c> already does against real CODESYS, bytes and all — and would say
/// nothing about the caller, which is the half that was wrong. No toolchain is needed, because
/// the refusal happens before one would be launched.
/// </para>
/// </remarks>
public sealed class ImportConfirmationTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    private static ShellViewModel Shell(out InMemoryLogSink sink) => TestShell.Build(out sink);

    /// <summary>A repository with one tracked project whose mirror is already exported.</summary>
    /// <remarks>
    /// Internal because <see cref="ImportPlanHandoverTests"/> needs exactly this repository —
    /// one binding, one manifest that records one object, and a mirror already committed. Two
    /// fixtures drifting apart is how one of them ends up testing a layout nothing ships.
    /// </remarks>
    internal static void GivenATrackedProject(TempRepo repo)
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

        var app = Path.Combine(repo.Path, "plc", "Machine", "App");
        Directory.CreateDirectory(app);

        File.WriteAllBytes(
            Path.Combine(repo.Path, "plc", "Machine.project"), [0x00, 0x01, 0x02, 0x03]);
        File.WriteAllText(Path.Combine(app, "FB_Motor.st"), "FUNCTION_BLOCK FB_Motor\n");

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

    private static WatchedChange Settled(TempRepo repo, string relative) =>
        new(Path.Combine(repo.Path, relative.Replace('/', Path.DirectorySeparatorChar)),
            DateTimeOffset.UtcNow,
            WatchTier.Watcher);

    internal static string ProjectPath(TempRepo repo) =>
        Path.Combine(repo.Path, "plc", "Machine.project");

    /// <summary>Edit the exported text, which is the whole point of the mirror.</summary>
    internal static async Task EditTheMirrorAndSettle(TempRepo repo, ShellViewModel shell)
    {
        await File.AppendAllTextAsync(
            Path.Combine(repo.Path, "plc", "Machine", "App", "FB_Motor.st"), "// edited\n");

        await shell.Sync.SyncSettledChangeAsync(Settled(repo, "plc/Machine/App/FB_Motor.st"));
    }

    [Fact]
    public async Task An_edit_to_the_mirror_is_not_imported_on_its_own()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        var before = await File.ReadAllBytesAsync(ProjectPath(repo));
        await EditTheMirrorAndSettle(repo, shell);

        // NOTHING was written to the project. This is the assertion the whole section exists
        // for, and before this change it could not have been made: the caller answered its own
        // question with "confirmed, because there is nobody to ask".
        Assert.Equal(before, await File.ReadAllBytesAsync(ProjectPath(repo)));

        // And it is waiting, visibly, rather than being silently dropped.
        Assert.True(shell.Sync.HasPendingImport);
        Assert.Equal("Machine", shell.Sync.PendingImportProject?.Name);
    }

    [Fact]
    public async Task The_waiting_import_says_what_it_would_overwrite_and_what_is_kept()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        await EditTheMirrorAndSettle(repo, shell);

        // The .project, not the mirror: those are different files, and the one being rewritten
        // is the one worth naming.
        Assert.Contains("plc/Machine.project", shell.Sync.PendingImportDetail);

        // The backup is why this is recoverable, and somebody deciding should not have to
        // already know it exists.
        Assert.Contains(".versio/local/", shell.Sync.PendingImportDetail);
    }

    [Fact]
    public async Task Declining_writes_nothing()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell(out var sink);
        await shell.OpenAsync(repo.Path);

        var before = await File.ReadAllBytesAsync(ProjectPath(repo));
        await EditTheMirrorAndSettle(repo, shell);

        var asked = 0;
        shell.Asked().Import = _ =>
        {
            asked++;
            return false;
        };

        await Eventually.Press(shell.Sync.ConfirmImportCommand, "the import button");

        Assert.Equal(1, asked);
        Assert.Equal(before, await File.ReadAllBytesAsync(ProjectPath(repo)));
        Assert.Contains(sink.Snapshot(), e => e.Message.Contains("import declined"));

        // AND THE OFFER STAYS UP. Declining is "not now", not "never": the exported text is
        // still ahead of the project, and a banner that vanished on a no would leave the
        // repository in a state with nothing on screen saying so — reachable again only by
        // editing the mirror a second time.
        Assert.True(shell.Sync.HasPendingImport);
    }

    [Fact]
    public async Task An_unwired_confirmation_means_no()
    {
        // The wrong way round for a wiring mistake would be a silent import. ConfirmDestructive
        // already establishes this convention; this follows it, and this test is what holds it.
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        var before = await File.ReadAllBytesAsync(ProjectPath(repo));
        await EditTheMirrorAndSettle(repo, shell);

        // Nothing attached, so the questions are NullPrompts' — which answer no.
        Assert.Same(NullPrompts.Instance, shell.Sync.Prompts);

        await Eventually.Press(shell.Sync.ConfirmImportCommand, "the import button");

        Assert.Equal(before, await File.ReadAllBytesAsync(ProjectPath(repo)));
        Assert.True(shell.Sync.HasPendingImport);
    }

    [Fact]
    public async Task Nothing_waiting_means_no_banner_and_a_dead_button()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        Assert.False(shell.Sync.HasPendingImport);
        Assert.Null(shell.Sync.PendingImportProject);
        Assert.Equal(string.Empty, shell.Sync.PendingImportDetail);
        Assert.False(shell.Sync.ConfirmImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_project_change_is_not_gated_by_this_at_all()
    {
        // The confirmation guards the IMPORT direction only. Gating the export as well would
        // mean asking permission to write a file whose whole purpose is to be reviewable, on
        // every save.
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        await File.WriteAllBytesAsync(ProjectPath(repo), [0x09, 0x09, 0x09]);
        await shell.Sync.SyncSettledChangeAsync(Settled(repo, "plc/Machine.project"));

        Assert.False(shell.Sync.HasPendingImport);
    }

    [Fact]
    public async Task A_project_save_while_an_edit_waits_is_refused_and_the_banner_stays()
    {
        // The hole the first round left open (its item 3; the second round's item 1): the
        // waiting import claims no direction, so the binding is Clean, and a project save
        // from Clean exported straight over the edit — the one copy of it anywhere. The
        // orchestrator refuses that export now, and the caller keeps the banner up, because
        // the import it names is the way out of the refusal.
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell(out var sink);
        await shell.OpenAsync(repo.Path);
        await EditTheMirrorAndSettle(repo, shell);
        Assert.True(shell.Sync.HasPendingImport);

        await File.WriteAllBytesAsync(ProjectPath(repo), [0x09, 0x09, 0x09]);
        await shell.Sync.SyncSettledChangeAsync(Settled(repo, "plc/Machine.project"));

        Assert.Contains(sink.Snapshot(), e => e.Message.Contains("BlockedByPendingEdit"));
        Assert.True(shell.Sync.HasPendingImport);
        Assert.EndsWith("// edited\n", await File.ReadAllTextAsync(
            Path.Combine(repo.Path, "plc", "Machine", "App", "FB_Motor.st")));
    }

    [Fact]
    public async Task A_mirror_edit_restored_from_an_auto_stash_is_still_waiting_for_its_import()
    {
        // Review 2026-10-25, seventh round, item 1: switching branches stashed the unconfirmed
        // mirror edit; restoring it on the way back popped the stash and then ADOPTED the tree,
        // so the restored edit was recorded as synced — banner gone, the gate beside the
        // direction lock blind to it — and the next project save exported over the one copy
        // of it anywhere. The restore must leave the edit in the state that gate protects.
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell(out var sink);
        shell.Asked().Guard = _ => GuardChoice.StashAndProceed;
        shell.Asked().StashedWork = _ => StashedWorkChoice.Restore;
        await shell.OpenAsync(repo.Path);

        // Opening writes the attribute and ignore rules; committed, and the other branch cut
        // after them, so the only uncommitted work on either branch is the mirror edit — a
        // dirty tree on arrival would make the pop stop short of it.
        repo.Git("add -A");
        repo.Git("commit -qm rules");
        repo.Git("branch other");
        await shell.RefreshAsync();

        await EditTheMirrorAndSettle(repo, shell);
        Assert.True(shell.Sync.HasPendingImport);

        await shell.SwitchBranchAsync(new BranchEntry(new BranchInfo { Name = "other", IsCurrent = false }));
        Assert.Contains("versio-autostash", TempRepo.GitOutput(repo.Path, "stash list"), StringComparison.Ordinal);

        await shell.SwitchBranchAsync(new BranchEntry(new BranchInfo { Name = "main", IsCurrent = false }));

        // The edit is back, and so is the question it owes.
        var mirror = Path.Combine(repo.Path, "plc", "Machine", "App", "FB_Motor.st");
        var content = await File.ReadAllTextAsync(mirror);
        var stashes = TempRepo.GitOutput(repo.Path, "stash list").Trim();
        Assert.True(content.EndsWith("// edited\n", StringComparison.Ordinal),
            $"mirror: '{content}'; stashes: '{stashes}'; branch: {TempRepo.GitOutput(repo.Path, "branch --show-current").Trim()}; " +
            "log: " + string.Join(" | ", sink.Snapshot().Where(e => e.Message.Contains("stash", StringComparison.OrdinalIgnoreCase)
                                                                   || e.Message.Contains("switch", StringComparison.OrdinalIgnoreCase)).Select(e => e.Message)));
        Assert.Equal(string.Empty, stashes);
        Assert.True(shell.Sync.HasPendingImport, "the restored edit was adopted as synced");

        // And a project save is refused, as it was before the stash.
        await File.WriteAllBytesAsync(ProjectPath(repo), [0x09, 0x09, 0x09]);
        await shell.Sync.SyncSettledChangeAsync(Settled(repo, "plc/Machine.project"));

        Assert.Contains(sink.Snapshot(), e => e.Message.Contains("BlockedByPendingEdit"));
        Assert.EndsWith("// edited\n", await File.ReadAllTextAsync(mirror));
    }

    [Fact]
    public async Task After_a_restart_the_refused_save_is_what_brings_the_banner_back()
    {
        // The banner lives in memory and a restart loses it, while the edit and the record
        // of what was last synced both survive on disk. The first thing to notice the edit
        // after the restart can therefore be a project save, and the refusal it gets is the
        // only signal the caller has: it must raise the banner from that, not only from the
        // text event that will not come again.
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        await EditTheMirrorAndSettle(repo, shell);
        Assert.True(shell.Sync.HasPendingImport);

        shell.Sync.Detach();
        Assert.False(shell.Sync.HasPendingImport);

        using var again = Shell();
        await again.OpenAsync(repo.Path);
        Assert.False(again.Sync.HasPendingImport);

        await File.WriteAllBytesAsync(ProjectPath(repo), [0x09, 0x09, 0x09]);
        await again.Sync.SyncSettledChangeAsync(Settled(repo, "plc/Machine.project"));

        Assert.True(again.Sync.HasPendingImport);
        Assert.Equal("Machine", again.Sync.PendingImportProject?.Name);
    }

    [Fact]
    public async Task Closing_the_repository_takes_the_waiting_import_with_it()
    {
        // Binding ids are unique per repository, so a leftover would offer to write into a
        // project that is no longer open — the defect the failure banner was fixed for.
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        await EditTheMirrorAndSettle(repo, shell);
        Assert.True(shell.Sync.HasPendingImport);

        shell.Sync.Detach();

        Assert.False(shell.Sync.HasPendingImport);
        Assert.Equal(string.Empty, shell.Sync.PendingImportDetail);
        Assert.False(shell.Sync.ConfirmImportCommand.CanExecute(null));
    }
}
