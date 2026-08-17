using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Plc.Codesys;
using Xunit;

namespace Versio.Core.Tests.Logging;

public class VersioLoggerTests
{
    private static (VersioLogger Logger, InMemoryLogSink Sink, FakeClock Clock) Make()
    {
        var clock = new FakeClock();
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(clock).AddSink(sink);
        return (logger, sink, clock);
    }

    [Fact]
    public void An_entry_reaches_the_sink()
    {
        var (logger, sink, _) = Make();

        logger.Info(LogCategory.Sync, "exported 3 objects");

        var entry = Assert.Single(sink.Snapshot());
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("Sync", entry.Category);
        Assert.Equal("exported 3 objects", entry.Message);
    }

    [Fact]
    public void An_operation_scope_stamps_every_line_inside_it()
    {
        var (logger, sink, _) = Make();

        logger.Info(LogCategory.App, "before");
        using (logger.BeginOperation("7f3a"))
        {
            logger.Info(LogCategory.Sync, "inside");
        }

        logger.Info(LogCategory.App, "after");

        var entries = sink.Snapshot();
        Assert.Null(entries[0].OperationId);
        Assert.Equal("7f3a", entries[1].OperationId);
        Assert.Null(entries[2].OperationId);
    }

    [Fact]
    public async Task An_operation_scope_survives_an_await()
    {
        // AsyncLocal, not a field: an export awaits a subprocess and resumes on a different
        // worker thread, and the correlation has to follow it there.
        var (logger, sink, _) = Make();

        using (logger.BeginOperation("7f3a"))
        {
            await Task.Yield();
            await Task.Run(() => logger.Info(CodesysLog.Category, "on a pool thread"));
        }

        Assert.Equal("7f3a", Assert.Single(sink.Snapshot()).OperationId);
    }

    [Fact]
    public void Nested_scopes_restore_the_outer_id()
    {
        var (logger, sink, _) = Make();

        using (logger.BeginOperation("outer"))
        {
            using (logger.BeginOperation("inner"))
            {
                logger.Info(LogCategory.App, "inner line");
            }

            logger.Info(LogCategory.App, "outer line");
        }

        var entries = sink.Snapshot();
        Assert.Equal("inner", entries[0].OperationId);
        Assert.Equal("outer", entries[1].OperationId);
    }

    [Fact]
    public void A_failing_sink_does_not_take_down_the_caller()
    {
        // Losing a log line is bad. Losing the export because the log file was locked by a
        // backup agent is worse.
        var (logger, sink, _) = Make();
        logger.AddSink(new ThrowingSink());

        logger.Info(LogCategory.App, "still gets through");

        Assert.Contains(sink.Snapshot(), e => e.Message == "still gets through");
    }

    [Fact]
    public void A_failing_sink_is_reported_through_the_sinks_that_still_work()
    {
        // Swallowing the failure entirely is how "the log file stopped updating" becomes
        // invisible: the window looks healthy and the one artefact a bug report needs is
        // silently empty.
        var (logger, sink, _) = Make();
        logger.AddSink(new ThrowingSink());

        logger.Info(LogCategory.App, "anything");

        var notice = Assert.Single(sink.Snapshot(), e => e.Level == LogLevel.Error);
        Assert.Contains(nameof(ThrowingSink), notice.Message);
        Assert.IsType<IOException>(notice.Exception);
    }

    [Fact]
    public void A_failing_sink_is_reported_once_and_not_on_every_entry()
    {
        // A sink that throws on every entry would otherwise double the volume of the log
        // with complaints about itself.
        var (logger, sink, _) = Make();
        logger.AddSink(new ThrowingSink());

        logger.Info(LogCategory.App, "one");
        logger.Info(LogCategory.App, "two");
        logger.Info(LogCategory.App, "three");

        Assert.Single(sink.Snapshot(), e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void A_failing_sink_raises_SinkFailed_for_the_UI()
    {
        var (logger, _, _) = Make();
        var faulty = new ThrowingSink();
        ILogSink? reported = null;
        logger.SinkFailed += (s, _) => reported = s;
        logger.AddSink(faulty);

        logger.Info(LogCategory.App, "anything");

        Assert.Same(faulty, reported);
    }

    [Fact]
    public async Task A_sink_added_while_another_thread_is_logging_does_not_throw()
    {
        // A List<T> iterated during an Add throws InvalidOperationException — and it would
        // throw inside the logger, at the moment the log is most wanted. The file sink is
        // attached while a repository opens, which is exactly when the UI is logging.
        var (logger, _, _) = Make();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                logger.Info(LogCategory.App, "concurrent");
            }
        });

        while (!stop.IsCancellationRequested)
        {
            logger.AddSink(new InMemoryLogSink(capacity: 4));
        }

