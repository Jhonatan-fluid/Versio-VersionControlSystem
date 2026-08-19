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
