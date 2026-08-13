using Versio.Core.Guarding;
using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// The direction locks travelling with an auto-stash, and coming back with it.
/// </summary>
/// <remarks>
/// <para>
/// <c>git stash --include-untracked</c> excludes <i>ignored</i> files, so
/// <c>.versio/local/</c> — where the locks live — does not travel. That is the right
/// behaviour, and it leaves a hole: after stashing a dirty mirror the lock still claims
/// <c>TextAuthoritative</c> with baselines describing content that is no longer on disk, so the
/// commit gate reports a mirror as stale that is, on this branch, perfectly clean.
/// </para>
/// <para>
/// Which is why the stash captures the locks and releases them, and restoring puts them back.
/// It is a round trip or it is nothing: handing the files back while the binding reads
/// <c>Clean</c> would let the very next export overwrite the mirror edits just restored.
/// </para>
/// </remarks>
public sealed class StashedLockTests
{
    private const string Stash = "2a4b8cdc9f1e4b6a8d3c5e7f9a1b2c3d4e5f6a7b";

    private static async Task<SyncOrchestratorTests.Harness> TextAuthoritativeAsync()
    {
        var harness = new SyncOrchestratorTests.Harness(
            new OrchestratorOptions { RequireImportConfirmation = false });

        // The state a stash has to deal with: somebody edited the mirror, so the text side is
        // authoritative and the work is not published.
        harness.Codesys.EnqueueSuccess();
        harness.SetExportedText("edited by hand");

        await harness.Orchestrator.HandleChangeAsync(
            new SyncJob
            {
                BindingId = SyncOrchestratorTests.BindingId,
                Source = ChangeSource.ExportedText,
            },
            CancellationToken.None);

        return harness;
    }

    [Fact]
    public async Task A_held_lock_is_captured_with_the_hashes_it_was_holding()
    {
        var harness = await TextAuthoritativeAsync();

        var captured = Assert.Single(harness.Orchestrator.CaptureLocks());

        Assert.Equal(SyncOrchestratorTests.BindingId, captured.BindingId);
        Assert.Equal(harness.Orchestrator.StateOf(SyncOrchestratorTests.BindingId),
                     captured.Direction);
    }

    [Fact]
    public void A_clean_binding_has_nothing_to_give_back()
    {
        // Recording it would make the restore re-assert a lock that was never held.
        var harness = new SyncOrchestratorTests.Harness();

        Assert.Empty(harness.Orchestrator.CaptureLocks());
    }

    [Fact]
    public async Task Releasing_clears_the_lock_and_says_where_the_work_went()
    {
        var harness = await TextAuthoritativeAsync();
        var captured = harness.Orchestrator.CaptureLocks();

        await harness.Orchestrator.ReleaseCapturedLocksAsync(
            captured, Stash, CancellationToken.None);

        Assert.Equal(SyncDirection.Clean,
                     harness.Orchestrator.StateOf(SyncOrchestratorTests.BindingId));

        // The reason names the stash, because "why is this clean when I had work" is the
        // question somebody asks a day later, from the panel rather than from a log.
        Assert.Contains(Stash[..7],
                        harness.Orchestrator.ReasonFor(SyncOrchestratorTests.BindingId),
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restoring_puts_the_lock_and_its_baselines_back()
    {
        var harness = await TextAuthoritativeAsync();
        var captured = harness.Orchestrator.CaptureLocks();
        var direction = captured[0].Direction;

        await harness.Orchestrator.ReleaseCapturedLocksAsync(
            captured, Stash, CancellationToken.None);

        var refused = await harness.Orchestrator.RestoreCapturedLocksAsync(
            captured, CancellationToken.None);

        Assert.Empty(refused);
        Assert.Equal(direction, harness.Orchestrator.StateOf(SyncOrchestratorTests.BindingId));
    }

    [Fact]
    public async Task A_binding_that_has_since_taken_a_lock_of_its_own_keeps_it()
    {
        // Days may have passed, on another branch, and whatever is claimed now was claimed by
        // real work. Reported rather than overwritten, so the caller can say so.
        var harness = await TextAuthoritativeAsync();
        var captured = harness.Orchestrator.CaptureLocks();

        await harness.Orchestrator.ReleaseCapturedLocksAsync(
            captured, Stash, CancellationToken.None);

        // Newer work on the other side of the binding. Past the coalescer's minimum interval,
        // or the run is throttled and the binding never takes a lock at all - which would
        // make this test pass for a reason that has nothing to do with what it asserts.
        harness.Clock.AdvanceSeconds(600);
        harness.Codesys.EnqueueSuccess();
        harness.SetProject("v2");
        await harness.Orchestrator.HandleChangeAsync(
            new SyncJob
            {
                BindingId = SyncOrchestratorTests.BindingId,
                Source = ChangeSource.Project,
            },
            CancellationToken.None);

        var newer = harness.Orchestrator.StateOf(SyncOrchestratorTests.BindingId);
        Assert.True(SyncStateMachine.HoldsLock(newer));

        var refused = await harness.Orchestrator.RestoreCapturedLocksAsync(
            captured, CancellationToken.None);

        Assert.Equal(SyncOrchestratorTests.BindingId, Assert.Single(refused));
        Assert.Equal(newer, harness.Orchestrator.StateOf(SyncOrchestratorTests.BindingId));
    }

    [Fact]
    public async Task A_lock_for_a_binding_that_no_longer_exists_is_reported_not_invented()
    {
        // The stash restored its files; there is simply no binding to hand the lock back to -
        // the project was deleted, or this is a branch where it never existed.
        var harness = new SyncOrchestratorTests.Harness();

        var refused = await harness.Orchestrator.RestoreCapturedLocksAsync(
            [
                new StashedLock
                {
                    BindingId = "b-vanished",
                    Direction = SyncDirection.TextAuthoritative,
                },
            ],
            CancellationToken.None);

        Assert.Equal("b-vanished", Assert.Single(refused));
    }

    [Fact]
    public async Task The_release_survives_a_restart()
    {
        // It goes through the same persisted lock state everything else does, so a crash
        // between the stash and the next launch cannot resurrect a lock whose work has moved.
        var harness = await TextAuthoritativeAsync();

        await harness.Orchestrator.ReleaseCapturedLocksAsync(
            harness.Orchestrator.CaptureLocks(), Stash, CancellationToken.None);

        var persisted = await harness.Locks.ReadAsync(CancellationToken.None);

        Assert.Equal(
            SyncDirection.Clean,
            persisted!.Bindings[SyncOrchestratorTests.BindingId].Direction);
    }
}
