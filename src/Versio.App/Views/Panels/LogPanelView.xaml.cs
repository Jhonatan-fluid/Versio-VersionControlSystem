using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Versio.App.ViewModels;

namespace Versio.App.Views.Panels;

/// <summary>
/// The collapsible log console.
/// </summary>
/// <remarks>
/// <para>
/// The last of the four panels out of <c>MainWindow.xaml</c>, and the one held back through
/// three earlier passes because it is the only one with real code-behind. The other three moved
/// with nothing but their markup.
/// </para>
/// <para>
/// <b>View concerns live here and none of them belong in a view model.</b> The list scrolls to
/// the newest row when one arrives or when the panel opens, which is a <c>ScrollViewer</c>
/// operation; scrolling away from that tail freezes the feed, which is the same pause the
/// header button already owns; the chevron swaps glyph on expand, which is a resource lookup;
/// and selected rows copy as the file would have written them. <see cref="LogViewModel"/> knows
/// it is expanded, paused, and that a row was appended — what to *do* about any of that is this
/// control's business.
/// </para>
/// <para>
/// <b>What deliberately stayed in the window.</b> The <c>GridSplitter</c> above this console and
/// the <c>RowDefinition</c> it resizes belong to <c>MainWindow</c>'s grid: a panel cannot own the
/// row it is placed in. So expanding moves two things in two places — this control shows its
/// list and turns its chevron, the window gives the row a pixel height and reveals the splitter.
/// Both listen to the same property rather than one calling the other, so neither has to know
/// the other exists.
/// </para>
/// <para>
/// <b>Reached through the inherited DataContext</b>, like every other panel: the XAML parser
/// constructs this with nothing passed to it, so the shell is null until the context arrives.
/// That is why the wiring hangs off <see cref="FrameworkElement.DataContextChanged"/> rather
/// than the constructor, and why the old handlers are released first — a context that changes
/// twice would otherwise leave the first subscription live and scroll a list nobody is showing.
/// </para>
/// </remarks>
public partial class LogPanelView : UserControl
{
    /// <summary>
    /// A bit more than one log row. A one-pixel wheel tick from the tail must not freeze
    /// the feed; leaving by a row or more is a real "I am reading something further up".
    /// </summary>
    private const double TailSlack = 24;

    private LogViewModel? _log;
    private ScrollViewer? _viewer;
    private int _ignoreScroll;
    private int _highlightAnchor = -1;
    private bool _highlighting;

    public LogPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        LogList.Loaded += OnLogListLoaded;
        LogList.PreviewMouseLeftButtonDown += OnLogPreviewMouseDown;
        LogList.PreviewMouseMove += OnLogPreviewMouseMove;
        LogList.PreviewMouseLeftButtonUp += OnLogPreviewMouseUp;

