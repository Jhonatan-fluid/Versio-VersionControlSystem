using Versio.Core.Abstractions;

namespace Versio.Core.Logging;

/// <summary>A long operation currently in flight, and its progress so far.</summary>
public sealed record RunningOperation
{
    public required string OperationId { get; init; }
    public required string Description { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public string? Detail { get; set; }
    public int? Done { get; set; }
    public int? Total { get; set; }

    public TimeSpan Elapsed(IClock clock) => clock.UtcNow - StartedUtc;

    public string Describe(IClock clock)
    {
        var elapsed = $"{Elapsed(clock).TotalSeconds:F0}s";
        var progress = Done is not null && Total is not null ? $" — {Done}/{Total}" : string.Empty;
        var detail = string.IsNullOrEmpty(Detail) ? string.Empty : $" {Detail}";
        return $"♥ {Description} — {elapsed}{progress}{detail}";
    }
}

/// <summary>
/// Proves a long operation is still alive.
/// </summary>
/// <remarks>
/// A CODESYS export takes 17–31 seconds with nothing on screen to show for it, and silence
/// is indistinguishable from a hang. Emitting elapsed time every two seconds is enough on
/// its own; where the script reports progress the detail is included too.
/// <para>
/// Every beat goes to the log file, but the UI collapses them into one updating row — 300
/// identical lines would bury the entries that matter.
/// </para>
/// </remarks>
public sealed class HeartbeatService(IClock clock, VersioLogger logger) : IDisposable
{
    private readonly Dictionary<string, RunningOperation> _running = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private Timer? _timer;

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Raised on every beat so the status bar can show the latest without reading
    /// the log.</summary>
    public event Action<RunningOperation>? Beat;

    public IReadOnlyList<RunningOperation> Running
    {
        get
        {
            lock (_gate)
            {
                return [.. _running.Values];
            }
        }
    }

    public IDisposable Track(string operationId, string description)
    {
        lock (_gate)
        {
            _running[operationId] = new RunningOperation
            {
                OperationId = operationId,
                Description = description,
                StartedUtc = clock.UtcNow,
            };

            _timer ??= new Timer(_ => Tick(), null, Interval, Interval);
        }

        logger.Info(LogCategory.Perf, $"{description} started");
        return new Tracker(this, operationId, description);
    }

    public void Report(string operationId, int? done, int? total, string? detail)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(operationId, out var op))
            {
                return;
            }

            op.Done = done;
            op.Total = total;
            op.Detail = detail;
        }
    }

    private void Tick()
    {
        foreach (var op in Running)
        {
            // Perf, not a vendor category. This service times any long operation - a
            // toolchain run today, a clone tomorrow - and filing its beats under one brand
            // would hide them from anyone filtering for the operation they are waiting on.
            logger.Log(LogLevel.Information, LogCategory.Perf, op.Describe(clock),
                       isHeartbeat: true);
            Beat?.Invoke(op);
        }
    }

    private void Complete(string operationId, string description)
    {
        TimeSpan elapsed;
        lock (_gate)
        {
            if (!_running.Remove(operationId, out var op))
            {
                return;
            }

            elapsed = op.Elapsed(clock);

            if (_running.Count == 0)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        // Anything over 250 ms is worth a duration line: it is what turns "the app felt
        // slow" into a specific step with a number attached.
        if (elapsed > TimeSpan.FromMilliseconds(250))
        {
            logger.Info(LogCategory.Perf, $"{description} finished in {elapsed.TotalSeconds:F1}s");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private sealed class Tracker(HeartbeatService owner, string id, string description) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.Complete(id, description);
        }
    }
}
