using Versio.App.ViewModels;
using Versio.Core.Git;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The per-row verbs behind the staging lists' context menu and double-click.
/// </summary>
/// <remarks>
/// <para>
/// <b>These exist because the buttons could not be reused.</b> <c>StageCommand</c> and its
/// neighbours act on <c>Chosen</c> — the ticked rows, or every row when nothing is ticked. A
/// right-click or a double-click is a statement about one row, and wiring the menu to the
/// existing commands would have meant right-clicking a file staged three others along with it.
/// </para>
/// <para>
/// Against a real repository rather than a fake status, on the same argument
/// <c>StagingSelectionFlowTests</c> makes: what is being checked is which paths reach git, and
/// a fake would agree with the code by construction.
/// </para>
/// </remarks>
public sealed class RowActionFlowTests
{
    [Fact]
    public async Task Staging_one_row_leaves_everybody_elses_ticks_alone()
    {
        // THE RULE THE WHOLE FEATURE RESTS ON. Somebody ticks three files on purpose, then
        // right-clicks a fourth to stage just that one. If the menu acted on the ticked set
        // they would get four, and the three they had lined up would be gone from the list
        // they were building. That is a commit to unpick.
        using var repo = new TempRepo(unstagedFiles: 4);
        using var shell = TestShell.Build();

        await shell.OpenAsync(repo.Path);
        Assert.Equal(4, shell.Unstaged.Count);

        shell.Unstaged[0].IsSelected = true;
        shell.Unstaged[1].IsSelected = true;

        var clicked = shell.Unstaged[3];
        var clickedPath = clicked.Path;

        await shell.StageRowAsync(clicked);

        // Exactly the row that was clicked.
        Assert.Contains(shell.Staged, r => r.Path == clickedPath);
        Assert.Single(shell.Staged);

        // And the two that were ticked are still unstaged, still ticked.
        Assert.Equal(2, shell.UnstagedSelectedCount);
        Assert.DoesNotContain(shell.Staged, r => r.IsSelected && r.Path != clickedPath);
    }

    [Fact]
    public async Task Staging_one_row_with_nothing_ticked_still_takes_only_that_row()
    {
        // The other half, and the one that would pass by accident if the menu reused
        // StageCommand: with nothing ticked, Chosen() returns EVERY row, so a menu wired to
        // the button would stage all four here and look perfectly correct in the first test.
        using var repo = new TempRepo(unstagedFiles: 4);
        using var shell = TestShell.Build();

        await shell.OpenAsync(repo.Path);

        var clicked = shell.Unstaged[2];
        var clickedPath = clicked.Path;

        Assert.Equal(0, shell.UnstagedSelectedCount);

        await shell.StageRowAsync(clicked);

        Assert.Single(shell.Staged);
        Assert.Equal(clickedPath, shell.Staged[0].Path);
        Assert.Equal(3, shell.Unstaged.Count);
    }

    [Fact]
    public async Task Unstaging_one_row_takes_only_that_row_back_out()
    {
        using var repo = new TempRepo(unstagedFiles: 3);
        using var shell = TestShell.Build();

        await shell.OpenAsync(repo.Path);

        repo.Git("add -A");
        await shell.RefreshAsync();

        Assert.Equal(3, shell.Staged.Count);

        var clicked = shell.Staged[1];
        var clickedPath = clicked.Path;

        await shell.UnstageRowAsync(clicked);

        Assert.Equal(2, shell.Staged.Count);
        Assert.DoesNotContain(shell.Staged, r => r.Path == clickedPath);
        Assert.Contains(shell.Unstaged, r => r.Path == clickedPath);
    }

    /// <summary>
    /// Copy path takes the row's repo-relative path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>STA, and no repository.</b> <c>Clipboard.SetText</c> is an OLE call and throws off an
    /// STA thread, so this runs through the harness — but nothing here needs git, because what
    /// is being checked is which string the row hands over.
    /// </para>
    /// <para>
    /// <b>Asserted through the status bar rather than by reading the clipboard back.</b> The
    /// clipboard can be held open by another process, which the method already treats as a
    /// status-bar sentence rather than a crash — and that sentence deliberately still carries
    /// the path, so the assertion holds whether the copy succeeded or was refused. Reading the
    /// clipboard back would make this test fight every other process on the machine for it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Copying_a_rows_path_takes_the_repo_relative_one()
    {
        StaHarness.Run(() =>
        {
            using var shell = TestShell.Build();

            var row = new ChangeRow(new FileChange
            {
                Path = "src/MainProject/PLC/POU/FB_Motor.st",
                Staged = ChangeKind.Unmodified,
                Unstaged = ChangeKind.Modified,
            });

            shell.CopyRowPath(row);

            // The path that means the same thing in every clone - a commit message, an issue,
            // a chat message - rather than this machine's absolute one.
            Assert.Contains("src/MainProject/PLC/POU/FB_Motor.st", shell.StatusSummary);
        });
    }
}
