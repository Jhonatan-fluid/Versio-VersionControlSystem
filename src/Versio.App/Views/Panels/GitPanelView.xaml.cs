using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Versio.App.ViewModels;

namespace Versio.App.Views.Panels;

/// <summary>
/// Staging, the commit message, and the remote operations.
/// </summary>
/// <remarks>
/// Split out of MainWindow.xaml, which held this panel, the history panel, the project panel
/// and the log console in one file - so a change to any one of them produced a diff touching
/// all of them. Nothing about the markup changed in the move.
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
    /// Double-click stages or unstages the row, depending on which list it is in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single most common interaction in every git client there is, and Versio asked for a
    /// tick and then a button press every time. It moves the row between the two lists, which
    /// is what double-clicking a file in a staging pane has meant for twenty years.
    /// </para>
    /// <para>
    /// <b>The clicked row, not the ticks, and not the selection either.</b> The row is read
    /// from the DataContext under the pointer rather than from <c>SelectedItem</c>, because a
    /// double-click on empty space below the last row leaves the selection untouched and would
    /// otherwise stage whatever happened to be selected — a file the user never pointed at.
    /// </para>
    /// <para>
    /// The tick state is deliberately left alone. The checkbox says what the buttons will act
    /// on and this says what to do with one row; a double-click that also cleared somebody's
    /// selection of three would be the same conflation the row template's own comment warns
    /// about.
    /// </para>
    /// </remarks>
    private void OnStagedRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ShellViewModel shell)
        {
            e.Handled = true;
            _ = shell.UnstageRowAsync(row);
        }
    }

    private void OnUnstagedRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ShellViewModel shell)
        {
            e.Handled = true;
            _ = shell.StageRowAsync(row);
        }
    }

    private void OnStageRow(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ShellViewModel shell)
        {
            _ = shell.StageRowAsync(row);
        }
    }

    private void OnUnstageRow(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ShellViewModel shell)
        {
            _ = shell.UnstageRowAsync(row);
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
    /// <remarks>
    /// The command runs on its own; this is only about the popup, which is StaysOpen="False"
    /// and so closes on a click OUTSIDE itself but not on one within. Left alone it would hang
    /// over the dialog its own entry just opened - the settings popup carries the same note,
    /// having learned it the same way.
    /// </remarks>
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
