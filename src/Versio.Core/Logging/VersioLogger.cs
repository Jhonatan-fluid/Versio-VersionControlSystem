using Versio.Core.Abstractions;

namespace Versio.Core.Logging;

/// <summary>
/// The logger. Deliberately in-house rather than Serilog.
/// </summary>
/// <remarks>
/// <para>
/// The plan specified Serilog, and for a general app that would be right. Here the
/// requirements are narrow and specific — a bounded ring buffer the UI can bind to, an
/// ambient correlation id, and heartbeat lines the UI collapses — so most of Serilog's
/// value would have been reimplemented around it anyway. Writing it directly keeps
/// <c>Versio.Core</c> at zero third-party dependencies, which is worth more than the
/// formatting features we would not use.
/// </para>
/// <para>
/// The correlation scope is an <see cref="AsyncLocal{T}"/>, so it follows an operation
/// across <c>await</c> boundaries and onto whichever worker thread continues it — which is
/// exactly the case a plain field would get wrong.
/// </para>
/// <para>
/// The <c>Debug</c>/<c>Info</c>/<c>Warn</c>/<c>Error</c>/<c>Click</c> vocabulary lives in
/// <see cref="LoggerExtensions"/> rather than here, so it works against
/// <see cref="IVersioLogger"/> everywhere and nothing has to depend on this class to get it.
/// </para>
/// </remarks>
public sealed class VersioLogger(IClock clock) : IVersioLogger
{
    private static readonly AsyncLocal<string?> CurrentOperation = new();

    private readonly Lock _gate = new();

    /// <summary>Sinks that have already thrown once and been reported. Prevents a broken
    /// sink from emitting a fault line on every single entry thereafter.</summary>
    private readonly HashSet<ILogSink> _reportedFaulty = [];

    /// <summary>
    /// Copy-on-write so <see cref="Log"/> never takes a lock on the hot path.
    /// </summary>
    /// <remarks>
    /// Sinks are added at startup and while a repository is being opened — which happens on
    /// a background thread while the UI is already logging. A plain <see cref="List{T}"/>
    /// iterated during an <c>Add</c> throws <see cref="InvalidOperationException"/>, and it
    /// would throw inside the logger, at the exact moment the log is most wanted.
    /// </remarks>
    private volatile ILogSink[] _sinks = [];

    public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    /// <summary>
    /// Raised the first time a given sink throws.
    /// </summary>
    /// <remarks>
    /// The app surfaces this in the status bar. Without it, "the log file stopped updating"
    /// is invisible: the ring buffer keeps working, the window looks healthy, and the one
    /// artefact a bug report depends on is silently empty.
    /// </remarks>
    public event Action<ILogSink, Exception>? SinkFailed;

    public VersioLogger AddSink(ILogSink sink)
    {
        lock (_gate)
        {
            _sinks = [.. _sinks, sink];
        }

        return this;
    }

    public void Log(LogLevel level, string category, string message,
                    Exception? exception = null, bool isHeartbeat = false)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var entry = new LogEvent
        {
            Timestamp = clock.UtcNow,
            Level = level,
            Category = category,
            Message = message,
            OperationId = CurrentOperation.Value,
            IsHeartbeat = isHeartbeat,
            Exception = exception,
        };

        var sinks = _sinks;
        foreach (var sink in sinks)
        {
            try
            {
                sink.Emit(entry);
            }
            catch (Exception ex)
            {
                // A failing sink must never take down the operation being logged. Losing a
                // log line is bad; losing the export because the log file was locked is
                // worse.
                //
                // But it must not be silent either. Reported once per sink, through the
                // sinks that still work, so a dead file sink is visible in the UI instead of
                // being discovered when someone asks for the log after a crash.
                ReportSinkFailure(sinks, sink, ex);
            }
        }
    }

    /// <summary>
    /// Announce a broken sink exactly once, without recursing back into <see cref="Log"/>.
    /// </summary>
    /// <remarks>
    /// Writes straight to the surviving sinks rather than calling <see cref="Log"/>: if the
    /// failure is systemic, routing the complaint through the same path that just failed is
    /// how a logger turns one locked file into a stack overflow.
    /// </remarks>
    private void ReportSinkFailure(ILogSink[] sinks, ILogSink faulted, Exception ex)
    {
        lock (_gate)
        {
            if (!_reportedFaulty.Add(faulted))
            {
                return;
            }
        }

        SinkFailed?.Invoke(faulted, ex);

        var notice = new LogEvent
        {
            Timestamp = clock.UtcNow,
            Level = LogLevel.Error,
            Category = LogCategory.App,
            Message = $"log sink {faulted.GetType().Name} failed and its output is being " +
                      "dropped; further failures from it are not reported",
            OperationId = CurrentOperation.Value,
            Exception = ex,
        };

        foreach (var sink in sinks)
        {
            if (ReferenceEquals(sink, faulted))
            {
                continue;
            }

            try
            {
                sink.Emit(notice);
            }
            catch (Exception)
            {
                // Nowhere left to complain to.
            }
        }
    }

    public string? CurrentOperationId => CurrentOperation.Value;

    public IDisposable BeginOperation(string operationId)
    {
        var previous = CurrentOperation.Value;
        CurrentOperation.Value = operationId;
        return new Scope(() => CurrentOperation.Value = previous);
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            onDispose();
        }
    }
}

/// <summary>
/// Bounded in-memory ring buffer, the source the UI binds to.
/// </summary>
/// <remarks>
/// Bounded on purpose: an export can emit thousands of lines, and an unbounded list would
/// grow until the app died — slowly enough that nobody would connect the two.
/// </remarks>
public sealed class InMemoryLogSink(int capacity = 10_000) : ILogSink
{
    private readonly Queue<LogEvent> _buffer = new();
    private readonly Lock _gate = new();

    public int Capacity { get; } = capacity;

    /// <summary>Raised for every entry. The UI batches these rather than binding directly:
    /// a chatty export would otherwise flood the dispatcher and freeze the window the log
    /// exists to explain.</summary>
    public event Action<LogEvent>? Emitted;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _buffer.Count;
            }
        }
    }

    public void Emit(LogEvent entry)
    {
        lock (_gate)
        {
            _buffer.Enqueue(entry);
            while (_buffer.Count > Capacity)
            {
                _buffer.Dequeue();
            }
        }

        Emitted?.Invoke(entry);
    }

    public IReadOnlyList<LogEvent> Snapshot()
    {
        lock (_gate)
        {
            return [.. _buffer];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _buffer.Clear();
        }
    }
}
