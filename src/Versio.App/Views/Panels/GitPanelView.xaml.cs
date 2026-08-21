using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Versio.App.ViewModels;

namespace Versio.App.Views.Panels;

/// <summary>
/// The commit message, the one changes list, and the remote operations.
/// </summary>
/// <remarks>
/// Split out of MainWindow.xaml, which held this panel, the history panel, the project panel
/// and the log console in one file - so a change to any one of them produced a diff touching
/// all of them.
/// </remarks>
public partial class GitPanelView : UserControl
{
    public GitPanelView() => InitializeComponent();

    /// <summary>
    /// Ctrl+Enter commits from the message box.
    /// </summary>
    /// <remarks>
    /// The box accepts newlines, so plain Enter has to keep inserting one - a commit body is
    /// the whole reason the box is multi-line. Ctrl+Enter is the convention every git client
    /// uses for this, and reaching for the mouse after typing a message is the interaction
    /// here that happens dozens of times a day.
    /// <para>
    /// Reached through the inherited DataContext rather than a shell field, because this
    /// control is constructed by the XAML parser with nothing passed to it.
    /// </para>
    /// </remarks>
    private void OnCommitMessageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter ||
            (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        // Handled either way: a Ctrl+Enter that cannot commit must not fall through and
        // insert a newline the user did not ask for.
        e.Handled = true;

        if (DataContext is ShellViewModel shell && shell.CommitCommand.CanExecute(null))
        {
            shell.CommitCommand.Execute(null);
        }
    }

    /// <summary>
    /// Double-click toggles the row's checkbox.
    /// </summary>
    /// <remarks>
    /// There is no second list to move the row into. The checkbox is still not the list
    /// selection — clicking once still opens the diff — so a double-click is the leftover
    /// gesture that used to stage, now pointing at the include set instead.
    /// <para>
    /// <b>The clicked row, not the ticks, and not the selection either.</b> The row is read
    /// from the DataContext under the pointer rather than from <c>SelectedItem</c>, because a
    /// double-click on empty space below the last row leaves the selection untouched.
    /// </para>
    /// </remarks>
    private void OnChangeRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            e.Handled = true;
            row.IsSelected = !row.IsSelected;
        }
    }

    private void OnDiscardRow(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ShellViewModel shell)
        {
            _ = shell.DiscardRowAsync(row);
        }
    }

    private void OnRevealRow(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ShellViewModel shell)
        {
            shell.RevealRow(row);
        }
    }

    private void OnCopyRowPath(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ShellViewModel shell)
        {
            shell.CopyRowPath(row);
        }
    }

    /// <summary>
    /// Close the branch overflow once one of its entries has been pressed.
    /// </summary>
    private void OnBranchMenuItem(object sender, RoutedEventArgs e) =>
        BranchMenuButton.IsChecked = false;

    /// <summary>
    /// The row a menu entry or a double-click belongs to.
    /// </summary>
    /// <remarks>
    /// <b>Both callers are handed the row itself as <c>sender</c>, and neither reads the list's
    /// selection.</b> A <c>ContextMenu</c> inherits its DataContext from the element it is set
    /// on — the row's own Grid — so a menu entry acts on the right-clicked row; and the
    /// double-click is attached per container, so <c>sender</c> is the ListBoxItem. Reading
    /// <c>SelectedItem</c> instead would act on whatever was clicked <i>last</i>, because WPF
    /// does not select a row on right-click.
    /// </remarks>
    private static ChangeRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ChangeRow;
}
