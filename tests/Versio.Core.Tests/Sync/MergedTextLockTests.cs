using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// What a merge leaves behind, and why the commit gate must not act on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The state this covers was measured, not imagined.</b> Probe P-M1, on 2026-08-18, merged
/// two real exports of the fixture: the <c>.project</c> is <c>binary -diff -merge</c> so git
/// conflicts on it and one side is kept whole, while the mirror three-way merges and ends up
/// holding both. The committed result had 41 mirror files, two of them carrying content the
/// kept project does not contain — a POU method body, and the manifest.
/// </para>
/// <para>
/// <b>The danger is the export gate, not the merge.</b> After that resolution the project's
/// hash no longer matches <c>LastSynced.ProjectHash</c>, so <c>CommitGate</c> reports
/// <c>ExportRequired</c> and the commit gate runs an export — which regenerates the mirror from
/// one branch's project and deletes the other branch's work. These tests are about the
/// ordering that stops it.
/// </para>
/// </remarks>
public sealed class MergedTextLockTests
{
    /// <summary>
    /// A binding that has actually been synced once, then released — Clean, with a
    /// <c>LastSynced</c> describing what is on disk.
    /// </summary>
    /// <remarks>
    /// <b>A fresh harness will not do for the release tests.</b> It has never synced, so
    /// <c>LastSynced</c> is empty and no hash on disk can ever match it — which makes
    /// "nothing outstanding" unreachable and the assertion vacuous in the failing direction.
    /// Exporting and then releasing is the shortest honest route to the state a merge really
    /// arrives at.
    /// </remarks>
    private static async Task<SyncOrchestratorTests.Harness> SyncedAndCleanAsync()
    {
        var harness = new SyncOrchestratorTests.Harness();

        harness.Codesys.EnqueueSuccess();
        harness.SetProject("as the owner left it");

        await harness.Orchestrator.HandleChangeAsync(
            new SyncJob
            {
                BindingId = SyncOrchestratorTests.BindingId,
                Source = ChangeSource.Project,
            },
            CancellationToken.None);

        // The export recorded LastSynced as the state both sides are actually in; the push
        // releases the direction lock and moves only the baseline. (Until 2026-09-14 a push
        // rewrote LastSynced too, which is how an unexported save got forgotten.)
        await harness.Orchestrator.ReleaseAfterPushAsync("head", CancellationToken.None);

        Assert.Empty(harness.Orchestrator.CaptureLocks());
        return harness;
    }

    [Fact]
    public async Task A_merged_mirror_puts_the_text_in_charge()
    {
        var harness = new SyncOrchestratorTests.Harness();

        await harness.Orchestrator.NoteMergedTextAsync(
            SyncOrchestratorTests.BindingId, CancellationToken.None);

        var held = Assert.Single(harness.Orchestrator.CaptureLocks());

        Assert.Equal(SyncDirection.TextAuthoritative, held.Direction);
    }

    [Fact]
    public async Task The_commit_gates_export_is_refused_before_the_toolchain_runs()
    {
        // THE ASSERTION THE WHOLE FEATURE RESTS ON, and it is about ORDERING rather than
        // outcome: AttemptAsync asks the state machine before it opens the project, so the
        // refusal has to arrive with the toolchain never having been invoked. A version that
        // exported and then failed the transition would leave the mirror already overwritten
        // and the lock unchanged - the exact loss this exists to prevent, with a red test to
        // show for it and the work gone anyway.
        var harness = new SyncOrchestratorTests.Harness();

        // A merge has landed: the mirror holds both sides and the project holds one.
        harness.SetProject("the side the resolution kept");
        await harness.Orchestrator.NoteMergedTextAsync(
            SyncOrchestratorTests.BindingId, CancellationToken.None);

        // The fake records every job it is handed, so an empty Jobs list is a positive
        // statement that the toolchain was never invoked - not merely that nothing crashed.
        var outcome = await harness.Orchestrator.HandleChangeAsync(
            new SyncJob
            {
                BindingId = SyncOrchestratorTests.BindingId,
                Source = ChangeSource.Project,
                Synchronous = true,
                Reason = "commit gate",
            },
            CancellationToken.None);

        Assert.Equal(SyncVerdict.BlockedByLock, outcome.Verdict);
        Assert.Empty(harness.Codesys.Jobs);

        // And the lock did not move: a refusal is not a state change.
        var held = Assert.Single(harness.Orchestrator.CaptureLocks());
        Assert.Equal(SyncDirection.TextAuthoritative, held.Direction);
    }

