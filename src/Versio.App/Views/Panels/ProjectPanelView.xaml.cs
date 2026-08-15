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
}
