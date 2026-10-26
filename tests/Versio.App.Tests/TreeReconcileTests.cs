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

    // ------------------------------------------------------------------------- reordering

    /// <summary>
    /// The same rows in a different order are moved, not duplicated. The forward pass
    /// inserted on a path mismatch and never took the stale row out, so [A,B] reconciled
    /// with [B,A] came out as [B,A,B] — reachable, because the library level is sorted by
    /// system-first then name while identity is the path, and tasks follow the user's
    /// own order (the 2026-10-24 review's item 37).
    /// </summary>
    [Fact]
    public void Rows_that_swapped_places_are_moved_and_stay_the_same_objects()
    {
        var rows = RowsFor(Node("A"), Node("B"));
        var a = rows[0];
        var b = rows[1];

        TreeRow.Reconcile(rows, [Node("B"), Node("A")], 0, null);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["B", "A"], rows.Select(r => r.Name));
        Assert.Same(b, rows[0]);
        Assert.Same(a, rows[1]);
    }

    [Fact]
    public void A_row_that_moved_to_the_end_leaves_no_copy_behind()
    {
        var rows = RowsFor(Node("A"), Node("B"), Node("C"));

        TreeRow.Reconcile(rows, [Node("B"), Node("C"), Node("A")], 0, null);

        Assert.Equal(["B", "C", "A"], rows.Select(r => r.Name));
    }

    [Fact]
    public void A_level_that_shrank_to_a_path_it_had_twice_keeps_one_row()
    {
        // The removal pass keeps every row whose path is live, so a level that once held a
        // path twice (a builder listing it twice) would keep both; the trim after the pass
        // is what takes the second one out.
        var rows = RowsFor(Node("A"), Node("A"));

        TreeRow.Reconcile(rows, [Node("A")], 0, null);

        Assert.Single(rows);
    }

    [Fact]
    public void Children_that_swapped_places_are_moved_too()
    {
        var rows = RowsFor(Node("F", Node("F/one"), Node("F/two")));
        var one = rows[0].Children[0];
        var two = rows[0].Children[1];

        TreeRow.Reconcile(rows, [Node("F", Node("F/two"), Node("F/one"))], 0, null);

        Assert.Equal(2, rows[0].Children.Count);
        Assert.Same(two, rows[0].Children[0]);
        Assert.Same(one, rows[0].Children[1]);
    }
}
