namespace Versio.Core.Sync;

/// <summary>
/// Turns sync state into sentences a control engineer can act on.
/// </summary>
/// <remarks>
/// <para>
/// In Core rather than in the view model on purpose. The direction lock is the mechanism most
/// likely to stop somebody mid-task, and an unexplained refusal reads as a broken tool — so
/// the wording is part of the feature, and it is asserted by tests like the rest of the
/// state machine. Plan §9.5 requires the panel to state the lock in plain English; this is
/// the single place that wording lives, so the panel, the log and any future CLI agree.
/// </para>
/// <para>
/// Every sentence names <b>what to do next</b>, not just what happened. "Blocked" without a
/// remedy is the message that generates the support call.
/// </para>
/// </remarks>
public static class SyncNarrative
{
    /// <summary>Two or three words for a list row.</summary>
    public static string Label(SyncDirection state) => state switch
    {
        SyncDirection.Clean => "in sync",
        SyncDirection.ProjectAuthoritative => "project ahead",
        SyncDirection.TextAuthoritative => "text ahead",
        SyncDirection.Conflict => "conflict",
        SyncDirection.Failed => "failed",
        _ => "unknown",
    };

    /// <summary>A full sentence for the panel.</summary>
    public static string Describe(SyncDirection state) => state switch
    {
        SyncDirection.Clean =>
            "Nothing outstanding. The exported text matches the project, and either side may " +
            "be edited next.",

        SyncDirection.ProjectAuthoritative =>
            "The project file is authoritative. Edits to the exported text will not be " +
            "imported until this work is pushed or discarded.",

        SyncDirection.TextAuthoritative =>
            "The exported text is authoritative. Changes made in the IDE will not be " +
            "exported until this work is imported, pushed or discarded.",

        SyncDirection.Conflict =>
            "Both the project and the exported text changed independently. Pick a side " +
            "before either direction can run again.",

        SyncDirection.Failed =>
            "The last operation failed. Acknowledge the failure to allow this project to " +
            "sync again — nothing further runs until you do.",

        _ => "The sync state of this project could not be determined.",
    };

    /// <summary>What an outcome means, for the panel and the log.</summary>
    /// <remarks>
    /// <see cref="SyncOutcome.Explanation"/> already carries the specifics; this supplies the
    /// leading clause so a refusal reads as a decision Versio made deliberately rather than
    /// as a failure it suffered.
    /// </remarks>
    public static string Describe(SyncOutcome outcome) => outcome.Verdict switch
    {
        SyncVerdict.Completed => outcome.ChangedFiles.Count switch
        {
            0 => $"Export finished in {Seconds(outcome)}; no files changed.",
            1 => $"Export finished in {Seconds(outcome)}; 1 file written.",
            var n => $"Export finished in {Seconds(outcome)}; {n} files written.",
        },

        SyncVerdict.NoChange =>
            "Nothing to do — the project has not changed since the last export.",

        SyncVerdict.SelfWriteSuppressed =>
            "Ignored: that change was the echo of Versio's own export.",

        SyncVerdict.GitWriteSuppressed =>
            "Ignored: git was rewriting the working tree, so that was not an edit.",

        SyncVerdict.BlockedByLock =>
            $"Blocked by the direction lock. {outcome.Explanation}",

        SyncVerdict.BlockedByCircuitBreaker =>
            "Sync is halted for this project after repeated cycles. Acknowledge it to resume.",

        SyncVerdict.Coalesced =>
            "A sync is already running; this one was folded into the queued follow-up.",

        SyncVerdict.Throttled =>
            "Throttled — a final sync will run once the changes settle.",

        SyncVerdict.AwaitingConfirmation =>
            "The exported text changed. Review the diff and confirm before importing.",

        // Leads with what did NOT happen: the person saved the project and sees no diff, and
        // the pending-import banner alone reads as unrelated to that save.
        SyncVerdict.BlockedByPendingEdit =>
            "The project change was not exported: the exported text has an edit that is " +
            "waiting to be imported, and the export would write over it. Confirm or discard " +
            "that edit first.",

        SyncVerdict.BlockedByOpenProject => outcome.Explanation,

        // The explanation names the files, which is the part worth reading. The leading clause
        // says why nothing happened, since a refusal is otherwise invisible: the user saves,
        // and no diff appears.
        SyncVerdict.BlockedByConflict =>
            $"Waiting for the merge to be finished. {outcome.Explanation}",

        SyncVerdict.Failed =>
            $"The operation failed: {outcome.Explanation}",

        SyncVerdict.UnknownBinding =>
            "That project is not tracked, or tracking is disabled for it.",

        _ => outcome.Explanation,
    };

    private static string Seconds(SyncOutcome outcome) =>
        $"{outcome.Duration.TotalSeconds:0.#} s";
}
