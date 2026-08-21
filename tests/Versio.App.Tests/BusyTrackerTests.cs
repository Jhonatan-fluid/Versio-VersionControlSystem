using Versio.App.Mvvm;
using Versio.Core.Abstractions;
using Versio.Core.Guarding;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The one thing that knows whether Versio is doing something, and whether it may be stopped.
/// </summary>
/// <remarks>
/// No STA thread and no window: this is a counted dictionary with a clock, and the reason it
/// is tested at all is that two of its properties are now safety-relevant rather than
/// cosmetic. <see cref="BusyTracker.HasUninterruptible"/> is what disables the branch dropdown,
/// and <see cref="BusyTracker.CancelCancellable"/> skips an import (branch-switch).
/// <see cref="BusyTracker.CancelRunning"/> is what Stop and close-Stop call.
/// </remarks>
public sealed class BusyTrackerTests
{
    /// <summary>A clock the test moves by hand, so elapsed times are exact rather than racy.</summary>
    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    [Fact]
    public void An_idle_tracker_says_so()
    {
        var tracker = new BusyTracker(new StoppedClock());

        Assert.False(tracker.IsBusy);
        Assert.False(tracker.HasUninterruptible);
        Assert.Equal(string.Empty, tracker.Message);
        Assert.True(tracker.Snapshot().IsIdle);
    }

    [Fact]
    public void The_bar_goes_away_when_the_last_operation_ends_not_the_first()
    {
        var tracker = new BusyTracker(new StoppedClock());

        var outer = tracker.Begin(WorkKind.Export, "Exporting…");
        var inner = tracker.Begin(WorkKind.GitRead, "Reading git status…");

        inner.Dispose();
        Assert.True(tracker.IsBusy);

        outer.Dispose();
        Assert.False(tracker.IsBusy);
    }

    [Fact]
    public void The_caption_is_whatever_started_most_recently()
    {
        var tracker = new BusyTracker(new StoppedClock());

        using var export = tracker.Begin(WorkKind.Export, "Exporting…");
        using var status = tracker.Begin(WorkKind.GitRead, "Reading git status…");

        Assert.Equal("Reading git status…", tracker.Message);
    }

    [Fact]
    public void The_snapshot_names_the_longest_running_first()
    {
        // The opposite order to the caption, deliberately: the bar answers "what did I just
        // do" and the guard's dialog answers "what is holding me up".
        var clock = new StoppedClock();
        var tracker = new BusyTracker(clock);

        using var slow = tracker.Begin(WorkKind.Import, "Importing…");
        clock.Advance(TimeSpan.FromSeconds(30));
        using var quick = tracker.Begin(WorkKind.GitPush, "Pushing…");
        clock.Advance(TimeSpan.FromSeconds(2));

        var running = tracker.Snapshot().Uninterruptible;

        Assert.Equal("Importing…", running[0].Description);
        Assert.Equal(TimeSpan.FromSeconds(32), running[0].Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(2), running[1].Elapsed);
    }

    [Fact]
    public void An_export_leaves_the_dropdowns_alive_and_an_import_does_not()
    {
        var tracker = new BusyTracker(new StoppedClock());

        using (tracker.Begin(WorkKind.Export, "Exporting…"))
        {
            Assert.True(tracker.IsBusy);
            Assert.False(tracker.HasUninterruptible);
        }

        using (tracker.Begin(WorkKind.Import, "Importing…"))
        {
            Assert.True(tracker.HasUninterruptible);
        }

        Assert.False(tracker.HasUninterruptible);
    }

    [Fact]
    public void A_scope_carries_the_token_that_stops_it()
    {
        var tracker = new BusyTracker(new StoppedClock());

        using var busy = tracker.Begin(WorkKind.Export, "Exporting…");

        Assert.False(busy.Token.IsCancellationRequested);
        Assert.Equal(1, tracker.CancelCancellable());
        Assert.True(busy.Token.IsCancellationRequested);
    }

    [Fact]
    public void Work_that_must_finish_is_never_cancelled_even_when_asked()
    {
        // The guard refuses to ask this question while an import runs, so in practice this
        // never happens. "In practice" is not the standard for the method that can kill a
        // CODESYS process tree: the filter is the guarantee, the guard is the courtesy.
        var tracker = new BusyTracker(new StoppedClock());

        using var import = tracker.Begin(WorkKind.Import, "Importing…");
        using var export = tracker.Begin(WorkKind.Export, "Exporting…");

        Assert.Equal(1, tracker.CancelCancellable());

        Assert.True(export.Token.IsCancellationRequested);
        Assert.False(import.Token.IsCancellationRequested);
    }

