using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Merging a named branch into the current one, end to end against real git.
/// </summary>
/// <remarks>
/// <para>
/// The rule about when this is <i>refused</i> lives in <c>MergePreflightTests</c>, pure and
/// without a repository. What is checked here is the wiring around it: that the picker is
/// asked, that cancelling does nothing, and that a merge Versio allows actually lands.
/// </para>
/// <para>
/// Against a real repository, because the question is which commits end up on the branch —
/// and a fake git client would answer that by agreeing with the code.
/// </para>
/// </remarks>
public sealed class MergeBranchFlowTests
{
    /// <summary>A second branch carrying one commit the current branch does not have.</summary>
    private static void GiveItABranchToMerge(TempRepo repo, string branch, string file)
    {
        repo.Git($"checkout -q -b {branch}");
        repo.Touch(file);
        repo.Git("add -A");
        repo.Git($"commit -qm work-on-{branch}");
        repo.Git("checkout -q main");
    }

    [Fact]
    public async Task Cancelling_the_picker_merges_nothing()
    {
        using var repo = new TempRepo();
        GiveItABranchToMerge(repo, "feature", "feature.md");

        using var shell = TestShell.Build();
        shell.Prompts = new StubPrompts { BranchMerge = (_, _) => null };

        await shell.OpenAsync(repo.Path);
        await Eventually.Press(shell.MergeBranchCommand, "merge a branch");

        Assert.False(File.Exists(Path.Combine(repo.Path, "feature.md")),
                     "cancelling the picker merged the branch anyway");
    }

    [Fact]
    public async Task The_picker_is_asked_about_the_branch_being_merged_into()
    {
        // The dialog's headline names the destination, and the destination is whichever branch
        // happens to be checked out - the half people get wrong. It comes from the shell rather
        // than the dialog guessing.
        using var repo = new TempRepo();
        GiveItABranchToMerge(repo, "feature", "feature.md");

        string? askedAbout = null;

        using var shell = TestShell.Build();
        shell.Prompts = new StubPrompts
        {
            BranchMerge = (_, into) => { askedAbout = into; return null; },
        };

        await shell.OpenAsync(repo.Path);
        await Eventually.Press(shell.MergeBranchCommand, "merge a branch");

        Assert.Equal(shell.BranchName, askedAbout);

        // And the overflow's caption names it too, so the destination is on screen BEFORE the
        // dialog opens rather than only inside it. With no repository open there is no honest
        // answer and it falls back to the bare verb — asserted in GitPanelChromeTests.
        Assert.Equal($"Merge a branch into {shell.BranchName}…", shell.MergeBranchLabel);
    }

    [Fact]
    public async Task A_branch_the_preflight_allows_is_actually_merged()
    {
        // Nothing tracked in this repository, so the preflight says Proceed and the merge is
        // ordinary git. The assertion is that the other branch's work is now in the tree.
        using var repo = new TempRepo();
        GiveItABranchToMerge(repo, "feature", "feature.md");

        using var shell = TestShell.Build();
        shell.Prompts = new StubPrompts { BranchMerge = (_, _) => "feature" };

        await shell.OpenAsync(repo.Path);
        await Eventually.Press(shell.MergeBranchCommand, "merge a branch");

        await Eventually.Until(() => File.Exists(Path.Combine(repo.Path, "feature.md")),
                               "the merged branch's file arrived");
    }

    [Fact]
    public async Task A_branch_already_in_this_one_is_marked_as_such()
    {
        // "already in" is what stops somebody running a merge that can do nothing. It is asked
        // of the CURRENT branch, not of the trunk - which is exactly where it differs from the
        // delete dialog's identically-named column, where "merged" means safe to delete.
        using var repo = new TempRepo();
        GiveItABranchToMerge(repo, "feature", "feature.md");

        using var shell = TestShell.Build();
        shell.Prompts = new StubPrompts { BranchMerge = (_, _) => "feature" };

        await shell.OpenAsync(repo.Path);
        await Eventually.Press(shell.MergeBranchCommand, "merge a branch");
        await Eventually.Until(() => File.Exists(Path.Combine(repo.Path, "feature.md")),
                               "the first merge landed");

        IReadOnlyList<BranchCandidate> seen = [];
        shell.Prompts = new StubPrompts
        {
            BranchMerge = (candidates, _) => { seen = candidates; return null; },
        };

        await Eventually.Press(shell.MergeBranchCommand, "merge a branch again");

        var feature = Assert.Single(seen, c => c.Name == "feature");
        Assert.True(feature.IsMerged, "feature is in this branch and was not marked as such");
    }
}