        // SELECTING ROWS DID NOTHING AND COPIED NOTHING.
        //
        // The list was a stock ListBox, so SelectionMode defaulted to Single and there was
        // no Copy binding anywhere on it. A user who wanted two error lines for a message
        // could highlight one row, could not highlight two, and Ctrl+C put nothing on the
        // clipboard either. Copying a run of log lines into a bug report is one of the
        // most common things anybody does with this panel.
        //
        // Extended mode is not enough on its own. Click-and-drag still moves a single
        // selection — Shift and Ctrl are what extend it, which is not how anybody
        // highlights a run of lines in a log. The handlers above treat a drag as a
        // range, the way a console does.
        //
        // A CommandBinding rather than a KeyDown handler: Ctrl+C is already routed to
        // ApplicationCommands.Copy by the input system, and a binding means the same command
        // works from a context menu or a shortcut somebody remaps later. The same shape
        // DiffView already uses, for the same reason.
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopy, OnCanCopy));
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_log is not null)
        {
            _log.RowsAppended -= OnRowsAppended;
            _log.PropertyChanged -= OnLogPropertyChanged;
        }

        _log = (DataContext as ShellViewModel)?.Log;

        if (_log is null)
        {
            return;
        }

        _log.RowsAppended += OnRowsAppended;
        _log.PropertyChanged += OnLogPropertyChanged;

        ApplyExpanded();
    }

    private void OnLogListLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewer is not null)
        {
            _viewer.ScrollChanged -= OnLogScrollChanged;
            _viewer = null;
        }

        EnsureViewer();
        FollowTailAfterLayout();
    }

    /// <summary>Follow the tail, unless the reader has asked it to stop.</summary>
    /// <remarks>
    /// Paused means frozen, so scrolling while paused would move the list under somebody who
    /// pressed the button precisely to stop that happening.
    /// </remarks>
    private void OnRowsAppended() => FollowTail();

    private void OnLogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewModel.IsExpanded))
        {
            ApplyExpanded();
        }
    }

    /// <summary>
    /// Collapse the list, leaving the header in place.
    /// </summary>
    /// <remarks>
    /// The header stays visible so the panel can be reopened and the entry count is still
    /// readable — collapsing the whole thing to nothing leaves no obvious way back.
    /// </remarks>
    private void ApplyExpanded()
    {
        var expanded = _log?.IsExpanded ?? false;

        LogList.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        LogChevron.Text = (string)FindResource(
            expanded ? "Icon.ChevronDown" : "Icon.ChevronUp");

        if (expanded)
        {
            FollowTailAfterLayout();
        }
    }

    /// <summary>
    /// Scroll to the newest row after the list has a real viewport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opening used to land on the first rows. Follow-the-tail lived only in
    /// <see cref="OnRowsAppended"/>, and <c>ScrollIntoView</c> on a collapsed list is a
    /// no-op, so a panel that already held the startup lines painted from offset 0 the
    /// moment it became visible.
    /// </para>
    /// <para>
    /// Same-tick is not enough either. This control shows the list, then the window gives
    /// the row a pixel height — two listeners, this one first — so a scroll here would
    /// still run against a header-only row. <see cref="DispatcherPriority.Loaded"/> is
    /// after both have run and the list has been measured.
    /// </para>
    /// </remarks>
    private void FollowTailAfterLayout() =>
        Dispatcher.BeginInvoke(FollowTail, DispatcherPriority.Loaded);

    private void FollowTail()
    {
        if (_log is not { IsPaused: false, IsExpanded: true, Rows.Count: > 0 })
        {
            return;
        }

        if (LogList.Visibility != Visibility.Visible)
        {
            return;
        }

        EnsureViewer();

        // ScrollToBottom raises ScrollChanged. Without this, following the tail would
        // look like the user leaving it, and every append would freeze the feed.
        _ignoreScroll++;
        try
        {
            LogList.UpdateLayout();

            // ScrollIntoView on a virtualizing ListBox that has just come out of
            // Collapsed is a no-op: the generator has not placed the last row, so
            // the viewer stays at offset 0 — which is how opening used to land on
            // the first lines. The viewer itself can be told to go to the bottom.
            // ScrollToEnd is the wrong call: it also jumps horizontally, and a long
            // message would hide its own start.
            if (_viewer is not null)
            {
                _viewer.ScrollToBottom();
            }
            else
            {
                LogList.ScrollIntoView(_log.Rows[^1]);
            }
        }
        finally
        {
            Dispatcher.BeginInvoke(() => _ignoreScroll--, DispatcherPriority.Input);
        }
    }

    private void EnsureViewer()
    {
        if (_viewer is not null)
        {
            return;
        }

        LogList.ApplyTemplate();
        _viewer = FindScrollViewer(LogList);
        if (_viewer is not null)
        {
            _viewer.ScrollChanged += OnLogScrollChanged;
        }
    }

    /// <summary>
    /// Freeze the feed when the user leaves the tail; resume when they come back.
    /// </summary>
    /// <remarks>
    /// Pause already means freeze, not "stop auto-scroll": the list stopping under the
    /// reader is the whole reason the button exists. Scrolling up is the same request,
    /// made with the wheel instead of the header, so it sets the same flag — the PAUSED
    /// pill stays honest. Scrolling back to the newest line is the opposite request:
    /// they are done reading and the feed should catch up, which is Drain then follow.
    /// </remarks>
    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_ignoreScroll > 0 || _log is null)
        {
            return;
        }

        // Extent and viewport changes are the list growing or the row being given a
        // height — opening the log, an append we are about to follow. The user leaving
        // or returning to the tail is a vertical move with the size of the document
        // unchanged.
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0 || e.VerticalChange == 0)
        {
            return;
        }

        var viewer = (ScrollViewer)sender;
        var remaining = viewer.ExtentHeight - viewer.ViewportHeight - viewer.VerticalOffset;
        _log.IsPaused = remaining > TailSlack;
    }

    private void OnCanCopy(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = LogList.SelectedItems.Count > 0;

    /// <summary>
    /// Click-and-drag highlights a range of rows, the way a console does.
    /// </summary>
    /// <remarks>
    /// Extended mode still needs Shift or Ctrl to grow a selection. A plain drag moves
    /// the single selected row with the mouse, which is how somebody can copy one line
    /// and never two. Shift and Ctrl stay with the ListBox; this is only the case with
    /// no modifiers — press, drag, release, copy.
    /// </remarks>
    private void OnLogPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers is ModifierKeys.Shift or ModifierKeys.Control)
        {
            _highlighting = false;
            return;
        }

        var index = IndexFromEvent(e);
        if (index < 0)
        {
            return;
        }

        _highlightAnchor = index;
        _highlighting = true;
    }

    private void OnLogPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_highlighting || _highlightAnchor < 0)
        {
            return;
        }

        var index = IndexFromEvent(e);
        if (index < 0 || index == _highlightAnchor)
        {
            return;
        }

        SelectRange(_highlightAnchor, index);
        e.Handled = true;
    }

    private void OnLogPreviewMouseUp(object sender, MouseButtonEventArgs e) =>
        _highlighting = false;

    /// <summary>
    /// Highlight a run of rows. The mouse handlers call this; the test does too, because
    /// raising a preview event on a generated container never tunnels through the list
    /// the way a real click does.
    /// </summary>
    internal void HighlightRange(int from, int to) => SelectRange(from, to);

    private void SelectRange(int from, int to)
    {
        if (_log is null)
        {
            return;
        }

        var lo = Math.Min(from, to);
        var hi = Math.Max(from, to);

        LogList.SelectedItems.Clear();
        for (var i = lo; i <= hi && i < _log.Rows.Count; i++)
        {
            LogList.SelectedItems.Add(_log.Rows[i]);
        }
    }

    private int IndexFromEvent(RoutedEventArgs e)
    {
        var index = IndexOfRow(e.OriginalSource);
        return index >= 0 ? index : IndexOfRow(e.Source);
    }

    private int IndexOfRow(object? source)
    {
        for (var current = source as DependencyObject;
             current is not null;
             current = VisualTreeHelper.GetParent(current)
                       ?? LogicalTreeHelper.GetParent(current) as DependencyObject)
        {
            if (current is ListBoxItem item)
            {
                return LogList.ItemContainerGenerator.IndexFromContainer(item);
            }
        }

        return -1;
    }

    /// <summary>
    /// Put the selected rows on the clipboard, in the order they appear in the log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Document order, not selection order.</b> <c>SelectedItems</c> is in the order
    /// things were picked, so Ctrl-clicking upwards through a burst would paste the
    /// lines reversed — a log that reads as though the errors happened backwards.
    /// </para>
    /// <para>
    /// The text is <c>LogEvent.Format()</c>, so a paste matches the file. The panel's
    /// four columns are a scan layout; the file line is what you send in a report.
    /// </para>
    /// <para>
    /// The clipboard genuinely fails: another process can hold it open and
    /// <see cref="Clipboard.SetText(string)"/> then throws. Letting that reach the
    /// dispatcher would take the window down while somebody was trying to copy an
    /// error, which is a memorable way to lose a bug report — the same trap the diff
    /// pane and the shell's own copy handlers document.
    /// </para>
    /// </remarks>
    private void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        if (_log is null || LogList.SelectedItems.Count == 0)
        {
            return;
        }

        var picked = LogList.SelectedItems.OfType<LogRow>().ToHashSet();
        var text = string.Join(
            Environment.NewLine,
            _log.Rows.Where(picked.Contains).Select(row => row.Entry.Format()));

        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // Another process has the clipboard. Nothing useful to do, and nothing worth
            // interrupting the user over — the lines are still on screen.
        }

        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
