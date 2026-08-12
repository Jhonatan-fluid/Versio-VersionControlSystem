using Versio.Core.Abstractions;

namespace Versio.Core.Watching;

/// <summary>What the shell should do about an incoming change.</summary>
public enum RefreshDecision
{
    /// <summary>Not worth a refresh, or suppressed as our own echo.</summary>
    Ignored,

    /// <summary>Start a refresh now.</summary>
    RunNow,

    /// <summary>Something is running or the throttle is closed; one refresh is now owed.</summary>
    Queued,

    /// <summary>A refresh was already owed. The queue never grows past one.</summary>
    AlreadyQueued,
}

/// <summary>
/// Decides when the changed-files panel re-reads git.
/// </summary>
/// <remarks>
/// <para>
/// Three jobs, all of which have to hold at once:
/// </para>
/// <list type="number">
///   <item><b>Coalesce.</b> A save, a build or a branch switch produces a burst of events.
///   They need one refresh, not one each, and the queue is a single slot rather than a list
///   because a refresh always reads current state.</item>
///   <item><b>Throttle.</b> Never spawn git faster than <c>minimumInterval</c>. A background
///   build writing continuously must not turn into a permanent git process.</item>
///   <item><b>Break the echo.</b> <c>git status</c> writes to <c>.git/index</c> itself when
///   it refreshes the stat cache. Treating that write as a reason to run <c>git status</c>
///   is a self-sustaining loop that never idles. After every refresh, metadata events are
///   ignored for a short window.</item>
/// </list>
/// <para>
/// This is <b>not</b> <see cref="JobCoalescer"/>, and the difference is deliberate. That one
/// governs CODESYS exports, which cost tens of seconds — so it can cancel and restart a
/// running job to avoid wasted work. A <c>git status</c> costs milliseconds; there is
/// nothing worth cancelling, and the interesting problem here is the echo, which exports do
/// not have.
/// </para>
/// <para>
/// A backstop tick covers what all of the above cannot: <c>FileSystemWatcher</c> silently
/// drops events when its buffer overflows, and network or cloud-synced folders drop them
/// routinely. Worst case the panel is <c>backstopInterval</c> stale rather than stale
/// forever.
/// </para>
/// </remarks>
public sealed class StatusRefreshPolicy(
    IClock clock,
    TimeSpan? minimumInterval = null,
    TimeSpan? metadataQuietPeriod = null,
    TimeSpan? backstopInterval = null)
{
    /// <summary>Floor on how often git may be spawned.</summary>
    private readonly TimeSpan _minimumInterval = minimumInterval ?? TimeSpan.FromMilliseconds(750);

    /// <summary>How long after a refresh <c>.git</c> events are treated as our own echo.</summary>
    private readonly TimeSpan _metadataQuietPeriod = metadataQuietPeriod ?? TimeSpan.FromSeconds(1);

    /// <summary>Refresh at least this often even when nothing is reported.</summary>
    private readonly TimeSpan _backstopInterval = backstopInterval ?? TimeSpan.FromSeconds(20);

    private DateTimeOffset? _lastStart;
    private DateTimeOffset? _metadataSuppressedUntil;
    private bool _isRunning;
    private bool _pending;

    public bool IsRunning => _isRunning;

    /// <summary>A refresh is owed but has not started.</summary>
    public bool HasPending => _pending;

    /// <summary>A settled change arrived.</summary>
    public RefreshDecision OnChange(RepositoryChangeRelevance relevance)
    {
        if (relevance == RepositoryChangeRelevance.Ignored)
        {
            return RefreshDecision.Ignored;
        }

        var now = clock.UtcNow;

        // Our own footprint. Only metadata is suppressed - Versio does not write to the
        // working tree from here, so a working-tree event is always somebody else and must
        // never be dropped.
        if (relevance == RepositoryChangeRelevance.GitMetadata &&
            _metadataSuppressedUntil is { } until && now < until)
        {
            return RefreshDecision.Ignored;
        }

        if (_isRunning)
        {
            if (_pending)
            {
                return RefreshDecision.AlreadyQueued;
            }

            _pending = true;
            return RefreshDecision.Queued;
        }

        if (_lastStart is { } last && now - last < _minimumInterval)
        {
            // Throttled, not dropped. TryStartPending picks it up once the interval passes,
            // which is what keeps a save-happy editor from producing a stale panel.
            if (_pending)
            {
                return RefreshDecision.AlreadyQueued;
            }

            _pending = true;
            return RefreshDecision.Queued;
        }

        Start(now);
        return RefreshDecision.RunNow;
    }

    /// <summary>
    /// The running refresh finished. Returns true when the queued one should start at once.
    /// </summary>
    public bool OnRefreshCompleted()
    {
        var now = clock.UtcNow;
        _isRunning = false;
        _metadataSuppressedUntil = now + _metadataQuietPeriod;

        if (!_pending)
        {
            return false;
        }

        // Respect the throttle even for queued work; otherwise a steady stream of changes
        // turns "at most one refresh per interval" into "back-to-back refreshes forever".
        if (_lastStart is { } last && now - last < _minimumInterval)
        {
            return false;
        }

        _pending = false;
        Start(now);
        return true;
    }

    /// <summary>Idle tick: start owed work whose throttle has now expired.</summary>
    public bool TryStartPending()
    {
        if (_isRunning || !_pending)
        {
            return false;
        }

        var now = clock.UtcNow;
        if (_lastStart is { } last && now - last < _minimumInterval)
        {
            return false;
        }

        _pending = false;
        Start(now);
        return true;
    }

    /// <summary>
    /// Idle tick: has it been long enough since the last refresh to re-read git regardless?
    /// </summary>
    /// <remarks>
    /// This is the only defence against a watcher that has silently stopped delivering. It
    /// costs one cheap git call per interval and converts "stale forever" - which is a bug
    /// report - into "stale for a few seconds", which nobody notices.
    /// </remarks>
    public bool ShouldRunBackstop()
    {
        if (_isRunning || _pending)
        {
            return false;
        }

        var now = clock.UtcNow;
        if (_lastStart is not { } last)
        {
            // Nothing has ever run; the caller does an initial refresh on open.
            return false;
        }

        if (now - last < _backstopInterval)
        {
            return false;
        }

        Start(now);
        return true;
    }

    /// <summary>Forget everything. Used when the shell switches repository.</summary>
    public void Reset()
    {
        _lastStart = null;
        _metadataSuppressedUntil = null;
        _isRunning = false;
        _pending = false;
    }

    /// <summary>
    /// Record a refresh that the caller started outside the policy — opening a repository,
    /// or the toolbar button.
    /// </summary>
    /// <remarks>
    /// Without this the policy's throttle and echo suppression do not apply to manual
    /// refreshes, and a click would be followed immediately by an automatic refresh
    /// triggered by the index write the click itself caused.
    /// </remarks>
    public void NoteExternalStart() => Start(clock.UtcNow);

    private void Start(DateTimeOffset now)
    {
        _isRunning = true;
        _lastStart = now;
    }
}
