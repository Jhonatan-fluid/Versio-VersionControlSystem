namespace Versio.Core.Guarding;

/// <summary>What the user is trying to do.</summary>
/// <remarks>
/// <para>
/// The intent is what decides which repository state matters. Switching branch rewrites the
/// working tree, so a dirty tree and a held direction lock are both its business; switching
/// repository leaves every file exactly where it is, so neither is.
/// </para>
/// <para>
/// <b>Every member here has a caller, or says why it does not.</b> This enum first shipped with
/// <c>Clone</c> and <c>Discard</c> alongside these, reaching nothing — which implied a guard
/// over cloning and discarding that did not exist. They were removed rather than wired, because
/// <see cref="InterruptionGuard"/> would have returned exactly what the commands' own
/// <c>Ready</c> check already enforces: both leave the working tree alone, so the guard has no
/// verdict to add beyond "something is running", and their buttons are already dead while
/// anything is.
/// </para>
/// <para>
/// The theory tests over this enum did not catch it, and could not have: they assert that every
/// intent has a label and a verdict, which proves the guard handles each one and says nothing
/// about whether anything asks.
/// </para>
/// </remarks>
public enum GuardIntent
{
    /// <summary>Check out a different branch.</summary>
    SwitchBranch,

    /// <summary>Open a different repository.</summary>
    SwitchRepository,

    /// <summary>Close the application.</summary>
    CloseApp,

    /// <summary>
    /// Pull from the upstream.
    /// </summary>
    /// <remarks>
    /// <b>Implemented and deliberately not yet wired.</b> A pull rewrites the working tree, so
    /// this is one of the two intents <c>RewritesWorkingTree</c> answers true for, and its
    /// dirty-tree branch is offered stash-without-discard for reasons argued in
    /// <c>ChoicesForDirtyTree</c>. All three of its verdicts — unmerged paths, a held direction
    /// lock, a dirty tree — resolve to the auto-stash, which is not built yet, so wiring it now
    /// would offer a button that cannot do what it says.
    /// <para>
    /// Kept rather than deleted for the same reason the stash client operations are kept while
    /// withdrawn from the UI: the behaviour is designed and tested, and the thing missing is a
    /// caller. Meanwhile <c>PullAsync</c> is gated on <c>Ready</c> like every other git command,
    /// so it cannot run while anything else is.
    /// </para>
    /// </remarks>
    Pull,
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
