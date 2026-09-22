using Versio.Core.Abstractions;
using Versio.Core.Logging;

namespace Versio.Infrastructure.Watching;

/// <summary>A file that changed and has since stopped changing, with the tier that spotted it.</summary>
public sealed record WatchedChange(string Path, DateTimeOffset DetectedUtc, WatchTier Tier);

/// <summary>Which of the three detection tiers reported a change. Worth recording: a
/// change found by anything other than the watcher means events are being dropped.</summary>
public enum WatchTier
{
    /// <summary>FileSystemWatcher — push-based, near-instant. The normal path.</summary>
    Watcher,

    /// <summary>Periodic stat comparison. Catches what tier 1 dropped.</summary>
    SafetyPoll,

    /// <summary>The watcher reported buffer overflow; everything must be re-checked.</summary>
    OverflowRescan,
}

/// <summary>
/// Three-tier change detection for one repository.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here polls in the happy case.</b> <see cref="FileSystemWatcher"/> is
/// push-based and fires within milliseconds. The 30-second poll exists because watchers
/// genuinely lose events: the internal buffer overflows under a burst and the events are
/// simply gone, and network paths, cloud-synced folders and antivirus all interfere.
/// </para>
/// <para>
/// Settle detection rather than a fixed debounce: a save produces several events (temp
/// file, rename, attribute change) and the file may still be locked when the last one
/// arrives. Waiting for quiet <i>and</i> readability is fast when the write is fast and
/// patient when it is not.
/// </para>
/// <para>
/// None of this is relied on for correctness. The commit gate re-checks hashes before any
/// commit, so the worst case of a completely broken watcher is that the diff appears at
/// commit time instead of at save time.
/// </para>
/// </remarks>
public sealed class FileChangeWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, DateTimeOffset> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folds a file that keeps settling into one line every few minutes. Read under
    /// <c>_gate</c>: the settle timer's callbacks can overlap when one runs long.
    /// </summary>
    private readonly RepeatCollapser _repeats = new();
    private readonly Dictionary<string, (long Size, DateTime Written)> _known
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly IClock _clock;
    private readonly TimeSpan _quietPeriod;
    private readonly Timer _settleTimer;
    private readonly Timer _pollTimer;
    private readonly IVersioLogger _log;

    public event Action<WatchedChange>? Settled;

    public FileChangeWatcher(
        IClock clock,
        TimeSpan? quietPeriod = null,
        TimeSpan? pollInterval = null,
        IVersioLogger? logger = null)
    {
        _clock = clock;
        _quietPeriod = quietPeriod ?? TimeSpan.FromMilliseconds(750);
        _log = logger ?? NullLogger.Instance;

        var poll = pollInterval ?? TimeSpan.FromSeconds(30);

        _settleTimer = new Timer(_ => CheckSettled(), null,
            TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
        _pollTimer = new Timer(_ => SafetyPoll(), null, poll, poll);

        _log.Debug(LogCategory.Watch,
            $"change detection started: quiet period {_quietPeriod.TotalMilliseconds:F0}ms, " +
            $"safety poll every {poll.TotalSeconds:F0}s");
    }

    /// <summary>Watch a single file — the .project.</summary>
    public void WatchFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            // Not watching something the caller asked us to watch is exactly the failure
            // that presents later as "saving does nothing", so it is never silent.
            _log.Warn(LogCategory.Watch,
                $"cannot watch {path}: the containing directory does not exist");
            return;
        }

        Track(path);
        Add(dir, Path.GetFileName(path), includeSubdirectories: false);
        _log.Info(LogCategory.Watch, $"watching file {path}");
    }

    /// <summary>Watch a directory tree — the export root.</summary>
    public void WatchTree(string root)
    {
        if (!Directory.Exists(root))
        {
            _log.Warn(LogCategory.Watch, $"cannot watch {root}: the directory does not exist");
            return;
        }

        var tracked = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            Track(file);
            tracked++;
        }

        Add(root, "*", includeSubdirectories: true);
        _log.Info(LogCategory.Watch, $"watching tree {root} ({tracked} existing file(s))");
    }

    private void Add(string dir, string filter, bool includeSubdirectories)
    {
        var watcher = new FileSystemWatcher(dir, filter)
        {
            // 64 KB. The default 8 KB overflows readily on an export that writes dozens of
            // files, and an overflow silently discards events.
            InternalBufferSize = 64 * 1024,
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };

        watcher.Changed += (_, e) => Enqueue(e.FullPath);
        watcher.Created += (_, e) => Enqueue(e.FullPath);
        watcher.Deleted += (_, e) => Enqueue(e.FullPath);
        watcher.Renamed += (_, e) => Enqueue(e.FullPath);

        // Never swallow this. An overflow means events were lost, and the only correct
        // response is to re-check everything.
        watcher.Error += (_, e) =>
        {
            var error = e.GetException();
            _log.Warn(LogCategory.Watch,
                $"watcher error on {dir} ({error.Message}) — events were lost, forcing a " +
                "full rescan");
            SafetyPoll(WatchTier.OverflowRescan);
        };

        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private void Enqueue(string path)
    {
        lock (_gate)
        {
            _pending[path] = _clock.UtcNow;
        }
    }

    private void CheckSettled()
    {
        List<string> ready;
        lock (_gate)
        {
            var now = _clock.UtcNow;
            ready = _pending.Where(kv => now - kv.Value >= _quietPeriod)
                            .Select(kv => kv.Key)
                            .ToList();
        }

        foreach (var path in ready)
        {
            // Quiet is not enough: the writer may still hold the file. Leaving it pending
            // costs one more 200 ms tick, which is far cheaper than reading a torn file.
            if (!IsReadable(path) && File.Exists(path))
            {
                lock (_gate)
                {
                    _pending[path] = _clock.UtcNow;
                }

                continue;
            }

            lock (_gate)
            {
                _pending.Remove(path);
            }

            Track(path);

            // Debug: one line per saved file, which during an export is dozens. Enough to
            // reconstruct what the watcher saw without making it the loudest thing in the log.
            //
            // Except the same file again and again, which is folded: the IDE rewrites its
            // open-project marker every five seconds for as long as the project is open, and
            // logging each one made an idle window the loudest thing in the log. See
            // RepeatCollapser.
            string? line;
            lock (_gate)
            {
                line = _repeats.Next(path, $"settled: {path}", _clock.UtcNow);
            }

            if (line is not null)
            {
                _log.Debug(LogCategory.Watch, line);
            }

            Settled?.Invoke(new WatchedChange(path, _clock.UtcNow, WatchTier.Watcher));
        }
    }

    private void SafetyPoll(WatchTier tier = WatchTier.SafetyPoll)
    {
        List<string> tracked;
        lock (_gate)
        {
            tracked = [.. _known.Keys];
        }

        foreach (var path in tracked)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    continue;
                }

                bool changed;
                lock (_gate)
                {
                    changed = !_known.TryGetValue(path, out var known) ||
                              known.Size != info.Length ||
                              known.Written != info.LastWriteTimeUtc;
                }

                if (changed)
                {
                    Track(path);

                    // Warning, not Debug. Tier 1 is push-based and should have caught this;
                    // that it did not means events are being dropped on this path — a
                    // network share, a cloud-synced folder, antivirus — and that is worth
                    // knowing before it is blamed on the export.
                    _log.Warn(LogCategory.Watch,
                        $"{tier} found a change the watcher missed: {path}");
                    Settled?.Invoke(new WatchedChange(path, _clock.UtcNow, tier));
                }
            }
            catch (IOException ex)
            {
                // Being written right now; the next poll will catch it.
                _log.Debug(LogCategory.Watch, $"polling {path} deferred: {ex.Message}");
            }
        }
    }

    private void Track(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return;
            }

            lock (_gate)
            {
                _known[path] = (info.Length, info.LastWriteTimeUtc);
            }
        }
        catch (IOException)
        {
            // Locked right now; it will be tracked on a later pass.
        }
    }

    private static bool IsReadable(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _settleTimer.Dispose();
        _pollTimer.Dispose();
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        // Change detection stopping is the sort of thing that gets noticed an hour later.
        // A line here dates it precisely.
        _log.Debug(LogCategory.Watch,
            $"change detection stopped ({_watchers.Count} watcher(s) disposed)");

        _watchers.Clear();
    }
}