    [Fact]
    public async Task An_import_is_still_allowed_out_of_it()
    {
        // The lock blocks exports and nothing else. If it blocked imports too it would be a
        // trap rather than a hold - the import is the entire way out, and the panel's own
        // banner points at it.
        var harness = new SyncOrchestratorTests.Harness(
            new OrchestratorOptions { RequireImportConfirmation = false });

        await harness.Orchestrator.NoteMergedTextAsync(
            SyncOrchestratorTests.BindingId, CancellationToken.None);

        harness.Codesys.EnqueueSuccess();
        harness.SetExportedText("the merged text");

        var outcome = await harness.Orchestrator.HandleChangeAsync(
            new SyncJob
            {
                BindingId = SyncOrchestratorTests.BindingId,
                Source = ChangeSource.ExportedText,
            },
            CancellationToken.None);

        Assert.NotEqual(SyncVerdict.BlockedByLock, outcome.Verdict);
    }

    [Fact]
    public async Task Adopting_first_would_record_an_agreement_that_never_existed()
    {
        // WHY THE MERGE FLOW PASSES adoptResult: false, asserted at the level where the
        // difference is visible. Adopt writes LastSynced from whatever is on disk right now.
        // After a merge that is one branch's project beside a mirror holding both, so the
        // record would claim they agree - and on a merge that stopped on a conflict it would
        // record the conflict markers themselves as the synced state.
        //
        // The lock still prevents the export either way, so this is about the RECORD rather
        // than about loss. It is asserted because a false LastSynced is what the next reader
        // of the lock file would take as truth.
        // Two runs identical but for the adopt, because CaptureLocks reports only HELD locks -
        // there is nothing to read before the marking, so the comparison has to be between the
        // two orders rather than before and after one of them.
        static async Task<string> HashRecordedAsync(bool adoptFirst)
        {
            var harness = new SyncOrchestratorTests.Harness();

            // The merge lands: the project on disk is now one side of it.
            harness.SetProject("the side the resolution kept");

            if (adoptFirst)
            {
                await harness.Orchestrator.AdoptExternalStateAsync(CancellationToken.None);
            }

            await harness.Orchestrator.NoteMergedTextAsync(
                SyncOrchestratorTests.BindingId, CancellationToken.None);

            var held = Assert.Single(harness.Orchestrator.CaptureLocks());
            Assert.Equal(SyncDirection.TextAuthoritative, held.Direction);

            return held.ProjectHash;
        }

        var adopted = await HashRecordedAsync(adoptFirst: true);
        var notAdopted = await HashRecordedAsync(adoptFirst: false);

        // Adopting first rewrites LastSynced from what is on disk, so the record ends up
        // asserting that this project and its mirror agree. Not adopting leaves the last
        // record that was actually true. If these ever match, the parameter has stopped
        // meaning anything and the merge flow is adopting again.
        Assert.NotEqual(adopted, notAdopted);
    }

