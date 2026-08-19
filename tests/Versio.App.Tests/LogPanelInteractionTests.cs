using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.App.Views.Panels;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The log console follows its tail, freezes when you leave it, and copies what you picked.
/// </summary>
/// <remarks>
/// <para>
/// Three defects, one panel. Opening landed on the first rows because follow-the-tail lived
/// only on append and <c>ScrollIntoView</c> on a collapsed list is a no-op. Wheel-up was
/// fought by the next 100 ms drain scrolling back to the end. The list was a stock
/// <c>ListBox</c>, so you could not pick two rows and Ctrl+C put nothing on the clipboard.
/// </para>
/// <para>
/// Asserted through the window rather than a panel constructed by hand, for the same reason
/// <see cref="LogPanelChromeTests"/> gives: the XAML parser builds this with nothing passed
/// to it, and the row height that makes a viewport lives on <c>MainWindow</c>.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class LogPanelInteractionTests
{
    /// <summary>
    /// Opening a log that already holds more rows than the viewport lands on the newest.
    /// </summary>
    /// <remarks>
    /// The list is collapsed at startup, so the rows are already there when it opens — the
    /// case that used to paint from offset 0. Deleting the expand-scroll call is what this
    /// fails on; the chevron test would still be green.
    /// </remarks>
    [Fact]
    public void Opening_the_log_lands_on_the_newest_row()
    {
        StaHarness.Run(() =>
        {
            using var shell = ShellWithRows(80);
            var (_, content, list) = Window(shell);

            shell.Log.IsExpanded = true;
            Lay(content);
            Pump();

            var viewer = ScrollViewerOf(list);
            Assert.True(
                viewer.ExtentHeight > viewer.ViewportHeight + 40,
                "the list must overflow so landing on the tail is distinguishable from offset 0");
            Assert.True(
                viewer.VerticalOffset + viewer.ViewportHeight >= viewer.ExtentHeight - 24,
                $"opened at offset {viewer.VerticalOffset} of {viewer.ExtentHeight}, viewport {viewer.ViewportHeight}");
            Assert.False(shell.Log.IsPaused);
        });
    }

    /// <summary>
    /// Scrolling away from the tail freezes the feed; following it does not.
    /// </summary>
    /// <remarks>
    /// Pause already means freeze, not stop-auto-scroll. Scrolling up is the same request
    /// made with the wheel, so it sets the same flag. Scrolling back to the newest line
    /// is the opposite: Drain delivers what was held and the list follows again.
    /// </remarks>
    [Fact]
    public void Scrolling_away_from_the_tail_freezes_the_feed()
    {
        StaHarness.Run(() =>
        {
            using var shell = ShellWithRows(80);
            var (_, content, list) = Window(shell);

            shell.Log.IsExpanded = true;
            Lay(content);
            Pump();

            Assert.False(shell.Log.IsPaused);

            var viewer = ScrollViewerOf(list);
            viewer.ScrollToVerticalOffset(0);
            Pump();

            Assert.True(shell.Log.IsPaused);

            viewer.ScrollToBottom();
            Pump();

            Assert.False(shell.Log.IsPaused);
        });
    }

    [Fact]
    public void Dragging_across_rows_selects_the_range()
    {
        // Extended mode still needs Shift or Ctrl. A plain drag otherwise keeps a
        // single row — which is how copy-one worked and copy-many did not.
        StaHarness.Run(() =>
        {
            using var shell = ShellWithRows(80);
            var (panel, content, list) = Window(shell);

            shell.Log.IsExpanded = true;
            Lay(content);
            Pump();

            ScrollViewerOf(list).ScrollToVerticalOffset(0);
            Lay(content);
            Pump();

            // Same method the click-and-drag handlers call. A raised preview event on a
            // generated container in this harness never tunnels the way a real click does,
            // so the handlers themselves are not the thing under the assertion — the range
            // they paint is.
            panel.HighlightRange(0, 2);

            Assert.Equal(3, list.SelectedItems.Count);
            Assert.True(ApplicationCommands.Copy.CanExecute(null, panel));
        });
    }

    [Fact]
    public void More_than_one_row_can_be_selected_at_once()
    {
        StaHarness.Run(() =>
        {
            using var shell = ShellWithRows(3);
            var (panel, content, list) = Window(shell);

            shell.Log.IsExpanded = true;
            Lay(content);
            Pump();

            Assert.Equal(SelectionMode.Extended, list.SelectionMode);

            var rows = shell.Log.Rows.ToList();
            list.SelectedItems.Add(rows[0]);
            list.SelectedItems.Add(rows[1]);

            Assert.Equal(2, list.SelectedItems.Count);
            Assert.True(ApplicationCommands.Copy.CanExecute(null, panel));
        });
    }

    [Fact]
    public void Copy_is_refused_with_nothing_selected_and_offered_with_something()
    {
        // A command that is always enabled and silently does nothing is the same failure
        // in a different shape.
        StaHarness.Run(() =>
        {
            using var shell = ShellWithRows(3);
            var (panel, content, list) = Window(shell);

            shell.Log.IsExpanded = true;
            Lay(content);
            Pump();

            list.SelectedItems.Clear();
            Assert.False(ApplicationCommands.Copy.CanExecute(null, panel));

            list.SelectedItem = shell.Log.Rows[0];
            Assert.True(ApplicationCommands.Copy.CanExecute(null, panel));
        });
    }

    [Fact]
    public void Selected_rows_copy_in_document_order()
    {
        // SelectedItems is in the order things were PICKED, so Ctrl-clicking upwards
        // through a burst would paste the lines reversed. Document order is what the
        // copy relies on, exercised the way a user produces it.
        StaHarness.Run(() =>
        {
            using var shell = ShellWithRows(3);
            var (panel, content, list) = Window(shell);

            shell.Log.IsExpanded = true;
            Lay(content);
            Pump();

            var rows = shell.Log.Rows.ToList();
            list.SelectedItems.Add(rows[^1]);
            list.SelectedItems.Add(rows[^2]);

            ApplicationCommands.Copy.Execute(null, panel);

            var expected = string.Join(
                Environment.NewLine,
                rows[^2].Entry.Format(),
                rows[^1].Entry.Format());

            Assert.Equal(expected, Clipboard.GetText());
        });
    }

    [Fact]
    public void The_list_offers_copy_from_a_context_menu()
    {
        // Ctrl+C is not discoverable. The menu is what makes copy reachable without
        // knowing the shortcut, so it has to name the same command the binding handles.
        StaHarness.Run(() =>
        {
            using var shell = ShellWithRows(3);
            var (_, content, list) = Window(shell);

            shell.Log.IsExpanded = true;
            Lay(content);

            var menu = Assert.IsType<ContextMenu>(list.ContextMenu);
            var copy = Assert.Single(menu.Items.OfType<MenuItem>());
            Assert.Equal(ApplicationCommands.Copy, copy.Command);
        });
    }

    private static ShellViewModel ShellWithRows(int count)
    {
        var sink = new InMemoryLogSink();
        for (var i = 0; i < count; i++)
        {
            sink.Emit(new LogEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = LogLevel.Information,
                Category = LogCategory.Ui,
                Message = $"log-row-{i}",
            });
        }

        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        return new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);
    }

    private static (LogPanelView Panel, FrameworkElement Content, ListBox List) Window(
        ShellViewModel shell)
    {
        var window = new MainWindow(shell);
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        Lay(content);

        var panel = Assert.IsAssignableFrom<LogPanelView>(window.FindName("LogConsole"));
        var list = Assert.IsAssignableFrom<ListBox>(panel.FindName("LogList"));
        return (panel, content, list);
    }

    private static void Lay(FrameworkElement content)
    {
        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();
    }

    /// <summary>
    /// Drain the follow-the-tail and ignore-scroll work queued at Loaded and Input.
    /// </summary>
    /// <remarks>
    /// <c>StaHarness.Run</c> is a synchronous <c>Invoke</c>, so a <c>BeginInvoke</c> from
    /// inside it does not run until we pump. Without this, opening would still look like
    /// it landed on the first row — the defect, as far as the assertion can see.
    /// </remarks>
    private static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Input);
    }

    private static ScrollViewer ScrollViewerOf(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (Find(child) is { } found)
            {
                return found;
            }
        }

        throw new InvalidOperationException("the log list has no ScrollViewer");

        static ScrollViewer? Find(DependencyObject node)
        {
            if (node is ScrollViewer scroll)
            {
                return scroll;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var found = Find(VisualTreeHelper.GetChild(node, i));
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
