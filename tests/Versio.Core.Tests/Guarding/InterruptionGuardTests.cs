using Versio.Core.Guarding;
using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Guarding;

/// <summary>
/// The gate between the user and everything that could interrupt work in flight.
/// </summary>
/// <remarks>
/// Written as a matrix rather than as a handful of examples, because the failures this exists
/// to prevent are combinations — an export running <i>and</i> a dirty tree, a held lock
/// <i>and</i> unmerged paths — and a guard that handles each one alone while getting the pair
/// wrong would pass a suite of single-cause tests.
/// </remarks>
public sealed class InterruptionGuardTests
{
    private static readonly ActiveWork Exporting =
        new(WorkKind.Export, "Exporting Machine…", TimeSpan.FromSeconds(21));

    private static readonly ActiveWork Importing =
        new(WorkKind.Import, "Importing into Machine…", TimeSpan.FromSeconds(12));

    private static WorkingTreeSnapshot Dirty(int files = 3) => new()
    {
        DirtyPaths = [.. Enumerable.Range(0, files).Select(i => $"plc/file{i}.st")],
    };

    private static WorkingTreeSnapshot Conflicted(int files = 2) => new()
    {
        ConflictedPaths = [.. Enumerable.Range(0, files).Select(i => $"plc/conflict{i}.st")],
    };

    private static WorkingTreeSnapshot Locked() => new()
    {
        HeldLocks = [new HeldLock("b-1", "Machine", SyncDirection.TextAuthoritative)],
    };

    public static TheoryData<GuardIntent> AllIntents => [.. Enum.GetValues<GuardIntent>()];

    public static TheoryData<WorkKind> AllWorkKinds => [.. Enum.GetValues<WorkKind>()];

    // ---------------------------------------------------------------- nothing in the way

    [Theory]
    [MemberData(nameof(AllIntents))]
    public void An_idle_app_on_a_clean_tree_proceeds(GuardIntent intent)
    {
        var verdict = InterruptionGuard.Evaluate(intent, WorkSnapshot.Idle, WorkingTreeSnapshot.Clean);

        Assert.Equal(GuardDecision.Proceed, verdict.Decision);
        Assert.Equal(GuardReason.None, verdict.Reason);
        Assert.True(verdict.MayProceedNow);
        Assert.Empty(verdict.Choices);
    }

    // ---------------------------------------------------------------- work in flight

    [Theory]
    [InlineData(GuardIntent.SwitchBranch)]
    [InlineData(GuardIntent.SwitchRepository)]
    [InlineData(GuardIntent.Pull)]
    [InlineData(GuardIntent.Merge)]
    [InlineData(GuardIntent.Rebase)]
    public void Work_that_must_finish_refuses_intents_that_are_not_closing(GuardIntent intent)
    {
        var verdict = InterruptionGuard.Evaluate(
            intent, WorkSnapshot.Of(Importing), WorkingTreeSnapshot.Clean);

        Assert.Equal(GuardDecision.Refuse, verdict.Decision);
        Assert.Equal(GuardReason.UninterruptibleWork, verdict.Reason);
        Assert.Equal(Importing, Assert.Single(verdict.Work));
    }

    [Fact]
    public void Closing_during_an_import_asks_rather_than_refusing()
    {
        // The process is leaving. Stop is offered because the backup is the recovery;
        // branch-switch still refuses, which is the test above.
        var verdict = InterruptionGuard.Evaluate(
            GuardIntent.CloseApp, WorkSnapshot.Of(Importing), WorkingTreeSnapshot.Clean);

        Assert.Equal(GuardDecision.Ask, verdict.Decision);
        Assert.Equal(GuardReason.UninterruptibleWork, verdict.Reason);
        Assert.Equal(
            [GuardChoice.WaitThenProceed, GuardChoice.CancelWorkAndProceed, GuardChoice.Cancel],
            verdict.Choices);
        Assert.Contains(Importing, verdict.Work);
    }