    [Fact]
    public void Stop_cancels_work_that_must_finish()
    {
        // CancelCancellable is the branch-switch path. Stop and close-Stop use CancelRunning,
        // which is the one that may kill an import.
        var tracker = new BusyTracker(new StoppedClock());

        using var import = tracker.Begin(WorkKind.Import, "Importing…");
        using var export = tracker.Begin(WorkKind.Export, "Exporting…");

        Assert.Equal(2, tracker.CancelRunning());

        Assert.True(export.Token.IsCancellationRequested);
        Assert.True(import.Token.IsCancellationRequested);
    }

    [Fact]
    public void An_external_source_is_cancelled_but_not_disposed()
    {
        // The automatic export's token is created by the caller and adopted by the scope,
        // because the run starts several frames before it announces itself. Disposing it here
        // would pull the token out from under an operation still using it.
        var tracker = new BusyTracker(new StoppedClock());
        using var external = new CancellationTokenSource();

        var busy = tracker.Begin(WorkKind.Export, "Exporting…", external);

        Assert.Equal(1, tracker.CancelCancellable());
        Assert.True(external.IsCancellationRequested);

        busy.Dispose();

        // Still usable: had the tracker disposed it, this would throw.
        Assert.True(external.Token.IsCancellationRequested);
    }

    [Fact]
    public void Disposing_a_scope_twice_is_harmless()
    {
        var tracker = new BusyTracker(new StoppedClock());
        var busy = tracker.Begin(WorkKind.Scan, "Scanning…");

        busy.Dispose();
        busy.Dispose();

        Assert.False(tracker.IsBusy);
        Assert.Equal(0, tracker.Count);
    }

    // ------------------------------------------------------------------------- the record

    [Fact]
    public void Cancelling_names_the_operation_in_the_log()
    {
        // "Why did my export not produce a diff?" has to have an answer somewhere, and a
        // count is not one. Named at Warning for the same reason discarding a file and
        // dropping a stash are: this line is what somebody is sent to look for afterwards.
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        var tracker = new BusyTracker(new StoppedClock(), logger);

        using var exporting = tracker.Begin(WorkKind.Export, "Exporting Machine…");

        tracker.CancelCancellable();

        var line = Assert.Single(sink.Snapshot(), e => e.Level == LogLevel.Warning);

        Assert.Contains("Exporting Machine…", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shutdown_that_stops_an_import_says_so_at_error()
    {
        // The one line somebody reads after a shutdown left a project that will not open. It
        // has to say plainly that it happened, and to what.
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        var tracker = new BusyTracker(new StoppedClock(), logger);

        using var importing = tracker.Begin(WorkKind.Import, "Importing into Machine…");

        tracker.CancelEverythingForShutdown();

        var line = Assert.Single(sink.Snapshot(), e => e.Level == LogLevel.Error);

        Assert.Contains("Importing into Machine…", line.Message, StringComparison.Ordinal);
        Assert.Contains("session is ending", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_does_not_log_as_if_the_session_is_ending()
    {
        // Mutation check: CancelRunning used to share CancelEverythingForShutdown's
        // sentence, so clicking Stop wrote "the session is ending" while the window
        // stayed open. That line is how the button looks broken in the log.
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        var tracker = new BusyTracker(new StoppedClock(), logger);

        using var importing = tracker.Begin(WorkKind.Import, "Importing into Machine…");

        tracker.CancelRunning();

        var line = Assert.Single(sink.Snapshot(), e => e.Level == LogLevel.Error);

        Assert.Contains("Importing into Machine…", line.Message, StringComparison.Ordinal);
        Assert.Contains("restore", line.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session is ending", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tracker_with_no_logger_still_works()
    {
        // Defaulted to NullLogger rather than to null, so nothing inside has to check.
        var tracker = new BusyTracker(new StoppedClock());

        using var exporting = tracker.Begin(WorkKind.Export, "Exporting…");

        Assert.Equal(1, tracker.CancelCancellable());
    }

    [Fact]
    public void Ending_an_operation_announces_the_uninterruptible_flag()
    {
        // The dropdowns bind to it, and a scope opening or closing can move it without moving
        // the count in a way any binding would notice.
        var tracker = new BusyTracker(new StoppedClock());
        var seen = new List<string>();

        tracker.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? string.Empty);

        tracker.Begin(WorkKind.Import, "Importing…").Dispose();

        Assert.Contains(nameof(BusyTracker.HasUninterruptible), seen);
    }
}
