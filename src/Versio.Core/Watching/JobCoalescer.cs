using Versio.Core.Abstractions;

namespace Versio.Core.Watching;

/// <summary>What to do with a change that has just settled.</summary>
public enum CoalesceAction
{
    /// <summary>Nothing running — start now.</summary>
    StartNow,

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
    TimeSpan? minimumInterval = null)
{
    private readonly TimeSpan _minimumInterval = minimumInterval ?? TimeSpan.FromSeconds(30);

    private DateTimeOffset? _runningSince;
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
            // A change arriving during a run is ALWAYS queued, however early it arrives.
            //
            // There used to be a "cancel and restart" answer for the first five seconds,
            // on the reasoning that restarting is cheap that early and avoids finishing
            // work already known to be stale. It was never true, because nothing cancels:
            // no cancellation was ever plumbed through to a running toolchain job, so the
            // orchestrator's only way to obey the instruction was to start a SECOND export
            // beside the first. Observed doing exactly that — two CODESYS instances on one
            // project, each taking 45 s instead of the usual 17-31 because they were
            // contending for the profile and the shared temp state.
            //
            // Queueing loses almost nothing in return: an export reads the file as it is
            // when it opens it, so a save landing during a run is usually captured by that
            // very run, and the follow-up then finds nothing to do at the hash gate and
            // costs a file read rather than a toolchain launch.
            //
            // Restarting could be made real by cancelling the process, and would then be
            // worth having. It is a different piece of work: killing CODESYS mid-export
            // leaves a staging folder behind, which the exporter already warns about.
            if (!_pending)
            {
                _pending = true;
                return CoalesceAction.Queue;
            }

            return CoalesceAction.Coalesced;
        }

        if (_lastStart is { } last && now - last < _minimumInterval)
        {
            // Save-happy engineer: throttle rather than pinning a CODESYS process all day.
            // The pending flag guarantees a final export once things go quiet.
            _pending = true;
            return CoalesceAction.Throttled;
        }

        _runningSince = now;
        _lastStart = now;
        return CoalesceAction.StartNow;
    }

    /// <summary>The running job finished. Returns true when a follow-up must start now.</summary>
    public bool OnJobCompleted()
    {
        _runningSince = null;
        if (!_pending)
        {
            return false;
        }

        _pending = false;
        _runningSince = clock.UtcNow;
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
        _lastStart = now;
        return true;
    }

    public void Reset()
    {
        _runningSince = null;
        _lastStart = null;
        _pending = false;
    }
}
