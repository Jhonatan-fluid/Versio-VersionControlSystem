using System.Windows;
using System.Windows.Controls;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Core.Projects;
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
    public void A_realised_commit_row_shows_the_branches_and_tags_on_it()
    {
        // The parser can read %D perfectly and the row still draw nothing; this is the row.
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            Assert.IsType<RadioButton>(window.FindName("RailHistory")).IsChecked = true;

            shell.History.Commits.Add(new CommitRow(
                new CommitEntry
                {
                    Id = "c0ffee1234567890",
                    ShortId = "c0ffee1",
                    Subject = "Release",
                    Refs =
                    [
                        new CommitRef("main", IsTag: false, IsHead: true),
                        new CommitRef("v0.1.1", IsTag: true, IsHead: false),
                    ],
                },
                _ => Task.CompletedTask));

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            var texts = AllOf<TextBlock>(content).Select(t => t.Text).ToList();
            Assert.Contains("main", texts);
            Assert.Contains("v0.1.1", texts);
        });
    }

    /// <summary>A row of a commit's tree, with the commit hung on it, as the panel builds them.</summary>
    private static TreeRow Row(CommitRow commit, ProjectTreeNode node) =>
        new(node, 0, expansion: null, context: new CommitTreeContext(commit,
            new CommitSnapshot(commit.Id, commit.ShortId, new ProjectSnapshot([], [], false, []), [], [], 0)));

    [Fact]
    public void A_realised_commit_file_row_shows_its_path_and_its_letter()
    {
        // A loose file - a document, a script - is drawn as its path in the mono face, with
        // the commit's letter before it, exactly as the Explorer draws a loose file.
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            var rail = Assert.IsType<RadioButton>(window.FindName("RailHistory"));
            rail.IsChecked = true;

            var commit = Commit();
            commit.Children.Clear();
            commit.Children.Add(Row(commit, new ProjectTreeNode
            {
                Name = "README.md",
                Path = "README.md",
                Kind = ProjectNodeKind.File,
                Status = ChangeKind.Modified,
                Files = ["README.md"],
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
                .Where(t => t.DataContext is TreeRow)
                .Select(t => t.Text)
                .ToList();

            // THE ASSERTION THE REGRESSION WOULD HAVE FAILED. Every row rendered blank.
            Assert.Contains("README.md", texts);

            // And the glyph is still there, the same letter and brush as everywhere else.
            Assert.Contains("M", texts);
        });
    }

    [Fact]
    public void A_commit_object_row_renders_its_name_and_type_chip_and_no_lock()
    {
        // The row that was deliberately a bare path until 2026-10-27: an object, named and
        // typed from the manifest as it was in that commit. Any control here bound to a property
        // the row lacks renders as nothing, or worse, as a lock glyph on every row in the panel -
        // the GP-J regression this file exists for - so the name, the chip and the letter are all
        // asserted present, and every visible text under the row non-empty.
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            Assert.IsType<RadioButton>(window.FindName("RailHistory")).IsChecked = true;

            var commit = Commit();
            commit.Children.Clear();
            commit.Children.Add(Row(commit, new ProjectTreeNode
            {
                Name = "FB_Motor",
                Path = "plc/Machine/App/FB_Motor.st",
                Kind = ProjectNodeKind.Object,
                Type = "POU",
                TypeName = "POU",
                Status = ChangeKind.Added,
                Files = ["plc/Machine/App/FB_Motor.st"],
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

            var blocks = AllOf<TextBlock>(panel)
                .Where(t => t.DataContext is TreeRow)
                .ToList();
            var texts = blocks.Select(t => t.Text).ToList();

            // The letter, the name and the type chip are all there...
            Assert.Contains("A", texts);
            Assert.Contains("FB_Motor", texts);
            Assert.Contains("POU", texts);

            // ...and nothing is drawn from a binding the row does not have. The Explorer's
            // template this one copies binds nothing a TreeRow lacks; the proof is that every
            // VISIBLE TextBlock under the row has text. The pill and the badge keep their empty
            // text behind a collapsed Border, which is the design, not a failed binding - and
            // the lock glyph of the GP-J regression is not a text this template can draw at all.
            Assert.All(blocks.Where(b => b.IsVisible), b => Assert.NotEqual("", b.Text));
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
