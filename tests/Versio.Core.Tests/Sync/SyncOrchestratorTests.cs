using Versio.Core.Abstractions;
using Versio.Core.Bindings;
using Versio.Core.Locking;
using Versio.Core.Logging;
using Versio.Core.Plc;
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
    /// <summary>
    /// Shared with the other suites built on this harness — see <c>ConflictGateTests</c>.
    /// </summary>
    internal const string BindingId = "b-1";
    private static readonly DateTimeOffset Boot = new(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The orchestrator wired to fakes, shared rather than copied.
    /// </summary>
    /// <remarks>
    /// Internal because a second suite grew that needs exactly this arrangement, and two
    /// harnesses drifting apart is how one of them ends up testing a configuration nothing
    /// ships. Still nested here: this is the file that owns it.
    /// </remarks>
    internal sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public FakeFileSystem Fs { get; } = new();
        public FakeCodesysRunner Codesys { get; } = new();
        public InMemoryLockStore Locks { get; } = new();
        public ProjectBinding Binding { get; }
        public SyncOrchestrator Orchestrator { get; private set; }

        /// <summary>The real logger, so what the orchestrator says can be asserted on
        /// rather than assumed.</summary>
        public InMemoryLogSink Sink { get; } = new();

        public IReadOnlyList<LogEvent> Log => Sink.Snapshot();

        /// <summary>Substitute logger, for the test that needs to open a correlation scope
        /// around the orchestrator from outside it.</summary>
        public IVersioLogger? LoggerOverride { get; set; }

        /// <summary>What the IDE is doing with the project, as the engine sees it.</summary>
        public StubOccupancy Occupancy { get; } = new();

        /// <summary>What git says is unmerged. Null until a test cares, which keeps the
        /// engine's default — nothing is ever conflicted — under test as well.</summary>
        public IConflictProbe? Conflicts { get; set; }

        /// <summary>
        /// Which vendor format claims a path, as the engine sees it.
        /// </summary>
        /// <remarks>
        /// Null leaves the engine on <c>PlcFormats.ForPath</c>, so every existing scenario is
        /// unchanged. Set it to make a test independent of a static, process-wide registry that
        /// any other test class can write to while this one runs — which has already caused two
        /// pass-alone-fail-in-company failures this week.
        /// </remarks>
        public Func<string, IPlcProjectFormat?>? Formats { get; set; }

        /// <summary>
        /// Which runner owns a vendor. Null leaves the engine on the single
        /// <see cref="Codesys"/> runner, so every existing scenario is unchanged.
        /// </summary>
        public Func<PlcVendor, IPlcRunner>? RunnerFor { get; set; }

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
            SetExportedText("v1");

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
                options ?? new OrchestratorOptions { RequireImportConfirmation = false },
                logger: LoggerOverride ?? new VersioLogger(Clock).AddSink(Sink),
                occupancy: Occupancy,
                conflicts: Conflicts,
                formats: Formats,
                runnerFor: RunnerFor);

        public void SetProject(string content) =>
            Fs.Write(Path.Combine(Root, "plc", "A.project"), content);

        public void SetManifest(string content) =>
            Fs.Write(Path.Combine(Root, "plc", "A", "_manifest.json"), content);

        /// <summary>
        /// Edit an exported source file — what a change to the mirror actually is.
        /// </summary>
        /// <remarks>
        /// These scenarios used to simulate a text edit by rewriting <c>_manifest.json</c>,
        /// which is the one file in the tree no human ever touches: it is written by an
        /// export and by nothing else. The suite therefore passed while the orchestrator was
        /// incapable of noticing a real edit, because both sides were agreeing about a signal
        /// that does not occur in the field.
        /// </remarks>
        public void SetExportedText(string content, string file = "Simulation.st") =>
            Fs.Write(Path.Combine(Root, "plc", "A", file), content);

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
            h.SetExportedText("edited in the editor");
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

    // --- unattended, both directions ------------------------------------------
    //
    // Export and import both run without anybody present. That moves the entire burden of
    // "do not overwrite the work I just did" onto the direction lock, the hash gate and the
    // breaker - so the cases below are no longer defence in depth, they are the defence.

    [Fact]
    public async Task An_import_copies_the_project_aside_before_writing_to_it()
    {
        // Nobody is watching, and an export can always be regenerated while a damaged
        // project cannot. The copy is what makes unattended import defensible.
        var h = await SyncedHarness();
        h.SetExportedText("edited by hand");

        Assert.Equal(SyncVerdict.Completed, (await h.TextChanged()).Verdict);

        var backups = h.Fs.EnumerateFiles(Path.Combine(Root, ".versio", "local", "backups"))
            .ToList();

        Assert.NotEmpty(backups);
        Assert.Contains(backups, b => b.EndsWith("A.project", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_import_that_cannot_be_backed_up_does_not_run()
    {
        // The worst of both: the operation that can destroy the project, having skipped the
        // step that makes it recoverable.
        var h = await SyncedHarness();
        h.SetExportedText("edited by hand");
        h.Fs.FailCopyTimes = 99;

        var outcome = await h.TextChanged();

        Assert.Equal(SyncVerdict.Failed, outcome.Verdict);
        Assert.Contains("back up", outcome.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(h.Codesys.Jobs, j => j.Operation == PlcOperation.Import);
        Assert.Equal(SyncDirection.Failed, h.Orchestrator.StateOf(BindingId));
    }

    [Fact]
    public async Task An_automatic_export_does_not_provoke_an_automatic_import()
    {
        // The loop the whole design exists to avoid. An export writes into the tree the text
        // watcher is watching, so its own output arrives back as a text change; with both
        // directions automatic, a version that acted on it would sync forever.
        var h = new Harness(new OrchestratorOptions { RequireImportConfirmation = false });
        h.Codesys.BeforeRun = job =>
        {
            if (job.Operation == PlcOperation.Export)
            {
                h.SetExportedText("written by the export");
            }

            return Task.CompletedTask;
        };

        Assert.Equal(SyncVerdict.Completed, (await h.ProjectChanged()).Verdict);

        // The echo of our own export, arriving as a settled text change.
        var echo = await h.TextChanged();

        Assert.NotEqual(SyncVerdict.Completed, echo.Verdict);
        Assert.DoesNotContain(h.Codesys.Jobs, j => j.Operation == PlcOperation.Import);
    }

    [Fact]
    public async Task The_side_that_moved_first_wins_until_it_is_released()
    {
        // With both directions automatic this is the rule that stops one engineer's save
        // from being overwritten by the other side a moment later.
        var h = new Harness(new OrchestratorOptions { RequireImportConfirmation = false });

        await h.ProjectChanged();
        Assert.Equal(SyncDirection.ProjectAuthoritative, h.Orchestrator.StateOf(BindingId));

        h.SetExportedText("a teammate edited the mirror");
        var blocked = await h.TextChanged();

        Assert.Equal(SyncVerdict.BlockedByLock, blocked.Verdict);
        Assert.DoesNotContain(h.Codesys.Jobs, j => j.Operation == PlcOperation.Import);

        // Released by a push, and the other direction becomes available again.
        await h.Orchestrator.ReleaseAfterPushAsync("c1", CancellationToken.None);
        Assert.Equal(SyncDirection.Clean, h.Orchestrator.StateOf(BindingId));
    }

    // --- the text side of the hash gate ---------------------------------------
    //
    // These exist because the suite was, for a while, incapable of failing here: a text edit
    // was simulated by rewriting _manifest.json, the one file in the tree that only an export
    // ever writes. The orchestrator hashed that same file to decide whether anything had
    // changed, so both sides agreed on a signal that never occurs in the field.

    [Fact]
    public async Task An_edited_source_file_is_detected_although_the_manifest_is_untouched()
    {
        // From a SYNCED harness, not a fresh one. A fresh binding has never recorded its
        // mirror, so it reports a change whatever the gate does — this assertion passed
        // against the broken gate for that reason alone, which is the same vacuous pass the
        // suite was already carrying one level up.
        var h = await SyncedHarness();

        // Exactly what an engineer does: open a .st, change a line, save. The manifest is not
        // theirs to write and they never touch it.
        h.SetExportedText("PROGRAM Simulation VAR x : INT := 42; END_VAR");
        var outcome = await h.TextChanged();

        Assert.NotEqual(SyncVerdict.NoChange, outcome.Verdict);
        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
        Assert.Equal(SyncDirection.TextAuthoritative, h.Orchestrator.StateOf(BindingId));
    }

    [Fact]
    public async Task A_new_file_in_the_mirror_is_a_change()
    {
        // Creating an object by adding a file is a supported workflow, not an edge case.
        var h = await SyncedHarness();
        h.SetExportedText("FUNCTION_BLOCK NewThing END_FUNCTION_BLOCK", "NewThing.st");

        Assert.Equal(SyncVerdict.Completed, (await h.TextChanged()).Verdict);
    }

    [Fact]
    public async Task An_untouched_mirror_is_still_no_change()
    {
        // The other half of the gate. If everything now looks changed the gate is useless in
        // the opposite direction, and every save would launch CODESYS for nothing.
        var h = await SyncedHarness();

        Assert.Equal(SyncVerdict.NoChange, (await h.TextChanged()).Verdict);
    }

    [Fact]
    public async Task Rewriting_a_file_with_identical_content_is_not_a_change()
    {
        // Editors routinely rewrite a file on save without altering a byte.
        var h = await SyncedHarness();
        h.SetExportedText("exported");

        Assert.Equal(SyncVerdict.NoChange, (await h.TextChanged()).Verdict);
    }

    [Fact]
    public async Task The_project_file_beside_the_mirror_is_not_part_of_it()
    {
        // plc/A.project sits next to the export tree plc/A. A prefix match pulls it in, and
        // every project save then looks like a text edit too — the orchestrator would see
        // both sides move at once and manufacture a conflict out of nothing.
        var h = await SyncedHarness();
        h.SetProject("v2");

        Assert.Equal(SyncVerdict.NoChange, (await h.TextChanged()).Verdict);
    }

    /// <summary>
    /// A harness that has exported once and been pushed — so the mirror is recorded and the
    /// direction lock is clean.
    /// </summary>
    /// <remarks>
    /// A fresh binding is deliberately <b>not</b> in this state: its mirror has never been
    /// recorded, so "the mirror differs from what was last synced" is true and reporting a
    /// change is correct. The no-change assertions need a real baseline to be about anything.
    /// </remarks>
    private static async Task<Harness> SyncedHarness()
    {
        var h = new Harness();

        // The real export writes the mirror; the orchestrator hashes the tree afterwards to
        // record what it synced. A fake that skips the write would record a hash of the old
        // tree and the very next check would look like somebody had edited it.
        h.Codesys.BeforeRun = job =>
        {
            if (job.Operation == PlcOperation.Export)
            {
                h.SetExportedText("exported");
            }

            return Task.CompletedTask;
        };

        await h.ProjectChanged();
        await h.Orchestrator.ReleaseAfterPushAsync("c1", CancellationToken.None);

        Assert.Equal(SyncDirection.Clean, h.Orchestrator.StateOf(BindingId));
        return h;
    }

    [Fact]
    public async Task Text_edits_are_refused_while_the_project_is_authoritative()
    {
        var h = new Harness();
        await h.ProjectChanged();

        h.SetExportedText("edited by hand");
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
    public async Task Recovery_that_releases_a_lock_still_remembers_what_was_exported()
    {
        // The bug this exists for cost a full toolchain run on every single launch.
        //
        // Recovery that does not re-adopt used to hand back a BLANK lock, which threw away
        // LastSynced along with the direction. The commit gate compares the project's hash
        // against LastSynced.ProjectHash, so an empty one reads as "the project has changed
        // since it was last exported" — for a project nobody had touched. Every open ran
        // CODESYS for 17–31 s to rebuild a mirror that was already correct, then left the
        // binding holding a fresh lock, so the panel reported "project ahead" until somebody
        // pushed.
        //
        // Releasing the DIRECTION and forgetting WHAT WAS EXPORTED are different facts, and
        // only the first one is recovery's business.
        var h = new Harness();

        await h.ProjectChanged();                    // exports; records LastSynced
        await h.Orchestrator.ReleaseAfterPushAsync("c1", CancellationToken.None);

        h.Build();                                   // restart, same store
        var policy = new LockRecoveryPolicy(
            new FakeMachineIdentity(Boot.AddDays(1)), new FakeProcessProbe(alive: false));

        await h.Orchestrator.RecoverAsync(policy, CancellationToken.None);

        // Nothing was held, so nothing is re-adopted — that part was always right.
        Assert.Equal(SyncDirection.Clean, h.Orchestrator.StateOf(BindingId));

        // And the gate must not ask for an export of a project that has not changed.
        var gate = await h.Orchestrator.EvaluateCommitGateAsync(CancellationToken.None);

        Assert.Equal(GateDecision.Proceed, gate.Decision);
        Assert.Empty(gate.StaleBindings);
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
        h.SetExportedText("edited by a teammate");

        var outcome = await h.TextChanged(confirmed: false);

        Assert.Equal(SyncVerdict.AwaitingConfirmation, outcome.Verdict);
        Assert.Empty(h.Codesys.Jobs);
        Assert.Equal(SyncDirection.Clean, h.Orchestrator.StateOf(BindingId));
    }

    [Fact]
    public async Task A_confirmed_import_runs_and_takes_the_text_direction()
    {
        var h = new Harness(new OrchestratorOptions { RequireImportConfirmation = true });
        h.SetExportedText("edited by a teammate");

        var outcome = await h.TextChanged(confirmed: true);

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
        Assert.Equal(SyncDirection.TextAuthoritative, outcome.State);
        Assert.Equal(Versio.Core.Plc.PlcOperation.Import,
            Assert.Single(h.Codesys.Jobs).Operation);
    }

    [Fact]
    public async Task A_project_save_is_refused_while_the_text_is_authoritative()
    {
        var h = new Harness();
        h.SetExportedText("edited");
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
    public async Task A_binding_runs_on_its_own_vendor_runner()
    {
        // Proven by mutation: if RunnerFor is ignored and every job hits the constructor's
        // single runner, this is the test that goes red. Two stubs, two vendors, one export
        // each — the second vendor's job must not appear on the first stub.
        var other = new FakeCodesysRunner();
        other.EnqueueSuccess("plc/B/Main.scl");

        var second = new ProjectBinding
        {
            Id = "b-2",
            ProjectPath = "plc/B.ap21",
            ExportPath = "plc/B",
            Vendor = PlcVendor.TiaPortal,
            Profile = "V21",
        };

        var h = new Harness();
        h.RunnerFor = v => v == PlcVendor.TiaPortal ? other : h.Codesys;
        h.Fs.Write(Path.Combine(Root, "plc", "B.ap21"), "pointer");
        h.Fs.Write(Path.Combine(Root, "plc", "B", "Main.scl"), "old");
        h.Build(null, second);

        var outcome = await h.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = "b-2", Source = ChangeSource.Project, Synchronous = true },
            CancellationToken.None);

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
        Assert.Single(other.Jobs);
        Assert.Empty(h.Codesys.Jobs);
        Assert.Equal("b-2", other.Jobs[0].Projects[0].BindingId);
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

    // --- the log is part of the contract --------------------------------------

    [Fact]
    public async Task Every_refusal_says_why_in_the_log()
    {
        // A refusal produces no visible effect by design: the user saves and nothing
        // happens. If it is not in the log there is no way to find out why, which is why
        // every refusal is built by Refuse() rather than by SyncOutcome.Refused directly.
        var h = new Harness();
        h.Codesys.EnqueueSuccess("plc/A/Main.st");
        await h.ProjectChanged();
        h.Sink.Clear();

        // Same content as the last sync: the hash gate refuses it.
        var outcome = await h.ProjectChanged();

        Assert.Equal(SyncVerdict.NoChange, outcome.Verdict);
        Assert.Contains(h.Log, e =>
            e.Category == LogCategory.Sync &&
            e.Message.Contains(nameof(SyncVerdict.NoChange), StringComparison.Ordinal) &&
            e.Message.Contains(BindingId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refusal_carries_the_same_explanation_the_user_is_shown()
    {
        // The log and the UI must not drift apart: a support log that explains the refusal
        // differently from the message on screen is worse than no log.
        var h = new Harness();
        h.Codesys.EnqueueSuccess("plc/A/Main.st");
        await h.ProjectChanged();
        h.Sink.Clear();

        var outcome = await h.ProjectChanged();

        Assert.Contains(h.Log, e => e.Message.Contains(outcome.Explanation, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_blocked_direction_is_logged_as_a_warning_not_buried_at_debug()
    {
        // This is the refusal users actually hit and are most confused by, so it has to be
        // visible at the level they will be reading.
        var h = new Harness();
        h.Codesys.EnqueueSuccess("plc/A/Main.st");
        await h.ProjectChanged();               // takes the project direction
        h.SetExportedText("edited in the editor");                    // now edit the text side
        h.Sink.Clear();

        var outcome = await h.TextChanged();

        Assert.Equal(SyncVerdict.BlockedByLock, outcome.Verdict);
        Assert.Contains(h.Log, e =>
            e.Level == LogLevel.Warning && e.Category == LogCategory.Sync);
    }

    [Fact]
    public async Task A_lock_transition_is_logged_with_both_states()
    {
        // "What took the lock, and when" is the first question after a blocked commit.
        var h = new Harness();
        h.Codesys.EnqueueSuccess("plc/A/Main.st");

        await h.ProjectChanged();

        Assert.Contains(h.Log, e =>
            e.Category == LogCategory.Lock &&
            e.Message.Contains(nameof(SyncDirection.Clean), StringComparison.Ordinal) &&
            e.Message.Contains(nameof(SyncDirection.ProjectAuthoritative), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_export_is_logged_at_error_with_the_reason()
    {
        var h = new Harness();
        h.Codesys.EnqueueFailure("CODESYS exited 1");
        h.Sink.Clear();

        var outcome = await h.ProjectChanged();

        Assert.Equal(SyncVerdict.Failed, outcome.Verdict);
        Assert.Contains(h.Log, e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains(outcome.Explanation, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Everything_logged_for_one_job_shares_a_correlation_id()
    {
        // Filtering to a single id is how "why did that export do that" is answered in
        // seconds. It only works if the whole job really does carry one id.
        var h = new Harness();
        h.Codesys.EnqueueSuccess("plc/A/Main.st");

        // The constructor logs what it is tracking before any operation exists, so those
        // lines correctly carry no id. The job starts here.
        h.Sink.Clear();

        await h.ProjectChanged();

        var ids = h.Log.Select(e => e.OperationId).Distinct().ToList();
        Assert.Single(ids);
        Assert.NotNull(ids[0]);
    }

    [Fact]
    public async Task A_job_inside_a_caller_scope_joins_it_rather_than_starting_a_new_one()
    {
        // Called from a button press the orchestrator should inherit that click's id, so
        // the UI action and the work it caused read as one operation.
        var h = new Harness();
        h.Codesys.EnqueueSuccess("plc/A/Main.st");
        var logger = new VersioLogger(h.Clock).AddSink(h.Sink);
        h.LoggerOverride = logger;
        h.Build();
        h.Sink.Clear();

        using (logger.BeginOperation("click123"))
        {
            await h.ProjectChanged();
        }

        Assert.All(h.Log, e => Assert.Equal("click123", e.OperationId));
    }

    /// <summary>An IDE that is holding the project, or is not, as the test requires.</summary>
    internal sealed class StubOccupancy : IProjectOccupancyProbe
    {
        public ProjectOccupancy Answer { get; set; } = ProjectOccupancy.Free;

        public string? AskedAbout { get; private set; }

        public Task<ProjectOccupancy> CheckAsync(
            ProjectBinding binding, string absoluteProjectPath, CancellationToken ct)
        {
            AskedAbout = absoluteProjectPath;
            return Task.FromResult(Answer);
        }

        public void Hold(string user = "elli_", string machine = "ROG-JVM") =>
            Answer = new ProjectOccupancy
            {
                Verdict = OccupancyVerdict.Held,
                User = user,
                Machine = machine,
                ProcessId = 45812,
            };
    }

    // --- the project is open in the IDE -------------------------------------------

    [Fact]
    public async Task An_import_waits_while_the_IDE_has_the_project_rather_than_failing()
    {
        // The behaviour this whole feature exists for. Somebody saving and leaving CODESYS
        // open is the most common working state there is; treating it as a failure marks the
        // binding failed and stops all syncing until a button is pressed, again and again.
        var h = new Harness();
        h.Occupancy.Hold();
        h.SetExportedText("edited by hand");

        var outcome = await h.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = BindingId, Source = ChangeSource.ExportedText,
                          Confirmed = true, Synchronous = true },
            CancellationToken.None);

        Assert.Equal(SyncVerdict.BlockedByOpenProject, outcome.Verdict);

        // Not a failure: no failed binding, so nothing has to be acknowledged before the
        // next attempt, and IsFailure stays false so callers do not report an error.
        Assert.False(outcome.IsFailure);
        Assert.NotEqual(SyncDirection.Failed, h.Orchestrator.StateOf(BindingId));

        // And nothing ran.
        Assert.Empty(h.Codesys.Jobs);

        // The sentence names who has it and what to do, because "blocked" without a remedy
        // is the message that generates the support call.
        Assert.Contains("open in the IDE", SyncNarrative.Describe(outcome), StringComparison.Ordinal);
        Assert.Contains("elli_ on ROG-JVM", SyncNarrative.Describe(outcome), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Waiting_for_the_IDE_does_not_feed_the_circuit_breaker()
    {
        // A breaker that counted these would trip after a few saves and halt the project for
        // the rest of the session - turning a wait into exactly the sticky failure this
        // verdict was introduced to avoid.
        var h = new Harness(new OrchestratorOptions
        {
            RequireImportConfirmation = false,
            CircuitBreakerThreshold = 2,
        });

        h.Occupancy.Hold();

        for (var i = 0; i < 6; i++)
        {
            h.SetExportedText($"edit {i}");
            var outcome = await h.Orchestrator.HandleChangeAsync(
                new SyncJob { BindingId = BindingId, Source = ChangeSource.ExportedText,
                              Confirmed = true, Synchronous = true },
                CancellationToken.None);

            Assert.Equal(SyncVerdict.BlockedByOpenProject, outcome.Verdict);
        }

        // Let go, and the next one runs as though nothing had happened.
        h.Occupancy.Answer = ProjectOccupancy.Free;
        h.SetExportedText("edit after closing");

        var after = await h.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = BindingId, Source = ChangeSource.ExportedText,
                          Confirmed = true, Synchronous = true },
            CancellationToken.None);

        Assert.Equal(SyncVerdict.Completed, after.Verdict);
    }

    [Fact]
    public async Task An_export_reads_a_copy_when_the_IDE_has_the_original_open()
    {
        // Export only reads, and the vendor lock is a sidecar on the path - so a copy at
        // another path is unencumbered and exports byte-identically. Measured against real
        // CODESYS; see the finding in STATUS.
        var h = new Harness();
        h.Occupancy.Hold();
        h.SetProject("v2");

        var outcome = await h.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = BindingId, Source = ChangeSource.Project,
                          Synchronous = true },
            CancellationToken.None);

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);

        var job = Assert.Single(h.Codesys.Jobs);
        var project = Assert.Single(job.Projects);

        // It read the copy, not the original.
        Assert.Contains(Path.Combine(".versio", "local", "open", BindingId),
                        project.ProjectPath, StringComparison.OrdinalIgnoreCase);

        // Keeping the project's own filename, because the manifest records the basename and
        // any other name would rewrite that field on every export while the IDE is open.
        Assert.Equal("A.project", Path.GetFileName(project.ProjectPath));

        // The mirror still goes where it always did.
        Assert.Equal(Path.Combine(Root, "plc", "A"), project.Path);

        // And the scratch copy does not survive the run.
        Assert.False(h.Fs.FileExists(project.ProjectPath));
    }

    [Fact]
    public async Task Two_held_exports_do_not_share_a_scratch_folder()
    {
        // The commit gate is synchronous and skips the coalescer, so a second
        // export can start while the first still holds `.versio/local/open/{id}/`.
        // Sharing that folder is how a successful export was followed by
        // ExportFailed when the first job deleted the copy. Each run gets its
        // own folder; putting them back in one folder makes these two paths equal.
        var h = new Harness();
        h.Occupancy.Hold();
        h.SetProject("v2");

        Assert.Equal(SyncVerdict.Completed, (await h.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = BindingId, Source = ChangeSource.Project,
                          Synchronous = true },
            CancellationToken.None)).Verdict);

        h.SetProject("v3");
        Assert.Equal(SyncVerdict.Completed, (await h.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = BindingId, Source = ChangeSource.Project,
                          Synchronous = true },
            CancellationToken.None)).Verdict);

        Assert.Equal(2, h.Codesys.Jobs.Count);
        var first = Path.GetDirectoryName(h.Codesys.Jobs[0].Projects[0].ProjectPath);
        var second = Path.GetDirectoryName(h.Codesys.Jobs[1].Projects[0].ProjectPath);
        Assert.NotEqual(first, second);
        Assert.Contains(Path.Combine(".versio", "local", "open", BindingId), first,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(".versio", "local", "open", BindingId), second,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_export_waits_when_the_format_cannot_copy_a_held_project()
    {
        // A format that opens the payload exclusively cannot export a copy the IDE
        // is not holding. Sync asks Capabilities, not a vendor name; the wait is
        // both directions, same verdict as an import.
        var h = new Harness();
        h.Formats = _ => ExclusiveOpenFormat.Instance;
        h.Build();
        h.Occupancy.Hold();
        h.SetProject("v2");

        var outcome = await h.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = BindingId, Source = ChangeSource.Project,
                          Synchronous = true },
            CancellationToken.None);

        Assert.Equal(SyncVerdict.BlockedByOpenProject, outcome.Verdict);
        Assert.False(outcome.IsFailure);
        Assert.Empty(h.Codesys.Jobs);
        Assert.Contains("cannot be exported", SyncNarrative.Describe(outcome), StringComparison.Ordinal);
        Assert.DoesNotContain("copied", string.Join(" ", h.Log.Select(e => e.Message)),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_export_reads_the_project_itself_when_nobody_has_it_open()
    {
        // The copy is for the awkward case only. Paying it always would double the IO on
        // every export for a situation that is usually not happening.
        var h = new Harness();
        h.SetProject("v2");

        await h.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = BindingId, Source = ChangeSource.Project,
                          Synchronous = true },
            CancellationToken.None);

        var project = Assert.Single(Assert.Single(h.Codesys.Jobs).Projects);

        Assert.Equal(Path.Combine(Root, "plc", "A.project"), project.ProjectPath);
        Assert.DoesNotContain("local", project.ProjectPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_stale_marker_left_by_a_crashed_IDE_does_not_block_anything()
    {
        // Resolved to Stale upstream by the probe, which is where the pid is checked. The
        // engine's part is simply to treat it as free - a crashed IDE must not halt sync
        // until somebody deletes a file they have never heard of.
        var h = new Harness();
        h.Occupancy.Answer = new ProjectOccupancy
        {
            Verdict = OccupancyVerdict.Stale,
            User = "elli_",
            Machine = "ROG-JVM",
            ProcessId = 45812,
        };

        h.SetProject("v2");

        var outcome = await h.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = BindingId, Source = ChangeSource.Project,
                          Synchronous = true },
            CancellationToken.None);

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);

        var project = Assert.Single(Assert.Single(h.Codesys.Jobs).Projects);
        Assert.Equal(Path.Combine(Root, "plc", "A.project"), project.ProjectPath);
    }

    [Fact]
    public async Task The_engine_asks_the_resolver_it_was_given_and_not_a_global_registry()
    {
        // SyncOrchestrator is the file the vendor rule exists to protect, and it used to call
        // the static PlcFormats.ForPath directly - a process-wide mutable registry whose
        // contents no caller could see and no test could set. The seam test could never notice:
        // a static call puts no vendor type in any signature.
        //
        // Handing a resolver in is what makes an orchestrator test independent of whatever else
        // has registered into that static. That is not hypothetical - two tests this week passed
        // alone and failed in company for exactly that reason.
        var asked = new List<string>();
        var h = new Harness();
        h.Formats = path =>
        {
            asked.Add(path);
            return null;   // nothing claims it, so no plan can be computed
        };
        h.Build();

        var plan = await h.Orchestrator.PlanImportAsync(BindingId, CancellationToken.None);

        // Null because THIS resolver said so, not because the registry happens to be empty:
        // CODESYS is registered in this assembly and would have claimed plc/A.project.
        Assert.Null(plan);
        Assert.Contains("plc/A.project", asked);
        Assert.NotNull(PlcFormats.ForPath("plc/A.project"));
    }

    /// <summary>
    /// A format that cannot export via a scratch copy while the IDE holds the project.
    /// </summary>
    /// <remarks>
    /// Stand-in, not a brand: Sync must wait because Capabilities says so, not because
    /// this test named a vendor.
    /// </remarks>
    private sealed class ExclusiveOpenFormat : IPlcProjectFormat
    {
        public static readonly ExclusiveOpenFormat Instance = new();

        public PlcVendor Vendor => new("ExclusiveOpen");

        public string DisplayName => "exclusive-open stand-in";

        public PlcCapabilities Capabilities => new()
        {
            TextualExport = true,
            TextualImport = true,
            ObjectCreation = true,
            Headless = true,
            ExportHeldViaCopy = false,
            TypicalStartupCost = TimeSpan.FromSeconds(1),
        };

        public IReadOnlyList<string> ProjectExtensions => [".project"];

        public bool IsTemplate(string path) => false;

        public string ConventionalExportPath(string projectPath) => projectPath;

        public string? OccupancyMarkerFor(string projectPath) => null;

        public IReadOnlyList<string> TransientPatternsFor(string projectPath) => [];

        public ProjectOccupancy ParseOccupancy(string markerContents) => ProjectOccupancy.Free;

        public bool NeedsTextToPlan(string relativePath) => false;

        public ImportPlan PlanImport(
            Versio.Core.Projects.ProjectManifest? manifest,
            IReadOnlyList<MirrorFile> mirror,
            IReadOnlyDictionary<string, string> departed) => new() { Files = [] };

        public Versio.Core.Projects.ObjectTypeName? DisplayNameFor(string type) => null;
    }
}
