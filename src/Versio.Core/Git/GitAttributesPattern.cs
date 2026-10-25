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
/// item 26. git's rule is that a pattern beginning with a double quote is C-quoted.
/// </para>
/// <para>
/// <b>C-quoted means git's <c>unquote_c_style</c>, not "drop the backslash".</b> The first
/// reader did the latter, which is right for <c>\\</c> and <c>\"</c> and wrong for every
/// other escape: <c>"huge\056bin" -filter</c> is git's own spelling of an override for
/// huge.bin (<c>\056</c> is the octal for a dot, and <c>git check-attr</c> answers
/// <c>unset</c>), and it was read as a rule for a file called huge056bin — so
/// <see cref="LargeFilePreflight"/> kept protecting the one file git would commit in full.
/// The 2026-10-25 review's item 6. So the reader now knows the named escapes
/// (<c>\a \b \f \n \r \t \v</c>), the two verbatim ones, and three-digit octal; and it
/// refuses what git refuses — a short or over-range octal, a letter git does not know, a
/// backslash with nothing after it, a quote that never closes — because git drops such a
/// line entirely and a lenient reading would turn a line git ignores into a rule.
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
    /// <para>
    /// Quoted only when it has to be: a plain <c>*.project</c> stays readable, and stays
    /// byte-identical to what every adoption before this wrote, so no existing file reads
    /// as uncovered. A backslash is quoted too: outside quotes it is a glob escape, and
    /// inside them git unescapes <c>\\</c> back to the one backslash — so the quoted form
    /// means exactly what the bare one did, and reading it back gives the same pattern.
    /// </para>
    /// <para>
    /// Inside the quotes the spelling is git's <c>quote_c_style</c>: the named escape where
    /// C has one, three octal digits for any other control character. A literal tab between
    /// the quotes would read back just as well, but it is invisible in a diff, and agreeing
    /// with git's writer rather than only with this file's reader is what keeps a line
    /// written here and a line written by git from being two dialects. Where this stops
    /// short of git: a non-ASCII name stays bare. git would write <c>\303\244</c> for
    /// <c>ä</c>, but it reads the bare form, the bare form is what every earlier adoption
    /// wrote, and a person can read it.
    /// </para>
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
            switch (c)
            {
                case '\\' or '"':
                    sb.Append('\\').Append(c);
                    break;
                case '\a':
                    sb.Append("\\a");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\v':
                    sb.Append("\\v");
                    break;
                default:
                    if (IsControl(c))
                    {
                        sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    /// <summary>
    /// The pattern at the start of a trimmed, non-comment line, and where the attributes
    /// begin.
    /// </summary>
    /// <returns>
    /// The pattern with any quoting decoded, and the index just past it (the line's length
    /// when there are no attributes). Null for an empty line, or a quoted pattern git
    /// would refuse — one that never closes, or one with an escape git does not decode.
    /// git ignores such a line, and so does this.
    /// </returns>
    /// <remarks>
    /// Octal escapes are bytes, not characters: git quotes a name byte by byte, so
    /// <c>ä</c> arrives as <c>\303\244</c>, and a run of them is one UTF-8 string.
    /// Appending each byte as its own char would give two Latin-1 characters and a pattern
    /// that matches no file in the repository.
    /// </remarks>
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
        var octal = new List<byte>();
        for (var i = 1; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                FlushOctal(sb, octal);
                return (sb.ToString(), i + 1);
            }

            if (c != '\\')
            {
                FlushOctal(sb, octal);
                sb.Append(c);
                continue;
            }

            if (i + 1 >= line.Length)
            {
                return null;
            }

            var escaped = line[++i];
            if (escaped is >= '0' and <= '3')
            {
                if (i + 2 >= line.Length || !IsOctal(line[i + 1]) || !IsOctal(line[i + 2]))
                {
                    return null;
                }

                octal.Add((byte)(((escaped - '0') << 6) | ((line[i + 1] - '0') << 3) | (line[i + 2] - '0')));
                i += 2;
                continue;
            }

            var named = escaped switch
            {
                'a' => '\a',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'v' => '\v',
                '\\' or '"' => escaped,
                _ => (char?)null,
            };
            if (named is not { } ch)
            {
                return null;
            }

            FlushOctal(sb, octal);
            sb.Append(ch);
        }

        return null;
    }

    private static void FlushOctal(StringBuilder sb, List<byte> octal)
    {
        if (octal.Count > 0)
        {
            sb.Append(Encoding.UTF8.GetString(octal.ToArray()));
            octal.Clear();
        }
    }

    private static bool IsOctal(char c) => c is >= '0' and <= '7';

    private static bool IsControl(char c) => c < ' ' || c == '\x7f';

    private static bool NeedsQuoting(string pattern) =>
        pattern.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\' || IsControl(c));
}
