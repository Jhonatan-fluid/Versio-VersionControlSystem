using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Core.Watching;

namespace Versio.Infrastructure.Watching;

/// <summary>One settled filesystem event, already classified.</summary>
public sealed record RepositoryChange(string FullPath, RepositoryChangeRelevance Relevance);

/// <summary>
/// Watches a working tree so the shell's git view can follow it without being asked.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="FileChangeWatcher"/>, which tracks a known set of files by size
/// and timestamp so the sync engine can tell which specific object changed. This one does
/// not care what changed or whether it really did — it only needs to know that
/// <c>git status</c> is worth running again, and git itself is the source of truth for the
/// answer. That makes it far cheaper: no file enumeration, no stat cache, nothing to keep
/// in sync with the disk.
/// </para>
/// <para>
/// A single recursive watcher covers the working tree and <c>.git</c> together, because
/// <c>.git</c> is inside the tree. <see cref="RepositoryChangeFilter"/> sorts the two apart.
/// </para>
/// <para>
/// Events are raised on threadpool threads. Marshalling to the UI is the caller's job.
/// </para>
/// </remarks>
public sealed class RepositoryWatcher : IDisposable
{
    private readonly string _root;
    private readonly IClock _clock;
    private readonly TimeSpan _quietPeriod;
    private readonly IVersioLogger _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RepositoryChangeRelevance> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private DateTimeOffset _lastEventUtc;
    private bool _disposed;

    /// <summary>A burst of changes has gone quiet.</summary>
    public event Action<IReadOnlyList<RepositoryChange>>? Settled;

    /// <summary>
    /// Fires on every timer tick regardless of activity, so the caller can run throttled
    /// work and the periodic backstop without owning a second timer.
    /// </summary>
    public event Action? Tick;

    /// <summary>True when the OS watcher could not be started; only the tick is live.</summary>
    public bool IsDegraded { get; private set; }

    /// <param name="root">Working tree root.</param>
    /// <param name="quietPeriod">
    /// How long a burst must be silent before it is considered settled. A single save
    /// produces several events (temp file, rename, attribute write) a few milliseconds
    /// apart; anything under about 150 ms would refresh two or three times per save.
    /// </param>
    /// <param name="tickInterval">
    /// How often <see cref="Tick"/> fires. Also the resolution of settle detection, so it
    /// wants to be comfortably shorter than <paramref name="quietPeriod"/>.
    /// </param>
    public RepositoryWatcher(
        string root,
        IClock clock,
        TimeSpan? quietPeriod = null,
        TimeSpan? tickInterval = null,
        IVersioLogger? logger = null)
    {
        _root = root;
        _clock = clock;
        _quietPeriod = quietPeriod ?? TimeSpan.FromMilliseconds(300);
        _log = logger ?? NullLogger.Instance;

        var tick = tickInterval ?? TimeSpan.FromMilliseconds(250);
        _timer = new Timer(_ => OnTimer(), null, tick, tick);

        Start();
    }

    private void Start()
    {
        try
        {
            _watcher = new FileSystemWatcher(_root)
            {
                // The 8 KB default overflows easily: a branch switch or a build rewrites
                // thousands of paths, and an overflow silently discards events.
                InternalBufferSize = 64 * 1024,
                IncludeSubdirectories = true,

                // DirectoryName matters as much as FileName here. Deleting a folder full of
                // tracked files can arrive as one directory event and nothing else.
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
            };

            _watcher.Changed += (_, e) => Enqueue(e.FullPath);
            _watcher.Created += (_, e) => Enqueue(e.FullPath);
            _watcher.Deleted += (_, e) => Enqueue(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                // Both ends matter: a rename can move a file out of an ignored folder into
                // a tracked one, and only the destination path shows that.
                Enqueue(e.OldFullPath);
                Enqueue(e.FullPath);
            };

            _watcher.Error += (_, e) =>
            {
                // Buffer overflow. Events were lost and there is no way to learn which, so
                // force a refresh and let git report the truth.
                _log.Warn(LogCategory.Watch,
                    $"watcher overflow ({e.GetException().Message}) — events were lost, " +
                    "forcing a refresh");
                Enqueue(_root);
            };

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Network shares, some cloud-sync providers and containerised mounts refuse to
            // host a watcher. Degrade to the periodic tick rather than failing to open the
            // repository - a panel that updates every 20 s beats one that does not open.
            IsDegraded = true;

            // Warning, not Debug: the panel now updates on a timer instead of instantly,
            // which is a real behavioural difference the user will notice and should be
            // able to explain without guessing at their storage setup.
            _log.Warn(LogCategory.Watch,
                $"filesystem watching unavailable ({ex.Message}); falling back to polling");
        }
    }

    private void Enqueue(string fullPath)
    {
        var relevance = RepositoryChangeFilter.Classify(_root, fullPath);
        if (relevance == RepositoryChangeRelevance.Ignored)
        {
            return;
        }

        lock (_gate)
        {
            // Metadata beats working-tree for the same path: it is the stronger signal and
            // makes the caller reload branches as well as the file list.
            if (!_pending.TryGetValue(fullPath, out var existing) ||
                relevance == RepositoryChangeRelevance.GitMetadata)
            {
                _pending[fullPath] = relevance;
            }
            else
            {
                _pending[fullPath] = existing;
            }

            _lastEventUtc = _clock.UtcNow;
        }
    }

    private void OnTimer()
    {
        if (_disposed)
        {
            return;
        }

        List<RepositoryChange>? settled = null;
        lock (_gate)
        {
            if (_pending.Count > 0 && _clock.UtcNow - _lastEventUtc >= _quietPeriod)
            {
                settled = [.. _pending.Select(kv => new RepositoryChange(kv.Key, kv.Value))];
                _pending.Clear();
            }
        }

        if (settled is not null)
        {
            Settled?.Invoke(settled);
        }

        // Always tick, even when a burst just settled: the caller uses it to start throttled
        // work, and skipping it here would delay that by a whole interval.
        Tick?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _timer?.Dispose();
        _timer = null;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
