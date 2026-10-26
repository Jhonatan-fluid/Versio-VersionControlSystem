namespace Versio.Core.Git;

/// <summary>What pressing the commit button will actually do.</summary>
public enum CommitIntent
{
    /// <summary>Nothing to commit.</summary>
    Nothing,

    /// <summary>Commit the ticked rows, and leave the rest in the working tree.</summary>
    CommitTicked,

    /// <summary>Commit every changed file. Nothing ticked means the whole list.</summary>
    CommitAll,

    /// <summary>
    /// Finish the paused rebase instead of committing. The button's caption, tooltip and the
    /// message requirement all change with it; the commit flow diverts to <c>rebase --continue</c>.
    /// </summary>
    ContinueRebase,
}

/// <summary>
/// One button, two behaviours, and a label that never lies about which.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one button.</b> The panel carried <c>Commit</c> and <c>Commit &amp; Push</c>, which
/// bundled two operations with different failure modes: the commit succeeds, the push fails, and
/// one status line has to describe both. Push is its own deliberate act now, up in the remote
/// row where the ahead-count already lives. Staging is the step people forget, so the button
/// still stages — in the background, after a preflight, never as a verb on the panel.
/// </para>
/// <para>
/// <b>Nothing ticked means the whole list.</b> That is the GitHub Desktop shape: check some
/// files to commit only those, or check none (or all) to commit everything. The old third
/// case — something already in the index and nothing ticked committed <i>only the index</i> —
/// needed a visible STAGED list to be honest. Without that list, a plain <c>Commit</c> that
/// left files behind would be the silent sweep this type exists to prevent, just in reverse.
/// </para>
/// <para>
/// <b>The PLC export gate is not modelled here and must not be.</b> It runs before the commit
/// regardless of what this decides, and stages whatever it exports — a commit recording a
/// project without its mirror is the state the gate exists to prevent. This type answers "what
/// did the user ask for"; the gate answers "what must be true anyway".
/// </para>
/// </remarks>
public sealed record CommitPlan
{
    public required CommitIntent Intent { get; init; }

    /// <summary>How many files the button will take. Zero when there is nothing to commit.</summary>
    public int IncludeCount { get; init; }

    /// <summary>The button's caption.</summary>
    public required string Label { get; init; }

    /// <summary>True when the button should be live, message and identity permitting.</summary>
    public bool CanCommit => Intent is not CommitIntent.Nothing;

    /// <summary>Whether the button needs a message in the box before it may be pressed.</summary>
    /// <remarks>
    /// False for exactly one intent. A rebase replays commits that already have messages,
    /// and <c>rebase --continue</c> keeps each one; the box is not the message of anything
    /// here, so demanding one would make the user type text that goes nowhere.
    /// </remarks>
    public bool NeedsMessage => Intent is not CommitIntent.ContinueRebase;

    /// <summary>
    /// The button while a rebase is paused: it continues the rebase, and does not commit.
    /// </summary>
    /// <remarks>
    /// Until 2026-10-25 the button stayed a plain <c>Commit</c> through a paused rebase, and
    /// pressing it ran <c>git commit</c> on the detached HEAD a rebase works on: the branch was
    /// unchanged, the later commits were never replayed, the rebase was still paused, and the
    /// banner above had just said "Continue the rebase" (the review's sixth round, P2). A
    /// caption that never lies about which operation it runs is the point of this type, so
    /// the paused rebase gets its own plan rather than a flag on the commit one.
    /// </remarks>
    public static CommitPlan ContinuingRebase { get; } = new()
    {
        Intent = CommitIntent.ContinueRebase,
        IncludeCount = 0,
        Label = "Continue rebase",
    };

    /// <summary>
    /// Work out what the button means right now.
    /// </summary>
    /// <param name="changes">Rows in the one changes list (not conflicted).</param>
    /// <param name="ticked">
    /// How many of those rows the user has ticked. Ticks win: an explicit selection is the
    /// least ambiguous instruction the panel can receive. None ticked means the whole list.
    /// </param>
    public static CommitPlan For(int changes, int ticked)
    {
        if (ticked > 0)
        {
            return new CommitPlan
            {
                Intent = CommitIntent.CommitTicked,
                IncludeCount = ticked,
                Label = $"Commit ({ticked})",
            };
        }

        if (changes > 0)
        {
            return new CommitPlan
            {
                Intent = CommitIntent.CommitAll,
                IncludeCount = changes,
                Label = "Commit",
            };
        }

        return new CommitPlan
        {
            Intent = CommitIntent.Nothing,
            IncludeCount = 0,
            Label = "Commit",
        };
    }

    /// <summary>
    /// Why the button is dead, or empty when it is not.
    /// </summary>
    /// <remarks>
    /// <b>Every disabled control in this panel states its reason.</b> A dead button with no
    /// explanation is what sends somebody to the command line, which is the thing the panel
    /// exists to avoid — and WPF hides tooltips on disabled controls unless told otherwise, so
    /// the one moment the explanation is wanted is the one moment it is missing by default.
    /// <para>
    /// Ordered by what the user should fix first: nothing to commit beats no message, because
    /// typing a message for an empty commit is wasted effort.
    /// </para>
    /// </remarks>
    public string BlockedReason(bool hasMessage, bool hasIdentity, bool hasConflicts)
    {
        if (hasConflicts)
        {
            return "Resolve the conflicts first.";
        }

        if (!CanCommit)
        {
            return "Nothing to commit.";
        }

        if (!hasIdentity)
        {
            return "Set a name and email for git first.";
        }

        return hasMessage || !NeedsMessage ? string.Empty : "Write a commit message first.";
    }

    /// <summary>What the button will do, spelled out for its tooltip.</summary>
    /// <remarks>
    /// Said even when the button is live. The two behaviours are the point of the design and
    /// a caption of one word cannot carry the difference between "the three you ticked" and
    /// "every changed file".
    /// </remarks>
    public string Explanation => Intent switch
    {
        CommitIntent.CommitTicked =>
            IncludeCount == 1
                ? "Commit the file you ticked. The others stay in the working tree."
                : $"Commit the {IncludeCount} files you ticked. The others stay in the working tree.",

        CommitIntent.CommitAll =>
            IncludeCount == 1
                ? "Commit the changed file."
                : $"Commit all {IncludeCount} changed files.",

        // Said plainly because the box is right beside the button: whatever is typed in it
        // is neither used nor cleared, and the user should not have to find that out.
        CommitIntent.ContinueRebase =>
            "Continue the rebase. Each replayed commit keeps its own message; " +
            "the message box is left as it is.",

        _ => "Nothing to commit.",
    };
}
