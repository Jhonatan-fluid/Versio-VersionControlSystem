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
/// <b>A tree is watched for its path, not for the directory that happens to be there.</b>
/// Two things were true until 2026-10-24 (the review's item 11). <see cref="WatchTree"/> on a
/// root that did not exist warned and returned, and nothing called it again — the view model
/// starts watching on attach and on a rescan that adopted something, never after an export —
/// so a mirror the first export created was never watched, and a later edit to it produced
/// no import. And an export replaces the root wholesale (rename to <c>.versio_trash</c>,
/// rename the staging tree in, delete the trash); a <see cref="FileSystemWatcher"/> holds a
/// handle on the directory, which follows the rename into the trash — silently; the rename
/// raises nothing on it — and reports <c>Access is denied</c> once the trash is deleted,
/// after the new tree is already in place. The error handler ran one poll over the files it
/// knew and left the dead handle where it was. Two answers now, because neither alone
/// covers both shapes of swap: every tree has a sentinel on its parent, watching for the
/// root itself to be created, renamed or deleted, and the tree watcher is rebuilt on the
/// new directory when it is — so the new tree is watched the moment it lands, not when
/// the trash goes; and an error rebuilds the watcher before the rescan instead of only
/// rescanning, which covers a handle that dies without a rename. A root whose parent is
/// missing too is waited for from the nearest ancestor that exists.
/// </para>
/// <para>
/// <b>A folder is its files, and a tracked file that is gone is a change.</b> Windows
/// reports a directory rename, move or deletion as one event about the directory and none
/// about what it carried, and until 2026-10-25 (the review's seventh round) the tree watcher
/// did not listen for directory names at all, while the poll skipped a tracked file that
/// no longer existed. So <c>Programs/</c> dragged out of a mirror was invisible to every
/// tier, and the import prompt stayed absent until something else changed. Now a directory
/// event is translated into the tracked files that were under the old path and the files
/// found under the new one (<see cref="OnTreeEvent"/>), the poll reports a tracked file
/// that is gone — once, then forgets it — and a root swap re-checks what was tracked under
/// the old root for the same reason.
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
    private readonly List<Tree> _trees = [];
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
    private bool _disposed;

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
        var watcher = Create(dir, Path.GetFileName(path), includeSubdirectories: false,
            NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            onEvent: e =>
            {
                // The file renamed away: its old path is a tracked path that is gone, enqueued
                // so it is reported and forgotten now rather than found by the poll as a
                // disappearance "missed". Only when tracked, though — the common rename here
                // is the IDE's heartbeat moving a temp sibling ONTO the watched name, and the
                // temp, which the filter never showed us, is nobody's change.
                if (e is RenamedEventArgs renamed && IsTracked(renamed.OldFullPath))
                {
                    Enqueue(renamed.OldFullPath);
                }

                Enqueue(e.FullPath);
            },
            onError: ex =>
            {
                // Never swallow this. An overflow means events were lost, and the only
                // correct response is to re-check everything.
                _log.Warn(LogCategory.Watch,
                    $"watcher error on {dir} ({ex.Message}) — events were lost, forcing a " +
                    "full rescan");
                SafetyPoll(WatchTier.OverflowRescan);
            });

        lock (_gate)
        {
            _watchers.Add(watcher);
        }

        _log.Info(LogCategory.Watch, $"watching file {path}");
    }

    /// <summary>Watch a directory tree — the export root — whether or not it exists yet.</summary>
    public void WatchTree(string root)
    {
        var tree = new Tree(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        lock (_gate)
        {
            _trees.Add(tree);
        }

        Arm(tree, Existing.Track);
    }

    /// <summary>What to do with the files already under a root when its watcher is built.</summary>
    private enum Existing
    {
        /// <summary>They are the baseline: the first look at a tree that was already there.</summary>
        Track,

        /// <summary>They are all new: the root was not there a moment ago, so each is a change.</summary>
        Enqueue,

        /// <summary>
        /// Leave them to the rescan: the watcher is being rebuilt after losing events, and
        /// recording their stats now would hide whatever changed while it was blind.
        /// </summary>
        Leave,
    }

    /// <summary>
    /// Build a tree's watchers for the directory that is at its path now: the recursive
    /// watcher and the parent sentinel when the root exists, the nearest-ancestor sentinel
    /// when it does not. Whatever watched before is dropped — it may be holding the handle
    /// of a directory that is now in the trash.
    /// </summary>
    private void Arm(Tree tree, Existing existing)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            tree.Watcher?.Dispose();
            tree.Watcher = null;
            tree.Sentinel?.Dispose();
            tree.Sentinel = null;

            if (Directory.Exists(tree.Root))
            {
                Attach(tree, existing);
            }
            else
            {
                WaitFor(tree);
            }
        }
    }

    private void Attach(Tree tree, Existing existing)
    {
        var files = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(tree.Root, "*", SearchOption.AllDirectories))
            {
                switch (existing)
                {
                    case Existing.Track:
                        Track(file);
                        break;
                    case Existing.Enqueue:
                        Enqueue(file);
                        break;
                }

                files++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The root vanished under the enumeration: an export mid-swap. The sentinel
            // below sees it come back, and the poll covers whatever was counted.
            _log.Debug(LogCategory.Watch, $"enumerating {tree.Root} stopped early: {ex.Message}");
        }

        // DirectoryName too, since 2026-10-25 (the review's seventh round). Without it a
        // folder renamed, moved out of the tree or moved in raised nothing at all: Windows
        // reports the directory, once, and never the files it carried — so Programs/ dragged
        // out of a mirror was invisible to this tier, and the poll below skipped files that
        // no longer existed, so no tier ever said so.
        tree.Watcher = Create(tree.Root, "*", includeSubdirectories: true,
            NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            onEvent: e => OnTreeEvent(tree, e),
            onError: ex => OnTreeError(tree, ex));

        // The sentinel on the parent: the only thing that sees the ROOT renamed away or
        // deleted, because the tree watcher's handle goes with the directory and says nothing.
        var parent = Path.GetDirectoryName(tree.Root);
        if (parent is not null && Directory.Exists(parent))
        {
            tree.Sentinel = Create(parent, "*", includeSubdirectories: false,
                NotifyFilters.DirectoryName,
                onEvent: e => OnSentinelEvent(tree, e),
                onError: ex => OnTreeError(tree, ex));
        }

        _log.Info(LogCategory.Watch, existing switch
        {
            Existing.Track => $"watching tree {tree.Root} ({files} existing file(s))",
            Existing.Enqueue => $"watching tree {tree.Root}, which appeared with {files} file(s)",
            _ => $"watching tree {tree.Root} again ({files} file(s), to be rescanned)",
        });
    }

    private void WaitFor(Tree tree)
    {
        // The nearest ancestor that exists. Recursive unless it is the direct parent,
        // because the root may be created several levels down in one go.
        var ancestor = Path.GetDirectoryName(tree.Root);
        while (ancestor is not null && !Directory.Exists(ancestor))
        {
            ancestor = Path.GetDirectoryName(ancestor);
        }

        if (ancestor is null)
        {
            // Not even the drive. Still said, because "saving does nothing" is what this
            // becomes an hour later.
            _log.Warn(LogCategory.Watch,
                $"cannot watch {tree.Root}: no part of the path exists");
            return;
        }

        var direct = string.Equals(ancestor, Path.GetDirectoryName(tree.Root), StringComparison.OrdinalIgnoreCase);
        tree.Sentinel = Create(ancestor, "*", includeSubdirectories: !direct,
            NotifyFilters.DirectoryName,
            onEvent: e => OnSentinelEvent(tree, e),
            onError: ex => OnTreeError(tree, ex));

        _log.Info(LogCategory.Watch,
            $"cannot watch {tree.Root} yet: the directory does not exist — watching {ancestor} for it to appear");
    }

    /// <summary>A directory came or went somewhere between the sentinel and the root.</summary>
    private void OnSentinelEvent(Tree tree, FileSystemEventArgs e)
    {
        var aboutRoot = IsRoot(tree, e.FullPath)
                        || (e is RenamedEventArgs renamed && IsRoot(tree, renamed.OldFullPath));

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (aboutRoot)
            {
                // Created, renamed in, renamed out or deleted: whichever it was, the directory
                // at the root's path is not the one the watcher holds. Rebuilt on what is
                // there now; what is there now is all new to the watcher.
                //
                // And what was tracked under the old one is re-checked, for the same reason a
                // subfolder's rename re-checks its files (OnTreeEvent): a file the new tree
                // does not have is a change, and left in the tracked set it would surface at
                // the next poll as "a change the watcher missed" — a false alarm after every
                // export that removes a file, because the watcher did see the swap.
                var carried = TrackedUnder(tree.Root);
                foreach (var file in carried)
                {
                    Enqueue(file);
                }

                _log.Info(LogCategory.Watch,
                    (Directory.Exists(tree.Root)
                        ? $"the tree {tree.Root} was replaced; watching the new one"
                        : $"the tree {tree.Root} is gone; watching {Path.GetDirectoryName(tree.Root)} for it to come back")
                    + $"; {carried.Count} tracked file(s) under the old one re-checked");
                Arm(tree, Existing.Enqueue);
                return;
            }

            // A deeper ancestor's sentinel sees every directory event under it; the one that
            // matters is the root existing while nothing watches it.
            if (tree.Watcher is null && Directory.Exists(tree.Root))
            {
                Arm(tree, Existing.Enqueue);
            }
        }
    }

    private static bool IsRoot(Tree tree, string path) =>
        string.Equals(Path.TrimEndingDirectorySeparator(path), tree.Root, StringComparison.OrdinalIgnoreCase);

    /// <summary>Something under a tree's root changed: a file, or a folder standing in for its files.</summary>
    /// <remarks>
    /// A directory event is one event about the directory and none about its contents, so it
    /// is translated here into what the consumer understands: the tracked files whose path
    /// began with the old one (each now a path that is gone — a change), and the files found
    /// under the new one (each new to the watcher — a change). The directory's own path is not
    /// enqueued for them, because the orchestrator hashes the whole tree on any path under its
    /// root and the files already name it. A deleted path cannot be told from a deleted
    /// folder once it is gone, so a deletion with nothing tracked under it is enqueued as the
    /// file it most likely was — and an empty folder going is a harmless extra.
    /// </remarks>
    private void OnTreeEvent(Tree tree, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        switch (e.ChangeType)
        {
            case WatcherChangeTypes.Deleted:
            {
                var carried = TrackedUnder(e.FullPath);
                if (carried.Count == 0)
                {
                    Enqueue(e.FullPath);
                    return;
                }

                foreach (var file in carried)
                {
                    Enqueue(file);
                }

                _log.Info(LogCategory.Watch,
                    $"the folder {e.FullPath} was deleted or moved out of {tree.Root}; " +
                    $"{carried.Count} tracked file(s) went with it");
                return;
            }

            case WatcherChangeTypes.Renamed when e is RenamedEventArgs renamed:
            {
                if (!Directory.Exists(renamed.FullPath))
                {
                    // A file. Its old path is gone and its new one is new; both are changes,
                    // and enqueuing the old one is what stops the poll from later reporting
                    // it as a disappearance the watcher missed.
                    Enqueue(renamed.OldFullPath);
                    Enqueue(renamed.FullPath);
                    return;
                }

                var carried = TrackedUnder(renamed.OldFullPath);
                foreach (var file in carried)
                {
                    Enqueue(file);
                }

                var arrived = EnqueueUnder(renamed.FullPath);
                _log.Info(LogCategory.Watch,
                    $"the folder {renamed.OldFullPath} was renamed to {renamed.FullPath}; " +
                    $"{carried.Count} tracked file(s) went with it, {arrived} file(s) now under the new name");
                return;
            }

            case WatcherChangeTypes.Created when Directory.Exists(e.FullPath):
            {
                // A folder created empty has nothing to say yet; its files announce
                // themselves. A folder moved in from outside the tree is the one that matters:
                // Windows reports the directory and not one of the files it brought.
                var arrived = EnqueueUnder(e.FullPath);
                if (arrived > 0)
                {
                    _log.Info(LogCategory.Watch,
                        $"the folder {e.FullPath} appeared in {tree.Root} with {arrived} file(s)");
                }

                return;
            }

            default:
                Enqueue(e.FullPath);
                return;
        }
    }

    private bool IsTracked(string path)
    {
        lock (_gate)
        {
            return _known.ContainsKey(path);
        }
    }

    /// <summary>The tracked files whose path is under the folder, as it was named when they were tracked.</summary>
    private List<string> TrackedUnder(string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        lock (_gate)
        {
            return _known.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    /// <summary>Enqueue every file under a folder that just arrived; returns how many.</summary>
    private int EnqueueUnder(string folder)
    {
        var count = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                Enqueue(file);
                count++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Renamed again, or gone, before it could be listed. The next event about it, or
            // the poll's walk of the root, finds whatever is there then.
            _log.Debug(LogCategory.Watch, $"enumerating {folder} stopped early: {ex.Message}");
        }

        return count;
    }

    private void OnTreeError(Tree tree, Exception error)
    {
        // Never swallow this. An overflow means events were lost, and the only correct
        // response is to re-check everything — and, since 2026-10-24, to rebuild the
        // watcher, because the error used to leave a dead handle in place for good.
        _log.Warn(LogCategory.Watch,
            $"watcher error on {tree.Root} ({error.Message}) — events were lost, re-arming " +
            "and forcing a full rescan");
        Arm(tree, Existing.Leave);
        SafetyPoll(WatchTier.OverflowRescan);
    }

    private static FileSystemWatcher Create(
        string dir,
        string filter,
        bool includeSubdirectories,
        NotifyFilters notify,
        Action<FileSystemEventArgs> onEvent,
        Action<Exception> onError)
    {
        var watcher = new FileSystemWatcher(dir, filter)
        {
            // 64 KB. The default 8 KB overflows readily on an export that writes dozens of
            // files, and an overflow silently discards events.
            InternalBufferSize = 64 * 1024,
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = notify,
        };

        watcher.Changed += (_, e) => onEvent(e);
        watcher.Created += (_, e) => onEvent(e);
        watcher.Deleted += (_, e) => onEvent(e);
        watcher.Renamed += (_, e) => onEvent(e);
        watcher.Error += (_, e) => onError(e.GetException());

        watcher.EnableRaisingEvents = true;
        return watcher;
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
        List<string> roots;
        lock (_gate)
        {
            tracked = [.. _known.Keys];
            roots = [.. _trees.Where(t => t.Watcher is not null).Select(t => t.Root)];
        }

        foreach (var path in tracked)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    // A tracked file that is gone is a change — the one this poll used to
                    // skip, which is how a folder moved out of a mirror went unreported by
                    // every tier until 2026-10-25. Said once: Track forgets it, so the next
                    // poll has nothing to say about it. Not said at all when the watcher
                    // already has it pending, because then nothing was missed.
                    bool pending;
                    lock (_gate)
                    {
                        pending = _pending.ContainsKey(path);
                    }

                    if (pending)
                    {
                        continue;
                    }

                    Track(path);
                    _log.Warn(LogCategory.Watch,
                        $"{tier} found a tracked file gone that the watcher missed: {path}");
                    Settled?.Invoke(new WatchedChange(path, _clock.UtcNow, tier));
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

        // The roots too, not only the files already known: a file that appeared while the
        // watcher was blind — written between a root appearing and its watcher attaching, or
        // under a handle that had quietly followed a rename — is in no list, and polling only
        // what was known could never find it (the 2026-10-24 review's item 11).
        foreach (var root in roots)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    bool unknown;
                    lock (_gate)
                    {
                        unknown = !_known.ContainsKey(file) && !_pending.ContainsKey(file);
                    }

                    if (!unknown)
                    {
                        continue;
                    }

                    Track(file);
                    _log.Warn(LogCategory.Watch, $"{tier} found a file the watcher missed: {file}");
                    Settled?.Invoke(new WatchedChange(file, _clock.UtcNow, tier));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Mid-swap, or a folder that refuses to be listed; the next poll tries again.
                _log.Debug(LogCategory.Watch, $"polling {root} deferred: {ex.Message}");
            }
        }
    }

    /// <summary>Record a file's stats as the baseline — or, if it is gone, forget it.</summary>
    /// <remarks>
    /// Forgetting is what makes "a tracked file that is gone is a change" a change reported
    /// once: every path that reaches the listener comes through here first, so a deletion
    /// the watcher saw, a folder's carried files, and a disappearance the poll found all
    /// leave the tracked set at the moment they are reported, and the poll has no reason to
    /// mention them again.
    /// </remarks>
    private void Track(string path)
    {
        try
        {
            var info = new FileInfo(path);
            lock (_gate)
            {
                if (info.Exists)
                {
                    _known[path] = (info.Length, info.LastWriteTimeUtc);
                }
                else
                {
                    _known.Remove(path);
                }
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

        int disposed;
        lock (_gate)
        {
            _disposed = true;
            disposed = _watchers.Count;
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();

            foreach (var tree in _trees)
            {
                if (tree.Watcher is not null)
                {
                    disposed++;
                    tree.Watcher.EnableRaisingEvents = false;
                    tree.Watcher.Dispose();
                    tree.Watcher = null;
                }

                tree.Sentinel?.Dispose();
                tree.Sentinel = null;
            }

            _trees.Clear();
        }

        // Change detection stopping is the sort of thing that gets noticed an hour later.
        // A line here dates it precisely.
        _log.Debug(LogCategory.Watch,
            $"change detection stopped ({disposed} watcher(s) disposed)");
    }

    /// <summary>One watched export root: the watcher over it while it exists, and the sentinel that sees it come and go.</summary>
    private sealed class Tree(string root)
    {
        public string Root { get; } = root;

        public FileSystemWatcher? Watcher { get; set; }

        public FileSystemWatcher? Sentinel { get; set; }
    }
}
