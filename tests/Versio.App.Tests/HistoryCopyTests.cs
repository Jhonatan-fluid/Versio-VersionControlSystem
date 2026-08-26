using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Git;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// UX-8: a commit in the history can be copied — id, subject, whole message — and that is
/// all it can be.
/// </summary>
/// <remarks>
/// Read-only history is the decision under test as much as the copying: the menu must offer
/// exactly the three read-only entries, because a fourth entry appearing here is somebody
/// crossing the line the panel's own comment draws (revert, reset and cherry-pick destroy
/// work; "Branch from here…" is an owner question, not a default).
/// </remarks>
[Collection("theme")]
public sealed class HistoryCopyTests : IDisposable
{
    private readonly List<ShellViewModel> _shells = [];

    [Fact]
    public void The_three_entries_put_the_expected_text_on_the_clipboard()
    {
        using var repo = new TempRepo(unstagedFiles: 1);
        repo.Git("add -A");
        repo.Git("commit -qm subject -m body");

        StaHarness.Run(async () =>
        {
            using var shell = TestShell.Build();
            await shell.OpenAsync(repo.Path);

            shell.History.IsVisible = true;
            await Eventually.Until(() => shell.History.Commits.Count > 0, "history loaded");
            var row = shell.History.Commits[0];

            // The full id, not the seven characters on screen: the id is copied to be
            // pasted into a tool, and every tool takes the long form.
            shell.CopyCommitId(row);
            Assert.Equal(row.Id, Clipboard.GetText());
            Assert.Equal(40, row.Id.Length);

            shell.CopyCommitSubject(row);
            Assert.Equal("subject", Clipboard.GetText());

            // Subject, blank line, body — exactly as git recorded it, without the trailing
            // newline %B carries, which would paste an extra blank line.
            await shell.CopyCommitMessageAsync(row);
            Assert.Equal("subject\n\nbody", Clipboard.GetText());
        });
    }

    [Fact]
    public void A_missing_commit_is_not_reported_as_copied()
    {
        // git show of an unknown id used to return "", and CopyCommitMessageAsync then
        // claimed a copy. The status has to name the miss, and the clipboard must not
        // be treated as having received a message.
        using var repo = new TempRepo();
        StaHarness.Run(async () =>
        {
            using var shell = TestShell.Build();
            await shell.OpenAsync(repo.Path);

            var row = new CommitRow(
                new CommitEntry
                {
                    Id = new string('0', 40),
                    ShortId = "0000000",
                    Subject = "gone",
                    Author = "nobody",
                },
                _ => Task.CompletedTask);

            // The failure mode is a cleared clipboard wearing a success sentence:
            // WPF's SetText("") empties it and returns. Status naming the miss is
            // not enough on its own — a regression can still wipe the sentinel.
            Clipboard.SetText("sentinel");
            await shell.CopyCommitMessageAsync(row);

            Assert.Contains("Could not read", shell.StatusSummary, StringComparison.Ordinal);
            Assert.DoesNotContain("Copied", shell.StatusSummary, StringComparison.Ordinal);
            Assert.Equal("sentinel", Clipboard.GetText());
        });
    }

    [Fact]
    public void An_empty_subject_names_itself_instead_of_silently_copying_nothing()
    {
        // Reachable: git accepts a commit with an empty message, and its subject is then
        // "". The clipboard helper refuses empty payloads so a copy cannot clear the
        // clipboard and claim success — but a refusal nobody hears is a silent no-op,
        // which is the same failure in a different shape. The status has to say why
        // nothing was copied.
        StaHarness.Run(() =>
        {
            using var shell = TestShell.Build();

            var row = new CommitRow(
                new CommitEntry
                {
                    Id = new string('b', 40),
                    ShortId = "bbbbbbb",
                    Subject = string.Empty,
                    Author = "somebody",
                },
                _ => Task.CompletedTask);

            Clipboard.SetText("sentinel");
            shell.CopyCommitSubject(row);

            Assert.Contains("is empty", shell.StatusSummary, StringComparison.Ordinal);
            Assert.DoesNotContain("Copied", shell.StatusSummary, StringComparison.Ordinal);
            Assert.Equal("sentinel", Clipboard.GetText());
        });
    }

    [Fact]
    public void A_realised_commit_row_offers_copy_and_only_copy()
    {
        // Realised, not read from the template in isolation — a Click handler naming a
        // method that does not exist fails when the template is INSTANTIATED, silently
        // sailing past every test that never builds a row. The same lesson the git panel's
        // realised-row test records.
        StaHarness.Run(() =>
        {
            var shell = TestShell.Build();
            _shells.Add(shell);
            var window = new MainWindow(shell);

            var rail = Assert.IsType<RadioButton>(window.FindName("RailHistory"));
            rail.IsChecked = true;

            shell.History.Commits.Add(new CommitRow(
                new CommitEntry
                {
                    Id = new string('a', 40),
                    ShortId = "aaaaaaa",
                    Subject = "a subject",
                    Author = "somebody",
                },
                _ => Task.CompletedTask));

            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            var menu = AllOf<StackPanel>(content)
                .Select(p => p.ContextMenu)
                .FirstOrDefault(m => m is not null);

            Assert.True(menu is not null, "no realised commit row carries a context menu");

            Assert.Equal(
                ["Copy commit id", "Copy subject", "Copy the whole message"],
                menu!.Items.OfType<MenuItem>()
                    .Select(i => i.Header?.ToString() ?? string.Empty).ToArray());
        });
    }

    private static IEnumerable<T> AllOf<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
            {
                yield return hit;
            }

            foreach (var below in AllOf<T>(child))
            {
                yield return below;
            }
        }
    }

    /// <summary>Released on the STA thread that built them — see GitPanelChromeTests.</summary>
    public void Dispose() => StaHarness.Run(() =>
    {
        foreach (var shell in _shells)
        {
            shell.Dispose();
        }
    });
}
