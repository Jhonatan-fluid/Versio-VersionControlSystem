using System.Windows;
using System.Windows.Controls;
using Versio.App.ViewModels;

namespace Versio.App.Views.Panels;

/// <summary>
/// The commit history, and what each commit touched.
/// </summary>
/// <remarks>
/// Split out of MainWindow.xaml, which held this panel, the git panel, the project panel and
/// the log console in one 2,517-line file - so a change to any one of them produced a diff
/// touching all of them. Nothing about the markup changed in the move.
/// </remarks>
public partial class HistoryPanelView : UserControl
{
    public HistoryPanelView() => InitializeComponent();

    /// <summary>
    /// Selecting a row in the history shows it.
    /// </summary>
    /// <remarks>
    /// Code-behind for the same reason the project tree needs it: <c>TreeView.SelectedItem</c>
    /// is read-only and cannot be bound. What was picked - a commit or one file inside it - is
    /// the view model's decision, not this handler's.
    /// <para>
    /// Reached through the inherited DataContext rather than a shell field, because this
    /// control is constructed by the XAML parser with nothing passed to it. Null until the
    /// context arrives, which is why it is asked for rather than assumed.
    /// </para>
    /// </remarks>
    private void OnHistoryRowSelected(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        (DataContext as ShellViewModel)?.History.Select(e.NewValue);
}
