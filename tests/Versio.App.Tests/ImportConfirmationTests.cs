using System.IO;
using Versio.App.ViewModels;
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
    private static ShellViewModel Shell()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        return new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);
    }

    private static ShellViewModel Shell(out InMemoryLogSink sink)
    {
        sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        return new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);
    }

    /// <summary>A repository with one tracked project whose mirror is already exported.</summary>
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

    private static string ProjectPath(TempRepo repo) =>
        Path.Combine(repo.Path, "plc", "Machine.project");

    /// <summary>Edit the exported text, which is the whole point of the mirror.</summary>
    private static async Task EditTheMirrorAndSettle(TempRepo repo, ShellViewModel shell)
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
        shell.Sync.ConfirmImport = _ =>
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

        Assert.Null(shell.Sync.ConfirmImport);

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
