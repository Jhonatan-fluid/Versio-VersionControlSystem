using System.Collections.ObjectModel;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The changed-files list reconciles a reorder the way the tree does: moved, never doubled.
/// </summary>
/// <remarks>
/// The same forward pass as <see cref="TreeRow.Reconcile"/>, with the same hole: an insert
/// on a path mismatch and nothing taking the stale row out, so [a,b] against [b,a] gave
/// [b,a,b]. Its inputs are sorted by path today, which is why nobody has seen it there; the
/// tree's are not, and a method that is correct only while its callers sort is one
/// refactor from a duplicate row with a tick on it (the 2026-10-24 review's item 37).
/// </remarks>
public sealed class StagingListReconcileTests
{
    private static FileChange Change(string path) => new()
    {
        Path = path,
        Staged = ChangeKind.Unmodified,
        Unstaged = ChangeKind.Modified,
    };

    private static ObservableCollection<ChangeRow> RowsFor(params string[] paths)
    {
        var rows = new ObservableCollection<ChangeRow>();
        ShellViewModel.ReconcileInto(rows, [.. paths.Select(Change)]);
        return rows;
    }

    [Fact]
    public void Rows_that_swapped_places_are_moved_and_keep_their_ticks()
    {
        var rows = RowsFor("a.st", "b.st");
        var a = rows[0];
        var b = rows[1];
        b.IsSelected = true;

        ShellViewModel.ReconcileInto(rows, [Change("b.st"), Change("a.st")]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["b.st", "a.st"], rows.Select(r => r.Path));
        Assert.Same(b, rows[0]);
        Assert.Same(a, rows[1]);
        Assert.True(rows[0].IsSelected);
    }

    [Fact]
    public void A_row_that_moved_to_the_end_leaves_no_copy_behind()
    {
        var rows = RowsFor("a.st", "b.st", "c.st");

        ShellViewModel.ReconcileInto(rows, [Change("b.st"), Change("c.st"), Change("a.st")]);

        Assert.Equal(["b.st", "c.st", "a.st"], rows.Select(r => r.Path));
    }

    [Fact]
    public void A_list_that_shrank_to_a_path_it_had_twice_keeps_one_row()
    {
        // The removal pass keeps every row whose path is live; the trim after the pass is
        // what takes a second copy out.
        var rows = RowsFor("a.st", "a.st");

        ShellViewModel.ReconcileInto(rows, [Change("a.st")]);

        Assert.Single(rows);
    }
}
