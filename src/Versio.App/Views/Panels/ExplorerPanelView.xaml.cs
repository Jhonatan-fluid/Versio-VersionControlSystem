using System.Windows;
using System.Windows.Controls;
using Versio.App.ViewModels;

namespace Versio.App.Views.Panels;

/// <summary>
/// The open clone as a folder tree. PLC projects use the same row objects as the Project
/// panel; clicks visualize, they do not review.
/// </summary>
public partial class ExplorerPanelView : UserControl
{
    public ExplorerPanelView() => InitializeComponent();

    private void OnTreeNodeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeRow row && DataContext is ShellViewModel shell)
        {
            shell.Explorer.SelectedNode = row;
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
