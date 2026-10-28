using Versio.App.ViewModels;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A commit's tree, picked, reaches the shared pane.
/// </summary>
/// <remarks>
/// <para>
/// The History panel raises <c>TreeSelected</c> and the shell renders it; the subscription
/// between them is one line in the shell's constructor, and a line like that is the most
/// likely kind of change to lose its coverage silently (AGENTS.md: three moved subscriptions
/// left the whole suite green). This class is what goes red when it is deleted.
/// </para>
/// <para>
/// The history is a fake (its commits and tree are invented) while the pane reads a real
/// repository: what is under test is the route from a tree row to the pane, not git.
/// </para>
/// </remarks>
public sealed class HistoryTreeFlowTests
{
    [Fact]
    public async Task Picking_a_row_of_a_commits_tree_shows_that_file_at_that_commit()
    {
        using var repo = new TempRepo(unstagedFiles: 1);
        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);
        var git = new CountingGitClient();
        shell.History.Attach(git);
        shell.ActivePanel = ShellPanel.History;

        var commit = shell.History.Commits[0];
        commit.IsExpanded = true;
        var row = Assert.IsType<TreeRow>(commit.Children[0]);
        Assert.Equal("a.st", row.Node.Path);

        // What a click does, in the order the view does it.
        row.IsSelected = true;
        shell.History.Select(row);

        // The header is set before git is asked, so this is immediate; the commit is named in
        // it, which is what keeps a commit's diff apart from the working tree's of the same file.
        Assert.Equal(PaneMode.CommitFile, shell.PaneMode);
        Assert.Equal($"a.st @ {commit.ShortId}", shell.SelectedDiffPath);
        Assert.False(shell.CanStageHunks, "a commit is history; nothing in it can be staged");

        await Eventually.Until(() => shell.SelectedDiff.Length > 0, "the commit's patch to arrive");

        // And leaving the panel lets go of the row, as it does for every other tree.
        shell.ActivePanel = ShellPanel.Git;
        Assert.False(row.IsSelected, "the tree row is still highlighted, so it cannot be clicked again");
    }
}
