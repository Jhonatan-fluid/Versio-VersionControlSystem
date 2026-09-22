namespace Versio.Core.Locking;

/// <summary>Who has this repository open, as recorded on disk.</summary>
/// <remarks>
/// The same five fields <see cref="LockOwner"/> carries, and for the same reason: machine and
/// boot decide whether the process id means anything, and the session id distinguishes two
/// Versio processes that happen to reuse a pid.
/// </remarks>
public sealed record SessionMarker
{
    public required string Machine { get; init; }

    public required string User { get; init; }

    public required int ProcessId { get; init; }

    public required string SessionId { get; init; }

    /// <summary>Machine boot time. A marker from before a reboot describes a dead process.</summary>
    public required DateTimeOffset BootId { get; init; }

    public required DateTimeOffset OpenedUtc { get; init; }
}

/// <summary>Whether this process may open a repository.</summary>
public enum RepositoryAvailability
{
    /// <summary>Nobody has it, or the marker describes a process that is gone.</summary>
    Free,

    /// <summary>This very process already has it. Reopening is not a conflict.</summary>
    HeldHere,

    /// <summary>Another Versio has it, and that Versio is running.</summary>
    HeldElsewhere,
}

/// <summary>What the marker means for this process, and the sentence to say about it.</summary>
/// <param name="Availability">Whether this process may open the repository.</param>
/// <param name="Explanation">The sentence to say about it.</param>
/// <param name="LocalHolder">
/// The process id of the Versio holding it, when that Versio is running on THIS machine and
/// was confirmed alive — the one case where the refusal can be turned into "here it is"
/// instead of "go and find it". Null for a holder on another machine, which cannot be
/// reached, and for every verdict that is not a refusal.
/// </param>
public readonly record struct SessionVerdict(
    RepositoryAvailability Availability, string Explanation, int? LocalHolder = null)
{
    public bool CanOpen => Availability is not RepositoryAvailability.HeldElsewhere;
}

/// <summary>
/// One repository, one Versio.
/// </summary>
/// <remarks>
/// <para>
/// <b>Safeguards §5.G, and the reason it is a refusal rather than a read-only mode.</b>
/// <see cref="LockRecovery.HeldByLiveSession"/> has said <i>"opening read-only"</i> since it was
/// written and nothing implemented it — the second instance opened fully writable, and two
/// orchestrators then wrote <c>repo.json</c> and <c>state.json</c> over each other. The section
/// offered either a read-only mode or a single-instance guard.
/// </para>
/// <para>
/// <b>The guard was chosen because it can be complete and a read-only mode cannot.</b>
/// Read-only means finding every write in the application — git commands, exports, imports,
/// adoption, config, the lock store, the auto-stash — and gating each one. Missing a single
/// path is not a cosmetic bug there: it is exactly the concurrent write the feature exists to
/// prevent, arriving silently, in the one mode nobody tests as carefully. A guard has one
/// decision, made once, in a place a test can reach.
/// </para>
/// <para>
/// <b>The per-binding lock could not have answered this.</b> <c>LockRecoveryPolicy</c> only
/// reaches <c>HeldByLiveSession</c> for a binding that <i>holds a lock</i>, so a second
/// instance opened on a repository where nothing happened to be locked saw
/// <c>NothingToRecover</c> and carried on. The hazard is two orchestrators, not two locks.
/// </para>
/// <para>
/// <b>A marker from another machine is believed.</b> Its processes cannot be probed from here,
/// and this repository is very possibly on a share. That is the same rule the vendor's own
/// open-project marker follows — a stale one from a crashed IDE is ignored, one from another
/// machine is believed — and being wrong in that direction costs a person a message, while
/// being wrong in the other costs two writers on one repository. The message names the file, so
/// a marker left behind by a machine that is never coming back can be deleted.
/// </para>
/// <para>
/// Pure. The reading, writing and deleting belong to the caller.
/// </para>
/// </remarks>
public static class RepositoryPresence
{
    /// <summary>Where the marker lives, repo-relative.</summary>
    /// <remarks>
    /// Under <c>.versio/local/</c>, which is gitignored: who has a repository open on one
    /// machine is not something to commit and share with everyone who clones it.
    /// </remarks>
    public const string MarkerPath = ".versio/local/session.json";

    /// <summary>What an existing marker means for this process.</summary>
    /// <param name="marker">What was read, or null when there is no marker.</param>
    public static SessionVerdict Evaluate(
        SessionMarker? marker, IMachineIdentity machine, IProcessProbe processes)
    {
        if (marker is null)
        {
            return new SessionVerdict(RepositoryAvailability.Free, "nobody else has it open");
        }

        var sameMachine = string.Equals(
            marker.Machine, machine.MachineName, StringComparison.OrdinalIgnoreCase);

        if (!sameMachine)
        {
            // Believed, because it cannot be checked. Named so it can be cleared by hand.
            return new SessionVerdict(
                RepositoryAvailability.HeldElsewhere,
                $"This repository is open in Versio on {marker.Machine} ({marker.User}). " +
                $"Close it there first. If that machine is gone, delete {MarkerPath}.");
        }

        if (marker.ProcessId == machine.CurrentProcessId &&
            string.Equals(marker.SessionId, SessionIdOf(machine), StringComparison.Ordinal))
        {
            // Reopening the same repository in the same window. Not a conflict, and refusing
            // it would make the guard fire on the most ordinary action there is.
            return new SessionVerdict(
                RepositoryAvailability.HeldHere, "this window already has it open");
        }

        // A marker written before the last reboot describes a process that cannot exist.
        if (marker.BootId != machine.BootId)
        {
            return new SessionVerdict(
                RepositoryAvailability.Free,
                "the marker was left by a session from before the last restart");
        }

        if (!processes.IsVersioProcessAlive(marker.ProcessId, marker.SessionId))
        {
            return new SessionVerdict(
                RepositoryAvailability.Free,
                $"the session that left the marker (pid {marker.ProcessId}) is gone");
        }

        return new SessionVerdict(
            RepositoryAvailability.HeldElsewhere,
            $"This repository is already open in another Versio window " +
            $"(pid {marker.ProcessId}). Use that one, or close it first.",
            LocalHolder: marker.ProcessId);
    }

    /// <summary>The marker this process would write.</summary>
    public static SessionMarker MarkerFor(
        IMachineIdentity machine, string sessionId, DateTimeOffset now) => new()
    {
        Machine = machine.MachineName,
        User = machine.UserName,
        ProcessId = machine.CurrentProcessId,
        SessionId = sessionId,
        BootId = machine.BootId,
        OpenedUtc = now,
    };

    /// <summary>
    /// This process's session id, derived the way the lock owner's already is.
    /// </summary>
    /// <remarks>
    /// Pid and boot time together: a pid alone is reused within a boot, and the pair is what
    /// distinguishes two Versio processes that happened to get the same number. Derived rather
    /// than generated so that a marker written by <i>this</i> process is always recognisable as
    /// this process's, including after the marker has been read back from disk.
    /// </remarks>
    public static string SessionIdOf(IMachineIdentity machine) =>
        $"{machine.CurrentProcessId}-{machine.BootId.ToUnixTimeSeconds()}";
}
