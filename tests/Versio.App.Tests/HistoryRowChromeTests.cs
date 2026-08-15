using System.Windows;
using System.Windows.Controls;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The history panel's commit-file rows, actually rendered.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file exists because of a regression that shipped.</b> GP-J taught
/// <c>ChangeRowTemplate</c> to show an object — <c>Primary</c>, <c>TypeLabel</c>,
/// <c>IsExportOnly</c> — and the history panel was drawing its commit files with that same
/// template. A <see cref="CommitFileRow"/> has none of those properties, WPF fails such a
/// binding <b>silently</b>, and every row in the panel rendered with no filename at all and a
/// lock glyph it had no business showing: the <c>Visibility</c> binding failed too, and a
/// failed <c>Visibility</c> binding leaves the element <c>Visible</c>.
/// </para>
/// <para>
/// <b>Every test GP-J added was green.</b> They realise rows in the git panel's own lists —
/// which is the discipline this codebase already learned twice — and never came here. A shared
/// template means every list that uses it needs a realised row, not just the one the feature
/// was written for.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class HistoryRowChromeTests
{
    private static (MainWindow Window, ShellViewModel Shell) Build()
    {
        using var shell = TestShell.Build();
        return (new MainWindow(shell), shell);
    }

    private static CommitRow Commit() => new(
        new CommitEntry
        {
            Id = "c0ffee1234567890",
            ShortId = "c0ffee1",
            Subject = "Change something",
            Author = "tester",
            When = DateTimeOffset.Now,
        },

        // Never called: the test seeds the children itself, because what is under test is how
        // a row RENDERS, not how the panel goes and fetches one.
        _ => Task.CompletedTask);

    [Fact]
    public void A_realised_commit_file_row_shows_its_path()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            var rail = Assert.IsType<RadioButton>(window.FindName("RailHistory"));
            rail.IsChecked = true;

            var commit = Commit();
            commit.Children.Clear();
            commit.Children.Add(new CommitFileRow(commit, new CommitFile
            {
                Path = "plc/Machine/App/FB_Motor.st",
                Kind = ChangeKind.Modified,
            }));
            commit.IsExpanded = true;

            shell.History.Commits.Add(commit);

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            // A UserControl since the panel moved into its own file, so what this
            // asks for is an element to walk rather than a particular container.
            var panel = Assert.IsAssignableFrom<FrameworkElement>(
                window.FindName("HistoryPanel"));
            var texts = AllOf<TextBlock>(panel)
                .Where(t => t.DataContext is CommitFileRow)
                .Select(t => t.Text)
                .ToList();

            // THE ASSERTION THE REGRESSION WOULD HAVE FAILED. Every row rendered blank.
            Assert.Contains("plc/Machine/App/FB_Motor.st", texts);

            // And the glyph is still there, still coloured by ChangeGlyph - the part that was
            // always worth sharing between the two panels.
            Assert.Contains("M", texts);
        });
    }

    [Fact]
    public void A_commit_file_row_never_renders_an_object_chip_or_a_lock()
    {
        // A CommitFileRow is a path, not an object. Any control here bound to GP-J's object
        // surface is a control bound to a property that does not exist - which renders as
        // nothing, or worse, as a lock glyph on every row in the panel.
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            Assert.IsType<RadioButton>(window.FindName("RailHistory")).IsChecked = true;

            var commit = Commit();
            commit.Children.Clear();
            commit.Children.Add(new CommitFileRow(commit, new CommitFile
            {
                Path = "README.md",
                Kind = ChangeKind.Added,
            }));
            commit.IsExpanded = true;
            shell.History.Commits.Add(commit);

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            // A UserControl since the panel moved into its own file, so what this
            // asks for is an element to walk rather than a particular container.
            var panel = Assert.IsAssignableFrom<FrameworkElement>(
                window.FindName("HistoryPanel"));

            var texts = AllOf<TextBlock>(panel)
                .Where(t => t.DataContext is CommitFileRow)
                .Select(t => t.Text)
                .ToList();

            // The glyph and the path are both there...
            Assert.Contains("A", texts);
            Assert.Contains("README.md", texts);

            // ...and the lock glyph GP-J added is NOT, which is the symptom that gave the
            // regression away: a failed Visibility binding leaves an element Visible, so every
            // row in this panel wore a lock.
            //
            // Asserted on text rather than on Borders because a TreeViewItem's own chrome
            // carries the row's DataContext too — "no visible Border under a CommitFileRow"
            // matches the container and proves nothing, and neither does an empty TextBlock:
            // the container has one of those too.
            Assert.DoesNotContain("", texts);
        });
    }

    private static IEnumerable<T> AllOf<T>(DependencyObject root)
        where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();

            if (node is T wanted)
            {
                yield return wanted;
            }

            var children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
            {
                queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            }
        }
    }
}
