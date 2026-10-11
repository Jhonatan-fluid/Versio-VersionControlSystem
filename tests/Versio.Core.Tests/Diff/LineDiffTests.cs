using Versio.Core.Diff;
using Xunit;

namespace Versio.Core.Tests.Diff;

/// <summary>The line diff the Rendered tab marks changes with.</summary>
public class LineDiffTests
{
    private static string Steps(string a, string b) => string.Concat(
        LineDiff.Compare(a.Split(','), b.Split(',')).Select(s => s.Op switch
        {
            LineOp.Same => "=",
            LineOp.Added => "+",
            _ => "-",
        }));

    [Theory]
    [InlineData("a,b,c", "a,b,c", "===")]
    [InlineData("a,b,c", "a,x,c", "=-+=")]
    [InlineData("a,b,c", "a,c", "=-=")]
    [InlineData("a,c", "a,b,c", "=+=")]
    [InlineData("a,b,c,d", "a,c,b,d", "=-=+=")]
    public void Lines_shared_stay_paired_and_the_rest_are_removed_then_added(string a, string b, string expected)
    {
        Assert.Equal(expected, Steps(a, b));
    }

    [Fact]
    public void Every_step_names_its_lines_in_file_order()
    {
        var steps = LineDiff.Compare(["a", "b", "c"], ["a", "x", "c"]);

        Assert.Equal(
            [new LineStep(LineOp.Same, 0, 0), new LineStep(LineOp.Removed, 1, -1), new LineStep(LineOp.Added, -1, 1), new LineStep(LineOp.Same, 2, 2)],
            steps);
    }

    [Fact]
    public void A_middle_too_big_to_align_is_reported_whole_rather_than_stalling()
    {
        var old = Enumerable.Range(0, 2500).Select(i => "o" + i).ToList();
        var @new = Enumerable.Range(0, 2500).Select(i => "n" + i).ToList();

        var steps = LineDiff.Compare(old, @new);

        Assert.Equal(2500, steps.Count(s => s.Op == LineOp.Removed));
        Assert.Equal(2500, steps.Count(s => s.Op == LineOp.Added));
    }
}
