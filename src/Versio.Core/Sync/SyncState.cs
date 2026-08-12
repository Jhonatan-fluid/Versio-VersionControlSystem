namespace Versio.Core.Sync;

/// <summary>Which side of the sync currently owns the truth for one binding.</summary>
public enum SyncDirection
{
    /// <summary>Nothing outstanding. Either direction may be taken.</summary>
    Clean = 0,

    /// <summary>The .project changed. Exports allowed; <b>imports blocked</b>.</summary>
    ProjectAuthoritative,

    /// <summary>The exported text changed. Imports allowed; <b>exports blocked</b>
    /// except the verification re-export.</summary>
    TextAuthoritative,

    /// <summary>Both sides changed independently. Requires the user to pick a side.</summary>
    Conflict,

    /// <summary>The last operation failed. Requires acknowledgement before retrying.</summary>
    Failed,
}

/// <summary>What happened to make the state change.</summary>
public enum SyncTrigger
{
    ProjectChanged,
    TextChanged,
    ExportSucceeded,
    ExportFailed,
    ImportSucceeded,
    ImportFailed,
    PushSucceeded,
    ChangesDiscarded,
    ConflictResolved,
    FailureAcknowledged,
    ForceReleased,
}

public readonly record struct TransitionResult(
    bool Allowed,
    SyncDirection State,
    string Reason)
{
    public static TransitionResult Ok(SyncDirection to, string reason) => new(true, to, reason);
    public static TransitionResult Blocked(SyncDirection stay, string reason) => new(false, stay, reason);
}

/// <summary>
/// The direction lock, as a pure state machine.
/// </summary>
/// <remarks>
/// This is the mechanism that makes bi-directional sync safe: once a direction has been
/// taken, the opposite direction is refused until the working tree returns to a clean,
/// pushed state. It is deliberately free of IO, time and threading so that every
/// transition — including every illegal one — can be asserted in a unit test.
/// <para>
/// Note that <see cref="SyncTrigger.PushSucceeded"/> releases the lock but a plain commit
/// does not: until work is pushed nobody else can see it, so the direction is still
/// meaningfully owned.
/// </para>
/// </remarks>
public static class SyncStateMachine
{
    public static TransitionResult Next(SyncDirection current, SyncTrigger trigger) =>
        (current, trigger) switch
        {
            // --- from Clean: either direction may be claimed -----------------------
            (SyncDirection.Clean, SyncTrigger.ProjectChanged) =>
                TransitionResult.Ok(SyncDirection.ProjectAuthoritative, "project file changed"),
            (SyncDirection.Clean, SyncTrigger.TextChanged) =>
                TransitionResult.Ok(SyncDirection.TextAuthoritative, "exported text changed"),

            // --- ProjectAuthoritative: further project edits fine, imports refused --
            (SyncDirection.ProjectAuthoritative, SyncTrigger.ProjectChanged) =>
                TransitionResult.Ok(SyncDirection.ProjectAuthoritative, "further project edit"),
            (SyncDirection.ProjectAuthoritative, SyncTrigger.ExportSucceeded) =>
                TransitionResult.Ok(SyncDirection.ProjectAuthoritative, "export completed"),
            (SyncDirection.ProjectAuthoritative, SyncTrigger.TextChanged) =>
                TransitionResult.Blocked(SyncDirection.ProjectAuthoritative,
                    "the project file is authoritative; text edits will not be imported until you push or discard"),

            // --- TextAuthoritative: mirror image ------------------------------------
            (SyncDirection.TextAuthoritative, SyncTrigger.TextChanged) =>
                TransitionResult.Ok(SyncDirection.TextAuthoritative, "further text edit"),
            (SyncDirection.TextAuthoritative, SyncTrigger.ImportSucceeded) =>
                TransitionResult.Ok(SyncDirection.TextAuthoritative, "import completed"),
            (SyncDirection.TextAuthoritative, SyncTrigger.ProjectChanged) =>
                TransitionResult.Blocked(SyncDirection.TextAuthoritative,
                    "the exported text is authoritative; the project change was not exported. Push, discard, or import first"),

            // --- release conditions --------------------------------------------------
            (SyncDirection.ProjectAuthoritative, SyncTrigger.PushSucceeded) or
            (SyncDirection.TextAuthoritative, SyncTrigger.PushSucceeded) =>
                TransitionResult.Ok(SyncDirection.Clean, "pushed"),
            (SyncDirection.ProjectAuthoritative, SyncTrigger.ChangesDiscarded) or
            (SyncDirection.TextAuthoritative, SyncTrigger.ChangesDiscarded) =>
                TransitionResult.Ok(SyncDirection.Clean, "changes discarded"),
            (_, SyncTrigger.ForceReleased) =>
                TransitionResult.Ok(SyncDirection.Clean, "force released by user"),

            // A push while already clean is a no-op, not an error.
            (SyncDirection.Clean, SyncTrigger.PushSucceeded) or
            (SyncDirection.Clean, SyncTrigger.ChangesDiscarded) =>
                TransitionResult.Ok(SyncDirection.Clean, "already clean"),

            // --- failure -------------------------------------------------------------
            (_, SyncTrigger.ExportFailed) =>
                TransitionResult.Ok(SyncDirection.Failed, "export failed"),
            (_, SyncTrigger.ImportFailed) =>
                TransitionResult.Ok(SyncDirection.Failed, "import failed"),
            (SyncDirection.Failed, SyncTrigger.FailureAcknowledged) =>
                TransitionResult.Ok(SyncDirection.Clean, "failure acknowledged"),

            // A failed binding does no further work until acknowledged: silently resuming
            // after a failure is how half-written state becomes permanent.
            (SyncDirection.Failed, _) =>
                TransitionResult.Blocked(SyncDirection.Failed,
                    "the last operation failed; acknowledge the failure before continuing"),

            // --- conflict ------------------------------------------------------------
            (SyncDirection.Conflict, SyncTrigger.ConflictResolved) =>
                TransitionResult.Ok(SyncDirection.Clean, "conflict resolved"),
            (SyncDirection.Conflict, _) =>
                TransitionResult.Blocked(SyncDirection.Conflict,
                    "both sides changed; resolve the conflict before continuing"),

            _ => TransitionResult.Blocked(current, $"no transition for {trigger} from {current}"),
        };

    /// <summary>Both sides changed in the same scan — detected by the orchestrator, not
    /// reachable by a single trigger.</summary>
    public static TransitionResult EnterConflict(string reason) =>
        TransitionResult.Ok(SyncDirection.Conflict, reason);

    public static bool ImportAllowed(SyncDirection state) =>
        state is SyncDirection.Clean or SyncDirection.TextAuthoritative;

    public static bool ExportAllowed(SyncDirection state) =>
        state is SyncDirection.Clean or SyncDirection.ProjectAuthoritative;

    /// <summary>A held direction blocks branch switching: changing branches under unsynced
    /// work is how people lose it.</summary>
    public static bool HoldsLock(SyncDirection state) => state is not SyncDirection.Clean;
}
