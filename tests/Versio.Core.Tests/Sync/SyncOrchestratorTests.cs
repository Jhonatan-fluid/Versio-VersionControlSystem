using Versio.Core.Abstractions;
using Versio.Core.Bindings;
using Versio.Core.Locking;
using Versio.Core.Sync;
using Versio.Core.Tests.Fakes;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// The scenario suite: the orchestrator wired to fakes, exercising the paths that could
/// lose someone's work.
/// </summary>
/// <remarks>
/// These are the tests the whole architecture was shaped to make possible. Every one of
/// them would otherwise require a real CODESYS run, a real crash, or a real reboot.
/// </remarks>
public class SyncOrchestratorTests
{
    private const string Root = @"C:\repo";
    private const string BindingId = "b-1";
    private static readonly DateTimeOffset Boot = new(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public FakeFileSystem Fs { get; } = new();
        public FakeCodesysRunner Codesys { get; } = new();
        public InMemoryLockStore Locks { get; } = new();
        public ProjectBinding Binding { get; }
        public SyncOrchestrator Orchestrator { get; private set; }

        public Harness(OrchestratorOptions? options = null, params ProjectBinding[] extra)
        {
            Binding = new ProjectBinding
            {
                Id = BindingId,
                ProjectPath = "plc/A.project",
                ExportPath = "plc/A",
                Profile = "CODESYS V3.5 SP22 Patch 2",
            };

            SetProject("v1");
            SetManifest("m1");

            Orchestrator = Build(options, extra);
        }

        public SyncOrchestrator Build(OrchestratorOptions? options = null,
                                      params ProjectBinding[] extra) =>
            Orchestrator = new SyncOrchestrator(
                Root, [Binding, .. extra],
                new LockOwner
                {
                    Machine = "TEST-PC", User = "tester", SessionId = "s1",
                    ProcessId = 4242, BootId = Boot,
                },
                Clock, Fs, Codesys, Locks,
                options ?? new OrchestratorOptions { RequireImportConfirmation = false });

        public void SetProject(string content) =>
            Fs.Write(Path.Combine(Root, "plc", "A.project"), content);

        public void SetManifest(string content) =>
            Fs.Write(Path.Combine(Root, "plc", "A", "_manifest.json"), content);

        public Task<SyncOutcome> ProjectChanged(bool synchronous = true) =>
            Orchestrator.HandleChangeAsync(new SyncJob
            {
                BindingId = BindingId, Source = ChangeSource.Project, Synchronous = synchronous,
            }, CancellationToken.None);

        public Task<SyncOutcome> TextChanged(bool confirmed = true, bool synchronous = true) =>
            Orchestrator.HandleChangeAsync(new SyncJob
            {
                BindingId = BindingId, Source = ChangeSource.ExportedText,
                Confirmed = confirmed, Synchronous = synchronous,
            }, CancellationToken.None);
    }

    // --- the happy path -------------------------------------------------------

    [Fact]
    public async Task A_project_save_exports_and_takes_the_project_direction()
    {
        var h = new Harness();
        h.Codesys.EnqueueSuccess("plc/A/Main.st");

        var outcome = await h.ProjectChanged();

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
        Assert.Equal(SyncDirection.ProjectAuthoritative, outcome.State);
        Assert.Equal(["plc/A/Main.st"], outcome.ChangedFiles);
        Assert.Single(h.Codesys.Jobs);
    }

    [Fact]
    public async Task The_export_job_carries_the_pinned_profile_and_absolute_paths()
    {
        var h = new Harness();
        await h.ProjectChanged();

        var job = Assert.Single(h.Codesys.Jobs);
        Assert.Equal("CODESYS V3.5 SP22 Patch 2", job.Profile);
        Assert.Equal(Path.Combine(Root, "plc", "A.project"), job.Projects[0].ProjectPath);
    }

    // --- loop defences --------------------------------------------------------

    [Fact]
    public async Task An_unchanged_project_does_NOT_launch_CODESYS()
    {
        // The hash gate. Cold start is 17-31 s; a no-op save must not pay it.
        var h = new Harness();
        await h.ProjectChanged();
        h.Codesys.Jobs.Clear();

        var outcome = await h.ProjectChanged();

        Assert.Equal(SyncVerdict.NoChange, outcome.Verdict);
        Assert.Empty(h.Codesys.Jobs);
    }

