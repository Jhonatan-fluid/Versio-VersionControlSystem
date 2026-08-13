namespace Versio.Core.Guarding;

/// <summary>What the user is trying to do.</summary>
/// <remarks>
/// The intent is what decides which repository state matters. Switching branch rewrites the
/// working tree, so a dirty tree and a held direction lock are both its business; switching
/// repository leaves every file exactly where it is, so neither is.
/// </remarks>
public enum GuardIntent
{
    /// <summary>Check out a different branch.</summary>
    SwitchBranch,

    /// <summary>Open a different repository.</summary>
    SwitchRepository,

    /// <summary>Close the application.</summary>
    CloseApp,

    /// <summary>Clone into a new folder.</summary>
    Clone,

    /// <summary>Pull from the upstream.</summary>
    Pull,

    /// <summary>Discard changes to a file.</summary>
    Discard,
}

/// <summary>The three answers the guard can give.</summary>
public enum GuardDecision
{
    /// <summary>Nothing is in the way. Carry on without asking.</summary>
    Proceed,

    /// <summary>The user must choose. <see cref="GuardVerdict.Choices"/> says between what.</summary>
    Ask,

    /// <summary>Not now, and no choice would make it safe. The reason names the remedy.</summary>
    Refuse,
}

/// <summary>Why the guard did not simply say yes.</summary>
public enum GuardReason
{
    /// <summary>Nothing was in the way.</summary>
    None,

    /// <summary>Something is running that cannot be cut short.</summary>
    UninterruptibleWork,

    /// <summary>Something is running that could be cancelled.</summary>
    CancellableWork,

    /// <summary>The working tree has uncommitted changes.</summary>
    DirtyWorkingTree,

    /// <summary>A binding holds a direction lock, so unsynced work would cross the switch.</summary>
    HeldDirectionLock,

    /// <summary>The tree has unmerged paths.</summary>
    UnresolvedConflicts,
}

/// <summary>One option offered to the user.</summary>
/// <remarks>
/// An enum rather than free text so the view model acts on a decision instead of comparing the
/// label it happened to render. <see cref="GuardNarrative.Label"/> is the only place the words
/// live.
/// </remarks>
public enum GuardChoice
{
    /// <summary>Do nothing. Always last, and always the safe answer to a wiring mistake.</summary>
    Cancel,

    /// <summary>Go ahead exactly as asked.</summary>
    Proceed,

    /// <summary>Stash the working tree first, then go ahead.</summary>
    StashAndProceed,

    /// <summary>Throw the changes away, then go ahead.</summary>
    DiscardAndProceed,

    /// <summary>Cancel what is running, then go ahead.</summary>
    CancelWorkAndProceed,

    /// <summary>Let the running work finish, then go ahead.</summary>
    WaitThenProceed,
}

/// <summary>
/// The guard's answer: a decision, and every fact needed to explain it.
/// </summary>
/// <remarks>
/// <b>Facts, not prose.</b> The verdict carries counts and names; <see cref="GuardNarrative"/>
/// turns them into sentences. Keeping the two apart is what lets the decision be asserted by a
/// test that does not care about wording, and the wording be asserted by a test that does not
/// have to reconstruct a scenario to reach it — the same split that already works for
/// <see cref="SyncNarrative"/> and the state machine beneath it.
/// </remarks>
public sealed record GuardVerdict
{
    public required GuardDecision Decision { get; init; }

    public required GuardIntent Intent { get; init; }

    public GuardReason Reason { get; init; } = GuardReason.None;

    /// <summary>What to offer, recommended first. Empty unless <see cref="Decision"/> is
    /// <see cref="GuardDecision.Ask"/>.</summary>
    public IReadOnlyList<GuardChoice> Choices { get; init; } = [];

    /// <summary>The work in the way, longest-running first. Empty when work is not the reason.</summary>
    public IReadOnlyList<ActiveWork> Work { get; init; } = [];

    /// <summary>The locks in the way. Empty when locks are not the reason.</summary>
    public IReadOnlyList<HeldLock> Locks { get; init; } = [];

    public int DirtyFileCount { get; init; }

    public int ConflictedFileCount { get; init; }

    /// <summary>What is being switched to, when there is such a thing — a branch or a
    /// repository name. Empty for intents that have no target.</summary>
    public string Target { get; init; } = string.Empty;

    public bool MayProceedNow => Decision == GuardDecision.Proceed;

    /// <summary>The option a dialog should focus. Always the first, which is always the
    /// recommended one.</summary>
    public GuardChoice? Recommended => Choices.Count > 0 ? Choices[0] : null;
}
