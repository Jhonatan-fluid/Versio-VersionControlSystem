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
}
