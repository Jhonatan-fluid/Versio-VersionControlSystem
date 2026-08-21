using System.Globalization;
using Versio.Core.Sync;

namespace Versio.Core.Guarding;

/// <summary>A verdict rendered for a dialog: what to call it, what to say, what to offer.</summary>
/// <param name="Choices">In the order they should appear. The first is the recommended one.</param>
public sealed record GuardPrompt(string Title, string Body, IReadOnlyList<GuardChoice> Choices)
{
    public GuardChoice? Recommended => Choices.Count > 0 ? Choices[0] : null;
}

/// <summary>
/// Turns a <see cref="GuardVerdict"/> into words.
/// </summary>
/// <remarks>
/// <para>
/// In Core, and separate from the guard itself, for the same two reasons
/// <see cref="SyncNarrative"/> is: the decision can then be asserted by tests that do not care
/// about phrasing, and the phrasing can be asserted by tests that do not have to reconstruct a
/// scenario to reach it. It also means the dialog, the status bar and the log cannot drift into
/// describing the same refusal three different ways.
/// </para>
/// <para>
/// <b>Every sentence names what to do next.</b> "Blocked" without a remedy is the message that
/// generates the support call — and a guard is, by construction, the thing standing between
/// somebody and what they were trying to do.
/// </para>
/// </remarks>
public static class GuardNarrative
{
    /// <summary>The button text for one choice.</summary>
    public static string Label(GuardChoice choice) => choice switch
    {
        GuardChoice.Cancel => "Cancel",
        GuardChoice.Proceed => "Continue",
        GuardChoice.StashAndProceed => "Stash my changes",
        GuardChoice.DiscardAndProceed => "Discard my changes",
        GuardChoice.CancelWorkAndProceed => "Stop it and continue",
        GuardChoice.WaitThenProceed => "Wait for it to finish",
        _ => "Continue",
    };

    /// <summary>The verb phrase for an intent, for use mid-sentence.</summary>
    public static string Action(GuardIntent intent) => intent switch
    {
        GuardIntent.SwitchBranch => "switch branch",
        GuardIntent.SwitchRepository => "open a different repository",
        GuardIntent.CloseApp => "close Versio",
        GuardIntent.Pull => "pull",
        _ => "do that",
    };

    /// <summary>Everything a dialog needs. Null when the verdict was to proceed.</summary>
    public static GuardPrompt? Prompt(GuardVerdict verdict) =>
        verdict.Decision == GuardDecision.Proceed
            ? null
            : new GuardPrompt(Title(verdict), Body(verdict), verdict.Choices);

    public static string Title(GuardVerdict verdict) => verdict.Reason switch
    {
        GuardReason.UninterruptibleWork => "Versio is in the middle of something",
        GuardReason.CancellableWork => "Something is still running",
        GuardReason.UnresolvedConflicts => "Unresolved conflicts",
        GuardReason.HeldDirectionLock => "Unsynced project work",
        GuardReason.DirtyWorkingTree => "You have uncommitted changes",
        _ => "Are you sure?",
    };

    /// <summary>The dialog body: what is in the way, and what to do about it.</summary>
    public static string Body(GuardVerdict verdict) => verdict.Reason switch
    {
        GuardReason.UninterruptibleWork =>
            verdict.Intent == GuardIntent.CloseApp
                ? $"{Running(verdict.Work)}\n\n" +
                  "Stopping it can leave a project the IDE will not open. Restore a backup " +
                  "from the project panel if that happens. Waiting lets it finish, which is " +
                  "safer; stopping is how you close now."
                : $"{Running(verdict.Work)}\n\n" +
                  "That writes directly into files Versio cannot put back, so it is never " +
                  $"interrupted — it will finish, or time out on its own. You can {Action(verdict.Intent)} " +
                  "once it has.",

        GuardReason.CancellableWork =>
            $"{Running(verdict.Work)}\n\n" +
            "Nothing is lost either way: stopping it only costs the time it has spent, and " +
            "the work it was doing runs again when it is next needed.",

        GuardReason.UnresolvedConflicts =>
            $"{Files(verdict.ConflictedFileCount)} still {Have(verdict.ConflictedFileCount)} " +
            "unresolved merge conflicts.\n\n" +
            $"Resolve them and commit the merge before you {Action(verdict.Intent)}. Exported " +
            "text containing conflict markers must never reach a project, so Versio will not " +
            "sync anything until the merge is finished.",

        GuardReason.HeldDirectionLock =>
            $"{Locks(verdict.Locks)}\n\n" +
            "Push or discard that work first. The direction lock is stored per repository and " +
            "does not travel with the branch, so unsynced work would arrive " +
            $"{Somewhere(verdict.Target)} still claiming to belong here — and the next sync " +
            "would act on it.",

        GuardReason.DirtyWorkingTree => verdict.Intent == GuardIntent.SwitchBranch
            ? $"{Files(verdict.DirtyFileCount)} {Have(verdict.DirtyFileCount)} changes that are " +
              $"not committed.\n\nStashing keeps them, and Versio offers them back the moment " +
              $"you return to this branch. Discarding cannot be undone."
            : $"{Files(verdict.DirtyFileCount)} {Have(verdict.DirtyFileCount)} changes that are " +
              "not committed.\n\nStashing keeps them and puts them back once the pull has " +
              "landed.",

        _ => $"Continue and {Action(verdict.Intent)}?",
    };

