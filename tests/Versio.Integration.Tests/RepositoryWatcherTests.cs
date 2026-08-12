using Versio.Core.Watching;
using Versio.Infrastructure.Platform;
using Versio.Infrastructure.Watching;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// The watcher against a real filesystem.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RepositoryChangeFilter"/> is unit tested against strings; this checks the part
/// that cannot be faked — that Windows actually reports the events, that a burst settles
/// into one delivery, and that the tick keeps firing. All three have to work for the
/// changed-files panel to follow the repository, and none of them can be proven without a
/// real directory.
/// </para>
/// <para>
/// Timing-sensitive by nature, so every wait is generous: the assertions are about whether
/// something happens at all, never about how fast.
/// </para>
/// </remarks>
public class RepositoryWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "versio-watch-" + Guid.NewGuid().ToString("N")[..8]);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public RepositoryWatcherTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
    }

    private RepositoryWatcher Watch(List<RepositoryChange> sink, ManualResetEventSlim signal) =>
        new(_root, new SystemClock(),
            quietPeriod: TimeSpan.FromMilliseconds(150),
            tickInterval: TimeSpan.FromMilliseconds(50));

    /// <summary>Run an action and collect whatever the watcher reports about it.</summary>
    private List<RepositoryChange> Collect(Action act, int expectAtLeast = 1)
    {
        var sink = new List<RepositoryChange>();
        var signal = new ManualResetEventSlim();

        using var watcher = Watch(sink, signal);
        watcher.Settled += changes =>
        {
            lock (sink)
            {
                sink.AddRange(changes);
            }

            if (sink.Count >= expectAtLeast)
            {
                signal.Set();
            }
        };

        // FileSystemWatcher is not armed the instant the constructor returns.
        Thread.Sleep(250);
        act();

        signal.Wait(Patience);

        lock (sink)
        {
            return [.. sink];
        }
    }

    [Fact]
    public void A_new_working_tree_file_is_reported()
    {
        var changes = Collect(() =>
            File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "class C {}"));

        Assert.Contains(changes, c =>
            c.Relevance == RepositoryChangeRelevance.WorkingTree &&
            c.FullPath.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_deleted_file_is_reported()
    {
        var path = Path.Combine(_root, "src", "Doomed.cs");
        File.WriteAllText(path, "x");

        var changes = Collect(() => File.Delete(path));

        // Deletions are the case a stat-based poll handles worst, and the case a user
        // notices fastest - the row has to disappear.
        Assert.Contains(changes, c =>
            c.FullPath.EndsWith("Doomed.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_write_to_the_git_index_is_reported_as_metadata()
    {
        var changes = Collect(() =>
            File.WriteAllText(Path.Combine(_root, ".git", "index"), "not really an index"));

        // This is how staging or committing from a terminal reaches the app.
        Assert.Contains(changes, c => c.Relevance == RepositoryChangeRelevance.GitMetadata);
    }

    [Fact]
    public void A_ref_update_is_reported_as_metadata()
    {
        var changes = Collect(() =>
            File.WriteAllText(Path.Combine(_root, ".git", "refs", "heads", "main"),
                              "0000000000000000000000000000000000000000"));

        Assert.Contains(changes, c => c.Relevance == RepositoryChangeRelevance.GitMetadata);
    }

    [Fact]
    public void Lock_file_churn_alone_reports_nothing()
    {
        var sink = new List<RepositoryChange>();
        var signal = new ManualResetEventSlim();

        using var watcher = Watch(sink, signal);
        watcher.Settled += changes =>
        {
            lock (sink)
            {
                sink.AddRange(changes);
            }

            signal.Set();
        };

        Thread.Sleep(250);

        // Every git invocation creates and removes one of these, including the status calls
        // the app makes itself. Reacting would be a treadmill.
        var lockFile = Path.Combine(_root, ".git", "index.lock");
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(lockFile, "");
            File.Delete(lockFile);
        }

        Assert.False(signal.Wait(TimeSpan.FromSeconds(2)),
            "lock-file churn woke the watcher");
    }

    [Fact]
    public void A_burst_of_writes_settles_into_a_single_delivery()
    {
        var deliveries = 0;
        var signal = new ManualResetEventSlim();
        var sink = new List<RepositoryChange>();

        using var watcher = Watch(sink, signal);
        watcher.Settled += _ =>
        {
            Interlocked.Increment(ref deliveries);
            signal.Set();
        };

        Thread.Sleep(250);

        // Stand-in for a build or a branch switch: without coalescing this would be 50
        // refreshes, each spawning git.
        for (var i = 0; i < 50; i++)
        {
            File.WriteAllText(Path.Combine(_root, "src", $"F{i}.cs"), "x");
        }

        signal.Wait(Patience);
        Thread.Sleep(500);

        Assert.InRange(Volatile.Read(ref deliveries), 1, 3);
    }

    [Fact]
    public void The_tick_keeps_firing_with_nothing_happening()
    {
        var ticks = 0;
        using var watcher = new RepositoryWatcher(
            _root, new SystemClock(),
            quietPeriod: TimeSpan.FromMilliseconds(150),
            tickInterval: TimeSpan.FromMilliseconds(50));

        watcher.Tick += () => Interlocked.Increment(ref ticks);

        Thread.Sleep(600);

        // The tick drives both throttled work and the periodic backstop. If it stops, a
        // dropped watcher event becomes permanent staleness.
        Assert.True(Volatile.Read(ref ticks) > 3, $"only {ticks} ticks fired");
    }

    [Fact]
    public void Disposing_stops_delivery()
    {
        var fired = new ManualResetEventSlim();
        var sink = new List<RepositoryChange>();

        var watcher = Watch(sink, fired);
        watcher.Settled += _ => fired.Set();
        Thread.Sleep(250);
        watcher.Dispose();

        File.WriteAllText(Path.Combine(_root, "src", "After.cs"), "x");

        // A watcher left running after the repository is switched would refresh the panel
        // from the wrong tree - which looks exactly like the staleness bug it replaced.
        Assert.False(fired.Wait(TimeSpan.FromSeconds(2)), "a disposed watcher still delivered");
    }

    [Fact]
    public void Watching_a_missing_directory_degrades_instead_of_throwing()
    {
        // Network shares and some cloud-sync providers refuse to host a watcher. Failing to
        // open the repository over it would be a worse outcome than polling.
        using var watcher = new RepositoryWatcher(
            Path.Combine(_root, "does-not-exist"), new SystemClock());

        Assert.True(watcher.IsDegraded);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A watcher handle may still be closing. The temp folder is disposable.
        }
    }
}
