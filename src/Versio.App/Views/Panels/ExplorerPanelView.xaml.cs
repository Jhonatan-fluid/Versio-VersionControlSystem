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
    public ExplorerPanelView()
    {
        InitializeComponent();

        // The same as the project panel: a type link selects in this tree from code, and
        // only the view can scroll a virtualized row into existence. See TreeReveal.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ShellViewModel old)
            {
                old.RevealInTree -= OnRevealInTree;
            }

            if (e.NewValue is ShellViewModel shell)
            {
                shell.RevealInTree += OnRevealInTree;
            }
        };
    }

    private void OnRevealInTree(ShellPanel panel, IReadOnlyList<TreeRow> chain)
    {
        if (panel == ShellPanel.Explorer)
        {
            TreeReveal.Bring(ExplorerTree, chain);
        }
    }

    /// <summary>Put the caret in this panel's find box (Ctrl+F).</summary>
    /// <remarks>
    /// The window owns the gesture, because only the window knows which panel the rail is
    /// showing; the panel owns which box is its find box. This box predated UX-6 — the
    /// explorer was a tree with a filter from the day it shipped — and the first Ctrl+F
    /// pass skipped it on the false claim that it had none. Selected on arrival so typing
    /// replaces the previous search instead of appending to it.
    /// </remarks>
    public void FocusFindBox()
    {
        if (ExplorerSearchBox.Focus())
        {
            ExplorerSearchBox.SelectAll();
        }
    }

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
