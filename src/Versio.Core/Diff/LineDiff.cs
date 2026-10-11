namespace Versio.Core.Diff;

/// <summary>What happened to one line between two versions of a file.</summary>
public enum LineOp
{
    Same,
    Added,
    Removed,
}

/// <summary>One step of a line diff: an old line, a new line, or both.</summary>
/// <param name="OldIndex">Zero-based line in the old text, or -1 for an added line.</param>
/// <param name="NewIndex">Zero-based line in the new text, or -1 for a removed line.</param>
public readonly record struct LineStep(LineOp Op, int OldIndex, int NewIndex);

/// <summary>
/// The lines two versions of a file share, and the ones each has alone, in file order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one is needed at all.</b> The Raw tab is git's own patch. The Rendered tab draws the
/// file laid out for reading, and until 2026-10-10 it drew it with nothing marked, so a file the
/// tree called modified opened looking untouched (the owner, on an FBS struct). Marking it needs
/// to know which of the working file's lines are new, against a committed copy git hands over
/// as text, so the comparison happens here, not in a second git process.
/// </para>
/// <para>
/// <b>Longest common subsequence, after the shared ends are taken off.</b> A reviewed change
/// touches a few lines of one object, so trimming the common prefix and suffix leaves a small
/// middle, and a table over that middle is cheap. A middle too big for that (a regenerated file)
/// is reported as all removed and all added, which is the honest answer for a file nobody
/// edited by hand, rather than a long stall on the UI's behalf.
/// </para>
/// </remarks>
public static class LineDiff
{
    /// <summary>Cells in the table above which the middle is not aligned line by line.</summary>
    private const long MaxCells = 4_000_000;

    public static IReadOnlyList<LineStep> Compare(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        ArgumentNullException.ThrowIfNull(oldLines);
        ArgumentNullException.ThrowIfNull(newLines);

        var prefix = 0;
        while (prefix < oldLines.Count && prefix < newLines.Count && oldLines[prefix] == newLines[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Count - prefix && suffix < newLines.Count - prefix
               && oldLines[oldLines.Count - 1 - suffix] == newLines[newLines.Count - 1 - suffix])
        {
            suffix++;
        }

        var steps = new List<LineStep>(Math.Max(oldLines.Count, newLines.Count));
        for (var i = 0; i < prefix; i++)
        {
            steps.Add(new LineStep(LineOp.Same, i, i));
        }

        var oldMiddle = oldLines.Count - prefix - suffix;
        var newMiddle = newLines.Count - prefix - suffix;
        if ((long)oldMiddle * newMiddle > MaxCells)
        {
            for (var i = 0; i < oldMiddle; i++)
            {
                steps.Add(new LineStep(LineOp.Removed, prefix + i, -1));
            }

            for (var j = 0; j < newMiddle; j++)
            {
                steps.Add(new LineStep(LineOp.Added, -1, prefix + j));
            }
        }
        else
        {
            Middle(oldLines, newLines, prefix, oldMiddle, newMiddle, steps);
        }

        for (var k = 0; k < suffix; k++)
        {
            steps.Add(new LineStep(LineOp.Same, oldLines.Count - suffix + k, newLines.Count - suffix + k));
        }

        return steps;
    }

    private static void Middle(
        IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, int offset, int n, int m, List<LineStep> steps)
    {
        // lengths[i, j]: the common subsequence of old[i..] and new[j..], read back from the
        // start so the walk below emits steps in file order.
        var lengths = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lengths[i, j] = oldLines[offset + i] == newLines[offset + j]
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (oldLines[offset + a] == newLines[offset + b])
            {
                steps.Add(new LineStep(LineOp.Same, offset + a, offset + b));
                a++;
                b++;
            }
            else if (lengths[a + 1, b] >= lengths[a, b + 1])
            {
                // Removals before additions, as a patch reads: the old line, then its replacement.
                steps.Add(new LineStep(LineOp.Removed, offset + a, -1));
                a++;
            }
            else
            {
                steps.Add(new LineStep(LineOp.Added, -1, offset + b));
                b++;
            }
        }

        for (; a < n; a++)
        {
            steps.Add(new LineStep(LineOp.Removed, offset + a, -1));
        }

        for (; b < m; b++)
        {
            steps.Add(new LineStep(LineOp.Added, -1, offset + b));
        }
    }
}