    [Fact]
    public void A_refusal_offers_no_choices_at_all()
    {
        // The point of refusing rather than asking: there is no option that would make it
        // safe, so presenting one would be a lie with a button on it. CloseApp is the
        // exception — it asks — so this is a switch, which still refuses.
        var verdict = InterruptionGuard.Evaluate(
            GuardIntent.SwitchBranch, WorkSnapshot.Of(Importing), WorkingTreeSnapshot.Clean);

        Assert.Empty(verdict.Choices);
        Assert.Null(verdict.Recommended);
        Assert.Equal(GuardDecision.Refuse, verdict.Decision);
    }

    [Theory]
    [MemberData(nameof(AllIntents))]
    public void Cancellable_work_asks_rather_than_refusing(GuardIntent intent)
    {
        var verdict = InterruptionGuard.Evaluate(
            intent, WorkSnapshot.Of(Exporting), WorkingTreeSnapshot.Clean);

        Assert.Equal(GuardDecision.Ask, verdict.Decision);
        Assert.Equal(GuardReason.CancellableWork, verdict.Reason);
        Assert.Equal(Exporting, Assert.Single(verdict.Work));
    }

    [Fact]
    public void Waiting_is_recommended_over_cancelling()
    {
        // An export is 17-31 s. Offering "stop it" first would invite throwing away a run
        // that is very probably nearly finished.
        var verdict = InterruptionGuard.Evaluate(
            GuardIntent.SwitchBranch, WorkSnapshot.Of(Exporting), WorkingTreeSnapshot.Clean);

        Assert.Equal(GuardChoice.WaitThenProceed, verdict.Recommended);
        Assert.Equal(
            [GuardChoice.WaitThenProceed, GuardChoice.CancelWorkAndProceed, GuardChoice.Cancel],
            verdict.Choices);
    }

    [Fact]
    public void Work_that_must_finish_wins_over_work_that_could_be_cancelled()
    {
        // Both running. Closing still names the import and offers Stop; a switch would
        // refuse. Wait remains recommended — an import that is about to finish is safer
        // to let finish than to kill.
        var verdict = InterruptionGuard.Evaluate(
            GuardIntent.CloseApp,
            WorkSnapshot.Of(Exporting, Importing),
            WorkingTreeSnapshot.Clean);

        Assert.Equal(GuardDecision.Ask, verdict.Decision);
        Assert.Equal(GuardReason.UninterruptibleWork, verdict.Reason);
        Assert.Contains(Importing, verdict.Work);
        Assert.Equal(GuardChoice.WaitThenProceed, verdict.Recommended);
        Assert.Contains(GuardChoice.CancelWorkAndProceed, verdict.Choices);
    }

    [Fact]
    public void Work_is_reported_before_anything_about_the_repository()
    {
        // Dirty, conflicted, locked AND running. The answer is still "not yet" - and it says
        // so about the run, because that is the one the user can do nothing about.
        var repo = new WorkingTreeSnapshot
        {
            DirtyPaths = ["a.st"],
            ConflictedPaths = ["b.st"],
            HeldLocks = [new HeldLock("b-1", "Machine", SyncDirection.ProjectAuthoritative)],
        };

        var verdict = InterruptionGuard.Evaluate(
            GuardIntent.SwitchBranch, WorkSnapshot.Of(Importing), repo);

        Assert.Equal(GuardReason.UninterruptibleWork, verdict.Reason);
    }

    // ------------------------------------------------- intents that leave the tree alone

    [Theory]
    [InlineData(GuardIntent.SwitchRepository)]
    [InlineData(GuardIntent.CloseApp)]
    public void An_intent_that_does_not_rewrite_the_tree_ignores_its_state(GuardIntent intent)
    {
        // Switching repository leaves every file where it is; closing leaves them on disk and
        // the locks are recovered on the next open. Blocking either on a dirty tree would be
        // an obstacle rather than a safeguard.
        var repo = new WorkingTreeSnapshot
        {
            DirtyPaths = ["a.st", "b.st"],
            ConflictedPaths = ["c.st"],
            HeldLocks = [new HeldLock("b-1", "Machine", SyncDirection.TextAuthoritative)],
        };

        var verdict = InterruptionGuard.Evaluate(intent, WorkSnapshot.Idle, repo);

        Assert.Equal(GuardDecision.Proceed, verdict.Decision);
    }

