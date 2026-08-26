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
    /// Bring the upstream's commits into this working tree with a pull.
    /// </summary>
    /// <remarks>
    /// <b>Wired, by <c>PullAsync</c> in <c>GitRemoteViewModel</c>.</b> A pull rewrites the
    /// project and its mirror together — the scenario <c>SAFEGUARDS</c> §5.A calls able to
    /// corrupt a project through no user error — so it is one of the intents
    /// <c>RewritesWorkingTree</c> answers true for, and its dirty-tree branch is offered
    /// stash-without-discard for reasons argued in <c>ChoicesForDirtyTree</c>.
    /// <para>
    /// <b>Why it waited, as history.</b> It shipped implemented, tested and deliberately
    /// unreachable: all three of its verdicts — unmerged paths, a held direction lock, a dirty
    /// tree — resolve to the auto-stash, and until that existed, wiring it would have offered a
    /// button that could not do what it said. The auto-stash landed and the callers followed.
    /// This paragraph is the record of that, not a description of today.
    /// </para>
    /// <para>
    /// <b>Closed gap, as history.</b> For a while this was the intent for merge and rebase
    /// too, because it was the whole of "bring commits in" when the enum was written — so a
    /// guarded merge asked "Continue and pull?" and logged "asking before pull". The decision
    /// was right in every case; only the verb was wrong. <see cref="Merge"/> and
    /// <see cref="Rebase"/> exist so each sentence names the thing the button will do, and
    /// <c>RewritesWorkingTree</c> learned them in the same change — the silent hole the old
    /// remark warned a partial fix would open.
    /// </para>
    /// </remarks>
    Pull,

    /// <summary>
    /// Merge another branch's commits into this working tree.
    /// </summary>
    /// <remarks>
    /// <b>Wired, by two callers in the shell:</b> the merge verb in the branch menu, and the
    /// divergence reconcile when the user picks merge over rebase. The guard treats it
    /// exactly as <see cref="Pull"/> — same rewrite of project and mirror together, same
    /// verdicts — the intent exists so the dialog and the log say "merge" to somebody about
    /// to merge. The words are the safeguard here: a person who just clicked Merge and is
    /// asked about a pull has been given a reason to distrust the question.
    /// </remarks>
    Merge,

    /// <summary>
    /// Replay this branch's commits onto the upstream.
    /// </summary>
    /// <remarks>
    /// <b>Wired, by the divergence reconcile</b> when the user picks rebase. Guarded like
    /// <see cref="Pull"/> and <see cref="Merge"/> and, if anything, more deservedly — a
    /// rebase rewrites the working tree several times over as each commit replays — but the
    /// guard does not grade severity, it only decides whether to ask; the intent exists for
    /// the same reason <see cref="Merge"/> does, so the sentence names the verb.
    /// </remarks>
    Rebase,
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
