using System.IO;
using Versio.Core.Plc;
using Versio.Infrastructure.Watching;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// What a project's row says once its run is over.
/// </summary>
/// <remarks>
/// <para>
/// <b>Seen 2026-10-29:</b> LBB101.ap20 read "exporting…" at 14:36, an hour after a 136 s
/// export had finished at 13:51:40. The run was announced, the row was set, the export did
/// its work — and a save that landed while it ran was queued by the coalescer, found nothing
/// to do afterwards, and its NoChange was what came back to the view model. "Nothing
/// happened" ran no rescan, and the rescan was the only thing that ever put the row's words
/// back. Two fixes, each enough on its own: the orchestrator's loop returns the attempt that
/// did the work (Core, <c>RunLoopAsync</c>), and <c>OnRunFinished</c> tells the row the
/// lock's state itself rather than waiting for a scan that may not come.
/// </para>
/// <para>
/// Driven below the watcher's timers through <c>SyncSettledChangeAsync</c>, as
/// <see cref="ImportConfirmationTests"/> does, with the mid-run save made from inside the
/// fake toolchain's run — the only place that moment exists.
/// </para>
/// </remarks>
public sealed class RunFinishedRowStateTests
{
    private static WatchedChange Settled(TempRepo repo, string relative) =>
        new(Path.Combine(repo.Path, relative.Replace('/', Path.DirectorySeparatorChar)),
            DateTimeOffset.UtcNow,
            WatchTier.Watcher);

    [Fact]
    public async Task A_row_stops_saying_exporting_when_the_follow_up_finds_nothing()
    {
        using var repo = new TempRepo(identity: true);
        ImportConfirmationTests.GivenATrackedProject(repo);

        var toolchain = new CapturingToolchain();
        using var shell = TestShell.Build(out var sink, toolchain: toolchain);
        await shell.OpenAsync(repo.Path);

        // A real change, so the settle exports rather than stopping at the hash gate.
        File.WriteAllBytes(ImportConfirmationTests.ProjectPath(repo), [0x09, 0x09]);

        // A second save, landing while the export runs. The export already going captures
        // the file as it is, so the queued follow-up finds nothing — the ordinary case,
        // and the one that left the row stuck.
        toolchain.BeforeRun = job =>
            job.Operation == PlcOperation.Export && toolchain.Jobs.Count == 1
                ? shell.Sync.SyncSettledChangeAsync(Settled(repo, "plc/Machine.project"))
                : Task.CompletedTask;

        await shell.Sync.SyncSettledChangeAsync(Settled(repo, "plc/Machine.project"));

        Assert.Single(toolchain.Jobs, j => j.Operation == PlcOperation.Export);
        Assert.Contains(sink.Snapshot(),
            e => e.Message.Contains("arrived while the last run was in flight", StringComparison.Ordinal));

        // The export succeeded, so the lock is project-authoritative and the row says so:
        // the words the rescan would have written, had it run.
        var row = Assert.Single(shell.Sync.Projects, p => p.BindingId == "b1");
        Assert.False(row.Busy);
        Assert.Equal("project ahead", row.State);
    }
}
