using Versio.Core.Locking;
using Xunit;

namespace Versio.Core.Tests.Locking;

/// <summary>
/// One repository, one Versio.
/// </summary>
/// <remarks>
/// <para>
/// Safeguards §5.G. <c>LockRecovery.HeldByLiveSession</c> promised <i>"opening read-only"</i>
/// since it was written and nothing implemented it, so a second instance opened fully writable
/// and two orchestrators wrote <c>repo.json</c> and <c>state.json</c> over each other.
/// </para>
/// <para>
/// <b>The direction of every doubt here is the same:</b> when it cannot be known whether the
/// other session is alive, believe that it is. Being wrong that way costs somebody a message;
/// being wrong the other way costs two writers on one repository.
/// </para>
/// </remarks>
public sealed class RepositoryPresenceTests
{
    private static readonly DateTimeOffset Boot =
        new(2026, 8, 14, 6, 0, 0, TimeSpan.Zero);

    private sealed class Machine : IMachineIdentity
    {
        public string MachineName { get; init; } = "ROG-JVM";
        public string UserName { get; init; } = "elli_";
        public int CurrentProcessId { get; init; } = 1000;
        public DateTimeOffset BootId { get; init; } = Boot;
    }

    private sealed class Probe : IProcessProbe
    {
        public bool Alive { get; set; }

        public bool IsVersioProcessAlive(int processId, string sessionId) => Alive;

        public bool IsProcessAlive(int processId) => Alive;
    }

    private static SessionMarker Marker(
        string machine = "ROG-JVM",
        int pid = 2000,
        string? sessionId = null,
        DateTimeOffset? boot = null) => new()
    {
        Machine = machine,
        User = "elli_",
        ProcessId = pid,
        SessionId = sessionId ?? $"{pid}-{(boot ?? Boot).ToUnixTimeSeconds()}",
        BootId = boot ?? Boot,
        OpenedUtc = Boot.AddHours(1),
    };

    [Fact]
    public void No_marker_means_the_repository_is_free()
    {
        var verdict = RepositoryPresence.Evaluate(null, new Machine(), new Probe());

        Assert.Equal(RepositoryAvailability.Free, verdict.Availability);
        Assert.True(verdict.CanOpen);
    }

    [Fact]
    public void A_live_session_on_this_machine_holds_it()
    {
        var verdict = RepositoryPresence.Evaluate(
            Marker(pid: 2000), new Machine(), new Probe { Alive = true });

        Assert.Equal(RepositoryAvailability.HeldElsewhere, verdict.Availability);
        Assert.False(verdict.CanOpen);

        // Names the process, because "already open" invites "where?" from somebody with four
        // windows on three monitors.
        Assert.Contains("pid 2000", verdict.Explanation);

        // And hands it over as a number, so a launch can bring that window forward instead of
        // only naming it.
        Assert.Equal(2000, verdict.LocalHolder);
    }

    [Fact]
    public void A_dead_session_on_this_machine_does_not()
    {
        // A crash must not lock somebody out of their own repository for ever.
        var verdict = RepositoryPresence.Evaluate(
            Marker(pid: 2000), new Machine(), new Probe { Alive = false });

        Assert.Equal(RepositoryAvailability.Free, verdict.Availability);
        Assert.Contains("pid 2000", verdict.Explanation);
    }

    [Fact]
    public void A_marker_from_before_the_last_restart_is_ignored_without_probing()
    {
        // The pid may well be alive again and belong to something else entirely. Boot time is
        // what makes a pid mean anything, which is the same reasoning LockRecoveryPolicy uses.
        var probe = new Probe { Alive = true };

        var verdict = RepositoryPresence.Evaluate(
            Marker(boot: Boot.AddDays(-1)), new Machine(), probe);

        Assert.Equal(RepositoryAvailability.Free, verdict.Availability);
        Assert.Contains("before the last restart", verdict.Explanation);
    }

    [Fact]
    public void This_very_window_reopening_its_own_repository_is_not_a_conflict()
    {
        // The most ordinary action there is. A guard that fired on it would be unusable.
        var machine = new Machine { CurrentProcessId = 1000 };

        var verdict = RepositoryPresence.Evaluate(
            Marker(pid: 1000, sessionId: RepositoryPresence.SessionIdOf(machine)),
            machine,
            new Probe { Alive = true });

        Assert.Equal(RepositoryAvailability.HeldHere, verdict.Availability);
        Assert.True(verdict.CanOpen);
    }

    [Fact]
    public void A_same_pid_from_a_different_session_is_still_somebody_else()
    {
        // Pids are reused. Without the session id, a new Versio that happened to get the old
        // one's number would silently decide the marker was its own.
        var machine = new Machine { CurrentProcessId = 1000 };

        var verdict = RepositoryPresence.Evaluate(
            Marker(pid: 1000, sessionId: "1000-somethingelse"),
            machine,
            new Probe { Alive = true });

        Assert.Equal(RepositoryAvailability.HeldElsewhere, verdict.Availability);
    }

    [Fact]
    public void A_marker_from_another_machine_is_believed_and_says_how_to_clear_it()
    {
        // Its processes cannot be probed from here, and the repository is very possibly on a
        // share — the same rule the vendor's own open-project marker follows. The escape hatch
        // is named, because otherwise a machine that never comes back locks the file for ever.
        var probe = new Probe { Alive = false };

        var verdict = RepositoryPresence.Evaluate(
            Marker(machine: "LINE-2-HMI"), new Machine(), probe);

        Assert.Equal(RepositoryAvailability.HeldElsewhere, verdict.Availability);
        Assert.Contains("LINE-2-HMI", verdict.Explanation);
        Assert.Contains(RepositoryPresence.MarkerPath, verdict.Explanation);

        // A pid on another machine means nothing here. Handing it over would let a launch
        // bring forward whatever local process happens to have the same number.
        Assert.Null(verdict.LocalHolder);
    }

    [Fact]
    public void The_marker_this_process_writes_describes_this_process()
    {
        var machine = new Machine();
        var now = Boot.AddHours(2);

        var marker = RepositoryPresence.MarkerFor(
            machine, RepositoryPresence.SessionIdOf(machine), now);

        Assert.Equal("ROG-JVM", marker.Machine);
        Assert.Equal(1000, marker.ProcessId);
        Assert.Equal(Boot, marker.BootId);
        Assert.Equal(now, marker.OpenedUtc);

        // And reading it straight back is recognised as ours rather than as a rival.
        Assert.Equal(
            RepositoryAvailability.HeldHere,
            RepositoryPresence.Evaluate(marker, machine, new Probe { Alive = true }).Availability);
    }

    [Fact]
    public void The_marker_is_kept_where_git_will_not_carry_it()
    {
        // Who has a repository open on one machine is not something to commit and share with
        // everyone who clones it. .versio/local/ is gitignored by adoption.
        Assert.StartsWith(".versio/local/", RepositoryPresence.MarkerPath);
    }
}
