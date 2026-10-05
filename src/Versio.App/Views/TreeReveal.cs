using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Versio.App.ViewModels;

namespace Versio.App.Views;

/// <summary>
/// Scroll a row selected from code into view in a virtualized tree.
/// </summary>
/// <remarks>
/// <para>
/// Selecting a row through the view model sets its flags; it does not make a virtualized
/// TreeView draw it. A row nobody has scrolled to has no container, so nothing scrolls, and the
/// tree looks as though the selection did not happen - which is the thing a type link's
/// "go there in the tree too" (owner, 2026-10-05) exists to prevent.
/// </para>
/// <para>
/// So this walks the chain the way a person would: at each level ask the panel to bring the
/// row's index into view, which makes its container, expand it, and go down. At the bottom,
/// bring the row itself into view. Run at Loaded priority, after the expansion the view model
/// already did has been laid out. Any level that still has no container stops the walk quietly:
/// the selection is right either way, and only the scrolling is lost.
/// </para>
/// </remarks>
internal static class TreeReveal
{
    public static void Bring(TreeView tree, IReadOnlyList<TreeRow> chain)
    {
        tree.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Walk(tree, chain));
    }

    private static void Walk(TreeView tree, IReadOnlyList<TreeRow> chain)
    {
        ItemsControl parent = tree;
        TreeViewItem? container = null;

        foreach (var row in chain)
        {
            var index = parent.Items.IndexOf(row);
            if (index < 0)
            {
                return;
            }

            parent.ApplyTemplate();
            parent.UpdateLayout();

            container = parent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
            if (container is null && ItemsHost(parent) is VirtualizingStackPanel panel)
            {
                panel.BringIndexIntoViewPublic(index);
                parent.UpdateLayout();
                container = parent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
            }

            if (container is null)
            {
                return;
            }

            if (!ReferenceEquals(row, chain[^1]))
            {
                container.IsExpanded = true;
            }

            parent = container;
        }

        container?.BringIntoView();
    }

    private static Panel? ItemsHost(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Panel { IsItemsHost: true } host)
            {
                return host;
            }

            if (ItemsHost(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
