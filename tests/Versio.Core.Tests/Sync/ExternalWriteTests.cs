using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// What happens when git, not an engineer, rewrites the working tree.
/// </summary>
/// <remarks>
/// <para>
/// A checkout or a pull rewrites the project <i>and</i> its mirror at once. The watcher cannot
/// tell those writes from somebody editing the files: the hash gate agrees something changed,
/// because it genuinely did, and the self-write suppressor recognises only the hashes Versio's
/// own export produced.
/// </para>
/// <para>
/// Left alone that is the one failure in this design needing no user error at all — a pull lands
/// a new mirror, the engine reads it as an edit to the exported text, and imports it into the
/// binary project on the strength of a change nobody made.
/// </para>
/// </remarks>
public sealed class ExternalWriteTests
{
    private static SyncOrchestratorTests.Harness Build() => new();

    private static SyncJob Change(ChangeSource source) => new()
    {
        BindingId = SyncOrchestratorTests.BindingId,
        Source = source,
    };

    [Fact]
    public async Task A_change_arriving_while_git_writes_is_not_an_edit()
    {
        var harness = Build();

        using (harness.Orchestrator.BeginExternalWrite("checkout main"))
        {
            harness.SetExportedText("whatever the new commit says");

            var outcome = await harness.Orchestrator.HandleChangeAsync(
                Change(ChangeSource.ExportedText), CancellationToken.None);

            Assert.Equal(SyncVerdict.GitWriteSuppressed, outcome.Verdict);
        }
    }

    [Fact]
    public async Task It_is_dropped_before_the_hash_gate_is_asked()
    {
        // The distinguishing property. Git really did change the content, so a check that
        // asked "did anything change" first would answer yes and send a pull's new mirror
        // straight down the import path.
        var harness = Build();

        using (harness.Orchestrator.BeginExternalWrite("pull"))
        {
            harness.SetProject("a genuinely different project");

            var outcome = await harness.Orchestrator.HandleChangeAsync(
                Change(ChangeSource.Project), CancellationToken.None);

            Assert.Equal(SyncVerdict.GitWriteSuppressed, outcome.Verdict);
            Assert.NotEqual(SyncVerdict.NoChange, outcome.Verdict);
        }
    }

    [Fact]
    public async Task Normal_service_resumes_when_git_has_finished()
    {
        var harness = Build();

        using (harness.Orchestrator.BeginExternalWrite("checkout main"))
        {
            harness.SetProject("v2");

            Assert.Equal(SyncVerdict.GitWriteSuppressed, (await harness.Orchestrator
                .HandleChangeAsync(Change(ChangeSource.Project), CancellationToken.None)).Verdict);
        }

        harness.SetProject("v3");

        Assert.Equal(SyncVerdict.Completed, (await harness.Orchestrator
            .HandleChangeAsync(Change(ChangeSource.Project), CancellationToken.None)).Verdict);
    }

    [Fact]
    public void Nested_writes_are_counted_not_flagged()
    {
        // A pull that stages the mirror afterwards is two nested writes, and whichever
        // finished first would clear a boolean while the other was still going.
        var harness = Build();

        var outer = harness.Orchestrator.BeginExternalWrite("pull");
        var inner = harness.Orchestrator.BeginExternalWrite("stage the mirror");

        inner.Dispose();
        Assert.True(harness.Orchestrator.IsExternalWriteInProgress);

        outer.Dispose();
        Assert.False(harness.Orchestrator.IsExternalWriteInProgress);
    }

    [Fact]
    public void Disposing_a_window_twice_is_harmless()
    {
        var harness = Build();
        var window = harness.Orchestrator.BeginExternalWrite("checkout");

        window.Dispose();
        window.Dispose();

        Assert.False(harness.Orchestrator.IsExternalWriteInProgress);
    }

    // ---------------------------------------------------------------- adopting what git left

    [Fact]
    public async Task The_state_git_left_is_adopted_rather_than_synced()
    {
        // Both sides are whatever the new commit says, and they agree with each other because
        // the commit gate refuses to record one without the other. Nothing needs exporting;
        // what needs to happen is that the engine stops believing the previous commit's hashes.
        var harness = Build();

        harness.SetProject("what the new commit holds");
        harness.SetExportedText("the mirror from that same commit");

        var adopted = await harness.Orchestrator.AdoptExternalStateAsync(CancellationToken.None);

        Assert.Equal(SyncOrchestratorTests.BindingId, Assert.Single(adopted));

        // And the very next change is measured against the new state, not the old one.
        var outcome = await harness.Orchestrator.HandleChangeAsync(
            Change(ChangeSource.Project), CancellationToken.None);

        Assert.Equal(SyncVerdict.NoChange, outcome.Verdict);
    }

    [Fact]
    public async Task Adopting_twice_over_the_same_state_changes_nothing()
    {
        var harness = Build();

        harness.SetProject("v2");

        Assert.NotEmpty(await harness.Orchestrator.AdoptExternalStateAsync(CancellationToken.None));
        Assert.Empty(await harness.Orchestrator.AdoptExternalStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_binding_holding_a_lock_is_left_alone()
    {
        // Unsynced work is something the new commit does not contain. Adopting over it would
        // lose exactly what the direction lock exists to protect - so this refuses rather than
        // assuming the guard above the UI already prevented the checkout.
        var harness = Build();

        harness.SetProject("v2");

        await harness.Orchestrator.HandleChangeAsync(
            Change(ChangeSource.Project), CancellationToken.None);

        Assert.True(SyncStateMachine.HoldsLock(
            harness.Orchestrator.StateOf(SyncOrchestratorTests.BindingId)));

        harness.SetProject("something git checked out over the top");

        Assert.Empty(await harness.Orchestrator.AdoptExternalStateAsync(CancellationToken.None));

        Assert.Contains(harness.Log, e =>
            e.Message.Contains("baseline is left alone", StringComparison.Ordinal));
    }
}
