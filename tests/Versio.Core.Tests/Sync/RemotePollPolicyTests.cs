using Versio.Core.Abstractions;
using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// How often the remote gets asked, and when it stops being asked at all.
/// </summary>
/// <remarks>
/// Every interval here is asserted by moving a fake clock. That is the whole reason the pace is
/// a rule taking a timestamp rather than a timer owning one — otherwise proving that a quiet
/// hour backs off to sixty seconds would take a quiet hour.
/// </remarks>
public sealed class RemotePollPolicyTests
{
    private static (RemotePollPolicy Policy, FakeClock Clock) Build(
        TimeSpan? interval = null, TimeSpan? floor = null)
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero));
        return (new RemotePollPolicy(clock, interval, floor), clock);
    }

    [Fact]
    public void The_first_probe_is_due_immediately()
    {
        var (policy, clock) = Build();

        // A repository that has just opened should learn where it stands without waiting out
        // an interval first.
        Assert.True(policy.ShouldProbe(clock.UtcNow));
    }

    [Fact]
    public void Nothing_is_due_again_until_the_interval_has_passed()
    {
        var (policy, clock) = Build();
        policy.Record(ProbeOutcome.Unchanged);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(policy.ShouldProbe(clock.UtcNow));

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(policy.ShouldProbe(clock.UtcNow));
    }

    [Fact]
    public void A_quiet_run_stretches_the_interval()
    {
        var (policy, _) = Build();
        Assert.Equal(RemotePollPolicy.DefaultInterval, policy.CurrentInterval);

        for (var i = 0; i < RemotePollPolicy.QuietProbesPerStep; i++)
        {
            policy.Record(ProbeOutcome.Unchanged);
        }

        // A window left open overnight should stop hammering a remote for nobody.
        Assert.True(policy.CurrentInterval > RemotePollPolicy.DefaultInterval);
    }

    [Fact]
    public void The_stretch_stops_at_the_ceiling()
    {
        var (policy, _) = Build();

        for (var i = 0; i < 500; i++)
        {
            policy.Record(ProbeOutcome.Unchanged);
        }

        Assert.Equal(RemotePollPolicy.Ceiling, policy.CurrentInterval);
    }

    [Fact]
    public void Movement_snaps_the_interval_straight_back()
    {
        var (policy, _) = Build();

        for (var i = 0; i < 100; i++)
        {
            policy.Record(ProbeOutcome.Unchanged);
        }

        Assert.Equal(RemotePollPolicy.Ceiling, policy.CurrentInterval);

        policy.Record(ProbeOutcome.Moved);

        // Something is happening again. Near-live counts are the point.
        Assert.Equal(RemotePollPolicy.DefaultInterval, policy.CurrentInterval);
        Assert.Equal(0, policy.QuietRun);
    }

    [Fact]
    public void A_user_touching_the_panel_snaps_it_back_too()
    {
        var (policy, _) = Build();

        for (var i = 0; i < 100; i++)
        {
            policy.Record(ProbeOutcome.Unchanged);
        }

        policy.Wake();

        // The backoff exists to stop polling for nobody. The moment there is somebody, it
        // should be as live as it was at the start.
        Assert.Equal(RemotePollPolicy.DefaultInterval, policy.CurrentInterval);
    }

    [Fact]
    public void The_floor_is_respected_however_short_an_interval_is_asked_for()
    {
        var (policy, _) = Build(interval: TimeSpan.FromMilliseconds(50));

        // Below the floor the process spawns start to matter and the answer is no fresher in
        // any way a person can perceive.
        Assert.True(policy.CurrentInterval >= RemotePollPolicy.DefaultFloor);
    }

    [Fact]
    public void A_configured_interval_is_used()
    {
        var (policy, _) = Build(interval: TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), policy.CurrentInterval);
    }

    [Fact]
    public void A_refusal_halts_on_the_very_first_one()
    {
        var (policy, clock) = Build();

        policy.Record(ProbeOutcome.Refused, "Authentication failed");

        // Retrying a rejected credential every ten seconds cannot succeed and may lock an
        // account, which is considerably worse than a stale count. The breaker's threshold is
        // for things that might recover; this is not one of them.
        Assert.True(policy.Halted);
        Assert.False(policy.ShouldProbe(clock.UtcNow));
        Assert.Contains("Authentication failed", policy.HaltReason);
    }

    [Fact]
    public void Being_unreachable_is_tolerated_for_a_while_and_then_stops()
    {
        var (policy, clock) = Build();

        for (var i = 0; i < RemotePollPolicy.FailuresBeforeHalt - 1; i++)
        {
            policy.Record(ProbeOutcome.Failed);
            Assert.False(policy.Halted);
        }

        policy.Record(ProbeOutcome.Failed);

        // A laptop on a train comes back. An offline laptop that never does should not log a
        // failure every ten seconds for as long as it stays shut.
        Assert.True(policy.Halted);
        Assert.False(policy.ShouldProbe(clock.UtcNow));
    }

    [Fact]
    public void A_success_forgives_the_failures_before_it()
    {
        var (policy, _) = Build();

        for (var i = 0; i < RemotePollPolicy.FailuresBeforeHalt - 1; i++)
        {
            policy.Record(ProbeOutcome.Failed);
        }

        policy.Record(ProbeOutcome.Unchanged);

        // The VPN came back. The count starts again rather than the next hiccup halting it.
        for (var i = 0; i < RemotePollPolicy.FailuresBeforeHalt - 1; i++)
        {
            policy.Record(ProbeOutcome.Failed);
            Assert.False(policy.Halted);
        }
    }

    [Fact]
    public void Waking_does_not_quietly_resume_a_halted_poller()
    {
        var (policy, clock) = Build();
        policy.Record(ProbeOutcome.Refused, "Authentication failed");

        policy.Wake();

        // A halt is a state a user has to resolve. Resuming on a mouse movement would hide it
        // and start the hammering again.
        Assert.True(policy.Halted);
        Assert.False(policy.ShouldProbe(clock.UtcNow));
    }

    [Fact]
    public void Resuming_deliberately_starts_it_over()
    {
        var (policy, clock) = Build();
        policy.Record(ProbeOutcome.Refused, "Authentication failed");

        policy.Resume();

        Assert.False(policy.Halted);
        Assert.Equal(string.Empty, policy.HaltReason);
        Assert.True(policy.ShouldProbe(clock.UtcNow));
    }

    [Fact]
    public void A_halt_always_carries_a_reason_worth_showing()
    {
        var (policy, _) = Build();
        policy.Record(ProbeOutcome.Refused);

        // One notification says why polling stopped. An empty reason would make it say
        // nothing, which is worse than not stopping.
        Assert.False(string.IsNullOrWhiteSpace(policy.HaltReason));
    }
}