        await writer;   // rethrows anything the writer hit
    }

    [Fact]
    public void Entries_below_the_minimum_level_are_dropped()
    {
        var (logger, sink, _) = Make();
        logger.MinimumLevel = LogLevel.Warning;

        logger.Debug(LogCategory.App, "noise");
        logger.Info(LogCategory.App, "also noise");
        logger.Warn(LogCategory.App, "kept");

        Assert.Equal("kept", Assert.Single(sink.Snapshot()).Message);
    }

    [Fact]
    public void A_click_records_which_control_was_pressed()
    {
        var (logger, sink, _) = Make();

        logger.Click("GitPanel.PushButton");

        var entry = Assert.Single(sink.Snapshot());
        Assert.Equal(LogCategory.Ui, entry.Category);
        Assert.Contains("GitPanel.PushButton", entry.Message);
    }

    [Fact]
    public void Formatting_includes_time_level_category_and_operation()
    {
        var (logger, sink, _) = Make();

        using (logger.BeginOperation("7f3a"))
        {
            logger.Warn(CodesysLog.Category, "object not exportable");
        }

        var text = Assert.Single(sink.Snapshot()).Format();

        Assert.Contains("WRN", text);
        Assert.Contains("[Codesys]", text);
        Assert.Contains("{7f3a}", text);
        Assert.Contains("object not exportable", text);
    }

    private sealed class ThrowingSink : ILogSink
    {
        public void Emit(LogEvent entry) => throw new IOException("log file is locked");
    }
}

public class InMemoryLogSinkTests
{
    [Fact]
    public void The_buffer_is_bounded_and_drops_the_oldest()
    {
        // Unbounded, an export's output would grow the buffer until the app died - slowly
        // enough that nobody would connect the two.
        var sink = new InMemoryLogSink(capacity: 100);

        for (var i = 0; i < 250; i++)
        {
            sink.Emit(Entry($"line {i}"));
        }

        var snapshot = sink.Snapshot();
        Assert.Equal(100, snapshot.Count);
        Assert.Equal("line 150", snapshot[0].Message);
        Assert.Equal("line 249", snapshot[^1].Message);
    }

    [Fact]
    public void Subscribers_are_notified_for_every_entry()
    {
        var sink = new InMemoryLogSink();
        var seen = new List<LogEvent>();
        sink.Emitted += seen.Add;

        sink.Emit(Entry("a"));
        sink.Emit(Entry("b"));

        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void Clear_empties_the_buffer()
    {
        var sink = new InMemoryLogSink();
        sink.Emit(Entry("a"));

        sink.Clear();

        Assert.Empty(sink.Snapshot());
    }

    private static LogEvent Entry(string message) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        Level = LogLevel.Information,
        Category = LogCategory.App,
        Message = message,
    };
}

public class HeartbeatServiceTests
{
    [Fact]
    public void A_tracked_operation_describes_its_elapsed_time()
    {
        var clock = new FakeClock();
        var sink = new InMemoryLogSink();
        using var heartbeat = new HeartbeatService(clock, new VersioLogger(clock).AddSink(sink));

        using var _ = heartbeat.Track("7f3a", "exporting MyMachine");
        clock.AdvanceSeconds(14);

        var op = Assert.Single(heartbeat.Running);
        Assert.Contains("14s", op.Describe(clock));
        Assert.Contains("exporting MyMachine", op.Describe(clock));
    }

    [Fact]
    public void Progress_detail_is_included_when_the_script_reports_it()
    {
        var clock = new FakeClock();
        using var heartbeat = new HeartbeatService(clock, new VersioLogger(clock));

        using var _ = heartbeat.Track("7f3a", "exporting");
        heartbeat.Report("7f3a", done: 23, total: 61, detail: "Conveyor_FB");

        var described = Assert.Single(heartbeat.Running).Describe(clock);
        Assert.Contains("23/61", described);
        Assert.Contains("Conveyor_FB", described);
    }

    [Fact]
    public void Completing_removes_the_operation()
    {
        var clock = new FakeClock();
        using var heartbeat = new HeartbeatService(clock, new VersioLogger(clock));

        var tracker = heartbeat.Track("7f3a", "exporting");
        Assert.Single(heartbeat.Running);

        tracker.Dispose();
        Assert.Empty(heartbeat.Running);
    }

    [Fact]
    public void A_slow_operation_logs_its_duration_on_completion()
    {
        var clock = new FakeClock();
        var sink = new InMemoryLogSink();
        using var heartbeat = new HeartbeatService(clock, new VersioLogger(clock).AddSink(sink));

        var tracker = heartbeat.Track("7f3a", "exporting");
        clock.AdvanceSeconds(18);
        tracker.Dispose();

        Assert.Contains(sink.Snapshot(),
            e => e.Category == LogCategory.Perf && e.Message.Contains("18.0s"));
    }

    [Fact]
    public void Several_operations_are_tracked_independently()
    {
        var clock = new FakeClock();
        using var heartbeat = new HeartbeatService(clock, new VersioLogger(clock));

        using var a = heartbeat.Track("a", "export A");
        using var b = heartbeat.Track("b", "export B");

        Assert.Equal(2, heartbeat.Running.Count);
    }

    [Fact]
    public void Disposing_a_tracker_twice_is_harmless()
    {
        var clock = new FakeClock();
        using var heartbeat = new HeartbeatService(clock, new VersioLogger(clock));

        var tracker = heartbeat.Track("7f3a", "exporting");
        tracker.Dispose();
        tracker.Dispose();

        Assert.Empty(heartbeat.Running);
    }
}
