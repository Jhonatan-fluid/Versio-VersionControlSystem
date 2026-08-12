using System.Diagnostics;
using Versio.Core.Diff;
using Xunit;

namespace Versio.Core.Tests.Diff;

/// <summary>
/// The parser against an input the size the fixture project actually produces.
/// </summary>
/// <remarks>
/// A 2.5 MB visualisation export is not a pathological case here - the fixture contains one,
/// 33,644 lines long, and selecting it in the changed-files list froze the window. The
/// rendering was the larger half of that and is fixed in the view, but the parse sits in
/// front of it and is worth holding to a number.
/// </remarks>
public sealed class LargeDiffTests
{
    private static string HugeDiff(int lines)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("diff --git a/Live_Visu.vis.xml b/Live_Visu.vis.xml\n");
        sb.Append("new file mode 100644\n");
        sb.Append("--- /dev/null\n");
        sb.Append("+++ b/Live_Visu.vis.xml\n");
        sb.Append("@@ -0,0 +1,").Append(lines).Append(" @@\n");

        for (var i = 0; i < lines; i++)
        {
            sb.Append("+  <element id=\"").Append(i).Append("\" type=\"rectangle\" />\n");
        }

        return sb.ToString();
    }

    [Fact]
    public void A_visualisation_sized_diff_parses_in_well_under_a_second()
    {
        var text = HugeDiff(33_644);

        var watch = Stopwatch.StartNew();
        var document = UnifiedDiffParser.Parse(text);
        watch.Stop();

        Assert.Equal(33_644 + 5, document.Lines.Count);

        // Generous on purpose: this is a regression guard against the parse becoming
        // quadratic, not a benchmark. A per-line substring search over a 2.5 MB string would
        // blow straight through it.
        Assert.True(watch.ElapsedMilliseconds < 2_000,
            $"parsing {document.Lines.Count} lines took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Every_line_of_a_new_file_is_an_addition()
    {
        // What paints the view green, and the reason the untracked fallback exists at all.
        var document = UnifiedDiffParser.Parse(HugeDiff(50));

        var body = document.Lines.Where(l => l.Kind == DiffLineKind.Added).ToList();
        Assert.Equal(50, body.Count);
        Assert.DoesNotContain(document.Lines, l => l.Kind == DiffLineKind.Removed);
    }
}
