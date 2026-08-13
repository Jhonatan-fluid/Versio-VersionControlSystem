using Versio.Core.Plc.Codesys;
using Versio.Infrastructure.Platform;
using Versio.Infrastructure.Watching;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// The watcher stays quiet while a PLC IDE beats on its lock marker, and loud about everything else.
/// </summary>
/// <remarks>
/// <para>
/// Measured in the running app before this existed: with a project open in CODESYS and nothing
/// happening, Versio ran a <c>git status</c> roughly every 2.5 seconds — about 24 git processes
/// a minute, forever. CODESYS rewrites <c>&lt;project&gt;.~u</c> every few seconds while it
/// holds a project, through a transient <c>&lt;project&gt;.~u~</c> sibling, and each write also
/// moved the containing folder's timestamp.
/// </para>
/// <para>
/// <b>This is a real <see cref="RepositoryWatcher"/> over a real directory</b>, because the
/// pure filter cannot answer the question that actually matters: whether the events Windows
/// raises for that sequence reach a refresh. The rule was right in unit tests while the app
/// still spawned git every five seconds — twice, for two different reasons.
/// </para>
/// <para>
/// Both failure directions are tested, and the second is the dangerous one: a watcher that
/// filters too much makes the panel silently stale, which is worse than the noise it replaced.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class WatcherQuietWhileIdleTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "versio-idle-" + Guid.NewGuid().ToString("N")[..8]);

    private const string Project = "plc/Machine.project";

    public WatcherQuietWhileIdleTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(Path.Combine(_root, "plc"));
        File.WriteAllText(Path.Combine(_root, "plc", "Machine.project"), "binary-ish");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private RepositoryWatcher Watch() =>
        new(_root,
            new SystemClock(),
            quietPeriod: TimeSpan.FromMilliseconds(100),
            tickInterval: TimeSpan.FromMilliseconds(50),
            knownIgnored: CodesysProjectFormat.Instance.TransientPatternsFor(Project));

    private async Task<List<string>> ObserveAsync(Action work, int settleMs = 900)
    {
        var seen = new List<string>();
        using var watcher = Watch();

        watcher.Settled += changes =>
        {
            lock (seen)
            {
                seen.AddRange(changes.Select(c => c.FullPath));
            }
        };

        // Let the watcher arm itself before anything is written, or the first events are
        // raced and the test passes for the wrong reason.
        await Task.Delay(200);
        work();
        await Task.Delay(settleMs);

        lock (seen)
        {
            foreach (var path in seen)
            {
                _output.WriteLine(path);
            }

            return [.. seen];
        }
    }

    /// <summary>What CODESYS does every few seconds while it holds a project.</summary>
    private void RewriteMarker()
    {
        var marker = Path.Combine(_root, "plc", "Machine.~u");
        var temp = marker + "~";

        for (var i = 0; i < 3; i++)
        {
            File.WriteAllText(temp, $"elli_|ROG-JVM|{i}");
            File.Move(temp, marker, overwrite: true);
        }
    }

    [Fact]
    public async Task An_idle_ide_rewriting_its_marker_produces_no_refresh()
    {
        var seen = await ObserveAsync(RewriteMarker);

        Assert.Empty(seen);
    }

    [Fact]
    public async Task A_real_edit_beside_the_marker_is_still_reported()
    {
        // The failure direction that matters. The marker's folder is the project's folder, so
        // a rule that dropped events by folder rather than by name would take this with it.
        var seen = await ObserveAsync(() =>
        {
            RewriteMarker();
            File.WriteAllText(Path.Combine(_root, "plc", "notes.md"), "a real edit");
        });

        Assert.Contains(seen, p => p.EndsWith("notes.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_file_in_a_folder_is_reported_even_though_the_folder_write_is_not()
    {
        // The directory-write rule rests entirely on this: the folder's own event carries no
        // information the child's event does not, so dropping it loses nothing. If Windows
        // ever stopped raising the child event, this is what would catch it.
        var seen = await ObserveAsync(
            () => File.WriteAllText(Path.Combine(_root, "plc", "brand-new.st"), "PROGRAM P"));

        Assert.Contains(seen, p => p.EndsWith("brand-new.st", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_deleted_file_is_still_reported()
    {
        var doomed = Path.Combine(_root, "plc", "doomed.st");
        File.WriteAllText(doomed, "PROGRAM P");

        var seen = await ObserveAsync(() => File.Delete(doomed));

        Assert.Contains(seen, p => p.EndsWith("doomed.st", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_project_itself_is_never_filtered_by_the_markers_rule()
    {
        // Machine.~u* must not swallow Machine.project. The stem is shared and the glob is
        // one character away from doing exactly that.
        var seen = await ObserveAsync(
            () => File.WriteAllText(Path.Combine(_root, "plc", "Machine.project"), "saved"));

        Assert.Contains(seen, p => p.EndsWith("Machine.project", StringComparison.Ordinal));
    }
}
