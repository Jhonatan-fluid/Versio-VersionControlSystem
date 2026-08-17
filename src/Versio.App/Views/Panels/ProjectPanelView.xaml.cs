using System.Windows;
using System.Windows.Controls;
using Versio.App.ViewModels;

namespace Versio.App.Views.Panels;

/// <summary>
/// Every tracked project with its objects, and every other changed file beside them.
/// </summary>
/// <remarks>
/// Split out of MainWindow.xaml, which held this panel, the git panel, the history panel and
/// the log console in one file - so a change to any one of them produced a diff touching all
/// of them. Nothing about the markup changed in the move.
/// </remarks>
public partial class ProjectPanelView : UserControl
{
    public ProjectPanelView() => InitializeComponent();

    /// <summary>
    /// Selecting a row in the tree shows whatever it has to show.
    /// </summary>
    /// <remarks>
    /// A code-behind handler because <c>TreeView.SelectedItem</c> is read-only and cannot be
    /// bound - the standard WPF workaround, and cheaper than the attached-behaviour it would
    /// otherwise take.
    /// <para>
    /// Reached through the inherited DataContext rather than a shell field, because this
    /// control is constructed by the XAML parser with nothing passed to it.
    /// </para>
    /// </remarks>
    private void OnTreeNodeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeRow row && DataContext is ShellViewModel shell)
        {
            shell.Sync.SelectedNode = row;
        }
    }

    /// <summary>
    /// The context menu's verbs, routed to the shell.
    /// </summary>
    /// <remarks>
    /// Click handlers rather than commands, because the menu opens in its own popup tree:
    /// a binding from a MenuItem cannot walk up to the panel's DataContext, and the
    /// alternatives — a proxy resource per panel, or commands stapled onto every row — are
    /// more machinery than four one-line handlers. The row arrives as the menu's
    /// DataContext, inherited from the grid it was opened on.
    /// </remarks>
    private void OnStageNode(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ShellViewModel shell)
        {
            _ = shell.StageNodeAsync(row);
        }
    }

    private void OnDiscardNode(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ShellViewModel shell)
        {
            _ = shell.DiscardNodeAsync(row);
        }
    }

    private void OnRevealNode(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ShellViewModel shell)
        {
            shell.RevealNode(row);
        }
    }

    private void OnCopyNodePath(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ShellViewModel shell)
        {
            shell.CopyNodePath(row);
        }
    }

    private static TreeRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as TreeRow;
}
