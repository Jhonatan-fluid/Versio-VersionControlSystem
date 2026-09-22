using Versio.Core.Diff;
using Xunit;

namespace Versio.Core.Tests.Diff;

/// <summary>
/// Folding a unified diff into two columns without inventing or losing a line.
/// </summary>
public sealed class SideBySideDiffTests
{
    private static IReadOnlyList<SideBySideRow> Build(string patch) =>
        SideBySideDiff.Build(UnifiedDiffParser.Parse(patch));

    [Fact]
    public void A_context_line_is_one_row_on_both_sides()
    {
        var rows = Build("@@ -1,1 +1,1 @@\n unchanged\n");

        var row = Assert.Single(rows, r => r.Spanning is null);
        Assert.Same(row.Left, row.Right);
        Assert.Equal("unchanged", row.Left!.Text);
    }

    [Fact]
    public void An_edit_pairs_its_removed_and_added_lines_on_one_row()
    {
        var rows = Build(
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new\n");

        var row = Assert.Single(rows, r => r.Spanning is null);
        Assert.Equal("old", row.Left!.Text);
        Assert.Equal("new", row.Right!.Text);
    }

    [Fact]
    public void The_longer_blocks_tail_gets_one_sided_rows()
    {
        // A pure insertion is blank on the left — padding it with a fake pairing would draw
        // an edit that never happened.
        var rows = Build(
            "@@ -1,1 +1,3 @@\n" +
            "-old\n" +
            "+new\n" +
            "+inserted\n").Where(r => r.Spanning is null).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(("old", "new"), (rows[0].Left!.Text, rows[0].Right!.Text));
        Assert.Null(rows[1].Left);
        Assert.Equal("inserted", rows[1].Right!.Text);
    }

    [Fact]
    public void Headers_span_rather_than_split()
    {
        // A hunk header split in two columns would be two half-sentences, and it is also
        // the row the stage button lives on.
        var rows = Build(
            "diff --git a/x b/x\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new\n");

        Assert.Equal(2, rows.Count(r => r.Spanning is not null));
        Assert.Contains(rows, r => r.Spanning is { Kind: DiffLineKind.HunkHeader });
        Assert.Contains(rows, r => r.Spanning is { Kind: DiffLineKind.FileTitle });
    }

    [Fact]
    public void No_line_is_lost_or_invented_in_the_fold()
    {
        const string patch =
            "@@ -1,4 +1,4 @@\n" +
            " ctx1\n" +
            "-a\n" +
            "-b\n" +
            "+A\n" +
            " ctx2\n" +
            "+tail\n";

        var document = UnifiedDiffParser.Parse(patch);
        var rows = SideBySideDiff.Build(document);

        // Every parsed line appears exactly once across the fold: context once (it is the
        // same object on both sides), each one-sided line once, spanning lines once.
        var seen = rows.SelectMany(r =>
                r.Spanning is { } s ? new[] { s }
                : ReferenceEquals(r.Left, r.Right) ? new[] { r.Left! }
                : new[] { r.Left, r.Right }.OfType<DiffLine>().ToArray())
            .ToList();

        Assert.Equal(document.Lines.Count, seen.Count);
        Assert.All(document.Lines, l => Assert.Contains(seen, x => ReferenceEquals(x, l)));
    }
}
