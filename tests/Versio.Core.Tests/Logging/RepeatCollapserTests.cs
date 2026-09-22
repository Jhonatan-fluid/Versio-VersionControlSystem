using Versio.Core.Logging;
using Xunit;

namespace Versio.Core.Tests.Logging;

/// <summary>
/// One line for a storm, not one per drop.
/// </summary>
/// <remarks>
/// The storm that motivated it: an IDE rewriting its open-project marker every five seconds,
/// logged as a settled file each time — twice. Seen in the running window on 2026-09-21.
/// </remarks>
public sealed class RepeatCollapserTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_occurrence_always_prints()
    {
        // So the log still says when the storm began.
        var collapser = new RepeatCollapser();

        Assert.Equal("settled: a.~u", collapser.Next("a.~u", "settled: a.~u", T0));
    }

    [Fact]
    public void Repeats_inside_the_window_are_folded_and_counted_on_the_next_line()
    {
        var collapser = new RepeatCollapser(TimeSpan.FromMinutes(5));
        collapser.Next("a.~u", "settled: a.~u", T0);

        for (var i = 1; i <= 59; i++)
        {
            Assert.Null(collapser.Next("a.~u", "settled: a.~u", T0.AddSeconds(5 * i)));
        }

        Assert.Equal(
            "settled: a.~u (59 more times since the last line)",
            collapser.Next("a.~u", "settled: a.~u", T0.AddMinutes(5)));
    }

    [Fact]
    public void Different_files_never_fold_into_each_other()
    {
        // An export settles dozens of different files once each; every one keeps its line.
        var collapser = new RepeatCollapser();

        Assert.NotNull(collapser.Next("a.st", "settled: a.st", T0));
        Assert.NotNull(collapser.Next("b.st", "settled: b.st", T0));
        Assert.NotNull(collapser.Next("c.st", "settled: c.st", T0));
    }

    [Fact]
    public void A_single_fold_reads_as_once_more()
    {
        var collapser = new RepeatCollapser(TimeSpan.FromMinutes(5));
        collapser.Next("a", "x", T0);
        collapser.Next("a", "x", T0.AddSeconds(5));

        Assert.Equal("x (once more since the last line)", collapser.Next("a", "x", T0.AddMinutes(6)));
    }

    [Fact]
    public void After_a_quiet_window_the_count_starts_again()
    {
        var collapser = new RepeatCollapser(TimeSpan.FromMinutes(5));
        collapser.Next("a", "x", T0);
        collapser.Next("a", "x", T0.AddSeconds(5));
        collapser.Next("a", "x", T0.AddMinutes(6));

        Assert.Equal("x", collapser.Next("a", "x", T0.AddMinutes(12)));
    }

    [Fact]
    public void Keys_ignore_case_as_Windows_paths_do()
    {
        var collapser = new RepeatCollapser();
        collapser.Next(@"C:\A\b.~u", "x", T0);

        Assert.Null(collapser.Next(@"c:\a\B.~u", "x", T0.AddSeconds(5)));
    }
}
