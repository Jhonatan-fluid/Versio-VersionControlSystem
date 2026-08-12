using Versio.Core.Sync;

namespace Versio.Core.Locking;

/// <summary>Who owns the locks in this repo. One owner per app session, shared by all bindings.</summary>
public sealed record LockOwner
{
    public required string Machine { get; init; }
    public required string User { get; init; }

    /// <summary>New GUID per app launch.</summary>
    public required string SessionId { get; init; }

    public required int ProcessId { get; init; }

    /// <summary>Machine boot time. A different value proves the owning process is gone,
    /// which is how a lock survives a reboot without being either lost or falsely trusted.</summary>
    public required DateTimeOffset BootId { get; init; }

    /// <summary>Lease heartbeat. Used only for display — never for automatic expiry,
    /// because an abandoned lock still guards real unsynced work.</summary>
    public DateTimeOffset RenewedUtc { get; init; }
}

/// <summary>What the repo looked like when the lock was taken.</summary>
public sealed record SyncBaseline
{
    public string HeadCommit { get; init; } = string.Empty;
    public string ProjectHash { get; init; } = string.Empty;
    public string ManifestHash { get; init; } = string.Empty;
}

/// <summary>The lock state of one binding (one .project and its export tree).</summary>
public sealed record BindingLock
{
    public required SyncDirection Direction { get; init; }
    public DateTimeOffset? AcquiredUtc { get; init; }
    public string Reason { get; init; } = string.Empty;
    public SyncBaseline Baseline { get; init; } = new();
}

/// <summary>
/// The persisted lock state for a repository. Locks are per binding, so project A can be
/// authoritative while project B is clean (plan section 5.2).
/// </summary>
public sealed record LockState
{
    public int Schema { get; init; } = 2;
    public string RepoPath { get; init; } = string.Empty;
    public LockOwner? Owner { get; init; }
    public IReadOnlyDictionary<string, BindingLock> Bindings { get; init; }
        = new Dictionary<string, BindingLock>();
}

/// <summary>What a recovered lock turned out to be.</summary>
public enum LockRecovery
{
    /// <summary>No previous state, or nothing was held.</summary>
    NothingToRecover,

    /// <summary>Owned by a live process on this machine — another Versio instance.
    /// Open read-only; never steal.</summary>
    HeldByLiveSession,

    /// <summary>The machine rebooted or the process died, and the work is still unsynced.
    /// Re-adopt under the new session and log it loudly.</summary>
    ReadoptedAfterRestart,

    /// <summary>The owner is gone and the hashes are back at baseline — someone reverted
    /// externally. Safe to release.</summary>
    ReleasedStaleLock,
}
