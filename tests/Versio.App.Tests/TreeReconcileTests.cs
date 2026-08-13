using System.Collections.ObjectModel;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// An export landing mid-review updates the tree; it does not replace it.
/// </summary>
/// <remarks>
/// <para>
/// Versio exports on every detected change, because the point of the mirror is to be read
/// <i>before</i> a commit rather than produced by one. That means an export arrives while
/// somebody is part-way through reading the tree it is about to change.
/// </para>
/// <para>
/// Clearing the collection and refilling it — which is what this did — drops the selection,
/// discards every container WPF had realised, and throws the reader back to the top. The
/// changed-files list was fixed this way long ago and the tree, now the primary view, had not
/// been.
/// </para>
/// </remarks>
public sealed class TreeReconcileTests
{
    private static ProjectTreeNode Node(string path, params ProjectTreeNode[] children) => new()
    {
        Path = path,
        Name = path.Split('/')[^1],
        Kind = children.Length > 0 ? ProjectNodeKind.Folder : ProjectNodeKind.Object,
        Children = children,
        Status = ChangeKind.Unmodified,
    };

    private static ProjectTreeNode Changed(string path, ChangeKind kind) => new()
    {
        Path = path,
        Name = path.Split('/')[^1],
        Kind = ProjectNodeKind.Object,
        Children = [],
        Status = kind,
    };

    private static ObservableCollection<TreeRow> RowsFor(params ProjectTreeNode[] nodes)
    {
        var rows = new ObservableCollection<TreeRow>();
        TreeRow.Reconcile(rows, nodes, 0, null);
        return rows;
    }

    [Fact]
    public void A_row_that_is_still_there_is_the_same_object_afterwards()
    {
        // The whole point. SelectedItem is a reference to the row, so replacing it deselects
        // whatever the user was reading.
        var rows = RowsFor(Node("A"), Node("B"));
        var wasReading = rows[1];

        TreeRow.Reconcile(rows, [Node("A"), Node("B")], 0, null);

        Assert.Same(wasReading, rows[1]);
    }

    [Fact]
    public void An_object_that_appeared_is_inserted_in_its_place()
    {
        var rows = RowsFor(Node("A"), Node("C"));
        var a = rows[0];
        var c = rows[1];

        TreeRow.Reconcile(rows, [Node("A"), Node("B"), Node("C")], 0, null);

        Assert.Equal(3, rows.Count);
        Assert.Same(a, rows[0]);
        Assert.Equal("B", rows[1].Name);
        Assert.Same(c, rows[2]);
    }

    [Fact]
    public void An_object_that_went_away_is_removed_and_the_rest_survive()
    {
        var rows = RowsFor(Node("A"), Node("B"), Node("C"));
        var a = rows[0];
        var c = rows[2];

        TreeRow.Reconcile(rows, [Node("A"), Node("C")], 0, null);

        Assert.Equal(2, rows.Count);
        Assert.Same(a, rows[0]);
        Assert.Same(c, rows[1]);
    }

    [Fact]
    public void A_row_whose_state_moved_reports_it_without_being_replaced()
    {
        var rows = RowsFor(Changed("A", ChangeKind.Unmodified));
        var row = rows[0];
        var notified = 0;

        row.PropertyChanged += (_, _) => notified++;

        TreeRow.Reconcile(rows, [Changed("A", ChangeKind.Modified)], 0, null);

        Assert.Same(row, rows[0]);
        Assert.Equal("M", row.Glyph);
        Assert.True(notified > 0, "the row did not tell the view its facts had changed");
    }

    // ------------------------------------------------------------------------- the children

    [Fact]
    public void Children_already_on_screen_are_reconciled_too()
    {
        var rows = RowsFor(Node("F", Node("F/one"), Node("F/three")));

        // Realising them is what WPF does when the folder is drawn open.
        var before = rows[0].Children[1];

        TreeRow.Reconcile(
            rows, [Node("F", Node("F/one"), Node("F/two"), Node("F/three"))], 0, null);

        Assert.Equal(3, rows[0].Children.Count);
        Assert.Equal("two", rows[0].Children[1].Name);
        Assert.Same(before, rows[0].Children[2]);
    }

    [Fact]
    public void Children_nobody_has_looked_at_are_not_built_by_a_refresh()
    {
        // The laziness this class exists for. Reconciling an unrealised level would construct
        // a row per object in the project - thousands on a real machine - on every export.
        var rows = RowsFor(Node("F", Node("F/one")));

        TreeRow.Reconcile(rows, [Node("F", Node("F/one"), Node("F/two"))], 0, null);

        var children = typeof(TreeRow)
            .GetField("_children", System.Reflection.BindingFlags.Instance |
                                   System.Reflection.BindingFlags.NonPublic)!
            .GetValue(rows[0]);

        Assert.Null(children);

        // And when somebody finally opens it, they get the current truth rather than a
        // stale level built before the export.
        Assert.Equal(2, rows[0].Children.Count);
    }

    [Fact]
    public void Everything_being_replaced_still_works()
    {
        var rows = RowsFor(Node("A"), Node("B"));

        TreeRow.Reconcile(rows, [Node("X"), Node("Y")], 0, null);

        Assert.Equal(["X", "Y"], rows.Select(r => r.Name));
    }

    [Fact]
    public void An_emptied_tree_empties()
    {
        var rows = RowsFor(Node("A"), Node("B"));

        TreeRow.Reconcile(rows, [], 0, null);

        Assert.Empty(rows);
    }
}
