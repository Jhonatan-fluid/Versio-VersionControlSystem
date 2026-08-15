using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
/// <b>Two view concerns live here and neither belongs in a view model.</b> The list scrolls to
/// the newest row when one arrives, which is a <c>ScrollViewer</c> operation; and the chevron
/// swaps glyph on expand, which is a resource lookup. <see cref="LogViewModel"/> knows it is
/// expanded and knows a row was appended — what to *do* about either is this control's business.
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
    private LogViewModel? _log;

    public LogPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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

    /// <summary>Follow the tail, unless the reader has asked it to stop.</summary>
    /// <remarks>
    /// Paused means frozen, so scrolling while paused would move the list under somebody who
    /// pressed the button precisely to stop that happening.
    /// </remarks>
    private void OnRowsAppended()
    {
        if (_log is { IsPaused: false, Rows.Count: > 0 })
        {
            LogList.ScrollIntoView(_log.Rows[^1]);
        }
    }

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
    }
}