    [Fact]
    public async Task An_export_does_not_trigger_an_import_of_its_own_output()
    {
        // THE loop. Export writes into the tree the text watcher watches; if that echo were
        // treated as a user edit it would import, which would change the project, which
        // would export... forever.
        var h = new Harness();
        h.Codesys.Jobs.Clear();

        // Exporting updates the manifest, exactly as the real exporter does.
        h.Codesys.BeforeRun = _ =>
        {
            h.SetManifest("m2");
            return Task.CompletedTask;
        };

        await h.ProjectChanged();
        var jobsAfterExport = h.Codesys.Jobs.Count;

        var echo = await h.TextChanged();

        // No second CODESYS run: that is the loop not happening.
        Assert.Equal(jobsAfterExport, h.Codesys.Jobs.Count);

        // Any of the three defences swallowing it is a pass. In practice the HASH GATE
        // fires first and returns NoChange - the manifest on disk is exactly what the
        // export just wrote - which is the strongest of the three because it is derived
        // from content and needs no memory of what we did.
        Assert.True(
            echo.Verdict is SyncVerdict.NoChange
                or SyncVerdict.SelfWriteSuppressed
                or SyncVerdict.BlockedByLock,
            $"expected the echo to be swallowed, got {echo.Verdict}: {echo.Explanation}");
    }

    [Fact]
    public async Task Text_edits_are_refused_while_the_project_is_authoritative()
    {
        var h = new Harness();
        await h.ProjectChanged();

        h.SetManifest("edited-by-hand");
        var outcome = await h.TextChanged();

        Assert.Equal(SyncVerdict.BlockedByLock, outcome.Verdict);
        Assert.Contains("push or discard", outcome.Explanation);
        Assert.Equal(SyncDirection.ProjectAuthoritative, h.Orchestrator.StateOf(BindingId));
    }

    [Fact]
    public async Task The_circuit_breaker_halts_a_runaway_and_stays_halted()
    {
        // The seatbelt: even if every other defence were defeated, this stops the churn.
        var h = new Harness(new OrchestratorOptions
        {
            RequireImportConfirmation = false,
            CircuitBreakerThreshold = 3,
        });

        for (var i = 0; i < 6; i++)
        {
            h.SetProject($"v{i + 2}");
            await h.ProjectChanged();
        }

        Assert.True(h.Orchestrator.Breaker.IsTripped(BindingId));

        h.SetProject("v-later");
        var outcome = await h.ProjectChanged();

        Assert.Equal(SyncVerdict.BlockedByCircuitBreaker, outcome.Verdict);

        // Waiting it out must not silently re-arm.
        h.Clock.AdvanceSeconds(600);
        Assert.Equal(SyncVerdict.BlockedByCircuitBreaker, (await h.ProjectChanged()).Verdict);
    }

    // --- failure handling -----------------------------------------------------

    [Fact]
    public async Task A_failed_export_enters_the_Failed_state_and_blocks_further_work()
    {
        var h = new Harness();
        h.Codesys.EnqueueFailure("CODESYS refused to open the project");

        var failure = await h.ProjectChanged();

        Assert.Equal(SyncVerdict.Failed, failure.Verdict);
        Assert.Contains("refused to open", failure.Explanation);
        Assert.Equal(SyncDirection.Failed, h.Orchestrator.StateOf(BindingId));

        // Silently resuming after a failure is how half-written state becomes permanent.
        h.SetProject("v3");
        Assert.Equal(SyncVerdict.BlockedByLock, (await h.ProjectChanged()).Verdict);
    }

    [Fact]
    public async Task Acknowledging_a_failure_clears_it_and_resets_the_breaker()
    {
        var h = new Harness();
        h.Codesys.EnqueueFailure();
        await h.ProjectChanged();

        await h.Orchestrator.AcknowledgeFailureAsync(BindingId, CancellationToken.None);

        Assert.Equal(SyncDirection.Clean, h.Orchestrator.StateOf(BindingId));
        Assert.False(h.Orchestrator.Breaker.IsTripped(BindingId));
    }

