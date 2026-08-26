using System.Windows.Data;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// UX-6: the find box over the changes list.
/// </summary>
/// <remarks>
/// <para>
/// <b>The trap these tests exist for is the filter touching the ticks.</b> The ticked set is
/// what Commit and Discard apply to; a filter that narrowed it would mean somebody ticks three
/// files, types a word to find a fourth, and commits one. So every test here asserts the same
/// thing from a different side: the veil changes what is <i>shown</i> and nothing else.
/// </para>
/// <para>
/// The visible set is read through <see cref="CollectionViewSource.GetDefaultView"/>, because
/// that is the view the ListBox renders when it binds <c>Pending</c> — the test and the screen
/// look through the same veil. That view is dispatcher-affine: the first touch binds it to the
/// touching thread, and every later change to the collection must come from that thread. The
/// app satisfies this for free because everything happens on the UI thread; these tests
/// satisfy it the same way, by running whole on <see cref="StaHarness"/>'s dispatcher — a
/// body that set the filter on the xunit thread and then let an <c>await</c> resume on the
/// pool would throw <c>NotSupportedException</c> from inside a refresh.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class PendingFilterTests
{
    /// <summary>What the ListBox would show: the default view, filtered.</summary>
    private static string[] Visible(ShellViewModel shell) =>
        CollectionViewSource.GetDefaultView(shell.Pending)
            .Cast<ChangeRow>().Select(r => r.Path).ToArray();

    private static ChangeRow Row(string path) => new(new FileChange
    {
        Path = path,
        Staged = ChangeKind.Unmodified,
        Unstaged = ChangeKind.Modified,
    });

    [Fact]
    public void The_filter_hides_rows_without_touching_the_ticks()
    {
        using var repo = new TempRepo(unstagedFiles: 4);
        StaHarness.Run(async () =>
        {
            using var shell = TestShell.Build();

            await shell.OpenAsync(repo.Path);
            shell.Pending[0].IsSelected = true;
            shell.Pending[2].IsSelected = true;

            shell.PendingFilter = "file1";

            Assert.Equal(["file1.st"], Visible(shell));

            // The set is untouched: all four rows exist, the two ticks are kept even where
            // the ticked row is hidden, and the commit button still says it will take both.
            Assert.Equal(4, shell.Pending.Count);
            Assert.Equal(2, shell.PendingSelectedCount);
            Assert.Equal("Commit (2)", shell.Commit.Label);

            shell.PendingFilter = string.Empty;
            Assert.Equal(4, Visible(shell).Length);
            Assert.Equal(2, shell.PendingSelectedCount);
        });
    }

    [Fact]
    public void The_master_checkbox_ticks_hidden_rows_too()
    {
        // "Tick every changed file" means every changed file. A master checkbox that only
        // reached the visible ones would make the same gesture mean different sets depending
        // on what happened to be typed in the box above it.
        using var repo = new TempRepo(unstagedFiles: 4);
        StaHarness.Run(async () =>
        {
            using var shell = TestShell.Build();

            await shell.OpenAsync(repo.Path);
            shell.PendingFilter = "file1";

            shell.PendingAllSelected = true;

            Assert.Equal(4, shell.PendingSelectedCount);
        });
    }

    [Fact]
    public void The_filter_matches_the_object_name_the_row_shows()
    {
        // A described row renders its object name, not its filename — so "FB_Motor" is what
        // somebody reads on screen and what they will type. A filter that only read paths
        // would hide the row they are looking straight at.
        StaHarness.Run(() =>
        {
            using var shell = TestShell.Build();

            var motor = Row("plc/Device/Application/POU/Motor.st");
            motor.Describe(new NamedObject
            {
                Name = "FB_Motor",
                Type = "POU",
                Guid = "0000",
                Project = "Machine",
            });
            shell.Pending.Add(motor);
            shell.Pending.Add(Row("README.md"));

            shell.PendingFilter = "FB_Motor";

            Assert.Equal(["plc/Device/Application/POU/Motor.st"], Visible(shell));
        });
    }

    [Fact]
    public void An_empty_result_is_named_as_the_filter_s_doing()
    {
        // A list emptied by the filter looks exactly like a clean working tree. The flag
        // behind the "nothing matches" line has to be true only when the filter is the
        // reason the list is empty — not when the list is empty because the tree is clean.
        StaHarness.Run(() =>
        {
            using var shell = TestShell.Build();

            Assert.False(shell.PendingFilterHidesEverything);

            shell.PendingFilter = "zzz";
            Assert.False(shell.PendingFilterHidesEverything);

            shell.Pending.Add(Row("a.st"));
            Assert.True(shell.PendingFilterHidesEverything);

            shell.PendingFilter = "a.st";
            Assert.False(shell.PendingFilterHidesEverything);
        });
    }

    [Fact]
    public void The_filter_resets_when_the_repository_changes()
    {
        // Carrying a filter across repositories would open the next one on a changes list
        // mysteriously showing nothing — the same argument ResetForRepositoryChange makes
        // for the diff pane and the commit message.
        using var first = new TempRepo(unstagedFiles: 2);
        using var second = new TempRepo(unstagedFiles: 2);
        StaHarness.Run(async () =>
        {
            using var shell = TestShell.Build();

            await shell.OpenAsync(first.Path);
            shell.PendingFilter = "file0";

            await shell.OpenAsync(second.Path);

            Assert.Equal(string.Empty, shell.PendingFilter);
            Assert.Equal(2, Visible(shell).Length);
        });
    }
}
