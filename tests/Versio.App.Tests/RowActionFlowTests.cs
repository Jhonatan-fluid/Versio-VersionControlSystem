using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The per-row verbs behind the changes list's context menu.
/// </summary>
/// <remarks>
/// Discard on the header acts on <c>Chosen</c> — the ticked rows, or every row when nothing is
/// ticked. A right-click is a statement about one row. Wiring the menu to the header command
/// would have meant right-clicking a file discarded three others along with it.
/// </remarks>
public sealed class RowActionFlowTests
{
    [Fact]
    public async Task Discarding_one_row_leaves_everybody_elses_ticks_alone()
    {
        using var repo = new TempRepo(unstagedFiles: 4);
        repo.Git("add -A");
        repo.Git("commit -qm seed");
        File.WriteAllText(Path.Combine(repo.Path, "file0.st"), "changed 0");
        File.WriteAllText(Path.Combine(repo.Path, "file1.st"), "changed 1");
        File.WriteAllText(Path.Combine(repo.Path, "file2.st"), "changed 2");
        File.WriteAllText(Path.Combine(repo.Path, "file3.st"), "changed 3");

        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);
        Assert.Equal(4, shell.Pending.Count);

        var tickedPaths = new[] { shell.Pending[0].Path, shell.Pending[1].Path };
        shell.Pending[0].IsSelected = true;
        shell.Pending[1].IsSelected = true;

        var clicked = shell.Pending[3];
        var clickedPath = clicked.Path;

        shell.Asked().Destructive = _ => true;
        await shell.DiscardRowAsync(clicked);

        Assert.DoesNotContain(shell.Pending, r => r.Path == clickedPath);
        Assert.Equal(2, shell.PendingSelectedCount);

        foreach (var path in tickedPaths)
        {
            var row = Assert.Single(shell.Pending, r => r.Path == path);
            Assert.True(row.IsSelected, $"{path} lost its tick when another row was discarded");
        }
    }

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

            Assert.Contains("src/MainProject/PLC/POU/FB_Motor.st", shell.StatusSummary);
        });
    }
}