    // ------------------------------------------------------------ the repository's state

    [Theory]
    [InlineData(GuardIntent.SwitchBranch)]
    [InlineData(GuardIntent.Pull)]
    [InlineData(GuardIntent.Merge)]
    [InlineData(GuardIntent.Rebase)]
    public void Unmerged_paths_refuse_anything_that_rewrites_the_tree(GuardIntent intent)
    {
        var verdict = InterruptionGuard.Evaluate(intent, WorkSnapshot.Idle, Conflicted(2));

        Assert.Equal(GuardDecision.Refuse, verdict.Decision);
        Assert.Equal(GuardReason.UnresolvedConflicts, verdict.Reason);
        Assert.Equal(2, verdict.ConflictedFileCount);
    }

    [Theory]
    [InlineData(GuardIntent.SwitchBranch)]
    [InlineData(GuardIntent.Pull)]
    [InlineData(GuardIntent.Merge)]
    [InlineData(GuardIntent.Rebase)]
    public void A_held_direction_lock_refuses_anything_that_rewrites_the_tree(GuardIntent intent)
    {
        var verdict = InterruptionGuard.Evaluate(intent, WorkSnapshot.Idle, Locked());

        Assert.Equal(GuardDecision.Refuse, verdict.Decision);
        Assert.Equal(GuardReason.HeldDirectionLock, verdict.Reason);
        Assert.Equal("Machine", Assert.Single(verdict.Locks).ProjectName);
    }

    [Fact]
    public void Conflicts_are_reported_before_a_held_lock()
    {
        // Both true. The conflict is the more fundamental problem and has the clearer remedy;
        // reporting it as a lock violation would send the user to the wrong fix.
        var repo = new WorkingTreeSnapshot
        {
            ConflictedPaths = ["a.st"],
            HeldLocks = [new HeldLock("b-1", "Machine", SyncDirection.TextAuthoritative)],
        };

        var verdict = InterruptionGuard.Evaluate(GuardIntent.SwitchBranch, WorkSnapshot.Idle, repo);

        Assert.Equal(GuardReason.UnresolvedConflicts, verdict.Reason);
    }

    [Fact]
    public void A_held_lock_is_reported_before_a_merely_dirty_tree()
    {
        // Stashing does not release a direction lock, so offering "stash and switch" here
        // would be offering a button that cannot do what it says.
        var repo = new WorkingTreeSnapshot
        {
            DirtyPaths = ["a.st"],
            HeldLocks = [new HeldLock("b-1", "Machine", SyncDirection.ProjectAuthoritative)],
        };

        var verdict = InterruptionGuard.Evaluate(GuardIntent.SwitchBranch, WorkSnapshot.Idle, repo);

        Assert.Equal(GuardDecision.Refuse, verdict.Decision);
        Assert.Equal(GuardReason.HeldDirectionLock, verdict.Reason);
    }

    [Fact]
    public void A_dirty_tree_offers_stash_first_discard_second_when_switching_branch()
    {
        var verdict = InterruptionGuard.Evaluate(
            GuardIntent.SwitchBranch, WorkSnapshot.Idle, Dirty(4), target: "feature/pumps");

        Assert.Equal(GuardDecision.Ask, verdict.Decision);
        Assert.Equal(GuardReason.DirtyWorkingTree, verdict.Reason);
        Assert.Equal(4, verdict.DirtyFileCount);
        Assert.Equal(
            [GuardChoice.StashAndProceed, GuardChoice.DiscardAndProceed, GuardChoice.Cancel],
            verdict.Choices);
    }

    [Theory]
    [InlineData(GuardIntent.Pull)]
    [InlineData(GuardIntent.Merge)]
    [InlineData(GuardIntent.Rebase)]
    public void Bringing_commits_in_is_never_offered_the_discard_button(GuardIntent intent)
    {
        // "Throw my work away so the pull applies cleanly" is not an intention anybody
        // forms — nor its merge or rebase spelling.
        var verdict = InterruptionGuard.Evaluate(intent, WorkSnapshot.Idle, Dirty());

        Assert.Equal([GuardChoice.StashAndProceed, GuardChoice.Cancel], verdict.Choices);
        Assert.DoesNotContain(GuardChoice.DiscardAndProceed, verdict.Choices);
    }

