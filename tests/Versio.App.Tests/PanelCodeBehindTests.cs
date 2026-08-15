using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.App.Views.Panels;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Core.Projects;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The handlers that moved into the panels still reach the view model.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap these close.</b> Splitting MainWindow.xaml moved three code-behind handlers into
/// the panels that own them, and each one changed how it reaches the shell: it read a
/// <c>_shell</c> field the window set in its constructor, and now it asks its own
/// <c>DataContext</c>. That is a real assumption — a panel whose DataContext never arrived, or
/// arrived as something else, would swallow every click in silence.
/// </para>
/// <para>
/// Nothing was watching it. Disabling all three handlers outright left the whole suite green,
/// which is the same shape as the features this codebase has shipped with no reachable caller:
/// a seam every test answered directly and none crossed.
/// </para>
/// <para>
/// Driven through the real window, so what is checked is the panel MainWindow builds rather
/// than one a test constructed with a DataContext handed to it. The tree handlers are raised
/// as the routed events they are, which exercises both halves — that the XAML still attaches
/// them after the move, and that the body finds the shell.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class PanelCodeBehindTests
{
    private static (MainWindow Window, ShellViewModel Shell, FrameworkElement Content) Build()
    {
        using var shell = TestShell.Build();

        var window = new MainWindow(shell);
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

        Lay(content);

        return (window, shell, content);
    }

    private static void Lay(FrameworkElement content)
    {
        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();
    }

    private static T Panel<T>(MainWindow window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));

    /// <summary>The one tree inside a panel, found by type because it carries no name.</summary>
    private static TreeView TreeIn(FrameworkElement panel) =>
        Assert.Single(Descendants(panel).OfType<TreeView>());

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            yield return child;

            foreach (var further in Descendants(child))
            {
                yield return further;
            }
        }
    }

    [Fact]
    public void Every_panel_sees_the_shell_as_its_data_context()
    {
        // The assumption all three handlers now rest on, stated once. DataContext is inherited
        // rather than assigned, so nothing in the panels' own markup would fail if it stopped
        // arriving — the bindings would go blank and the handlers would quietly no-op.
        StaHarness.Run(() =>
        {
            var (window, shell, _) = Build();

            Assert.Same(shell, Panel<ProjectPanelView>(window, "ProjectPanel").DataContext);
            Assert.Same(shell, Panel<GitPanelView>(window, "GitPanel").DataContext);
            Assert.Same(shell, Panel<HistoryPanelView>(window, "HistoryPanel").DataContext);
        });
    }

    [Fact]
    public void Selecting_a_row_in_the_project_tree_reaches_the_sync_panel()
    {
        StaHarness.Run(() =>
        {
            var (window, shell, _) = Build();

            var tree = TreeIn(Panel<ProjectPanelView>(window, "ProjectPanel"));
            var row = new TreeRow(
                new ProjectTreeNode { Name = "FB_Motor", Path = "plc/Machine/App/FB_Motor.st" },
                depth: 0);

            Assert.Null(shell.Sync.SelectedNode);

            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
                null!, row, TreeView.SelectedItemChangedEvent));

            Assert.Same(row, shell.Sync.SelectedNode);
        });
    }

    [Fact]
    public void Selecting_a_commit_in_the_history_tree_reaches_the_history()
    {
        StaHarness.Run(() =>
        {
            var (window, shell, content) = Build();

            // The rail first: a collapsed UserControl has not applied its template, so the
            // tree inside it does not exist yet to raise anything on.
            Assert.IsType<RadioButton>(window.FindName("RailHistory")).IsChecked = true;
            Lay(content);

            var tree = TreeIn(Panel<HistoryPanelView>(window, "HistoryPanel"));

            CommitRow? chosen = null;
            shell.History.CommitSelected += c => chosen = c;

            var commit = new CommitRow(
                new CommitEntry
                {
                    Id = "c0ffee1234567890",
                    ShortId = "c0ffee1",
                    Subject = "Change something",
                    Author = "tester",
                    When = DateTimeOffset.Now,
                },
                _ => Task.CompletedTask);

            tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
                null!, commit, TreeView.SelectedItemChangedEvent));

            Assert.Same(commit, chosen);
        });
    }
}
