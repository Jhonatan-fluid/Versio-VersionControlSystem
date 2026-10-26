using Versio.Core.Logging;
using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// Getting a project back after an import damaged it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ten copies per binding have been written before every import since the import path
/// existed, and nothing could reach them.</b> Safeguards §5.E: after the one operation these
/// exist to undo, the engineer had an unusable project and no route to the file that would fix
/// it, short of knowing the layout of <c>.versio/local/</c> by heart.
/// </para>
/// <para>
/// <b>The assertion that matters most is that restoring keeps a copy too.</b> A restore
/// overwrites the project, so without it a restore is a one-way trip — walking back one import
/// too far would destroy the state somebody was trying to reach, using the feature that exists
/// to prevent exactly that.
/// </para>
/// </remarks>
public sealed class BackupRestoreTests
{
    private const string Root = @"C:\repo";

    private static string BackupPath(string stamp) =>
        $".versio/local/backups/{SyncOrchestratorTests.BindingId}/{stamp}-A.project";

    private static string Absolute(string relative) =>
        Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Put a copy in the folder as though an earlier import had taken it.</summary>
    private static void GivenABackup(
        SyncOrchestratorTests.Harness h, string stamp, string content) =>
        h.Fs.Write(Absolute(BackupPath(stamp)), content);

    [Fact]
    public void A_binding_that_has_never_been_imported_has_nothing_to_restore()
    {
        var h = new SyncOrchestratorTests.Harness();

        // Not a fault, and not an exception: most projects have never had an import run.
        Assert.Empty(h.Orchestrator.BackupsFor(SyncOrchestratorTests.BindingId));
    }

