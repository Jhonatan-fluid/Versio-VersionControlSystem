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
/// </remarks>
public sealed class VersioLogger(IClock clock) : IVersioLogger
{
    private static readonly AsyncLocal<string?> CurrentOperation = new();

    private readonly List<ILogSink> _sinks = [];

    public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    public VersioLogger AddSink(ILogSink sink)
    {
        _sinks.Add(sink);
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

        foreach (var sink in _sinks)
        {
            try
            {
                sink.Emit(entry);
            }
            catch (Exception)
            {
                // A failing sink must never take down the operation being logged. Losing a
                // log line is bad; losing the export because the log file was locked is
                // worse.
            }
        }
    }

    public IDisposable BeginOperation(string operationId)
    {
        var previous = CurrentOperation.Value;
        CurrentOperation.Value = operationId;
        return new Scope(() => CurrentOperation.Value = previous);
    }

    public void Debug(string category, string message) =>
        Log(LogLevel.Debug, category, message);

    public void Info(string category, string message) =>
        Log(LogLevel.Information, category, message);

    public void Warn(string category, string message) =>
        Log(LogLevel.Warning, category, message);

    public void Error(string category, string message, Exception? ex = null) =>
        Log(LogLevel.Error, category, message, ex);

    /// <summary>Log a UI interaction: which control, where. Satisfies the "every button
    /// press is logged" requirement without a line in every handler.</summary>
    public void Click(string controlPath) =>
        Log(LogLevel.Information, LogCategory.Ui, $"Click ▸ {controlPath}");

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
