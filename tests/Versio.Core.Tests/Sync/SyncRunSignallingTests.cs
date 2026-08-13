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
/// What the engine tells the outside world while it is working, and what the coalescer does
/// with the work it defers.
/// </summary>
/// <remarks>
/// <para>
/// All of this was reported as one symptom: <i>"I save, the project appears in the changed
/// files, and then nothing happens for half a minute."</i> The export was running the whole
/// time and the app had no way to say so — <c>Busy</c> was never begun on the automatic path,
/// the heartbeat was never started, and <c>ProjectRow.Busy</c> was documented and never
/// assigned. Tracing that turned up two further defects in the coalescer underneath it.
/// </para>
/// <para>
/// These tests are the layer that could have caught all three without a UI, which is the
/// point of keeping the engine free of one.
/// </para>
/// </remarks>
public class SyncRunSignallingTests
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
        public InMemoryLogSink Sink { get; } = new();
        public ProjectBinding Binding { get; }
        public SyncOrchestrator Orchestrator { get; }

        public List<SyncRunNotice> Started { get; } = [];
        public List<SyncRunNotice> Finished { get; } = [];

        public Harness(OrchestratorOptions? options = null)
        {
            Binding = new ProjectBinding
            {
                Id = BindingId,
                ProjectPath = "plc/A.project",
                ExportPath = "plc/A",
                Profile = "CODESYS V3.5 SP22 Patch 2",
            };

            SetProject("v1");
            Fs.Write(Path.Combine(Root, "plc", "A", "_manifest.json"), "m1");
            Fs.Write(Path.Combine(Root, "plc", "A", "Simulation.st"), "v1");

            Orchestrator = new SyncOrchestrator(
                Root, [Binding],
                new LockOwner
                {
                    Machine = "TEST-PC", User = "tester", SessionId = "s1",
                    ProcessId = 4242, BootId = Boot,
                },
                Clock, Fs, Codesys, Locks,
                options ?? new OrchestratorOptions { RequireImportConfirmation = false },
                logger: new VersioLogger(Clock).AddSink(Sink));

            Orchestrator.RunStarted += n => Started.Add(n);
            Orchestrator.RunFinished += n => Finished.Add(n);
        }

        public void SetProject(string content) =>
            Fs.Write(Path.Combine(Root, "plc", "A.project"), content);

        /// <summary>A save, as the watcher delivers one: never synchronous.</summary>
        public Task<SyncOutcome> Saved() =>
            Orchestrator.HandleChangeAsync(new SyncJob
            {
                BindingId = BindingId, Source = ChangeSource.Project, Synchronous = false,
            }, CancellationToken.None);
    }

    // --- announcing a run -----------------------------------------------------

    [Fact]
    public async Task A_run_is_announced_when_it_starts_and_when_it_ends()
    {
        var h = new Harness();
        h.Codesys.EnqueueSuccess("plc/A/Main.st");

        await h.Saved();

        var started = Assert.Single(h.Started);
        var finished = Assert.Single(h.Finished);

        Assert.Equal(BindingId, started.BindingId);
        Assert.Equal(PlcOperation.Export, started.Operation);
        Assert.Equal(started.OperationId, finished.OperationId);
    }

    [Fact]
    public async Task A_save_that_changed_nothing_announces_nothing()
    {
        // The reason this is a notification rather than something the caller wraps around
        // the call. Every settle that changes nothing would otherwise flash a progress bar,
        // and a no-op save is the single most common event in the tool's life.
        var h = new Harness();
        h.Codesys.EnqueueSuccess();
        await h.Saved();

        h.Started.Clear();
        h.Finished.Clear();

        var outcome = await h.Saved();

        Assert.Equal(SyncVerdict.NoChange, outcome.Verdict);
        Assert.Empty(h.Started);
        Assert.Empty(h.Finished);
    }

    [Fact]
    public async Task A_run_that_fails_is_still_announced_as_finished()
    {
        // A bar turned on by RunStarted and left on by a failure is indistinguishable from
        // a hang — which is the exact confusion this whole change exists to remove.
        var h = new Harness();
        h.Codesys.EnqueueFailure("the toolchain fell over");

        var outcome = await h.Saved();

        Assert.True(outcome.IsFailure);
        Assert.Single(h.Started);
        Assert.Single(h.Finished);
    }

    [Fact]
    public async Task A_run_that_throws_is_still_announced_as_finished()
    {
        var h = new Harness();
        h.Codesys.BeforeRun = _ => throw new InvalidOperationException("kaboom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Saved());

        Assert.Single(h.Started);
        Assert.Single(h.Finished);
    }

    // --- the operation id -----------------------------------------------------

    [Fact]
    public async Task The_job_carries_the_operation_id_the_run_was_announced_with()
    {
        // The defect this pins: PlcJob.OperationId defaulted to a fresh random value, so the
        // id the runner tagged its progress with had nothing to do with the id anything else
        // knew. A heartbeat keyed by the operation could never match a report, so every
        // phase and count the toolchain produced was dropped on the floor — on every path,
        // including the one that did start a heartbeat.
        var h = new Harness();
        h.Codesys.EnqueueSuccess();

        await h.Saved();

        var job = Assert.Single(h.Codesys.Jobs);
        var notice = Assert.Single(h.Started);

        Assert.Equal(notice.OperationId, job.OperationId);
        Assert.NotEmpty(job.OperationId);
    }

    [Fact]
    public async Task The_operation_id_also_correlates_the_log_lines_for_that_run()
    {
        var h = new Harness();
        h.Codesys.EnqueueSuccess();

        await h.Saved();

        var notice = Assert.Single(h.Started);
        Assert.Contains(h.Sink.Snapshot(), e => e.OperationId == notice.OperationId);
    }

    // --- the follow-up the coalescer queues -----------------------------------

    [Fact]
    public async Task A_save_arriving_during_an_export_gets_its_own_export_afterwards()
    {
        // The coalescer's stated contract, which nothing was honouring: OnJobCompleted
        // returns true to mean "a follow-up must start now", and its result was discarded.
        // The worked example in the plan is four saves during one export producing two
        // exports; it produced one, and the second was lost.
        //
        // The save is queued DURING the run and the file moves again as the run ends. Both
        // halves are needed, and the first draft of this test had only the first: a change
        // made mid-run is picked up by the export that is already going, so the follow-up
        // correctly found nothing to do and the test failed against working code. That is
        // worth keeping in mind before "fixing" it — the engine was right and the test was
        // describing a scenario that cannot happen.
        var h = new Harness(new OrchestratorOptions
        {
            RequireImportConfirmation = false,
            MinimumInterval = TimeSpan.Zero,
        });

        h.Codesys.EnqueueSuccess("plc/A/Main.st");
        h.Codesys.EnqueueSuccess("plc/A/Main.st");

        var firstRun = true;
        h.Codesys.BeforeRun = _ =>
        {
            if (!firstRun)
            {
                return Task.CompletedTask;
            }

            firstRun = false;
            h.Clock.Advance(TimeSpan.FromSeconds(20));
            h.SetProject("v2");
            return h.Saved();
        };

        // A save landing as the run ends, after the export has recorded what it captured.
        h.Orchestrator.RunFinished += _ =>
        {
            if (h.Codesys.Jobs.Count == 1)
            {
                h.SetProject("v3");
            }
        };

        await h.Saved();

        Assert.Equal(2, h.Codesys.Jobs.Count);
        Assert.Equal(2, h.Started.Count);
        Assert.Contains(h.Sink.Snapshot(),
            e => e.Message.Contains("arrived while the last run was in flight"));
    }

    [Fact]
    public async Task The_binding_still_syncs_after_a_burst_queued_a_follow_up()
    {
        // The nastier half of the same bug. OnJobCompleted marks the slot as running again
        // when it returns true, on the understanding that the caller is about to obey it.
        // Ignoring the result left the coalescer believing a job was running for the rest of
        // the session, so every later save answered Queue and then Coalesced — and nothing
        // noticed, because the commit gate is synchronous and skips the coalescer entirely.
        var h = new Harness(new OrchestratorOptions
        {
            RequireImportConfirmation = false,
            MinimumInterval = TimeSpan.Zero,
        });

        h.Codesys.EnqueueSuccess();
        h.Codesys.EnqueueSuccess();
        h.Codesys.EnqueueSuccess();

        var firstRun = true;
        h.Codesys.BeforeRun = _ =>
        {
            if (!firstRun)
            {
                return Task.CompletedTask;
            }

            firstRun = false;
            h.SetProject("v2");
            h.Clock.Advance(TimeSpan.FromSeconds(20));
            return h.Saved();
        };

        await h.Saved();
        h.Codesys.BeforeRun = null;

        // A completely ordinary save, well after the burst.
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.SetProject("v3");
        var later = await h.Saved();

        Assert.Equal(SyncVerdict.Completed, later.Verdict);
    }

    // --- the throttled job ----------------------------------------------------

    [Fact]
    public async Task A_throttled_save_is_refused_now_and_run_when_the_interval_passes()
    {
        // TryStartThrottled was written for "the idle tick" and had no caller anywhere, so
        // the pending flag that "guarantees a final export once things go quiet" guaranteed
        // nothing: the change waited for the next save or for somebody to commit.
        var h = new Harness(new OrchestratorOptions
        {
            RequireImportConfirmation = false,
            MinimumInterval = TimeSpan.FromSeconds(30),
        });

        h.Codesys.EnqueueSuccess();
        h.Codesys.EnqueueSuccess();

        await h.Saved();

        // A second save, five seconds later: inside the minimum interval.
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.SetProject("v2");
        var throttled = await h.Saved();

        Assert.Equal(SyncVerdict.Throttled, throttled.Verdict);
        Assert.Single(h.Codesys.Jobs);

        // Nothing else happens: no further save, just time passing.
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        var deferred = await h.Orchestrator.RunDeferredAsync(CancellationToken.None);

        Assert.Equal(SyncVerdict.Completed, Assert.Single(deferred).Verdict);
        Assert.Equal(2, h.Codesys.Jobs.Count);
    }

    [Fact]
    public async Task Nothing_is_deferred_when_the_interval_has_not_passed()
    {
        var h = new Harness(new OrchestratorOptions
        {
            RequireImportConfirmation = false,
            MinimumInterval = TimeSpan.FromSeconds(30),
        });

        h.Codesys.EnqueueSuccess();
        await h.Saved();

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.SetProject("v2");
        await h.Saved();

        // Called on a tick, as the app calls it: the answer must be "not yet", not "run it".
        Assert.Empty(await h.Orchestrator.RunDeferredAsync(CancellationToken.None));
        Assert.Single(h.Codesys.Jobs);
    }

    [Fact]
    public async Task A_tick_with_nothing_deferred_does_nothing_at_all()
    {
        // This runs every few seconds forever. It must not touch the toolchain, and it must
        // not log anything, or it becomes the next thing drowning the panel.
        var h = new Harness();
        h.Codesys.EnqueueSuccess();
        await h.Saved();

        var before = h.Sink.Snapshot().Count;
        h.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Empty(await h.Orchestrator.RunDeferredAsync(CancellationToken.None));
        Assert.Single(h.Codesys.Jobs);
        Assert.Equal(before, h.Sink.Snapshot().Count);
    }

    [Fact]
    public async Task A_deferred_job_is_re_checked_rather_than_trusted()
    {
        // By the time the interval expires the reason to run may have gone: the mirror may
        // already match, because the export that caused the throttle captured the newer file.
        // Re-entering the pipeline is what asks that question; running the stored job blindly
        // would pay 17-31 s of CODESYS to write the bytes that are already there.
        var h = new Harness(new OrchestratorOptions
        {
            RequireImportConfirmation = false,
            MinimumInterval = TimeSpan.FromSeconds(30),
        });

        h.Codesys.EnqueueSuccess();
        await h.Saved();

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.SetProject("v2");
        await h.Saved();

        // Put the project back to what was already exported: nothing is outstanding now.
        h.SetProject("v1");
        h.Clock.Advance(TimeSpan.FromSeconds(30));

        var deferred = await h.Orchestrator.RunDeferredAsync(CancellationToken.None);

        Assert.Equal(SyncVerdict.NoChange, Assert.Single(deferred).Verdict);
        Assert.Single(h.Codesys.Jobs);
    }
}