    // ------------------------------------------------------------------- shape invariants

    [Fact]
    public void Cancel_is_always_offered_and_always_last()
    {
        // A dialog with no way out is a trap, and one where the way out is not in the usual
        // place is a misclick waiting to happen.
        GuardVerdict[] asked =
        [
            InterruptionGuard.Evaluate(GuardIntent.SwitchBranch, WorkSnapshot.Of(Exporting), WorkingTreeSnapshot.Clean),
            InterruptionGuard.Evaluate(GuardIntent.SwitchBranch, WorkSnapshot.Idle, Dirty()),
            InterruptionGuard.Evaluate(GuardIntent.Pull, WorkSnapshot.Idle, Dirty()),
        ];

        foreach (var verdict in asked)
        {
            Assert.Equal(GuardDecision.Ask, verdict.Decision);
            Assert.Equal(GuardChoice.Cancel, verdict.Choices[^1]);
        }
    }

    [Theory]
    [MemberData(nameof(AllIntents))]
    public void The_target_survives_every_path_through_the_guard(GuardIntent intent)
    {
        // The sentence says which branch you are switching to. A path that dropped it would
        // produce "unsynced work would arrive on the other branch", which is the fallback,
        // not the answer.
        var repo = new WorkingTreeSnapshot { DirtyPaths = ["a.st"] };

        foreach (var work in new[] { WorkSnapshot.Idle, WorkSnapshot.Of(Exporting), WorkSnapshot.Of(Importing) })
        {
            Assert.Equal("main", InterruptionGuard.Evaluate(intent, work, repo, "main").Target);
        }
    }

    // ------------------------------------------------------------------- the work kinds

    [Fact]
    public void An_export_may_be_cancelled_and_an_import_may_not()
    {
        // The asymmetry the whole design rests on: an export stages and swaps, so killing it
        // leaves the previous mirror intact. An import writes into the binary project itself.
        Assert.Equal(Interruption.Cancellable, WorkKinds.InterruptionOf(WorkKind.Export));
        Assert.Equal(Interruption.MustFinish, WorkKinds.InterruptionOf(WorkKind.Import));
    }

    [Theory]
    [MemberData(nameof(AllWorkKinds))]
    public void Every_kind_of_work_has_a_decided_interruption(WorkKind kind)
    {
        Assert.True(Enum.IsDefined(WorkKinds.InterruptionOf(kind)));
    }

    [Fact]
    public void An_unrecognised_kind_is_treated_as_uninterruptible()
    {
        // Forgetting to extend the switch must cost a refusal, never a half-written project.
        Assert.Equal(Interruption.MustFinish, WorkKinds.InterruptionOf((WorkKind)9999));
    }

    [Fact]
    public void The_longest_running_operation_is_named_first()
    {
        // "Wait" is a reasonable answer only when the user can see what they are waiting for,
        // and that is the one that has been going longest - not whichever started last.
        var brief = new ActiveWork(WorkKind.Import, "Importing A…", TimeSpan.FromSeconds(2));
        var long_ = new ActiveWork(WorkKind.GitPush, "Pushing…", TimeSpan.FromSeconds(40));

        var snapshot = WorkSnapshot.Of(brief, long_);

        Assert.Equal(long_, snapshot.Uninterruptible[0]);
        Assert.True(snapshot.HasUninterruptible);
        Assert.False(snapshot.IsIdle);
    }

    [Fact]
    public void An_idle_snapshot_holds_nothing()
    {
        Assert.True(WorkSnapshot.Idle.IsIdle);
        Assert.False(WorkSnapshot.Idle.HasUninterruptible);
        Assert.Empty(WorkSnapshot.Idle.Cancellable);
        Assert.Empty(WorkSnapshot.Idle.Uninterruptible);
    }
}
