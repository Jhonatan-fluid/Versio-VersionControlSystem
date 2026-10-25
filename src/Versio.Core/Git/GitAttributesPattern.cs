using System.Text;

namespace Versio.Core.Git;

/// <summary>
/// How a pattern is written to, and read back from, a <c>.gitattributes</c> line.
/// </summary>
/// <remarks>
/// <para>
/// git splits an attribute line on whitespace, so a pattern that contains a space has to be
/// quoted or it is not that pattern: <c>src/My App/export/** text eol=lf</c> is the pattern
/// <c>src/My</c> with the attributes <c>App/export/**</c>, <c>text</c> and <c>eol=lf</c>.
/// The planner wrote exactly that for a mirror under a folder with a space in its name, the
/// mirror got no LF rule, and because the covered-check read the first whitespace token
/// too, every later adoption appended the same broken block again. The 2026-10-24 review's
/// item 26. git's rule is that a pattern beginning with a double quote is C-quoted:
/// <c>\\</c> and <c>\"</c> inside it, closing quote, then the attributes.
/// </para>
/// <para>
/// One place for both directions, so that what the planner writes is what the planner and
/// <see cref="LargeFilePreflight"/> read back. A writer and two readers that each had their
/// own idea of a token is how the duplicate blocks happened.
/// </para>
/// </remarks>
public static class GitAttributesPattern
{
    /// <summary>The pattern as it must appear at the start of a line.</summary>
    /// <remarks>
    /// Quoted only when it has to be: a plain <c>*.project</c> stays readable, and stays
    /// byte-identical to what every adoption before this wrote, so no existing file reads
    /// as uncovered. A backslash is quoted too: outside quotes it is a glob escape, and
    /// inside them git unescapes <c>\\</c> back to the one backslash — so the quoted form
    /// means exactly what the bare one did, and reading it back gives the same pattern.
    /// </remarks>
    public static string Quote(string pattern)
    {
        if (!NeedsQuoting(pattern))
        {
            return pattern;
        }

        var sb = new StringBuilder(pattern.Length + 2).Append('"');
        foreach (var c in pattern)
        {
            if (c is '\\' or '"')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.Append('"').ToString();
    }

    /// <summary>
    /// The pattern at the start of a trimmed, non-comment line, and where the attributes
    /// begin.
    /// </summary>
    /// <returns>
    /// The pattern with any quoting removed, and the index just past it (the line's length
    /// when there are no attributes). Null for an empty line, or a quoted pattern that never
    /// closes — git ignores such a line, and so does this.
    /// </returns>
    public static (string Pattern, int End)? Read(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        if (line[0] != '"')
        {
            var end = line.IndexOfAny([' ', '\t']);
            return end < 0 ? (line, line.Length) : (line[..end], end);
        }

        var sb = new StringBuilder();
        for (var i = 1; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                sb.Append(line[++i]);
            }
            else if (c == '"')
            {
                return (sb.ToString(), i + 1);
            }
            else
            {
                sb.Append(c);
            }
        }

        return null;
    }

    private static bool NeedsQuoting(string pattern) =>
        pattern.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\');
}
