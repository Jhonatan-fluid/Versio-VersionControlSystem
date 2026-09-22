using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Versio.Infrastructure.Watching;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// The watcher says a file settled once per storm, not once per rewrite.
/// </summary>
/// <remarks>
/// <see cref="RepeatCollapser"/> is tested on its own in Core; this is the half that proves
/// the watcher actually routes its line through it. Real files and a real
/// <see cref="System.IO.FileSystemWatcher"/>, because the timer and the settle check are what
/// is being wired. The file stands in for an IDE's open-project marker, which is rewritten
/// every five seconds for as long as a project is open.
/// </remarks>
public sealed class FileChangeWatcherLogTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "versio-watch-" + Guid.NewGuid().ToString("N")[..8]);

    public FileChangeWatcherLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public async Task A_file_rewritten_again_and_again_is_logged_once_and_still_raised_every_time()
    {
        var marker = Path.Combine(_dir, "Machine.~u");
        await File.WriteAllTextAsync(marker, "0");

        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);

        using var watcher = new FileChangeWatcher(
            new SystemClock(), quietPeriod: TimeSpan.FromMilliseconds(100), logger: logger);

        var settled = 0;
        var each = new SemaphoreSlim(0);
        watcher.Settled += _ =>
        {
            Interlocked.Increment(ref settled);
            each.Release();
        };

        watcher.WatchFile(marker);

        for (var i = 1; i <= 4; i++)
        {
            await File.WriteAllTextAsync(marker, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(await each.WaitAsync(TimeSpan.FromSeconds(10)), $"rewrite {i} never settled");
        }

        // Every rewrite still reaches the listener: the IDE closing is only ever seen as one
        // of these, so folding the EVENT would break the retry this watch exists for.
        Assert.Equal(4, settled);

        var lines = sink.Snapshot()
            .Where(e => e.Message.StartsWith("settled:", StringComparison.Ordinal))
            .ToList();
        Assert.Single(lines);
    }
}