    [Fact]
    public async Task Undoing_the_merge_lets_go_of_the_lock_it_took()
    {
        // THE TRAP THE THIRD READ FOUND, AND THIS WORK CREATED. Abandoning a merge puts the
        // tree back - but AdoptExternalStateAsync deliberately skips a binding holding a lock,
        // so the lock stayed on: exports blocked over a mirror that no longer differed from
        // anything, and an import banner offering to import a mirror identical to the project.
        // Neither documented way out applied, because after an abandoned merge there is
        // nothing left to push.
        var harness = await SyncedAndCleanAsync();

        Assert.True(await harness.Orchestrator.NoteMergedTextAsync(
            SyncOrchestratorTests.BindingId, CancellationToken.None));

        Assert.Single(harness.Orchestrator.CaptureLocks());

        // `git merge --abort` has put everything back, so both sides match what was last
        // synced - which is exactly the condition for letting go.
        var released = await harness.Orchestrator.ReleaseIfNothingOutstandingAsync(
            SyncOrchestratorTests.BindingId, CancellationToken.None);

        Assert.True(released);
        Assert.Empty(harness.Orchestrator.CaptureLocks());
    }

    [Fact]
    public async Task A_lock_that_still_owes_an_import_survives_the_undo()
    {
        // The other half, and the reason this is a hash comparison rather than a blanket
        // release. A TextAuthoritative lock can predate the merge - committed mirror edits
        // nobody has imported - and undoing the merge does not undo those. Releasing there
        // would drop the record of real work waiting to reach the project.
        var harness = await SyncedAndCleanAsync();

        Assert.True(await harness.Orchestrator.NoteMergedTextAsync(
            SyncOrchestratorTests.BindingId, CancellationToken.None));

        // Something in the mirror genuinely differs from the last synced state.
        harness.SetExportedText("an edit nobody has imported");

        var released = await harness.Orchestrator.ReleaseIfNothingOutstandingAsync(
            SyncOrchestratorTests.BindingId, CancellationToken.None);

        Assert.False(released);

        var held = Assert.Single(harness.Orchestrator.CaptureLocks());
        Assert.Equal(SyncDirection.TextAuthoritative, held.Direction);
    }

    [Fact]
    public async Task A_project_with_unexported_edits_refuses_and_says_so()
    {
        // THE SILENT NO-OP, CAUGHT BY MUTATION. The state machine blocks TextChanged from
        // ProjectAuthoritative - a project whose own edits have not been exported - and
        // ApplyAsync logs a refused trigger at Debug and returns. That is right for the
        // ordinary case and far too quiet for this one: a false answer here means the merged
        // mirror is NOT protected from the commit gate's export, and nothing would have said
        // so. Reporting `true` unconditionally left every other test in this file green.
        var harness = new SyncOrchestratorTests.Harness();

        harness.Codesys.EnqueueSuccess();
        harness.SetProject("edited and exported, never pushed");

        await harness.Orchestrator.HandleChangeAsync(
            new SyncJob
            {
                BindingId = SyncOrchestratorTests.BindingId,
                Source = ChangeSource.Project,
            },
            CancellationToken.None);

        var held = Assert.Single(harness.Orchestrator.CaptureLocks());
        Assert.Equal(SyncDirection.ProjectAuthoritative, held.Direction);

        var moved = await harness.Orchestrator.NoteMergedTextAsync(
            SyncOrchestratorTests.BindingId, CancellationToken.None);

        Assert.False(moved);

        // And it did not quietly become something else on the way.
        Assert.Equal(SyncDirection.ProjectAuthoritative,
                     Assert.Single(harness.Orchestrator.CaptureLocks()).Direction);
    }

    [Fact]
    public async Task Marking_it_twice_is_not_an_escalation()
    {
        // Two projects in one merge, or a merge on top of a merge. TextChanged from
        // TextAuthoritative is "further text edit" and stays put; it must not fall through to
        // Conflict, which would need a person to resolve something that has not happened.
        var harness = new SyncOrchestratorTests.Harness();

        await harness.Orchestrator.NoteMergedTextAsync(
            SyncOrchestratorTests.BindingId, CancellationToken.None);
        await harness.Orchestrator.NoteMergedTextAsync(
            SyncOrchestratorTests.BindingId, CancellationToken.None);

        var held = Assert.Single(harness.Orchestrator.CaptureLocks());
        Assert.Equal(SyncDirection.TextAuthoritative, held.Direction);
    }
}
