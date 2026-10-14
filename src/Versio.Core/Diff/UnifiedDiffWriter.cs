using System.Globalization;
using System.Text;

namespace Versio.Core.Diff;

/// <summary>
/// Two versions of a file as unified-diff text, the format git prints, computed by
/// <see cref="LineDiff"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why text, and not straight to rows.</b> The desktop gets its diff from git, as text, and
/// everything after that is <see cref="UnifiedDiffParser"/>, <see cref="SideBySideDiff"/> and
/// <see cref="IntralineEmphasis"/>. A page that reads two blobs from GitHub has no git to ask,
/// so it writes the same format itself, and the whole of that chain runs unchanged. A second
/// path from steps to rows would be a second answer to "how is a diff drawn".
/// </para>
/// <para>
/// <b>Hunks may sit differently from git's</b> in rare cases, since this is not git's
/// algorithm. They never disagree about which lines changed (docs/outpost/PLAN.md §8 accepted
/// the same trade). One source per view still holds: unified and side by side of one pair are
/// built from this one text.
/// </para>
/// <para>
/// Bare hunks, no <c>diff --git</c> header: the caller knows the path and draws the title.
/// A missing trailing newline is not reported; the mirror's writers always end files with one.
/// </para>
/// </remarks>
public static class UnifiedDiffWriter
{
    /// <summary>Lines of unchanged context kept around each change, git's default.</summary>
    public const int DefaultContext = 3;

    /// <summary>
    /// The unified diff from <paramref name="oldText"/> to <paramref name="newText"/>, or an empty
    /// string when they are the same. Null on either side is an empty file (an added or
    /// deleted file).
    /// </summary>
    public static string Write(string? oldText, string? newText, int context = DefaultContext)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var steps = LineDiff.Compare(oldLines, newLines);

        var changed = new List<int>();
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Op != LineOp.Same)
            {
                changed.Add(i);
            }
        }

        if (changed.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        var c = 0;
        while (c < changed.Count)
        {
            // A hunk runs from `context` steps before its first change to `context` after its
            // last, and swallows the next change when their context would touch or overlap.
            var from = Math.Max(0, changed[c] - context);
            var last = changed[c];
            while (c + 1 < changed.Count && changed[c + 1] - last <= (2 * context) + 1)
            {
                last = changed[++c];
            }

            var to = Math.Min(steps.Count, last + context + 1);
            Hunk(text, steps, from, to, oldLines, newLines);
            c++;
        }

        return text.ToString();
    }

    private static void Hunk(
        StringBuilder text, IReadOnlyList<LineStep> steps, int from, int to, List<string> oldLines, List<string> newLines)
    {
        int oldStart = -1, newStart = -1, oldCount = 0, newCount = 0;
        for (var i = from; i < to; i++)
        {
            var s = steps[i];
            if (s.OldIndex >= 0)
            {
                oldStart = oldStart < 0 ? s.OldIndex : oldStart;
                oldCount++;
            }

            if (s.NewIndex >= 0)
            {
                newStart = newStart < 0 ? s.NewIndex : newStart;
                newCount++;
            }
        }

        // git's convention: an empty side is numbered as the line before it (0 at the top).
        var oldFrom = oldCount == 0 ? PrecedingLine(steps, from, old: true) : oldStart + 1;
        var newFrom = newCount == 0 ? PrecedingLine(steps, from, old: false) : newStart + 1;
        text.Append("@@ -").Append(Range(oldFrom, oldCount)).Append(" +").Append(Range(newFrom, newCount)).Append(" @@\n");

        for (var i = from; i < to; i++)
        {
            var s = steps[i];
            switch (s.Op)
            {
                case LineOp.Same:
                    text.Append(' ').Append(oldLines[s.OldIndex]).Append('\n');
                    break;
                case LineOp.Removed:
                    text.Append('-').Append(oldLines[s.OldIndex]).Append('\n');
                    break;
                case LineOp.Added:
                    text.Append('+').Append(newLines[s.NewIndex]).Append('\n');
                    break;
            }
        }
    }

    /// <summary>The 1-based number of the last line on one side before step <paramref name="from"/>, or 0.</summary>
    private static int PrecedingLine(IReadOnlyList<LineStep> steps, int from, bool old)
    {
        for (var i = from - 1; i >= 0; i--)
        {
            var index = old ? steps[i].OldIndex : steps[i].NewIndex;
            if (index >= 0)
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static string Range(int start, int count) =>
        count == 1
            ? start.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{start},{count}");

    /// <summary>The same split <c>StSourceLayout</c> makes: CRLF folded, no phantom last line.</summary>
    private static List<string> SplitLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
