namespace Versio.Core.Guarding;

/// <summary>
/// Decides whether something the user just asked for may happen right now.
/// </summary>
/// <remarks>
/// <para>
/// Versio's dangerous half is well defended and its safe half was not. The direction lock, the
/// hash gate, the self-write suppressor and the circuit breaker all sit below the UI; the
/// actions a user takes with a mouse — switch branch, switch repository, close the window —
/// reached none of them. This is the missing gate, and it is deliberately built the same way
/// the others were: pure, so that every combination can be asserted without git, without
/// CODESYS, without a clock and without a window.
/// </para>
/// <para>
/// <b>Order of checks matters and is not arbitrary.</b>
/// </para>
/// <list type="number">
///   <item><b>Work that cannot be cut short comes first.</b> No answer about the repository is
///     interesting while a CODESYS import is writing into a <c>.project</c> — the only correct
///     response is "not yet", whatever else is true.</item>
///   <item><b>Then work that can.</b> Offering to cancel is only honest once we know nothing
///     uninterruptible is also running.</item>
///   <item><b>Then unmerged paths</b>, before the lock, because a conflicted tree is the more
///     fundamental problem and git will refuse the operation on its own account anyway.
///     Reporting it as a lock violation would send the user to the wrong remedy.</item>
///   <item><b>Then the direction lock</b>, before the dirty tree, because a held lock is not
///     something stashing fixes. Offering "stash and switch" for it would be offering a button
///     that cannot work.</item>
///   <item><b>Then the dirty tree</b>, which is the one case with a genuine choice in it.</item>
/// </list>
/// <para>
/// <b>Every refusal names its remedy.</b> A refusal is the message most likely to be read as a
/// broken tool, so the wording is part of the feature — see <see cref="GuardNarrative"/>, which
/// is the only place it lives.
/// </para>
/// </remarks>
public static class InterruptionGuard
{
    /// <param name="intent">What the user is trying to do.</param>
    /// <param name="work">Everything in flight at this moment.</param>
    /// <param name="repo">The repository state. Ignored for intents that leave the working
    /// tree alone — see <see cref="RewritesWorkingTree"/>.</param>
    /// <param name="target">The branch or repository being switched to, for the sentence.</param>
    public static GuardVerdict Evaluate(
        GuardIntent intent,
        WorkSnapshot work,
        WorkingTreeSnapshot repo,
        string target = "")
    {
        // 1. Work that must be allowed to finish. No choice makes this safe, so it is the one
        //    branch here that refuses outright rather than asking.
        if (work.HasUninterruptible)
        {
            return new GuardVerdict
            {
                Decision = GuardDecision.Refuse,
                Intent = intent,
                Reason = GuardReason.UninterruptibleWork,
                Work = work.Uninterruptible,
                Target = target,
            };
        }

        // 2. Work that can be cancelled. An export is 17-31 s, so "wait" is a real answer and
        //    is offered second — ahead of cancelling, which throws away a run that is probably
        //    nearly done.
        if (!work.IsIdle)
        {
            return new GuardVerdict
            {
                Decision = GuardDecision.Ask,
                Intent = intent,
                Reason = GuardReason.CancellableWork,
                Choices = [GuardChoice.WaitThenProceed, GuardChoice.CancelWorkAndProceed, GuardChoice.Cancel],
                Work = work.Cancellable,
                Target = target,
            };
        }

        // 3. Nothing is running. Whether the repository's state matters at all now depends
        //    entirely on the intent.
        if (!RewritesWorkingTree(intent))
        {
            return Proceed(intent, target);
        }

        if (repo.HasConflicts)
        {
            return new GuardVerdict
            {
                Decision = GuardDecision.Refuse,
                Intent = intent,
                Reason = GuardReason.UnresolvedConflicts,
                ConflictedFileCount = repo.ConflictedPaths.Count,
                Target = target,
            };
        }

        if (repo.HasHeldLocks)
        {
            return new GuardVerdict
            {
                Decision = GuardDecision.Refuse,
                Intent = intent,
                Reason = GuardReason.HeldDirectionLock,
                Locks = repo.HeldLocks,
                Target = target,
            };
        }

        if (repo.IsDirty)
        {
            return new GuardVerdict
            {
                Decision = GuardDecision.Ask,
                Intent = intent,
                Reason = GuardReason.DirtyWorkingTree,
                Choices = ChoicesForDirtyTree(intent),
                DirtyFileCount = repo.DirtyPaths.Count,
                Target = target,
            };
        }

        return Proceed(intent, target);
    }

    /// <summary>
    /// Does this intent rewrite the files in the working tree?
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction that decides whether the repository's state is any of the guard's
    /// business. <b>Switching repository leaves every file exactly where it is</b> — the folder
    /// the user was working in is still on disk, still dirty, still holding its locks, and
    /// still there when they come back. Blocking that on a dirty tree would be refusing to
    /// look at a second repository because the first one has unsaved work, which is not a
    /// safety property, only an obstacle.
    /// </para>
    /// <para>
    /// Closing the app is the same argument. A dirty tree survives the process, the direction
    /// locks are persisted and recovered on the next open, and the recovery policy already
    /// re-validates them against content hashes rather than trusting elapsed time.
    /// </para>
    /// </remarks>
    private static bool RewritesWorkingTree(GuardIntent intent) =>
        intent is GuardIntent.SwitchBranch or GuardIntent.Pull;

    /// <summary>
    /// What to offer when the only thing in the way is uncommitted work.
    /// </summary>
    /// <remarks>
    /// Stash leads in both cases: it is the only option that keeps the work, and it is what
    /// somebody in this position almost always wants.
    /// <para>
    /// <b>Discard is offered for a branch switch and not for a pull.</b> "Throw this away and
    /// switch" is a real intention — an experiment that turned out badly is abandoned exactly
    /// this way. "Throw my work away so the pull applies cleanly" is not an intention anybody
    /// forms; it is a thing that happens to them. Offering it would put an irreversible button
    /// under a routine one for no use that was asked for.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<GuardChoice> ChoicesForDirtyTree(GuardIntent intent) =>
        intent == GuardIntent.SwitchBranch
            ? [GuardChoice.StashAndProceed, GuardChoice.DiscardAndProceed, GuardChoice.Cancel]
            : [GuardChoice.StashAndProceed, GuardChoice.Cancel];

    private static GuardVerdict Proceed(GuardIntent intent, string target) => new()
    {
        Decision = GuardDecision.Proceed,
        Intent = intent,
        Reason = GuardReason.None,
        Target = target,
    };
}
