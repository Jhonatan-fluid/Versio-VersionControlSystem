using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Versio.Infrastructure.Watching;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// A folder that moves inside a watched tree, or out of it, is a change to the files it
/// carried; and a tracked file that is simply gone is a change too.
/// </summary>
/// <remarks>
/// <para>
/// Real directories and a real <see cref="System.IO.FileSystemWatcher"/>, because what is
/// being pinned is what Windows says when a directory is renamed or moved: one event, for
/// the directory, and nothing at all for the files under it. Until 2026-10-25 (the review's
/// seventh round) the tree watcher did not listen for directory names, so moving
/// <c>Programs/</c> out of a mirror raised nothing, and the safety poll skipped a tracked
/// file that no longer existed — so the disappearance was never reported by any tier, and
/// the import prompt stayed absent until something else happened to change.
/// </para>
/// <para>
/// The poll is an hour away in the folder tests, so the event arriving proves the watcher
/// tier handled the directory event itself. The assertions name the tracked file, not
/// because Windows promises an event per file — it promises the opposite — but because the
/// watcher derives the file paths from the one directory event it does get, and that
/// derivation is the thing under test. The poll test is the mirror image: a directory that
/// is renamed while a watcher holds its handle raises nothing on that handle (the root tests
/// measured this), so the tracked file under it is gone with no event, and only the poll can
/// say so.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FileChangeWatcherFolderTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "versio-folder-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly InMemoryLogSink _sink = new();
    private readonly ITestOutputHelper _output;

    public FileChangeWatcherFolderTests(ITestOutputHelper output)
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

    private FileChangeWatcher Watcher(TimeSpan? poll = null) => new(
        new SystemClock(),
        quietPeriod: TimeSpan.FromMilliseconds(100),
        pollInterval: poll ?? TimeSpan.FromHours(1),
        logger: new VersioLogger(new SystemClock()).AddSink(_sink));

    private string Log() => string.Join("\n", _sink.Snapshot().Select(e => $"{e.Level} {e.Message}"));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Wait until the listener has seen a path the predicate accepts, or give up.</summary>
    private static async Task<bool> SeenAsync(
        List<WatchedChange> seen, SemaphoreSlim signal, Func<WatchedChange, bool> accept)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (seen)
            {
                if (seen.Any(accept))
                {
                    return true;
                }
            }

            await signal.WaitAsync(TimeSpan.FromMilliseconds(250));
        }

        lock (seen)
        {
            return seen.Any(accept);
        }
    }

    private static (List<WatchedChange> Seen, SemaphoreSlim Signal) Listen(FileChangeWatcher watcher)
    {
        var seen = new List<WatchedChange>();
        var signal = new SemaphoreSlim(0);
        watcher.Settled += c =>
        {
            lock (seen)
            {
                seen.Add(c);
            }

            signal.Release();
        };

        return (seen, signal);
    }

    [Fact]
    public async Task A_folder_moved_out_of_the_tree_reports_the_tracked_files_it_carried()
    {
        // The reviewer's reproduction: Programs/ dragged out of the mirror to a sibling folder.
        var root = Path.Combine(_dir, "Machine");
        var programs = Path.Combine(root, "Programs");
        Directory.CreateDirectory(programs);
        var main = Path.Combine(programs, "Main.st");
        await File.WriteAllTextAsync(main, "PROGRAM Main\n");

        using var watcher = Watcher();
        var (seen, signal) = Listen(watcher);
        watcher.WatchTree(root);

        Directory.Move(programs, Path.Combine(_dir, "Programs.elsewhere"));

        Assert.True(await SeenAsync(seen, signal, c => Same(c.Path, main) && c.Tier == WatchTier.Watcher),
            "the tracked file under the moved folder was never reported. log:\n" + Log());
    }

    [Fact]
    public async Task A_folder_renamed_inside_the_tree_reports_the_old_files_and_the_new_ones()
    {
        var root = Path.Combine(_dir, "Machine");
        var programs = Path.Combine(root, "Programs");
        Directory.CreateDirectory(programs);
        var oldMain = Path.Combine(programs, "Main.st");
        await File.WriteAllTextAsync(oldMain, "PROGRAM Main\n");

        using var watcher = Watcher();
        var (seen, signal) = Listen(watcher);
        watcher.WatchTree(root);

        var renamed = Path.Combine(root, "POUs");
        Directory.Move(programs, renamed);
        var newMain = Path.Combine(renamed, "Main.st");

        // Both sides: the path that is gone (so the mirror's hash moves) and the path that
        // arrived (so it is tracked from now on, and a later edit to it is one event, not
        // a poll's discovery of an unknown file).
        Assert.True(await SeenAsync(seen, signal, c => Same(c.Path, oldMain)),
            "the file's old path under the renamed folder was never reported. log:\n" + Log());
        Assert.True(await SeenAsync(seen, signal, c => Same(c.Path, newMain)),
            "the file's new path under the renamed folder was never reported. log:\n" + Log());
    }

    [Fact]
    public async Task A_temp_file_moved_onto_a_watched_file_reports_the_file_and_not_the_temp()
    {
        // The IDE's heartbeat: a temp sibling written and moved onto the marker every few
        // seconds. The rename's old path is gone, but it was never tracked — the single-file
        // filter never showed it — so reporting it would be a new event per heartbeat about
        // a path nobody watches. Only a TRACKED path that is gone is a change.
        var folder = Path.Combine(_dir, "plc");
        Directory.CreateDirectory(folder);
        var marker = Path.Combine(folder, "Machine.~u");
        await File.WriteAllTextAsync(marker, "0");

        using var watcher = Watcher();
        var (seen, signal) = Listen(watcher);
        watcher.WatchFile(marker);

        var temp = marker + "~";
        await File.WriteAllTextAsync(temp, "1");
        File.Move(temp, marker, overwrite: true);

        Assert.True(await SeenAsync(seen, signal, c => Same(c.Path, marker)),
            "the marker the temp was moved onto never settled. log:\n" + Log());

        // The temp's event, had there been one, carries the same timestamp as the marker's
        // and would settle on the same tick or the next; a few more are plenty.
        await Task.Delay(600);
        lock (seen)
        {
            Assert.DoesNotContain(seen, c => Same(c.Path, temp));
        }
    }

    [Fact]
    public async Task The_poll_reports_a_tracked_file_that_is_gone_once_and_then_forgets_it()
    {
        // WatchFile holds a handle on the file's directory. Renaming that directory raises
        // nothing on the handle — it follows the rename — so the tracked path is gone and
        // no event will ever say so. The poll is the only tier that can.
        var folder = Path.Combine(_dir, "plc");
        Directory.CreateDirectory(folder);
        var project = Path.Combine(folder, "Machine.project");
        await File.WriteAllTextAsync(project, "binary-ish");

        using var watcher = Watcher(poll: TimeSpan.FromMilliseconds(300));
        var (seen, signal) = Listen(watcher);
        watcher.WatchFile(project);

        Directory.Move(folder, Path.Combine(_dir, "plc.elsewhere"));

        Assert.True(await SeenAsync(seen, signal, c => Same(c.Path, project) && c.Tier == WatchTier.SafetyPoll),
            "the poll never reported the tracked file that is gone. log:\n" + Log());

        // Once. A file that is gone stays gone; a poll that kept saying so would be a warning
        // every thirty seconds for as long as the window is open.
        await Task.Delay(1200);
        lock (seen)
        {
            Assert.Single(seen, c => Same(c.Path, project));
        }
    }
}
