using Versio.Core.Abstractions;
using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

public class HashGateTests
{
    [Fact]
    public void Unchanged_content_is_not_written()
    {
        var gate = new HashGate();
        gate.Accept("a.st", "sha256:aaa");

        Assert.False(gate.ShouldWrite("a.st", "sha256:aaa"));
    }

    [Fact]
    public void Changed_content_is_written()
    {
        var gate = new HashGate();
        gate.Accept("a.st", "sha256:aaa");

        Assert.True(gate.ShouldWrite("a.st", "sha256:bbb"));
    }

    [Fact]
    public void Unknown_path_is_always_written()
    {
        Assert.True(new HashGate().ShouldWrite("new.st", "sha256:aaa"));
    }

    [Fact]
    public void Evaluate_splits_changed_from_unchanged()
    {
        // The real-world shape: an export produces 61 objects and 3 of them changed.
        // Writing the other 58 would create 58 spurious watcher events and a noisy diff.
        var gate = new HashGate();
        for (var i = 0; i < 61; i++)
        {
            gate.Accept($"obj{i}.st", $"sha256:{i}");
        }

        var candidates = new Dictionary<string, string>();
        for (var i = 0; i < 61; i++)
        {
            candidates[$"obj{i}.st"] = i < 3 ? $"sha256:changed{i}" : $"sha256:{i}";
        }

        var result = gate.Evaluate(candidates);

        Assert.Equal(3, result.Changed.Count);
        Assert.Equal(58, result.Unchanged.Count);
        Assert.Empty(result.Removed);
        Assert.True(result.AnyChange);
    }

    [Fact]
    public void Deleted_objects_are_reported_as_removed()
    {
        var gate = new HashGate();
        gate.Accept("kept.st", "sha256:a");
        gate.Accept("deleted.st", "sha256:b");

        var result = gate.Evaluate(new Dictionary<string, string> { ["kept.st"] = "sha256:a" });

        Assert.Equal(["deleted.st"], result.Removed);
        Assert.True(result.AnyChange);
    }

    [Fact]
    public void No_changes_at_all_reports_no_change()
    {
        var gate = new HashGate([new KeyValuePair<string, string>("a.st", "sha256:a")]);

        var result = gate.Evaluate(new Dictionary<string, string> { ["a.st"] = "sha256:a" });

        Assert.False(result.AnyChange);
    }
}

public class SelfWriteSuppressorTests
{
    [Fact]
    public void Our_own_write_is_suppressed()
    {
        var clock = new FakeClock();
        var s = new SelfWriteSuppressor(clock);
        s.Register("a.st", "sha256:aaa");

        Assert.True(s.ShouldSuppress("a.st", "sha256:aaa"));
    }

    [Fact]
    public void A_real_edit_at_the_same_path_is_NOT_suppressed()
    {
        // If this suppressed on path alone it would be a data-loss bug, not a noise filter:
        // a user edit landing in the same instant would be silently dropped.
        var s = new SelfWriteSuppressor(new FakeClock());
        s.Register("a.st", "sha256:ours");

        Assert.False(s.ShouldSuppress("a.st", "sha256:theirs"));
    }

    [Fact]
    public void Suppression_is_consumed_so_a_second_event_gets_through()
    {
        var s = new SelfWriteSuppressor(new FakeClock());
        s.Register("a.st", "sha256:aaa");

        Assert.True(s.ShouldSuppress("a.st", "sha256:aaa"));
        Assert.False(s.ShouldSuppress("a.st", "sha256:aaa"));
    }

    [Fact]
    public void Registrations_expire_so_a_missing_event_cannot_deafen_us_forever()
    {
        var clock = new FakeClock();
        var s = new SelfWriteSuppressor(clock, TimeSpan.FromSeconds(10));
        s.Register("a.st", "sha256:aaa");

        clock.AdvanceSeconds(11);

        Assert.False(s.ShouldSuppress("a.st", "sha256:aaa"));
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public void Unrelated_paths_are_never_suppressed()
    {
        var s = new SelfWriteSuppressor(new FakeClock());
        s.Register("a.st", "sha256:aaa");

        Assert.False(s.ShouldSuppress("b.st", "sha256:aaa"));
    }
}

public class CircuitBreakerTests
{
    [Fact]
    public void Normal_activity_passes()
    {
        var cb = new CircuitBreaker(new FakeClock(), threshold: 5);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(cb.TryRecord("obj"));
        }

        Assert.False(cb.IsTripped("obj"));
    }

    [Fact]
    public void Exceeding_the_threshold_inside_the_window_trips()
    {
        var cb = new CircuitBreaker(new FakeClock(), threshold: 5, window: TimeSpan.FromSeconds(60));

        for (var i = 0; i < 5; i++)
        {
            cb.TryRecord("obj");
        }

        Assert.False(cb.TryRecord("obj"));
        Assert.True(cb.IsTripped("obj"));
        Assert.True(cb.AnyTripped);
    }

    [Fact]
    public void Events_outside_the_window_do_not_count()
    {
        var clock = new FakeClock();
        var cb = new CircuitBreaker(clock, threshold: 3, window: TimeSpan.FromSeconds(60));

        cb.TryRecord("obj");
        cb.TryRecord("obj");
        clock.AdvanceSeconds(61);
        cb.TryRecord("obj");

        Assert.Equal(1, cb.CountInWindow("obj"));
        Assert.False(cb.IsTripped("obj"));
    }

    [Fact]
    public void Tripping_is_sticky_until_a_human_resets_it()
    {
        var clock = new FakeClock();
        var cb = new CircuitBreaker(clock, threshold: 2, window: TimeSpan.FromSeconds(60));

        cb.TryRecord("obj");
        cb.TryRecord("obj");
        cb.TryRecord("obj");
        Assert.True(cb.IsTripped("obj"));

        // Waiting it out must NOT silently re-arm: a runaway loop that pauses is still a
        // runaway loop.
        clock.AdvanceSeconds(600);
        Assert.False(cb.TryRecord("obj"));

        cb.Reset("obj");
        Assert.True(cb.TryRecord("obj"));
    }

    [Fact]
    public void Objects_are_tracked_independently()
    {
        var cb = new CircuitBreaker(new FakeClock(), threshold: 2);

        cb.TryRecord("a");
        cb.TryRecord("a");
        cb.TryRecord("a");

        Assert.True(cb.IsTripped("a"));
        Assert.True(cb.TryRecord("b"));
    }
}
