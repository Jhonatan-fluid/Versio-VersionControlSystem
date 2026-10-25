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
/// </para>
/// <para>
/// <b>A missing final newline is a change</b>, said the way git says it: the unterminated last
/// line is a different line from its terminated twin, and <c>\ No newline at end of file</c>
/// follows it. Until the 2026-10-25 review's item 10 it was not reported at all, on the
/// grounds that the mirror's writers always end a file with one — but the raw comparison
/// also reads files no writer of Versio's made (a vendor's own JSON, a README), and
/// <c>value\n</c> against <c>value</c> wrote an empty patch, which the page drew as "No
/// changes." over a file whose last byte had gone. <see cref="UnifiedDiffParser"/> already
/// read the marker out of git's own patches, so nothing downstream had to learn it.
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
        var (oldLines, oldUnterminated) = SplitLines(oldText);
        var (newLines, newUnterminated) = SplitLines(newText);
        // Compared with their terminators, as git compares them: a last line without its
        // newline is not the same line as one with it, and only the keys know. The written
        // lines never carry one.
        var steps = LineDiff.Compare(Keyed(oldLines, oldUnterminated), Keyed(newLines, newUnterminated));

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
            Hunk(text, steps, from, to, oldLines, newLines, oldUnterminated, newUnterminated);
            c++;
        }

        return text.ToString();
    }

    private static void Hunk(
        StringBuilder text, IReadOnlyList<LineStep> steps, int from, int to, List<string> oldLines, List<string> newLines,
        bool oldUnterminated, bool newUnterminated)
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

            // The marker goes right after the line it describes, on whichever side that line
            // was written from; a context line is the same line on both sides, so one marker.
            // Only inside a hunk: git says nothing about a last line it did not print.
            var unterminated = s.Op == LineOp.Added
                ? newUnterminated && s.NewIndex == newLines.Count - 1
                : oldUnterminated && s.OldIndex == oldLines.Count - 1;
            if (unterminated)
            {
                text.Append("\\ No newline at end of file\n");
            }
        }
    }

    /// <summary>The lines as git compares them, each with its newline, the unterminated last one without.</summary>
    private static List<string> Keyed(List<string> lines, bool unterminated)
    {
        var keys = new List<string>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            keys.Add(unterminated && i == lines.Count - 1 ? lines[i] : lines[i] + "\n");
        }

        return keys;
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

    /// <summary>
    /// The same split <c>StSourceLayout</c> makes, CRLF folded and no phantom last line, and
    /// whether the last line lacked its newline. An empty file is no lines and is not
    /// "unterminated": there is no line for the marker to follow.
    /// </summary>
    private static (List<string> Lines, bool Unterminated) SplitLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return ([], false);
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var unterminated = lines[^1].Length > 0;
        if (!unterminated)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return (lines, unterminated);
    }
}
