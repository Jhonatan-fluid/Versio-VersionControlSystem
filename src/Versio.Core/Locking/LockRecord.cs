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

/// <summary>
/// The last <b>released</b> state — what the repository looked like at the most recent push
/// or discard.
/// </summary>
/// <remarks>
/// This answers "is there unsynced work?", which is the question lock recovery asks after a
/// crash or a reboot. It deliberately does <b>not</b> move when an export succeeds: an
/// export is not a release, and treating it as one would make a recovered session conclude
/// there was nothing to guard.
/// </remarks>
public sealed record SyncBaseline
{
    public string HeadCommit { get; init; } = string.Empty;
    public string ProjectHash { get; init; } = string.Empty;
    /// <summary>
    /// Hash of the export tree's actual contents.
    /// </summary>
    /// <remarks>
    /// Was <c>ManifestHash</c>, and hashed <c>_manifest.json</c> alone. That file is written
    /// by an export and by nothing else, so a hand-edited <c>.st</c> left it identical and
    /// the hash gate reported no change — the exact edit the mirror exists to carry. Schema 3
    /// records the rename: a schema-2 value is a manifest hash and will not match.
    /// </remarks>
    /// <summary>Hash of the export tree's actual contents. See <see cref="SyncBaseline.MirrorHash"/>.</summary>
    public string MirrorHash { get; init; } = string.Empty;
}

/// <summary>
/// What the mirror last reflected.
/// </summary>
/// <remarks>
/// A separate concept from <see cref="SyncBaseline"/>, and conflating the two was a real
/// bug. Two different questions are being asked:
/// <list type="bullet">
///   <item><b>"Has anything changed since the last export?"</b> — the hash gate and the
///     commit gate. Answered by this record, which moves on every successful sync.</item>
///   <item><b>"Is there work that has not been pushed?"</b> — lock recovery. Answered by
///     <see cref="SyncBaseline"/>, which moves only on release.</item>
/// </list>
/// With one field serving both, an export either broke the hash gate (re-exporting
/// unchanged content forever) or broke recovery (releasing a lock that still guarded
/// unpushed work).
/// </remarks>
public sealed record SyncedState
{
    public string ProjectHash { get; init; } = string.Empty;
    public string MirrorHash { get; init; } = string.Empty;
}

/// <summary>The lock state of one binding (one .project and its export tree).</summary>
public sealed record BindingLock
{
    public required SyncDirection Direction { get; init; }
    public DateTimeOffset? AcquiredUtc { get; init; }
    public string Reason { get; init; } = string.Empty;

    /// <summary>Last pushed/discarded state. Used by lock recovery.</summary>
    public SyncBaseline Baseline { get; init; } = new();

    /// <summary>Last exported/imported state. Used by the hash gate and the commit gate.</summary>
    public SyncedState LastSynced { get; init; } = new();
}

/// <summary>
/// The persisted lock state for a repository. Locks are per binding, so project A can be
/// authoritative while project B is clean (plan section 5.2).
/// </summary>
public sealed record LockState
{
    public int Schema { get; init; } = 3;
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
