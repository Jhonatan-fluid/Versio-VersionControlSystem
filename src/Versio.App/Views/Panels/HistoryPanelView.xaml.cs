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

    /// <summary>Put the caret in this panel's find box (Ctrl+F).</summary>
    /// <remarks>
    /// The window owns the gesture, because only the window knows which panel the rail is
    /// showing; the panel owns which box is its find box. Selected on arrival so typing
    /// replaces the previous search instead of appending to it.
    /// </remarks>
    public void FocusFindBox()
    {
        if (HistoryFilterBox.Focus())
        {
            HistoryFilterBox.SelectAll();
        }
    }

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

    // ------------------------------------------------- copy a commit (UX-8)

    private void OnCopyCommitId(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ShellViewModel shell)
        {
            shell.CopyCommitId(row);
        }
    }

    private void OnCopyCommitSubject(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ShellViewModel shell)
        {
            shell.CopyCommitSubject(row);
        }
    }

    private void OnCopyCommitMessage(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ShellViewModel shell)
        {
            _ = shell.CopyCommitMessageAsync(row);
        }
    }

    /// <summary>
    /// The commit a menu entry belongs to: the right-clicked row, never the selection.
    /// </summary>
    /// <remarks>
    /// The same shape as the git panel's <c>RowOf</c>, for the same reason: a
    /// <c>ContextMenu</c> inherits its DataContext from the element it hangs off, and WPF
    /// does not select a tree row on right-click — reading the selection would copy
    /// whatever was clicked <i>last</i>.
    /// </remarks>
    private static CommitRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as CommitRow;
}
