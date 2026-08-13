using Versio.Core.Abstractions;
using Versio.Core.Watching;
using Xunit;

namespace Versio.Core.Tests.Watching;

/// <summary>
/// The queueing behaviour that answers "what if changes happen fast?".
/// </summary>
public class JobCoalescerTests
{
    private static JobCoalescer Make(FakeClock clock) =>
        new(clock, minimumInterval: TimeSpan.FromSeconds(30));

    [Fact]
    public void First_change_starts_immediately()
    {
        var c = Make(new FakeClock());

        Assert.Equal(CoalesceAction.StartNow, c.OnChangeSettled());
        Assert.True(c.IsRunning);
    }

    [Fact]
    public void A_save_early_in_a_run_is_queued_rather_than_restarting_it()
    {
        // This asserted CancelAndRestart, on the reasoning that restarting is cheap in the
        // first few seconds and avoids finishing work already known to be stale. The
        // reasoning was sound and the mechanism did not exist: nothing cancels a running
        // toolchain job, so "restart" meant the orchestrator launched a second CODESYS
        // beside the first. Caught in the field, not here — this test passed throughout,
        // because a coalescer answering on its own cannot see what the caller does with
        // the answer.
        var clock = new FakeClock();
        var c = Make(clock);
        c.OnChangeSettled();

        clock.AdvanceSeconds(3);

        Assert.Equal(CoalesceAction.Queue, c.OnChangeSettled());
        Assert.True(c.HasPending);
    }

    [Fact]
    public void Only_one_job_is_ever_running()
    {
        // The property the removed branch broke, stated directly: however many changes
        // arrive and however fast, a second job is never started while one is in flight.
        var clock = new FakeClock();
        var c = Make(clock);

        Assert.Equal(CoalesceAction.StartNow, c.OnChangeSettled());

        for (var i = 0; i < 20; i++)
        {
            clock.AdvanceSeconds(1);
            Assert.NotEqual(CoalesceAction.StartNow, c.OnChangeSettled());
        }
    }

    [Fact]
    public void A_save_later_in_a_run_is_queued_rather_than_discarding_the_work()
    {
        var clock = new FakeClock();
        var c = Make(clock);
        c.OnChangeSettled();

        clock.AdvanceSeconds(22);

        Assert.Equal(CoalesceAction.Queue, c.OnChangeSettled());
        Assert.True(c.HasPending);
    }

    [Fact]
    public void Further_saves_coalesce_into_the_single_pending_slot()
    {
        var clock = new FakeClock();
        var c = Make(clock);
        c.OnChangeSettled();
        clock.AdvanceSeconds(10);
        c.OnChangeSettled();                       // -> Queue

        clock.AdvanceSeconds(5);
        Assert.Equal(CoalesceAction.Coalesced, c.OnChangeSettled());
        clock.AdvanceSeconds(5);
        Assert.Equal(CoalesceAction.Coalesced, c.OnChangeSettled());
    }

    [Fact]
    public void Four_saves_during_one_export_produce_exactly_two_exports()
    {
        // The worked example from the plan. Four saves, two exports, and the final state
        // reflects the last save. The intermediate states were never wanted.
        var clock = new FakeClock();
        var c = Make(clock);
        var exports = 0;

        // Save #1 -> E1 starts
        Assert.Equal(CoalesceAction.StartNow, c.OnChangeSettled());
        exports++;

        clock.AdvanceSeconds(6);
        Assert.Equal(CoalesceAction.Queue, c.OnChangeSettled());      // save #2

        clock.AdvanceSeconds(16);
        Assert.Equal(CoalesceAction.Coalesced, c.OnChangeSettled());  // save #3

        clock.AdvanceSeconds(9);
        Assert.Equal(CoalesceAction.Coalesced, c.OnChangeSettled());  // save #4

        clock.AdvanceSeconds(9);                                      // E1 finishes at ~40 s
        Assert.True(c.OnJobCompleted());                              // -> E2 starts
        exports++;

        Assert.Equal(2, exports);
        Assert.False(c.HasPending);
        Assert.True(c.IsRunning);
    }

    [Fact]
    public void Completing_with_nothing_pending_starts_nothing()
    {
        var c = Make(new FakeClock());
        c.OnChangeSettled();

        Assert.False(c.OnJobCompleted());
        Assert.False(c.IsRunning);
    }

    [Fact]
    public void Rapid_saves_after_a_run_are_throttled_not_run_back_to_back()
    {
        // Prevents a save-happy engineer from pinning a CODESYS process all day.
        var clock = new FakeClock();
        var c = Make(clock);
        c.OnChangeSettled();
        clock.AdvanceSeconds(2);
        c.OnJobCompleted();

        clock.AdvanceSeconds(1);
        Assert.Equal(CoalesceAction.Throttled, c.OnChangeSettled());
        Assert.True(c.HasPending);
    }

    [Fact]
    public void A_throttled_change_still_runs_once_the_interval_elapses()
    {
        // Throttling must never mean "dropped": the end state has to be correct.
        var clock = new FakeClock();
        var c = Make(clock);
        c.OnChangeSettled();
        clock.AdvanceSeconds(2);
        c.OnJobCompleted();
        clock.AdvanceSeconds(1);
        c.OnChangeSettled();                       // -> Throttled, pending

        Assert.False(c.TryStartThrottled());       // interval not elapsed yet

        clock.AdvanceSeconds(30);
        Assert.True(c.TryStartThrottled());
        Assert.True(c.IsRunning);
        Assert.False(c.HasPending);
    }

    [Fact]
    public void A_long_storm_of_saves_never_queues_more_than_one_job()
    {
        var clock = new FakeClock();
        var c = Make(clock);
        c.OnChangeSettled();

        for (var i = 0; i < 200; i++)
        {
            clock.AdvanceSeconds(1);
            c.OnChangeSettled();
        }

        Assert.True(c.HasPending);
        c.OnJobCompleted();
        Assert.False(c.HasPending);   // exactly one follow-up, not 200
    }

    [Fact]
    public void A_save_storm_during_a_run_produces_exactly_one_follow_up()
    {
        // The livelock this replaces was real and is now structurally impossible: with no
        // restart there is nothing to thrash. Sixty saves during one run leave one queued
        // job, which is the coalescer's whole premise — an export reads the current file,
        // so N saves need one export and not N.
        var clock = new FakeClock();
        var c = Make(clock);
        c.OnChangeSettled();

        for (var i = 0; i < 60; i++)
        {
            clock.AdvanceSeconds(1);
            c.OnChangeSettled();
        }

        Assert.True(c.HasPending);

        // One follow-up, and then nothing left over.
        Assert.True(c.OnJobCompleted());
        Assert.False(c.OnJobCompleted());
    }
}
