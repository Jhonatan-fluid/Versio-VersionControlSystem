using Versio.App.Views;
using Versio.Core.Git;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The merge picker's row rules: which branches can be chosen, and what it says about the rest.
/// </summary>
/// <remarks>
/// Pure row logic, no window - the dialog is a list and a button over these answers, and the
/// answers are where the mistakes are. Whether the CHOSEN branch may then actually be merged is
/// a different question, decided by <c>MergePreflight</c> and the guard.
/// </remarks>
public sealed class BranchMergeWindowTests
{
    private static BranchCandidate Candidate(
        string name,
        BranchExistence where = BranchExistence.Both,
        bool merged = false,
        bool current = false) =>
        new()
        {
            Name = name,
            Existence = where,
            IsMerged = merged,
            IsCurrent = current,
            IsDefault = false,
        };

    [Fact]
    public void An_ordinary_branch_with_work_on_it_can_be_merged()
    {
        var row = new BranchMergeRow(Candidate("feature"), alreadyIn: false);

        Assert.True(row.CanMerge);
        Assert.Equal("has new work", row.MergedText);
    }

    [Fact]
    public void The_branch_you_are_on_cannot_be_merged_into_itself()
    {
        var row = new BranchMergeRow(Candidate("main", current: true), alreadyIn: false);

        Assert.False(row.CanMerge);
        Assert.Equal("this branch", row.MergedText);
    }

    [Fact]
    public void A_branch_already_in_this_one_is_offered_but_not_selectable()
    {
        // Offered rather than hidden: a branch missing from the list with no explanation reads
        // as a bug, and "already in" is the sentence that stops somebody running a no-op.
        var row = new BranchMergeRow(Candidate("done", merged: true), alreadyIn: true);

        Assert.False(row.CanMerge);
        Assert.Equal("already in", row.MergedText);
    }

    [Fact]
    public void A_remote_only_branch_cannot_be_merged_by_the_name_shown()
    {
        // FOUND ON A SECOND READ. Its short name has no local ref, so `git diff HEAD...name`
        // fails outright - and before this, the failure came back as an empty diff and the
        // preflight announced "already in this branch" about a branch that had never been
        // looked at. Merging it properly means naming its remote ref, which the dialog does
        // not do yet; until it does, the row says why rather than lying about it.
        var row = new BranchMergeRow(
            Candidate("upstream-work", BranchExistence.RemoteOnly), alreadyIn: false);

        Assert.False(row.CanMerge);
        Assert.Equal("not here yet", row.MergedText);
        Assert.Equal("remote only", row.WhereText);
    }
}