    [Fact]
    public async Task A_timeout_is_a_failure_not_a_success()
    {
        var h = new Harness();
        h.Codesys.EnqueueTimeout();

        var outcome = await h.ProjectChanged();

        Assert.Equal(SyncVerdict.Failed, outcome.Verdict);
        Assert.Contains("timeout", outcome.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // --- release ---------------------------------------------------------------

    [Fact]
    public async Task Push_releases_the_lock_but_a_completed_export_does_not()
    {
        var h = new Harness();
        await h.ProjectChanged();
        Assert.Equal(SyncDirection.ProjectAuthoritative, h.Orchestrator.StateOf(BindingId));

        await h.Orchestrator.ReleaseAfterPushAsync("abc123", CancellationToken.None);

        Assert.Equal(SyncDirection.Clean, h.Orchestrator.StateOf(BindingId));
        Assert.False(h.Orchestrator.AnyLockHeld);
    }

    [Fact]
    public async Task Discard_releases_the_lock()
    {
        var h = new Harness();
        await h.ProjectChanged();

        await h.Orchestrator.ReleaseAfterDiscardAsync(BindingId, CancellationToken.None);

        Assert.Equal(SyncDirection.Clean, h.Orchestrator.StateOf(BindingId));
    }

    // --- crash and reboot ------------------------------------------------------

    [Fact]
    public async Task State_survives_a_crash_and_the_lock_is_re_adopted()
    {
        // "Crash" = throw away the orchestrator, keep the lock store. The work is still
        // unsynced, so the lock must come back rather than being cleared.
        var h = new Harness();
        await h.ProjectChanged();
        h.SetProject("changed-since-export");

        h.Build();     // fresh orchestrator, same store
        var policy = new LockRecoveryPolicy(
            new FakeMachineIdentity(Boot), new FakeProcessProbe(alive: false));

        var decisions = await h.Orchestrator.RecoverAsync(policy, CancellationToken.None);

        Assert.Equal(LockRecovery.ReadoptedAfterRestart,
            Assert.Single(decisions, d => d.BindingId == BindingId).Outcome);
        Assert.Equal(SyncDirection.ProjectAuthoritative, h.Orchestrator.StateOf(BindingId));
    }

    [Fact]
    public async Task A_reboot_with_externally_reverted_content_releases_the_stale_lock()
    {
        // The scenario: work was exported and pushed, then more work was done, then someone
        // reverted it outside Versio (a git checkout, an undo in CODESYS) and the machine
        // rebooted. The lock now guards nothing, so it must be released rather than
        // re-adopted - otherwise it would block the user forever with no way to see why.
        var h = new Harness();

        await h.ProjectChanged();
        await h.Orchestrator.ReleaseAfterPushAsync("c1", CancellationToken.None);
        var pushedProject = "v1";                    // the state recorded in the baseline

        h.SetProject("v2-work-in-progress");
        await h.ProjectChanged();
        Assert.Equal(SyncDirection.ProjectAuthoritative, h.Orchestrator.StateOf(BindingId));

        h.SetProject(pushedProject);                 // reverted outside Versio

        h.Build();
        var policy = new LockRecoveryPolicy(
            new FakeMachineIdentity(Boot.AddDays(1)), new FakeProcessProbe(alive: false));

        var decisions = await h.Orchestrator.RecoverAsync(policy, CancellationToken.None);

        Assert.Equal(LockRecovery.ReleasedStaleLock,
            Assert.Single(decisions, d => d.BindingId == BindingId).Outcome);
        Assert.Equal(SyncDirection.Clean, h.Orchestrator.StateOf(BindingId));
    }

    [Fact]
    public async Task An_exported_but_UNPUSHED_change_keeps_its_lock_across_a_reboot()
    {
        // The distinction that made the baseline/last-synced split necessary. Exporting is
        // not publishing: until the work is pushed the lock still guards it, even though
        // the mirror is perfectly up to date.
        var h = new Harness();
        await h.ProjectChanged();

        h.Build();
        var policy = new LockRecoveryPolicy(
            new FakeMachineIdentity(Boot.AddDays(1)), new FakeProcessProbe(alive: false));

        var decisions = await h.Orchestrator.RecoverAsync(policy, CancellationToken.None);

        Assert.Equal(LockRecovery.ReadoptedAfterRestart,
            Assert.Single(decisions, d => d.BindingId == BindingId).Outcome);
        Assert.Equal(SyncDirection.ProjectAuthoritative, h.Orchestrator.StateOf(BindingId));
    }

    [Fact]
    public async Task A_live_owning_session_is_not_stolen_from()
    {
        var h = new Harness();
        await h.ProjectChanged();
        h.SetProject("still-dirty");

        h.Build();
        var policy = new LockRecoveryPolicy(
            new FakeMachineIdentity(Boot), new FakeProcessProbe(alive: true));

        var decisions = await h.Orchestrator.RecoverAsync(policy, CancellationToken.None);

        Assert.Equal(LockRecovery.HeldByLiveSession,
            Assert.Single(decisions, d => d.BindingId == BindingId).Outcome);
    }

    [Fact]
    public async Task Every_state_change_is_persisted_so_nothing_is_lost_on_a_kill()
    {
        var h = new Harness();
        await h.ProjectChanged();

        Assert.True(h.Locks.Writes > 0);
        Assert.Equal(SyncDirection.ProjectAuthoritative,
            h.Locks.State!.Bindings[BindingId].Direction);
    }

    // --- the commit gate --------------------------------------------------------

    [Fact]
    public async Task The_commit_gate_blocks_a_stale_export_and_the_export_clears_it()
    {
        var h = new Harness();
        await h.ProjectChanged();
        await h.Orchestrator.ReleaseAfterPushAsync("c1", CancellationToken.None);

        h.SetProject("saved-but-not-exported");

        var gate = await h.Orchestrator.EvaluateCommitGateAsync(CancellationToken.None);
        Assert.Equal(GateDecision.ExportRequired, gate.Decision);
        Assert.False(gate.CanCommit);

        var outcomes = await h.Orchestrator.SatisfyCommitGateAsync(CancellationToken.None);
        Assert.Equal(SyncVerdict.Completed, Assert.Single(outcomes).Verdict);

        var after = await h.Orchestrator.EvaluateCommitGateAsync(CancellationToken.None);
        Assert.True(after.CanCommit);
    }

    [Fact]
    public async Task A_fresh_export_lets_the_commit_through_without_launching_CODESYS()
    {
        var h = new Harness();
        await h.ProjectChanged();
        await h.Orchestrator.ReleaseAfterPushAsync("c1", CancellationToken.None);
        h.Codesys.Jobs.Clear();

        var gate = await h.Orchestrator.EvaluateCommitGateAsync(CancellationToken.None);
        await h.Orchestrator.SatisfyCommitGateAsync(CancellationToken.None);

        Assert.True(gate.CanCommit);
        Assert.Empty(h.Codesys.Jobs);
    }

    // --- import ------------------------------------------------------------------

    [Fact]
    public async Task An_unconfirmed_import_is_detected_but_not_applied()
    {
        // Import is the strictly more dangerous direction, so it is offered, never assumed.
        var h = new Harness(new OrchestratorOptions { RequireImportConfirmation = true });
        h.SetManifest("edited-by-a-teammate");

        var outcome = await h.TextChanged(confirmed: false);

        Assert.Equal(SyncVerdict.AwaitingConfirmation, outcome.Verdict);
        Assert.Empty(h.Codesys.Jobs);
        Assert.Equal(SyncDirection.Clean, h.Orchestrator.StateOf(BindingId));
    }

    [Fact]
    public async Task A_confirmed_import_runs_and_takes_the_text_direction()
    {
        var h = new Harness(new OrchestratorOptions { RequireImportConfirmation = true });
        h.SetManifest("edited-by-a-teammate");

        var outcome = await h.TextChanged(confirmed: true);

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
        Assert.Equal(SyncDirection.TextAuthoritative, outcome.State);
        Assert.Equal(Versio.Core.Plc.Codesys.CodesysOperation.Import,
            Assert.Single(h.Codesys.Jobs).Operation);
    }

    [Fact]
    public async Task A_project_save_is_refused_while_the_text_is_authoritative()
    {
        var h = new Harness();
        h.SetManifest("edited");
        await h.TextChanged();

        h.SetProject("saved-in-codesys-too");
        var outcome = await h.ProjectChanged();

        Assert.Equal(SyncVerdict.BlockedByLock, outcome.Verdict);
    }

    // --- multiple bindings --------------------------------------------------------

    [Fact]
    public async Task Bindings_hold_their_locks_independently()
    {
        var second = new ProjectBinding
        {
            Id = "b-2", ProjectPath = "plc/B.project", ExportPath = "plc/B",
            Profile = "CODESYS V3.5 SP22 Patch 2",
        };

        var h = new Harness();
        h.Fs.Write(Path.Combine(Root, "plc", "B.project"), "b-v1");
        h.Build(null, second);

        await h.ProjectChanged();

        Assert.Equal(SyncDirection.ProjectAuthoritative, h.Orchestrator.StateOf("b-1"));
        Assert.Equal(SyncDirection.Clean, h.Orchestrator.StateOf("b-2"));
    }

    [Fact]
    public async Task An_unknown_binding_is_reported_not_silently_ignored()
    {
        var h = new Harness();

        var outcome = await h.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = "nope", Source = ChangeSource.Project },
            CancellationToken.None);

        Assert.Equal(SyncVerdict.UnknownBinding, outcome.Verdict);
    }

    // --- coalescing ------------------------------------------------------------------

    [Fact]
    public async Task A_save_during_a_running_export_is_coalesced_rather_than_queued_twice()
    {
        var h = new Harness();
        var reentered = new List<SyncOutcome>();

        h.Codesys.BeforeRun = async _ =>
        {
            h.Clock.AdvanceSeconds(10);
            h.SetProject("saved-again-mid-export");
            reentered.Add(await h.ProjectChanged(synchronous: false));
            reentered.Add(await h.ProjectChanged(synchronous: false));
        };

        await h.ProjectChanged(synchronous: false);

        Assert.Equal(2, reentered.Count);
        Assert.All(reentered, o => Assert.Equal(SyncVerdict.Coalesced, o.Verdict));
    }
}
