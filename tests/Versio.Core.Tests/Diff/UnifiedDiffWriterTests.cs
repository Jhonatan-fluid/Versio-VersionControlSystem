using Versio.Core.Diff;
using Xunit;

namespace Versio.Core.Tests.Diff;

/// <summary>
/// The diff the web viewer writes for itself, because it has no git to ask: git's format, git's
/// hunk shape, and nothing lost, so the parser and both views read it as they read git's.
/// </summary>
public sealed class UnifiedDiffWriterTests
{
    private static string Lines(int count, Func<int, string>? line = null) =>
        string.Concat(Enumerable.Range(1, count).Select(i => (line ?? (n => $"line {n}"))(i) + "\n"));

    [Fact]
    public void The_same_text_is_no_diff()
    {
        Assert.Equal("", UnifiedDiffWriter.Write("a\nb\n", "a\nb\n"));
        Assert.Equal("", UnifiedDiffWriter.Write("a\r\nb\r\n", "a\nb\n"));
    }

    [Fact]
    public void A_changed_line_gets_three_lines_of_context_each_side()
    {
        var diff = UnifiedDiffWriter.Write(Lines(10), Lines(10, n => n == 5 ? "five" : $"line {n}"));

        Assert.Equal(
            "@@ -2,7 +2,7 @@\n line 2\n line 3\n line 4\n-line 5\n+five\n line 6\n line 7\n line 8\n",
            diff);
    }

    [Fact]
    public void An_added_file_and_a_deleted_file_are_numbered_from_zero_as_git_numbers_them()
    {
        Assert.Equal("@@ -0,0 +1,2 @@\n+a\n+b\n", UnifiedDiffWriter.Write(null, "a\nb\n"));
        Assert.Equal("@@ -1,2 +0,0 @@\n-a\n-b\n", UnifiedDiffWriter.Write("a\nb\n", ""));
    }

    [Fact]
    public void A_pure_insertion_names_the_line_before_it_on_the_empty_side()
    {
        var diff = UnifiedDiffWriter.Write("a\nb\n", "a\nb\nc\n", context: 0);

        Assert.Equal("@@ -2,0 +3 @@\n+c\n", diff);
    }

    [Fact]
    public void Changes_whose_context_would_touch_share_one_hunk_and_far_ones_do_not()
    {
        var near = UnifiedDiffWriter.Write(Lines(20), Lines(20, n => n is 5 or 11 ? "x" : $"line {n}"));
        var far = UnifiedDiffWriter.Write(Lines(30), Lines(30, n => n is 5 or 20 ? "x" : $"line {n}"));

        Assert.Equal(1, near.Split('\n').Count(l => l.StartsWith("@@", StringComparison.Ordinal)));
        Assert.Equal(2, far.Split('\n').Count(l => l.StartsWith("@@", StringComparison.Ordinal)));
    }

    [Fact]
    public void With_all_the_context_the_diff_rebuilds_both_files_exactly()
    {
        // Nothing dropped, nothing invented: the old file is every context and removed line, the
        // new one every context and added line. Over seeded random pairs, so a failure reproduces.
        var random = new Random(20261011);
        string[] vocabulary = ["IF x THEN", "END_IF", "a := 1;", "", "\tb := 2;", "(* c *)"];

        for (var n = 0; n < 300; n++)
        {
            var old = string.Concat(Enumerable.Range(0, random.Next(0, 25)).Select(_ => vocabulary[random.Next(vocabulary.Length)] + "\n"));
            var @new = string.Concat(Enumerable.Range(0, random.Next(0, 25)).Select(_ => vocabulary[random.Next(vocabulary.Length)] + "\n"));

            var document = UnifiedDiffParser.Parse(UnifiedDiffWriter.Write(old, @new, context: 1_000_000));

            if (old == @new)
            {
                Assert.True(document.IsEmpty);
                continue;
            }

            var rebuiltOld = string.Concat(document.Lines.Where(l => l.Kind is DiffLineKind.Context or DiffLineKind.Removed).Select(l => l.Text + "\n"));
            var rebuiltNew = string.Concat(document.Lines.Where(l => l.Kind is DiffLineKind.Context or DiffLineKind.Added).Select(l => l.Text + "\n"));
            Assert.Equal(old, rebuiltOld);
            Assert.Equal(@new, rebuiltNew);
        }
    }

    [Fact]
    public void The_parser_numbers_the_lines_the_writer_meant()
    {
        var document = UnifiedDiffParser.Parse(UnifiedDiffWriter.Write(Lines(10), Lines(10, n => n == 5 ? "five" : $"line {n}")));

        var removed = Assert.Single(document.Lines, l => l.Kind == DiffLineKind.Removed);
        var added = Assert.Single(document.Lines, l => l.Kind == DiffLineKind.Added);
        Assert.Equal(5, removed.OldLine);
        Assert.Equal(5, added.NewLine);
        Assert.Equal(1, document.Added);
        Assert.Equal(1, document.Removed);
    }
}