    /// <summary>One line for the log and the status bar.</summary>
    /// <remarks>
    /// Deliberately shorter than <see cref="Body"/> and never multi-line: this is read in a
    /// list of a hundred other lines, where a paragraph is worse than a sentence.
    /// </remarks>
    public static string Describe(GuardVerdict verdict) => verdict.Decision switch
    {
        GuardDecision.Proceed => $"nothing in the way of {Action(verdict.Intent)}",

        GuardDecision.Refuse => verdict.Reason switch
        {
            GuardReason.UninterruptibleWork =>
                $"refused {Action(verdict.Intent)}: {Summarise(verdict.Work)} must finish first",
            GuardReason.UnresolvedConflicts =>
                $"refused {Action(verdict.Intent)}: {Files(verdict.ConflictedFileCount)} " +
                $"{Have(verdict.ConflictedFileCount)} unresolved conflicts",
            GuardReason.HeldDirectionLock =>
                $"refused {Action(verdict.Intent)}: {verdict.Locks.Count} project(s) hold a " +
                "direction lock",
            _ => $"refused {Action(verdict.Intent)}",
        },

        GuardDecision.Ask => verdict.Reason switch
        {
            GuardReason.CancellableWork =>
                $"asking before {Action(verdict.Intent)}: {Summarise(verdict.Work)} is running",
            GuardReason.UninterruptibleWork =>
                $"asking before {Action(verdict.Intent)}: {Summarise(verdict.Work)} is running; " +
                "stopping it can leave a project that needs a backup restore",
            GuardReason.DirtyWorkingTree =>
                $"asking before {Action(verdict.Intent)}: {Files(verdict.DirtyFileCount)} " +
                $"{Have(verdict.DirtyFileCount)} uncommitted changes",
            _ => $"asking before {Action(verdict.Intent)}",
        },

        _ => $"unknown verdict for {Action(verdict.Intent)}",
    };

    /// <summary>"Exporting Machine… has been running for 21 seconds."</summary>
    private static string Running(IReadOnlyList<ActiveWork> work) => work.Count switch
    {
        0 => "Something is running.",
        1 => $"{Caption(work[0])} has been running for {Elapsed(work[0].Elapsed)}.",
        _ => string.Join("\n", work.Select(w => $"• {Caption(w)} — {Elapsed(w.Elapsed)}")),
    };

    private static string Summarise(IReadOnlyList<ActiveWork> work) => work.Count switch
    {
        0 => "an operation",
        1 => Caption(work[0]).TrimEnd('…'),
        var n => $"{n} operations",
    };

    /// <summary>
    /// The caption the progress bar is already showing, with its trailing ellipsis kept.
    /// </summary>
    /// <remarks>
    /// Reused rather than re-derived from <see cref="WorkKind"/> so the dialog names the
    /// operation in the same words as the bar the user is looking at while they read it. A
    /// second vocabulary for the same run would read as two different things happening.
    /// </remarks>
    private static string Caption(ActiveWork work) =>
        string.IsNullOrWhiteSpace(work.Description) ? Describe(work.Kind) : work.Description;

    private static string Describe(WorkKind kind) => kind switch
    {
        WorkKind.GitRead => "Reading git state…",
        WorkKind.GitWrite => "Writing to git…",
        WorkKind.GitPush => "Pushing…",
        WorkKind.Clone => "Cloning…",
        WorkKind.Export => "Exporting…",
        WorkKind.Import => "Importing…",
        WorkKind.Scan => "Scanning…",
        _ => "Working…",
    };

    private static string Locks(IReadOnlyList<HeldLock> locks) => locks.Count switch
    {
        0 => "A project has unsynced work.",
        1 => $"{locks[0].ProjectName} has unsynced work ({State(locks[0].Direction)}).",
        _ => string.Join("\n",
            locks.Select(l => $"• {l.ProjectName} — {State(l.Direction)}")),
    };

    /// <summary>The lock state in the words the project panel already uses for it.</summary>
    private static string State(SyncDirection direction) => SyncNarrative.Label(direction);

    /// <summary>
    /// Elapsed time as somebody would say it.
    /// </summary>
    /// <remarks>
    /// Whole seconds below a minute, because this appears in a sentence rather than a
    /// stopwatch and "21.4 seconds" reads as a measurement rather than an answer to "how long
    /// have I been waiting".
    /// </remarks>
    private static string Elapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromSeconds(1))
        {
            return "less than a second";
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            var seconds = (int)elapsed.TotalSeconds;
            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }

        var minutes = (int)elapsed.TotalMinutes;
        var remainder = (int)(elapsed.TotalSeconds - (minutes * 60));

        return remainder == 0
            ? minutes == 1 ? "1 minute" : $"{minutes} minutes"
            : string.Format(CultureInfo.InvariantCulture, "{0} m {1} s", minutes, remainder);
    }

    private static string Files(int count) =>
        count == 1 ? "1 file" : $"{count} files";

    private static string Have(int count) => count == 1 ? "has" : "have";

    /// <summary>"on main", or "on the other branch" when nothing named it.</summary>
    private static string Somewhere(string target) =>
        string.IsNullOrWhiteSpace(target) ? "on the other branch" : $"on {target}";
}
