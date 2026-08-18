using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Logging;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Closing the IDE is the retry now (PHASE2 item 3): the vendor's open-project marker
/// vanishing re-submits both sides of the binding through the ordinary gates.
/// </summary>
/// <remarks>
/// <para>
/// The waiting states themselves — an import refused while the IDE holds the project, an
/// export whose copy-aside failed — are the orchestrator's, and proven in its own suites
/// against real CODESYS. What these prove is the trigger: the marker is watched at all, the
/// storm of rewrites while the IDE works is told from the release, and a release submits
/// through the same door a save does rather than through a second one with its own answers.
/// </para>
/// <para>
/// One test drives the real watcher end to end and pays for it in seconds; the rest drive
/// the handler below the timers, the same split <c>SyncSettledChangeAsync</c>'s own remark
/// argues for.
/// </para>
/// </remarks>
public sealed class IdeReleaseRetryTests
{
    private static void GivenATrackedProject(TempRepo repo)
    {
        var versio = Path.Combine(repo.Path, ".versio");
        Directory.CreateDirectory(versio);

        File.WriteAllText(Path.Combine(versio, "repo.json"), """
        {
          "schema": 1,
          "bindings": [
            {
              "id": "b1",
              "projectPath": "plc/Machine.project",
              "exportPath": "plc/Machine",
              "enabled": true
            }
          ]
        }
        """);

        Directory.CreateDirectory(Path.Combine(repo.Path, "plc", "Machine"));
        File.WriteAllBytes(
            Path.Combine(repo.Path, "plc", "Machine.project"), [0x00, 0x01, 0x02, 0x03]);
    }

    private static string MarkerPath(TempRepo repo) =>
        Path.Combine(repo.Path, "plc", "Machine.~u");

    [Fact]
    public async Task The_markers_are_watched_the_moment_the_repository_opens()
    {
        // The resolution goes through the registered format — Sync/ never spells ".~u" —
        // and deleting the StartWatching block that builds this map turns exactly this red.
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);

        var watched = Assert.Single(shell.Sync.WatchedOccupancyMarkers);
        Assert.Equal(MarkerPath(repo), watched.Key);
        Assert.Equal("b1", watched.Value.Id);
    }

    [Fact]
    public async Task A_marker_that_still_exists_is_the_rewrite_storm_and_stays_quiet()
    {
        // CODESYS replaces the marker every few seconds for as long as it holds the
        // project. That storm reaches this handler all day; a log line per event would be
        // the 24-git-processes-a-minute defect one component over, as noise instead.
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        InMemoryLogSink sink;
        using var shell = TestShell.Build(out sink);
        await shell.OpenAsync(repo.Path);

        var lines = new List<string>();
        sink.Emitted += e => lines.Add(e.Message);

        File.WriteAllText(MarkerPath(repo), "user\nmachine\n1234\n");
        var binding = shell.Sync.WatchedOccupancyMarkers.Values.Single();

        await shell.Sync.RetryAfterIdeReleaseAsync(binding, MarkerPath(repo));

        Assert.DoesNotContain(lines, l => l.Contains("released"));
    }

    [Fact]
    public async Task A_vanished_marker_submits_both_sides_through_the_ordinary_gates()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        InMemoryLogSink sink;
        using var shell = TestShell.Build(out sink);
        await shell.OpenAsync(repo.Path);

        var lines = new List<string>();
        sink.Emitted += e => lines.Add(e.Message);

        var binding = shell.Sync.WatchedOccupancyMarkers.Values.Single();
        await shell.Sync.RetryAfterIdeReleaseAsync(binding, MarkerPath(repo));

        // The release is announced — the reason travels with the jobs, so the log can say
        // why a sync ran with nobody saving — and the gates answer for each side, which on
        // this quiet repository is two quiet refusals rather than work.
        Assert.Contains(lines, l => l.Contains("the IDE released the project"));
    }

    [Fact]
    public async Task Deleting_the_real_marker_file_triggers_the_retry_by_itself()
    {
        // End to end through the real FileChangeWatcher: the marker appears (IDE opens),
        // is deleted (IDE closes), and nothing but the filesystem tells Versio. This is
        // the one test that waits on real timers, and it is the one that proves the
        // feature needs no button.
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        InMemoryLogSink sink;
        using var shell = TestShell.Build(out sink);
        await shell.OpenAsync(repo.Path);

        var lines = new List<string>();
        sink.Emitted += e =>
        {
            lock (lines)
            {
                lines.Add(e.Message);
            }
        };

        File.WriteAllText(MarkerPath(repo), "user\nmachine\n1234\n");

        // Let the creation settle first, so the deletion is its own event rather than
        // part of one write burst the watcher coalesces.
        await Task.Delay(500);
        File.Delete(MarkerPath(repo));

        await Eventually.Until(
            () => { lock (lines) { return lines.Any(l => l.Contains("the IDE released the project")); } },
            "the marker deletion reached the retry");
    }
}
