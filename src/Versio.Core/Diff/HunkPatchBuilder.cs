using System.Text;

namespace Versio.Core.Diff;

/// <summary>
/// Builds a patch containing one hunk of a diff, for <c>git apply</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hunk is copied verbatim, never rewritten.</b> A hunk header states how many lines it
/// covers on each side, and those counts have to match the body exactly or git rejects the
/// patch — so the safe way to extract one is to take the bytes git produced and change
/// nothing. Recomputing the header would work right up until it did not, on some file with a
/// missing trailing newline, and the failure would be a refusal to stage with git's own
/// wording pointing at a patch the user never wrote.
/// </para>
/// <para>
/// The file header is carried with it because a patch without one applies to nothing. In a
/// multi-file diff — what <c>git show</c> produces for a commit — the header carried is the
/// one belonging to the hunk's own file, which is why this scans rather than simply taking
/// everything before the first <c>@@</c>.
/// </para>
/// <para>
/// Direction is the caller's business: the same patch stages a working-tree hunk with
/// <c>git apply --cached</c> and unstages an index hunk with <c>git apply --cached
/// --reverse</c>. Encoding it here would mean this had to know which side the diff came from,
/// and it does not.
/// </para>
/// </remarks>
public static class HunkPatchBuilder
{
    /// <summary>
    /// Extract the patch for one hunk.
    /// </summary>
    /// <param name="diff">A unified diff exactly as git printed it.</param>
    /// <param name="hunkIndex">Zero-based, counting hunks across the whole diff.</param>
    /// <returns>A patch, or null when there is no such hunk.</returns>
    public static string? Build(string? diff, int hunkIndex)
    {
        if (string.IsNullOrEmpty(diff) || hunkIndex < 0)
        {
            return null;
        }

        // Split on \n and keep \r on the line, so a CRLF diff is reassembled byte for byte.
        // Git compares what it applies against the file on disk, and a patch whose line
        // endings were normalised in passing would not match a CRLF working tree.
        var lines = diff.Split('\n');

        // A diff ending in a newline - which every diff git prints does - splits to a final
        // empty element that is an artifact of the split rather than a line. Carried through,
        // it reappears as a blank line appended to the patch, and git reads a blank line
        // inside a hunk as a context line that is not in the file.
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        var header = new List<string>();
        var body = new List<string>();
        var seen = -1;
        var collecting = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git", StringComparison.Ordinal))
            {
                if (collecting)
                {
                    break;
                }

                header.Clear();
                header.Add(line);
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (collecting)
                {
                    break;
                }

                seen++;
                if (seen == hunkIndex)
                {
                    collecting = true;
                    body.Add(line);
                }

                continue;
            }

            if (collecting)
            {
                body.Add(line);
                continue;
            }

            if (seen < 0 || IsFileHeader(line))
            {
                header.Add(line);
            }
        }

        if (body.Count == 0)
        {
            return null;
        }

        var patch = new StringBuilder();
        foreach (var line in header.Where(l => l.Length > 0))
        {
            patch.Append(line).Append('\n');
        }

        foreach (var line in body)
        {
            patch.Append(line).Append('\n');
        }

        // git apply refuses a patch whose last line has no terminator, and the trailing empty
        // element from the split has already been dropped by the emptiness check above.
        return patch.ToString();
    }

    /// <summary>How many hunks the diff contains.</summary>
    public static int Count(string? diff) =>
        string.IsNullOrEmpty(diff)
            ? 0
            : diff.Split('\n').Count(l => l.StartsWith("@@", StringComparison.Ordinal));

    /// <summary>
    /// Lines that belong to a file's header rather than to a hunk.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than the parser's equivalent: this list decides what gets copied
    /// into a patch git will act on, so it names the lines <c>git apply</c> reads and nothing
    /// else. <c>Binary files … differ</c> is absent because a binary file has no hunks to
    /// stage in the first place.
    /// </remarks>
    private static bool IsFileHeader(string line) =>
        line.StartsWith("index ", StringComparison.Ordinal) ||
        line.StartsWith("--- ", StringComparison.Ordinal) ||
        line.StartsWith("+++ ", StringComparison.Ordinal) ||
        line.StartsWith("old mode ", StringComparison.Ordinal) ||
        line.StartsWith("new mode ", StringComparison.Ordinal) ||
        line.StartsWith("new file mode ", StringComparison.Ordinal) ||
        line.StartsWith("deleted file mode ", StringComparison.Ordinal) ||
        line.StartsWith("similarity index ", StringComparison.Ordinal) ||
        line.StartsWith("rename from ", StringComparison.Ordinal) ||
        line.StartsWith("rename to ", StringComparison.Ordinal);
}
