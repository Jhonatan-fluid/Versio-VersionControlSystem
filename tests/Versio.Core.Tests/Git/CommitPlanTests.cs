using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// One button, two behaviours, and the label that keeps them apart.
/// </summary>
/// <remarks>
/// The case worth reading first is <see cref="Nothing_ticked_commits_the_whole_list"/>.
/// Ticking none used to mean "commit whatever is already in the index", which needed a
/// visible STAGED list to be honest. Without that list, a plain Commit that left files
/// behind would be the silent sweep this type exists to prevent.
/// </remarks>
public sealed class CommitPlanTests
{
    [Fact]
    public void An_empty_tree_has_nothing_to_commit()
    {
        var plan = CommitPlan.For(changes: 0, ticked: 0);

        Assert.Equal(CommitIntent.Nothing, plan.Intent);
        Assert.False(plan.CanCommit);
        Assert.Equal("Nothing to commit.",
                     plan.BlockedReason(hasMessage: true, hasIdentity: true, hasConflicts: false));
    }

    [Fact]
    public void Ticked_rows_are_the_commit()
    {
        var plan = CommitPlan.For(changes: 12, ticked: 3);

        Assert.Equal(CommitIntent.CommitTicked, plan.Intent);
        Assert.Equal(3, plan.IncludeCount);
        Assert.Equal("Commit (3)", plan.Label);
    }

    [Fact]
    public void Nothing_ticked_commits_the_whole_list()
    {
        // The common case: change some files, press the button, get a commit. Nobody should
        // have to tick first, and files already in the index are not a separate list.
        var plan = CommitPlan.For(changes: 4, ticked: 0);

        Assert.Equal(CommitIntent.CommitAll, plan.Intent);
        Assert.Equal(4, plan.IncludeCount);
        Assert.Equal("Commit", plan.Label);
    }

    [Fact]
    public void Ticking_all_is_the_same_count_as_ticking_none_but_says_so()
    {
        // The label carries the difference: Commit (4) is an explicit selection of every
        // row, which is not ambiguous, and Commit (with nothing ticked) is the whole list.
        var plan = CommitPlan.For(changes: 4, ticked: 4);

        Assert.Equal(CommitIntent.CommitTicked, plan.Intent);
        Assert.Equal("Commit (4)", plan.Label);
    }

    [Theory]
    [InlineData(1, "Commit the file you ticked. The others stay in the working tree.")]
    [InlineData(2, "Commit the 2 files you ticked. The others stay in the working tree.")]
    public void The_ticked_explanation_is_pluralised(int ticked, string expected)
    {
        Assert.Equal(expected, CommitPlan.For(5, ticked).Explanation);
    }

    [Theory]
    [InlineData(1, "Commit the changed file.")]
    [InlineData(4, "Commit all 4 changed files.")]
    public void The_all_explanation_is_pluralised(int changes, string expected)
    {
        Assert.Equal(expected, CommitPlan.For(changes, ticked: 0).Explanation);
    }

    [Fact]
    public void Conflicts_block_before_a_missing_message()
    {
        var plan = CommitPlan.For(3, 0);

        Assert.Equal("Resolve the conflicts first.",
                     plan.BlockedReason(hasMessage: false, hasIdentity: true, hasConflicts: true));
    }

    [Fact]
    public void A_missing_identity_beats_a_missing_message()
    {
        var plan = CommitPlan.For(3, 0);

        Assert.Equal("Set a name and email for git first.",
                     plan.BlockedReason(hasMessage: false, hasIdentity: false, hasConflicts: false));
    }

    [Fact]
    public void A_missing_message_is_the_last_thing_to_fix()
    {
        var plan = CommitPlan.For(3, 0);

        Assert.Equal("Write a commit message first.",
                     plan.BlockedReason(hasMessage: false, hasIdentity: true, hasConflicts: false));
        Assert.Equal(string.Empty,
                     plan.BlockedReason(hasMessage: true, hasIdentity: true, hasConflicts: false));
    }

    [Fact]
    public void A_paused_rebase_is_continued_and_the_caption_says_so()
    {
        // The button read "Commit" through a paused rebase and ran `git commit` on the detached
        // HEAD a rebase works on: a stray commit, the branch unchanged, the rest unreplayed
        // (the 2026-10-25 review's sixth round, P2). The caption is the safeguard here as it
        // is for the ticked commit: it has to name the operation that will actually run.
        var plan = CommitPlan.ContinuingRebase;

        Assert.Equal(CommitIntent.ContinueRebase, plan.Intent);
        Assert.True(plan.CanCommit);
        Assert.Equal("Continue rebase", plan.Label);
        Assert.Contains("message box is left as it is", plan.Explanation);
    }

    [Fact]
    public void Continuing_a_rebase_needs_no_message_but_still_needs_conflicts_resolved_and_an_identity()
    {
        // Each replayed commit keeps its own message, so the box is the message of nothing and
        // demanding one would have the user type text that goes nowhere. The other two gates
        // stand: git refuses to continue over an unmerged path, and the replayed commits are
        // re-committed under the current identity.
        var plan = CommitPlan.ContinuingRebase;

        Assert.False(plan.NeedsMessage);
        Assert.Equal(string.Empty,
                     plan.BlockedReason(hasMessage: false, hasIdentity: true, hasConflicts: false));
        Assert.Equal("Resolve the conflicts first.",
                     plan.BlockedReason(hasMessage: false, hasIdentity: true, hasConflicts: true));
        Assert.Equal("Set a name and email for git first.",
                     plan.BlockedReason(hasMessage: false, hasIdentity: false, hasConflicts: false));
    }

    [Fact]
    public void An_ordinary_commit_still_needs_its_message()
    {
        // The relaxation above is the rebase's alone; a plain commit with an empty box is
        // still refused here rather than by git's editor that never opens.
        Assert.True(CommitPlan.For(3, 0).NeedsMessage);
        Assert.True(CommitPlan.For(3, 2).NeedsMessage);
    }
}
