using Versio.Core.Locking;
using Versio.Core.Logging;
using Versio.Infrastructure.FileSystem;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Taking the repository lock and giving it back are both in the log.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> <c>Write</c> logged its success; <c>Release</c> logged only when it
/// refused (the marker belongs to another session) and when it failed (the file would not
/// delete). The path that runs on every ordinary exit said nothing, so a run's own log recorded
/// the claim and never the release — and a clean shutdown left the same trace as a crash.
/// </para>
/// <para>
/// The evidence that distinguishes them existed, but in the wrong place: the <i>next</i> launch
/// reports finding a marker whose process is gone. That is in a different run's log, and
/// useless to anybody reading the log of the session that misbehaved.
/// </para>
/// <para>
/// <b>Both halves are asserted, not just the new one.</b> A test for the release alone would go
/// on passing if the claim line were ever dropped, and the pair is the point — one without the
/// other is what made the log ambiguous in the first place.
/// </para>
/// </remarks>
public sealed class SessionMarkerLogTests
{
    [Fact]
    public void Claiming_and_releasing_the_repository_are_both_recorded()
    {
        using var repo = new TempRepo();

        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        var store = new JsonSessionMarkerStore(repo.Path, logger);

        var marker = new SessionMarker
        {
            Machine = "test-machine",
            User = "tester",
            ProcessId = 4321,
            SessionId = "session-under-test",
            BootId = DateTimeOffset.UnixEpoch,
            OpenedUtc = DateTimeOffset.UnixEpoch,
        };

        store.Write(marker);
        store.Release(marker.SessionId);

        var lockLines = sink.Snapshot()
            .Where(entry => entry.Category == LogCategory.Lock)
            .Select(entry => entry.Message)
            .ToList();

        Assert.Contains(lockLines, line => line.Contains("claimed this repository"));
        Assert.Contains(lockLines, line => line.Contains("released this repository"));
    }

    /// <summary>
    /// Refusing to release somebody else's marker is a different sentence, and stays one.
    /// </summary>
    /// <remarks>
    /// Pinned because the new line sits in the same method as that refusal, one branch apart.
    /// A release that announced itself on the path where it did nothing would be worse than
    /// the silence it replaced — it would state, in the log, that a lock this process never
    /// held had been handed back.
    /// </remarks>
    [Fact]
    public void Refusing_to_release_another_sessions_marker_does_not_claim_to_have_released_it()
    {
        using var repo = new TempRepo();

        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        var store = new JsonSessionMarkerStore(repo.Path, logger);

        store.Write(new SessionMarker
        {
            Machine = "test-machine",
            User = "tester",
            ProcessId = 4321,
            SessionId = "somebody-else",
            BootId = DateTimeOffset.UnixEpoch,
            OpenedUtc = DateTimeOffset.UnixEpoch,
        });

        store.Release("not-the-session-that-holds-it");

        var lockLines = sink.Snapshot()
            .Where(entry => entry.Category == LogCategory.Lock)
            .Select(entry => entry.Message)
            .ToList();

        Assert.Contains(lockLines, line => line.Contains("not releasing"));
        Assert.DoesNotContain(lockLines, line => line.Contains("released this repository"));

        // And it really is still there, so the log and the disk agree.
        Assert.NotNull(store.Read());
    }
}
