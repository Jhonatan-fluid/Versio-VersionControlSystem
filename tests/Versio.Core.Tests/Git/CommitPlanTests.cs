using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// One button, three behaviours, and the label that keeps them apart.
/// </summary>
/// <remarks>
/// The case worth reading first is <see cref="Something_staged_and_nothing_ticked_commits_only_that"/>.
/// It is the only one where getting it wrong is silent: sweeping the unstaged files into a
/// commit because somebody pressed a general-purpose button undoes a deliberate partial stage,
/// and they find out by reading the commit afterwards, by which time it is history.
/// </remarks>
public sealed class CommitPlanTests
{
    [Fact]
    public void An_empty_tree_has_nothing_to_commit()
    {
        var plan = CommitPlan.For(staged: 0, unstaged: 0, ticked: 0);

        Assert.Equal(CommitIntent.Nothing, plan.Intent);
        Assert.False(plan.CanCommit);
        Assert.Equal("Nothing to commit.",
                     plan.BlockedReason(hasMessage: true, hasIdentity: true, hasConflicts: false));
    }

    [Fact]
    public void Ticked_rows_are_staged_and_committed()
    {
        var plan = CommitPlan.For(staged: 0, unstaged: 12, ticked: 3);

        Assert.Equal(CommitIntent.StageTickedThenCommit, plan.Intent);
        Assert.Equal(3, plan.StagingCount);
        Assert.Equal("Stage & commit (3)", plan.Label);
    }

    [Fact]
    public void Ticks_win_over_what_is_already_staged()
    {
        // An explicit selection is the least ambiguous instruction the panel can receive, so it
        // beats every other signal.
        var plan = CommitPlan.For(staged: 5, unstaged: 12, ticked: 2);

        Assert.Equal(CommitIntent.StageTickedThenCommit, plan.Intent);
        Assert.Equal("Stage & commit (2)", plan.Label);
    }

    [Fact]
    public void Something_staged_and_nothing_ticked_commits_only_that()
    {
        // THE CASE THAT MATTERS. Somebody staged three files on purpose and left a fourth out.
        // Pressing this must not bring the fourth along, and the plain label is what tells them
        // so before the press rather than after.
        var plan = CommitPlan.For(staged: 3, unstaged: 1, ticked: 0);

        Assert.Equal(CommitIntent.CommitStaged, plan.Intent);
        Assert.Equal(0, plan.StagingCount);
        Assert.Equal("Commit", plan.Label);
        Assert.Contains("left alone", plan.Explanation);
    }

    [Fact]
    public void Nothing_staged_and_nothing_ticked_stages_everything()
    {
        // The common case: change some files, press the button, get a commit. Nobody should
        // have to stage first to use this panel at all.
        var plan = CommitPlan.For(staged: 0, unstaged: 4, ticked: 0);

        Assert.Equal(CommitIntent.StageAllThenCommit, plan.Intent);
        Assert.Equal(4, plan.StagingCount);
        Assert.Equal("Stage all & commit", plan.Label);
    }

    [Theory]
    [InlineData(1, "Stage the file you ticked, then commit it.")]
    [InlineData(2, "Stage the 2 files you ticked, then commit them.")]
    public void The_explanation_is_pluralised(int ticked, string expected)
    {
        Assert.Equal(expected, CommitPlan.For(0, 5, ticked).Explanation);
    }

    [Fact]
    public void One_changed_file_reads_as_one()
    {
        Assert.Equal("Stage the changed file, then commit it.",
                     CommitPlan.For(staged: 0, unstaged: 1, ticked: 0).Explanation);
    }

    [Fact]
    public void Every_live_state_explains_what_it_will_do()
    {
        // The three behaviours are the point of the design, and a two-word caption cannot carry
        // all of it. The tooltip is not decoration here.
        (int Staged, int Unstaged, int Ticked)[] live =
        [
            (0, 4, 0),
            (3, 1, 0),
            (0, 12, 3),
        ];

        foreach (var (staged, unstaged, ticked) in live)
        {
            var plan = CommitPlan.For(staged, unstaged, ticked);

            Assert.True(plan.CanCommit);
            Assert.False(string.IsNullOrWhiteSpace(plan.Explanation));
            Assert.False(string.IsNullOrWhiteSpace(plan.Label));
        }
    }

    // ------------------------------------------------------------------ blocked reasons

    [Fact]
    public void A_conflicted_tree_is_the_first_thing_reported()
    {
        // Ahead of everything else, because git will refuse the commit anyway and no amount of
        // typing a message or staging changes that.
        var plan = CommitPlan.For(staged: 3, unstaged: 0, ticked: 0);

        Assert.Equal("Resolve the conflicts first.",
                     plan.BlockedReason(hasMessage: true, hasIdentity: true, hasConflicts: true));
    }

    [Fact]
    public void Nothing_to_commit_beats_no_message()
    {
        // Typing a message for an empty commit is wasted effort, so the emptier problem is
        // named first.
        var plan = CommitPlan.For(0, 0, 0);

        Assert.Equal("Nothing to commit.",
                     plan.BlockedReason(hasMessage: false, hasIdentity: true, hasConflicts: false));
    }

    [Fact]
    public void A_missing_identity_beats_a_missing_message()
    {
        // Both must be fixed, and the identity is the one whose absence costs a 17-31 s export
        // before git refuses. Naming it first gets it fixed first.
        var plan = CommitPlan.For(staged: 1, unstaged: 0, ticked: 0);

        Assert.Equal("Set a name and email for git first.",
                     plan.BlockedReason(hasMessage: false, hasIdentity: false, hasConflicts: false));
    }

    [Fact]
    public void A_missing_message_is_reported_when_it_is_the_only_thing_left()
    {
        var plan = CommitPlan.For(staged: 1, unstaged: 0, ticked: 0);

        Assert.Equal("Write a commit message first.",
                     plan.BlockedReason(hasMessage: false, hasIdentity: true, hasConflicts: false));
    }

    [Fact]
    public void Nothing_is_reported_when_the_button_is_live()
    {
        var plan = CommitPlan.For(staged: 1, unstaged: 0, ticked: 0);

        Assert.Equal(string.Empty,
                     plan.BlockedReason(hasMessage: true, hasIdentity: true, hasConflicts: false));
    }
}
