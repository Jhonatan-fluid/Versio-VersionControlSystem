using System.Globalization;
using System.Text.RegularExpressions;

namespace Versio.Web.Review.GitHub;

/// <summary>
/// The lines of a file GitHub will accept a review comment on, read from the patch GitHub
/// computed (docs/web/PLAN.md, *Pull request review*, C6: GitHub-specific, so not in Core).
/// </summary>
/// <remarks>
/// <para>
/// <b>GitHub's hunks, not ours.</b> GitHub refuses a comment on a line outside its own diff hunks,
/// and its diff need not match Versio's line for line. So the server checks a line against the
/// patch GitHub gave for the snapshot before posting, and a comment GitHub would refuse is refused
/// here with a reason the page can say, rather than as GitHub's 422.
/// </para>
/// <para>
/// <b>Context lines count on both sides; a range stays inside one hunk.</b> GitHub accepts a comment
/// on an unchanged line shown in a hunk, on either side, and a multi-line comment only when both
/// ends are in the same hunk on the same side.
/// </para>
/// </remarks>
public sealed partial class HunkLines
{
    private readonly Dictionary<int, int> _right = [];
    private readonly Dictionary<int, int> _left = [];

    private HunkLines()
    {
    }

    /// <summary>The commentable lines of <paramref name="patch"/>, each with the hunk it belongs to.</summary>
    public static HunkLines Parse(string patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var lines = new HunkLines();
        int oldLine = 0, newLine = 0, hunk = -1;
        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (Header().Match(line) is { Success: true } header)
            {
                hunk++;
                oldLine = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                newLine = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (hunk < 0 || line.StartsWith('\\'))
            {
                // Before the first header, or "\ No newline at end of file": not a line of either file.
                continue;
            }

            switch (line.Length == 0 ? ' ' : line[0])
            {
                case '+':
                    lines._right[newLine++] = hunk;
                    break;
                case '-':
                    lines._left[oldLine++] = hunk;
                    break;
                default:
                    lines._right[newLine++] = hunk;
                    lines._left[oldLine++] = hunk;
                    break;
            }
        }

        return lines;
    }

    /// <summary>Whether GitHub will take a comment on <paramref name="line"/> (from <paramref name="start"/>, for a range) on that side.</summary>
    public bool Accepts(string side, int line, int? start = null)
    {
        var map = side == "LEFT" ? _left : _right;
        if (!map.TryGetValue(line, out var hunk))
        {
            return false;
        }

        return start is not { } from || from == line || (from < line && map.TryGetValue(from, out var startHunk) && startHunk == hunk);
    }

    /// <summary>
    /// Where a comment on an element spanning <paramref name="start"/>..<paramref name="end"/> goes
    /// on GitHub: the element's lines that GitHub's diff shows, within ONE hunk (GitHub refuses a
    /// range across two) — the hunk holding most of them — as a range ending on its last line.
    /// Null when the diff shows none of the element's lines: the comment then goes on the file.
    /// </summary>
    /// <remarks>
    /// The element's own lines, not merely nearby ones: a comment about a network pinned to a line
    /// of the network after it would read, on github.com, as a comment about the wrong network.
    /// </remarks>
    public (int Line, int? StartLine)? Placement(string side, int start, int end)
    {
        var map = side == "LEFT" ? _left : _right;
        var best = map.Where(kv => kv.Key >= start && kv.Key <= end)
            .GroupBy(kv => kv.Value, kv => kv.Key)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .FirstOrDefault();
        if (best is null)
        {
            return null;
        }

        var first = best.Min();
        var last = best.Max();
        return (last, first == last ? null : first);
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex Header();
}