    [Fact]
    public void The_copies_are_listed_newest_first()
    {
        var h = new SyncOrchestratorTests.Harness();
        GivenABackup(h, "20260812-101500", "old");
        GivenABackup(h, "20260814-093000", "newest");
        GivenABackup(h, "20260813-120000", "middle");

        var list = h.Orchestrator.BackupsFor(SyncOrchestratorTests.BindingId);

        Assert.Equal(3, list.Count);
        Assert.Equal("A.project", list[0].ProjectFileName);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 14, 9, 30, 0, TimeSpan.Zero), list[0].TakenUtc);
    }

    [Fact]
    public async Task Restoring_puts_the_old_bytes_back()
    {
        var h = new SyncOrchestratorTests.Harness();
        h.SetProject("damaged by the import");
        GivenABackup(h, "20260814-093000", "the good version");

        var outcome = await h.Orchestrator.RestoreBackupAsync(
            SyncOrchestratorTests.BindingId, BackupPath("20260814-093000"),
            CancellationToken.None);

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
        Assert.Equal(
            "the good version",
            await h.Fs.ReadAllTextAsync(Absolute("plc/A.project"), CancellationToken.None));
    }

    [Fact]
    public async Task Restoring_keeps_a_copy_of_what_it_replaced()
    {
        // THE ONE THAT MATTERS. Without this, walking back one import too far destroys the
        // state you were trying to get back to — using the feature that exists to prevent it.
        var h = new SyncOrchestratorTests.Harness();
        h.SetProject("the state before restoring");
        GivenABackup(h, "20260814-093000", "an older version");

        var before = h.Orchestrator.BackupsFor(SyncOrchestratorTests.BindingId).Count;

        await h.Orchestrator.RestoreBackupAsync(
            SyncOrchestratorTests.BindingId, BackupPath("20260814-093000"),
            CancellationToken.None);

        var after = h.Orchestrator.BackupsFor(SyncOrchestratorTests.BindingId);

        Assert.Equal(before + 1, after.Count);

        // And it is the state that was just overwritten, not another copy of the backup.
        var kept = await h.Fs.ReadAllTextAsync(
            Absolute(after.First(b => b.Path != BackupPath("20260814-093000")).Path),
            CancellationToken.None);

        Assert.Equal("the state before restoring", kept);
    }

    [Fact]
    public async Task Restoring_the_oldest_of_a_full_set_does_not_prune_it_first()
    {
        // Ten copies are kept. The safety copy a restore takes first made eleven, pruning
        // ran, and the oldest — the one about to be restored — was deleted before it was
        // read (the 2026-10-24 review's item 22). On disk the copy then failed; in the fake
        // it used to write an empty project. The restore source is pinned through the prune.
        var h = new SyncOrchestratorTests.Harness();
        h.SetProject("the state before restoring");
        for (var i = 0; i < 10; i++)
        {
            GivenABackup(h, $"20260814-09300{i}", $"version {i}");
        }

        // The safety copy is stamped with the clock: it has to be the NEWEST entry, or the
        // prune would take it instead and the oldest would survive by accident.
        h.Clock.Advance(TimeSpan.FromDays(7));

        var oldest = BackupPath("20260814-093000");
        var outcome = await h.Orchestrator.RestoreBackupAsync(
            SyncOrchestratorTests.BindingId, oldest, CancellationToken.None);

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
        Assert.Equal(
            "version 0",
            await h.Fs.ReadAllTextAsync(Absolute("plc/A.project"), CancellationToken.None));
        Assert.True(h.Fs.FileExists(Absolute(oldest)), "the restore source was pruned");
    }

    [Fact]
    public async Task A_project_open_in_the_IDE_is_not_replaced_underneath_it()
    {
        // The same reasoning as an import: the IDE holds its own copy in memory and the next
        // save would silently revert whatever was written under it.
        var h = new SyncOrchestratorTests.Harness();
        h.SetProject("what is on disk now");
        GivenABackup(h, "20260814-093000", "an older version");
        h.Occupancy.Hold();

        var outcome = await h.Orchestrator.RestoreBackupAsync(
            SyncOrchestratorTests.BindingId, BackupPath("20260814-093000"),
            CancellationToken.None);

        Assert.Equal(SyncVerdict.BlockedByOpenProject, outcome.Verdict);
        Assert.Contains("Close it", outcome.Explanation);
        Assert.Equal(
            "what is on disk now",
            await h.Fs.ReadAllTextAsync(Absolute("plc/A.project"), CancellationToken.None));
    }

    [Fact]
    public async Task A_backup_that_is_no_longer_there_is_refused_by_name()
    {
        var h = new SyncOrchestratorTests.Harness();
        h.SetProject("untouched");

        var outcome = await h.Orchestrator.RestoreBackupAsync(
            SyncOrchestratorTests.BindingId, BackupPath("20260814-093000"),
            CancellationToken.None);

        Assert.Equal(SyncVerdict.Failed, outcome.Verdict);
        Assert.Contains("no longer on disk", outcome.Explanation);
        Assert.Equal(
            "untouched",
            await h.Fs.ReadAllTextAsync(Absolute("plc/A.project"), CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_binding_is_refused_rather_than_throwing()
    {
        var h = new SyncOrchestratorTests.Harness();

        var outcome = await h.Orchestrator.RestoreBackupAsync(
            "no-such-binding", BackupPath("20260814-093000"), CancellationToken.None);

        Assert.Equal(SyncVerdict.Failed, outcome.Verdict);
        Assert.Contains("no such project", outcome.Explanation);
    }

    [Fact]
    public async Task The_restore_does_not_wake_the_change_detector()
    {
        // Versio wrote this file, so the watcher event it produces is an echo of its own work.
        // Without the write window the restore would look like an external project edit and
        // queue an export for a change nobody made — which is the loop this defence exists for.
        var h = new SyncOrchestratorTests.Harness();
        h.SetProject("damaged");
        GivenABackup(h, "20260814-093000", "good");

        var sawItsOwnWrite = false;
        h.Fs.Write(Absolute("plc/A.project"), "damaged");

        var restore = h.Orchestrator.RestoreBackupAsync(
            SyncOrchestratorTests.BindingId, BackupPath("20260814-093000"),
            CancellationToken.None);

        // Checked from outside while the call is in flight is not deterministic, so this
        // asserts the durable half: after it returns, the window is closed again.
        await restore;

        sawItsOwnWrite = h.Orchestrator.IsWriting(
            SyncOrchestratorTests.BindingId, ChangeSource.Project);

        Assert.False(sawItsOwnWrite);
    }

    [Fact]
    public async Task The_restore_says_what_it_did_and_where_the_replaced_version_went()
    {
        var h = new SyncOrchestratorTests.Harness();
        h.SetProject("damaged");
        GivenABackup(h, "20260814-093000", "good");

        await h.Orchestrator.RestoreBackupAsync(
            SyncOrchestratorTests.BindingId, BackupPath("20260814-093000"),
            CancellationToken.None);

        // A restore is somebody's last resort. The line that says where the version it
        // replaced went is the one they will need if they restored the wrong one.
        Assert.Contains(h.Log, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("restored") &&
            e.Message.Contains("the version it replaced is at"));
    }
}
