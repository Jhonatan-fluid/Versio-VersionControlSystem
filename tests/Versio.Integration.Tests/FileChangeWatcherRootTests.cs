using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Versio.Infrastructure.Watching;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// A mirror root that does not exist yet, or that an export replaced, is watched all the same.
/// </summary>
/// <remarks>
/// <para>
/// Real directories and a real <see cref="System.IO.FileSystemWatcher"/>, because what is
/// being pinned is what Windows does with a handle on a directory that is renamed away and
/// deleted, and what the watcher does about it. Two things were true until 2026-10-24
/// (the review's item 11): <c>WatchTree</c> on a missing root warned and returned, and
/// nothing ever called it again — <c>StartWatching</c> runs on attach and on a rescan that
/// adopted something, never after an export — so a mirror the first export created was
/// never watched and a later edit to it produced no import. And an export replaces the
/// root (rename to <c>.versio_trash</c>, rename the staging tree in, delete the trash),
/// which the watcher's handle follows into the trash; it reported an error, ran one poll
/// over the files it knew, and was never re-armed.
/// </para>
/// <para>
/// A short quiet period, and a poll an hour away: the poll also enumerates the watched
/// roots now and would deliver both of these on its own within a second, which would make
/// a dead re-arm invisible here. With it out of reach, an event arriving proves the
/// watcher was rebuilt — by the parent sentinel for a root that appears, and for a
/// swapped root by the sentinel or by the error the dying handle raises once the trash is
/// deleted, whichever lands first (measured 2026-10-24: with the sentinel ignored the error
/// delivered; with both ignored nothing did). The assertions are on the event arriving, not
/// on which path raised it, because Windows coalesces notifications per directory and the
/// exact sequence is never guaranteed.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FileChangeWatcherRootTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "versio-root-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly InMemoryLogSink _sink = new();
    private readonly ITestOutputHelper _output;

    public FileChangeWatcherRootTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        // What the watcher said, green or red: which path delivered an event is the
        // question when one of these turns flaky, and it is unanswerable afterwards.
        _output.WriteLine(Log());

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private FileChangeWatcher Watcher() => new(
        new SystemClock(),
        quietPeriod: TimeSpan.FromMilliseconds(100),
        pollInterval: TimeSpan.FromHours(1),
        logger: new VersioLogger(new SystemClock()).AddSink(_sink));

    private string Log() => string.Join("\n", _sink.Snapshot().Select(e => $"{e.Level} {e.Message}"));

    [Fact]
    public async Task A_root_that_does_not_exist_yet_is_watched_once_it_appears()
    {
        // Two levels down, as a fresh repository's plc/Machine is: the parent is missing too.
        var root = Path.Combine(_dir, "plc", "Machine");
        using var watcher = Watcher();
        var settled = new SemaphoreSlim(0);
        var seen = new List<string>();
        watcher.Settled += c =>
        {
            lock (seen)
            {
                seen.Add(c.Path);
            }

            settled.Release();
        };

        watcher.WatchTree(root);

        Directory.CreateDirectory(Path.Combine(root, "App"));
        var file = Path.Combine(root, "App", "FB_Motor.st");
        await File.WriteAllTextAsync(file, "FUNCTION_BLOCK FB_Motor\n");

        Assert.True(await settled.WaitAsync(TimeSpan.FromSeconds(10)),
            "a file written into the root that appeared never settled. log:\n" + Log());
        lock (seen)
        {
            Assert.Contains(seen, p => string.Equals(p, file, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task A_root_an_export_swapped_out_is_watched_again()
    {
        var root = Path.Combine(_dir, "Machine");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "old.st"), "old");

        using var watcher = Watcher();
        var settled = new SemaphoreSlim(0);
        var seen = new List<string>();
        watcher.Settled += c =>
        {
            lock (seen)
            {
                seen.Add(c.Path);
            }

            settled.Release();
        };

        watcher.WatchTree(root);

        // What versio_export.py does at the end of an export: the staging tree takes the
        // root's place through a rename pair, and the old tree is deleted last.
        var staging = Path.Combine(_dir, "Machine.staging");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "new.st"), "new");
        var trash = Path.Combine(_dir, "Machine.versio_trash");
        Directory.Move(root, trash);
        Directory.Move(staging, root);
        Directory.Delete(trash, recursive: true);

        // Give the swap's own events time to settle and drain, so the edit below is what is
        // being waited for.
        await Task.Delay(1500);
        while (settled.CurrentCount > 0)
        {
            await settled.WaitAsync();
        }

        var edited = Path.Combine(root, "edited.st");
        await File.WriteAllTextAsync(edited, "edited after the swap");

        var found = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!found && DateTime.UtcNow < deadline)
        {
            await settled.WaitAsync(TimeSpan.FromMilliseconds(250));
            lock (seen)
            {
                found = seen.Any(p => string.Equals(p, edited, StringComparison.OrdinalIgnoreCase));
            }
        }

        Assert.True(found, "an edit to the replaced root never settled. log:\n" + Log());
    }
}
