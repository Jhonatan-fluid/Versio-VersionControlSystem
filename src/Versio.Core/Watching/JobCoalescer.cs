using Versio.Core.Abstractions;

namespace Versio.Core.Watching;

/// <summary>What to do with a change that has just settled.</summary>
public enum CoalesceAction
{
    /// <summary>Nothing running — start now.</summary>
    StartNow,

    /// <summary>A job is running but only just; cancel and restart rather than doing
    /// work we already know is stale.</summary>
    CancelAndRestart,

    /// <summary>A job is running and has done real work; let it finish and queue one
    /// follow-up.</summary>
    Queue,

    /// <summary>A follow-up is already queued. Queue depth never exceeds one, because an
    /// export always reads the current file: N saves need one export, not N.</summary>
    Coalesced,

    /// <summary>Throttled by the minimum interval between export starts.</summary>
    Throttled,
}

/// <summary>
/// Decides what to do when a change arrives, possibly while an export is already running.
/// </summary>
/// <remarks>
/// The central rule: <b>the queue holds at most one pending job, not a list.</b> Because an
/// export always reads the current state of the file, five saves during a running export do
/// not need five exports — they need one, after the last of them.
/// <para>
/// Worked example from the plan: four saves during one 40 s export produce exactly two
/// exports, and the final state reflects the last save.
/// </para>
/// </remarks>
public sealed class JobCoalescer(
    IClock clock,
    TimeSpan? cancelWindow = null,
    TimeSpan? minimumInterval = null)
{
    private readonly TimeSpan _cancelWindow = cancelWindow ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan _minimumInterval = minimumInterval ?? TimeSpan.FromSeconds(30);

    private DateTimeOffset? _runningSince;
    private DateTimeOffset? _burstStartedAt;
    private DateTimeOffset? _lastStart;
    private bool _pending;

    public bool IsRunning => _runningSince is not null;
    public bool HasPending => _pending;

    /// <summary>A settled change arrived.</summary>
    public CoalesceAction OnChangeSettled()
    {
        var now = clock.UtcNow;

        if (_runningSince is not null)
        {
            if (_pending)
            {
                return CoalesceAction.Coalesced;
            }

            // The cancel window is measured from the START OF THE BURST, not from the last
            // restart. Measuring from the restart livelocks: saves arriving every second
            // with a five second window would cancel and restart forever, and the export
            // would never complete. Found by a 200-save storm test.
            if (now - (_burstStartedAt ?? now) <= _cancelWindow)
            {
                // Cheap to restart this early, and it avoids 40 s of work we know is stale.
                _runningSince = now;
                _lastStart = now;
                return CoalesceAction.CancelAndRestart;
            }

            _pending = true;
            return CoalesceAction.Queue;
        }

        if (_lastStart is { } last && now - last < _minimumInterval)
        {
            // Save-happy engineer: throttle rather than pinning a CODESYS process all day.
            // The pending flag guarantees a final export once things go quiet.
            _pending = true;
            return CoalesceAction.Throttled;
        }

        _runningSince = now;
        _burstStartedAt = now;
        _lastStart = now;
        return CoalesceAction.StartNow;
    }

    /// <summary>The running job finished. Returns true when a follow-up must start now.</summary>
    public bool OnJobCompleted()
    {
        _runningSince = null;
        if (!_pending)
        {
            _burstStartedAt = null;
            return false;
        }

        _pending = false;
        _runningSince = clock.UtcNow;
        _burstStartedAt = _runningSince;
        _lastStart = _runningSince;
        return true;
    }

    /// <summary>Throttle expired with work outstanding — used by the idle tick.</summary>
    public bool TryStartThrottled()
    {
        if (_runningSince is not null || !_pending)
        {
            return false;
        }

        var now = clock.UtcNow;
        if (_lastStart is { } last && now - last < _minimumInterval)
        {
            return false;
        }

        _pending = false;
        _runningSince = now;
        _burstStartedAt = now;
        _lastStart = now;
        return true;
    }

    public void Reset()
    {
        _runningSince = null;
        _burstStartedAt = null;
        _lastStart = null;
        _pending = false;
    }
}
