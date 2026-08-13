namespace Versio.Core.Sync;

/// <summary>Which side of the mirror changed.</summary>
public enum ChangeSource
{
    /// <summary>The binary .project was written — the engineer saved in CODESYS.</summary>
    Project,

    /// <summary>A file in the export tree was written — an editor, a pull, or a merge.</summary>
    ExportedText,
}

/// <summary>Why a sync did or did not happen. Every value is something the UI must be able
/// to explain to the user in one sentence.</summary>
public enum SyncVerdict
{
    /// <summary>Work was done and the mirror is now up to date.</summary>
    Completed,

    /// <summary>Content is identical to the recorded baseline — the hash gate stopped it.</summary>
    NoChange,

    /// <summary>The event was the echo of our own write.</summary>
    SelfWriteSuppressed,

    /// <summary>The direction lock refused this direction.</summary>
    BlockedByLock,

    /// <summary>The circuit breaker has tripped for this binding.</summary>
    BlockedByCircuitBreaker,

    /// <summary>A job is already running; this one was folded into the pending slot.</summary>
    Coalesced,

    /// <summary>Deferred by the minimum interval between runs.</summary>
    Throttled,

    /// <summary>Import needs the user to confirm before anything is written.</summary>
    AwaitingConfirmation,

    /// <summary>
    /// The project is open in its vendor's IDE, so this direction cannot run yet.
    /// </summary>
    /// <remarks>
    /// <b>A wait, not a failure.</b> Somebody saving and leaving CODESYS open is the most
    /// common working state there is; treating it as a failure marks the binding failed and
    /// stops all syncing until a button is pressed, over and over. This verdict deliberately
    /// leaves the direction lock alone and does not feed the circuit breaker.
    /// </remarks>
    BlockedByOpenProject,

    /// <summary>
    /// The working tree has unmerged paths, so neither direction may run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A wait, not a failure</b>, for the same reasons as
    /// <see cref="BlockedByOpenProject"/>: a merge in progress is a normal state somebody is
    /// working through, and marking the binding failed would stop all syncing until a button
    /// is pressed. It leaves the direction lock alone and does not feed the circuit breaker.
    /// </para>
    /// <para>
    /// Both directions, not just import. Exporting over a half-merged mirror would overwrite
    /// the conflict the user is in the middle of resolving; importing a mirror containing
    /// <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> markers would put them into the project, where they
    /// are not text but a structured file that will no longer compile.
    /// </para>
    /// </remarks>
    BlockedByConflict,

    /// <summary>CODESYS failed, or the run did not complete.</summary>
    Failed,

    /// <summary>No binding with that id, or it is disabled.</summary>
    UnknownBinding,
}

/// <summary>What the orchestrator did about one change, and why. Every field here is
/// something the UI or the log has to be able to state plainly.</summary>
public sealed record SyncOutcome
{
    public required SyncVerdict Verdict { get; init; }
    public required string BindingId { get; init; }
    public required string Explanation { get; init; }
    public SyncDirection State { get; init; } = SyncDirection.Clean;

    /// <summary>Files the export actually wrote — after the hash gate, not before.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
    public TimeSpan Duration { get; init; }

    public bool DidWork => Verdict == SyncVerdict.Completed;

    public bool IsFailure => Verdict is SyncVerdict.Failed;

    public static SyncOutcome Refused(SyncVerdict verdict, string bindingId, string why,
                                      SyncDirection state = SyncDirection.Clean) =>
        new() { Verdict = verdict, BindingId = bindingId, Explanation = why, State = state };
}

/// <summary>A request to reconcile one binding.</summary>
public sealed record SyncJob
{
    public required string BindingId { get; init; }
    public required ChangeSource Source { get; init; }

    /// <summary>Set when the caller has already confirmed a destructive import.</summary>
    public bool Confirmed { get; init; }

    /// <summary>Bypasses the coalescer and the throttle. Used by the commit gate, which
    /// must run now and block, not queue.</summary>
    public bool Synchronous { get; init; }

    public string Reason { get; init; } = string.Empty;
}
