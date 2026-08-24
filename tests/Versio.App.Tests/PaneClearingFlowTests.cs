using Versio.App.ViewModels;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The diff pane empties when what it was showing stops being the thing in front of the
/// user: the rail moved to another panel, or the file left the changed list.
/// </summary>
/// <remarks>
/// <para>
/// Reported 2026-08-22 by the owner: a file picked in the changed list went on showing
/// under the history, and a committed file went on showing after the commit. The first was
/// the rail never touching the pane. The second was the refresh doing exactly what it was
/// written to do — re-read the shown path when its status moved — which for a file that had
/// just left the list meant the whole file under a "no changes" note, indistinguishable at a
/// glance from the change still being there.
/// </para>
/// <para>
/// Half of each fix is letting go of the row. A row left selected raises no selection change
/// when it is clicked again, so a pane that was cleared but whose row stayed highlighted
/// would stay blank on the way back — the tests here check the row as well as the pane.
/// </para>
/// </remarks>
public sealed class PaneClearingFlowTests
{
    [Fact]
    public async Task Leaving_a_panel_empties_the_pane_and_lets_go_of_the_list_row()
    {
        using var repo = new TempRepo(unstagedFiles: 1);
        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);
        shell.ActivePanel = ShellPanel.Git;

        var row = shell.Pending.Single(r => r.Path == "file0.st");
        shell.SelectedPending = row;
        await shell.ShowWorkingTreePathAsync(row.Path);
        Assert.Equal("file0.st", shell.SelectedDiffPath);
        Assert.NotEqual(string.Empty, shell.SelectedDiff);

        shell.ActivePanel = ShellPanel.History;

        Assert.Equal(string.Empty, shell.SelectedDiff);
        Assert.Equal(string.Empty, shell.SelectedDiffPath);
        Assert.Null(shell.SelectedPending);
    }

    [Fact]
    public async Task Leaving_the_project_panel_lets_go_of_the_tree_row()
    {
        using var repo = new TempRepo(unstagedFiles: 1);
        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);

        var row = Find(shell.Sync.Tree, "file0.st");

        // What a click does, in the order the view does it: the container selects
        // through the two-way binding, then the code-behind hands the row over.
        row.IsSelected = true;
        shell.Sync.SelectedNode = row;
        Assert.Equal("file0.st", shell.SelectedDiffPath);

        shell.ActivePanel = ShellPanel.Git;

        Assert.Null(shell.Sync.SelectedNode);
        Assert.False(row.IsSelected, "the tree row is still highlighted, so it cannot be clicked again");
        Assert.Equal(string.Empty, shell.SelectedDiffPath);
    }

    [Fact]
    public void Leaving_the_history_lets_go_of_the_commit()
    {
        using var shell = TestShell.Build();
        shell.History.Attach(new CountingGitClient());
        shell.ActivePanel = ShellPanel.History;

        var commit = shell.History.Commits[0];
        commit.IsSelected = true;
        shell.History.Select(commit);

        shell.ActivePanel = ShellPanel.Git;

        Assert.False(commit.IsSelected, "the commit row is still highlighted, so it cannot be clicked again");
    }

    [Fact]
    public async Task A_file_that_was_committed_leaves_the_pane()
    {
        using var repo = new TempRepo(unstagedFiles: 1);
        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);

        await shell.ShowWorkingTreePathAsync("file0.st");
        Assert.Equal("file0.st", shell.SelectedDiffPath);

        repo.Git("add file0.st");
        repo.Git("commit -qm committed");
        await shell.RefreshAsync();

        // Not the whole file under a "no changes" note — nothing. The row it was opened
        // from is gone from the list, and the pane follows the list.
        Assert.Equal(string.Empty, shell.SelectedDiff);
        Assert.Equal(string.Empty, shell.SelectedDiffPath);
    }

    [Fact]
    public async Task A_file_that_was_discarded_leaves_the_pane_and_its_row()
    {
        using var repo = new TempRepo(unstagedFiles: 1);
        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);

        var row = shell.Pending.Single(r => r.Path == "file0.st");
        shell.SelectedPending = row;
        await shell.ShowWorkingTreePathAsync(row.Path);

        // An untracked file's discard is its deletion.
        repo.Delete("file0.st");
        await shell.RefreshAsync();

        Assert.Equal(string.Empty, shell.SelectedDiffPath);
        Assert.Null(shell.SelectedPending);
    }

    [Fact]
    public async Task A_file_still_in_the_list_keeps_its_diff_across_a_refresh()
    {
        // The guard against over-clearing: a refresh that finds the file still changed is
        // the common case — every editor save — and must leave the pane alone.
        using var repo = new TempRepo(unstagedFiles: 1);
        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);

        await shell.ShowWorkingTreePathAsync("file0.st");
        var shown = shell.SelectedDiff;

        repo.Touch("seed.txt");
        await shell.RefreshAsync();

        Assert.Equal("file0.st", shell.SelectedDiffPath);
        Assert.Equal(shown, shell.SelectedDiff);
    }

    [Fact]
    public async Task A_late_diff_does_not_refill_a_pane_that_was_emptied()
    {
        // A click followed by a rail switch inside the time git takes: the diff lands
        // after the pane was cleared and must be dropped, not shown.
        using var repo = new TempRepo(unstagedFiles: 1);
        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);

        var showing = shell.ShowWorkingTreePathAsync("file0.st");
        shell.ActivePanel = ShellPanel.History;
        await showing;

        Assert.Equal(string.Empty, shell.SelectedDiff);
        Assert.Equal(string.Empty, shell.SelectedDiffPath);
    }

    private static TreeRow Find(IEnumerable<TreeRow> rows, string name) =>
        Search(rows, name) ?? throw new InvalidOperationException($"no row named '{name}' in the tree");

    private static TreeRow? Search(IEnumerable<TreeRow> rows, string name)
    {
        foreach (var row in rows)
        {
            if (row.Name == name)
            {
                return row;
            }

            if (Search(row.Children, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
