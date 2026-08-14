namespace Versio.Core.Git;

/// <summary>What pressing the commit button will actually do.</summary>
public enum CommitIntent
{
    /// <summary>Nothing to commit.</summary>
    Nothing,

    /// <summary>Commit what is already staged, and touch nothing else.</summary>
    CommitStaged,

    /// <summary>Stage the ticked rows, then commit.</summary>
    StageTickedThenCommit,

    /// <summary>Stage everything unstaged, then commit.</summary>
    StageAllThenCommit,
}

/// <summary>
/// One button, three behaviours, and a label that never lies about which.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one button.</b> The panel carried <c>Commit</c> and <c>Commit &amp; Push</c>, which
/// bundled two operations with different failure modes: the commit succeeds, the push fails, and
/// one status line has to describe both. Push is its own deliberate act now, up in the remote
/// row where the ahead-count already lives. What is left is the commit — and staging is the
/// step people forget, so the button does both and says so.
/// </para>
/// <para>
/// <b>The third case is the one that matters.</b> With something already staged and nothing
/// ticked, this commits <i>only what is staged</i>. Sweeping the unstaged files in because
/// somebody pressed a general-purpose button would silently undo a deliberate partial stage —
/// and they would not find out until they read the commit, by which time it is history. So the
/// label drops to plain <c>Commit</c> there, and the difference is visible before the press
/// rather than discoverable after it.
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

    /// <summary>How many rows will be staged first. Zero when nothing needs staging.</summary>
    public int StagingCount { get; init; }

    /// <summary>The button's caption.</summary>
    public required string Label { get; init; }

    /// <summary>True when the button should be live, message and identity permitting.</summary>
    public bool CanCommit => Intent is not CommitIntent.Nothing;

    /// <summary>
    /// Work out what the button means right now.
    /// </summary>
    /// <param name="staged">Rows already in the index.</param>
    /// <param name="unstaged">Rows changed but not staged.</param>
    /// <param name="ticked">
    /// How many unstaged rows the user has ticked. Ticks win over everything: an explicit
    /// selection is the least ambiguous instruction the panel can receive.
    /// </param>
    public static CommitPlan For(int staged, int unstaged, int ticked)
    {
        if (ticked > 0)
        {
            return new CommitPlan
            {
                Intent = CommitIntent.StageTickedThenCommit,
                StagingCount = ticked,
                Label = $"Stage & commit ({ticked})",
            };
        }

        // Something is staged and nothing is ticked: commit exactly that. The plain label is
        // the whole safeguard - it is what tells somebody who staged three files deliberately
        // that the fourth is not coming with them.
        if (staged > 0)
        {
            return new CommitPlan
            {
                Intent = CommitIntent.CommitStaged,
                StagingCount = 0,
                Label = "Commit",
            };
        }

        if (unstaged > 0)
        {
            return new CommitPlan
            {
                Intent = CommitIntent.StageAllThenCommit,
                StagingCount = unstaged,
                Label = "Stage all & commit",
            };
        }

        return new CommitPlan
        {
            Intent = CommitIntent.Nothing,
            StagingCount = 0,
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

        return hasMessage ? string.Empty : "Write a commit message first.";
    }

    /// <summary>What the button will do, spelled out for its tooltip.</summary>
    /// <remarks>
    /// Said even when the button is live. The three behaviours are the point of the design and
    /// a caption of two or three words cannot carry all of it — particularly the one where the
    /// unstaged files are deliberately left behind.
    /// </remarks>
    public string Explanation => Intent switch
    {
        CommitIntent.StageTickedThenCommit =>
            StagingCount == 1
                ? "Stage the file you ticked, then commit it."
                : $"Stage the {StagingCount} files you ticked, then commit them.",

        CommitIntent.CommitStaged =>
            "Commit what is staged. The files below it are left alone.",

        CommitIntent.StageAllThenCommit =>
            StagingCount == 1
                ? "Stage the changed file, then commit it."
                : $"Stage all {StagingCount} changed files, then commit them.",

        _ => "Nothing to commit.",
    };
}
