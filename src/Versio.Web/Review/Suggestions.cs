using System.Text;
using System.Text.RegularExpressions;

namespace Versio.Web.Review;

/// <summary>
/// GitHub's suggested changes (docs/web/FINISH.md, Step 5): a <c>```suggestion</c> fence in a review
/// comment is the text the comment's lines should become. Reading one out of a body, and applying it
/// to the file, are the two halves; both pure, so both are tested without GitHub.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the first fence counts</b>, as on github.com, where a comment with two suggestion blocks
/// applies the first. The fence's text is taken as-is, newlines normalised to the file's own, and a
/// fence with no lines at all means "delete these lines" — GitHub's reading too.
/// </para>
/// <para>
/// <b>The file's line endings are kept.</b> A suggestion typed on github.com arrives with
/// <c>\n</c>; writing that into a CRLF file would turn one changed line into a whole-file diff on the
/// next export, which is exactly the kind of noise this project exists to avoid.
/// </para>
/// </remarks>
public static partial class Suggestions
{
    /// <summary>The suggestion a comment body carries, or null when there is none. Empty means "delete the lines".</summary>
    public static string? Read(string? body)
    {
        if (body is null)
        {
            return null;
        }

        var match = Fence().Match(body);
        return match.Success ? match.Groups[1].Value.Replace("\r\n", "\n", StringComparison.Ordinal) : null;
    }

    /// <summary>
    /// The body with its first suggestion fence taken out: what is drawn as prose, while the page
    /// draws the suggestion itself as a diff with its buttons — github.com shows the block once, as
    /// the widget, never also as a code block.
    /// </summary>
    public static string Without(string body) => body is null ? "" : Fence().Replace(body, "", 1).Trim();

    /// <summary>
    /// The file with lines <paramref name="startLine"/>..<paramref name="endLine"/> (1-based, inclusive,
    /// of the file as it is) replaced by <paramref name="suggestion"/>; null when the range is not in the file.
    /// </summary>
    public static string? Apply(string text, int startLine, int endLine, string suggestion)
    {
        if (startLine < 1 || endLine < startLine)
        {
            return null;
        }

        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewline = text.EndsWith('\n');
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (endsWithNewline)
        {
            // The split leaves an empty entry after the final newline; it is not a line.
            lines.RemoveAt(lines.Count - 1);
        }

        if (endLine > lines.Count)
        {
            return null;
        }

        var replacement = suggestion.Length == 0 ? [] : suggestion.TrimEnd('\n').Split('\n').ToList();
        lines.RemoveRange(startLine - 1, endLine - startLine + 1);
        lines.InsertRange(startLine - 1, replacement);
        var result = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            result.Append(lines[i]);
            if (i < lines.Count - 1 || endsWithNewline)
            {
                result.Append(eol);
            }
        }

        return result.ToString();
    }

    /// <summary>A fenced block whose info string is exactly <c>suggestion</c>; the body up to the closing fence.</summary>
    [GeneratedRegex(@"(?m)^[ \t]*```+[ \t]*suggestion[ \t]*\r?\n(.*?)^[ \t]*```+[ \t]*$", RegexOptions.Singleline)]
    private static partial Regex Fence();
}
