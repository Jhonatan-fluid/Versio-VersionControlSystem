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
/// fence with no lines at all means "delete these lines" — GitHub's reading too. The comment body
/// itself may be LF or CRLF (GitHub stores what the client sent; a Windows tool sends CRLF), and the
/// fence is found either way — <see cref="Fence"/> says what that cost once.
/// </para>
/// <para>
/// <b>The file's line endings are kept, line by line.</b> A suggestion typed on github.com arrives
/// with <c>\n</c>; writing that into a CRLF file would turn one changed line into a whole-file diff on
/// the next export, which is exactly the kind of noise this project exists to avoid. The same goes
/// for the lines the suggestion does not touch: a file of mixed endings keeps each one as it was, and
/// only the replaced lines are rewritten — "the file's ending" is not one thing when the file disagrees
/// with itself, and choosing one for the whole file rewrote the lines nobody selected (review, 2026-10-25).
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

        // Each line keeps its own terminator, so a line outside the range is copied back as the
        // bytes it was. The first version split on '\n', TrimEnd('\r')-ed every line and rejoined
        // with one ending chosen for the whole file: in a file of mixed endings that rewrote every
        // line of the minority kind, and a '\r' that was content — one before the ending, or one
        // closing a final line with no newline — went with it (review, 2026-10-25). Both are the
        // whole-file noise the class remarks say it exists to avoid, on the lines nobody selected.
        var lines = SplitKeepingEndings(text);
        if (endLine > lines.Count)
        {
            return null;
        }

        // The fence's body ends with the newline before the closing fence, which is not a line;
        // a blank line before that IS one, as on github.com — TrimEnd('\n') took every trailing
        // blank line with it (review, 2026-10-24).
        var body = suggestion.EndsWith('\n') ? suggestion[..^1] : suggestion;
        var replacement = suggestion.Length == 0 ? [] : body.Split('\n');

        // A fence typed on github.com says nothing about endings (Read normalised them to '\n'), so
        // the inserted lines take the ending of the first line they replace — the one reading as
        // "this line, changed". That line has none only when it is the file's last and the file has
        // no final newline; then the dominant ending stands in, and '\n' for a file with no endings.
        var ending = EndingOf(lines[startLine - 1]);
        if (ending.Length == 0)
        {
            ending = DominantEnding(lines);
        }

        // Whether the file's final line ends with a newline is a byte like any other: a range that
        // reaches the end of the file leaves its last inserted line bare only when the file was.
        var finalLineEnds = EndingOf(lines[^1]).Length > 0;
        var result = new StringBuilder(text.Length + suggestion.Length);
        for (var i = 0; i < startLine - 1; i++)
        {
            result.Append(lines[i]);
        }

        for (var i = 0; i < replacement.Length; i++)
        {
            result.Append(replacement[i]);
            if (i < replacement.Length - 1 || endLine < lines.Count || finalLineEnds)
            {
                result.Append(ending);
            }
        }

        for (var i = endLine; i < lines.Count; i++)
        {
            result.Append(lines[i]);
        }

        return result.ToString();
    }

    /// <summary>
    /// The text as lines, each with the terminator it has — <c>\n</c>, <c>\r\n</c>, or none for a
    /// final line with no newline. An empty text is one bare line, as <c>Split</c> read it before,
    /// so a suggestion on line 1 of an empty file still applies; text ending in a newline has no
    /// empty line after it, since that newline closes the last line rather than opening another.
    /// </summary>
    private static List<string> SplitKeepingEndings(string text)
    {
        var lines = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                lines.Add(text[start..]);
                break;
            }

            lines.Add(text[start..(newline + 1)]);
            start = newline + 1;
        }

        if (text.Length == 0)
        {
            lines.Add("");
        }

        return lines;
    }

    /// <summary>The terminator a line of <see cref="SplitKeepingEndings"/> carries; empty for none.</summary>
    private static string EndingOf(string line) =>
        line.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : line.EndsWith('\n') ? "\n" : "";

    /// <summary>
    /// The ending most of the file's lines use; CRLF on a tie, which is what the first version chose
    /// whenever a CRLF was present at all, and '\n' for a file with no line endings in it.
    /// </summary>
    private static string DominantEnding(List<string> lines)
    {
        var crlf = 0;
        var lf = 0;
        foreach (var line in lines)
        {
            switch (EndingOf(line))
            {
                case "\r\n": crlf++; break;
                case "\n": lf++; break;
            }
        }

        return crlf > 0 && crlf >= lf ? "\r\n" : "\n";
    }

    /// <summary>
    /// A fenced block whose info string is exactly <c>suggestion</c>; the body up to the closing fence.
    /// </summary>
    /// <remarks>
    /// Both fence lines allow a CRLF end. The opening one always did (<c>\r?\n</c>); the closing one
    /// ended in <c>$</c>, which under <c>(?m)</c> matches before <c>\n</c> only in .NET — so a CRLF
    /// body whose closing fence ended its line, the usual shape of a comment written in a Windows
    /// tool, matched nothing and was "no suggestion", while the same body with no newline after the
    /// fence matched at end-of-string (review, 2026-10-25). The <c>\r</c> is a lookahead rather than
    /// consumed: <see cref="Without"/> takes the match out and the <c>\r\n</c> after the fence is the
    /// prose's own line break, as the <c>\n</c> is in an LF body — eating half of it left a bare
    /// <c>\n</c> in a CRLF text. The captured body is normalised to <c>\n</c> by <see cref="Read"/>.
    /// </remarks>
    [GeneratedRegex(@"(?m)^[ \t]*```+[ \t]*suggestion[ \t]*\r?\n(.*?)^[ \t]*```+[ \t]*(?=\r?$)", RegexOptions.Singleline)]
    private static partial Regex Fence();
}
