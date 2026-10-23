using System.Text.RegularExpressions;

namespace Versio.Core.Text;

/// <summary>
/// The lines of an XML file that are markup and nothing else: the framing a table or a ladder
/// drawn from the file has no cell for (docs/web/PLAN.md, *Pull request review*, C4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a line-level rule and not the tree.</b> Review coverage is decided per changed line of
/// the file, because that is what GitHub's diff and the Raw tab show. A vendor's XML spends most
/// of its lines on wrappers — <c>&lt;Sections&gt;</c>, <c>&lt;/Member&gt;</c>, a
/// <c>&lt;Single Name="…" Type="{guid}"&gt;</c> envelope — that carry no value a drawing could
/// show differently; before 2026-10-23 every one of them that changed was a "may not show" gap,
/// which on a real export was all of them. A line is markup-only when, with its tags taken away,
/// nothing is left but spacing or a GUID: a tag's attributes are allowed to stay (a section's
/// name, an element's type id), text content is not, because text is where a value lives
/// (<c>&lt;Number&gt;5&lt;/Number&gt;</c>, a <c>&lt;Single&gt;True&lt;/Single&gt;</c> setting).
/// </para>
/// <para>
/// <b>Excluded spans are the renderer's own.</b> The caller names the spans its rows or networks
/// occupy, and the spans another renderer of the same file draws from (a table leaves the
/// implementation to the ladder, and the ladder leaves the interface to the table): lines inside
/// those are that drawing's evidence to give, never framing. A tag that spans lines, or a
/// multi-line comment, leaves text on its lines and is not framing — conservative, and cheap.
/// </para>
/// </remarks>
public static partial class MarkupLines
{
    /// <summary>
    /// The markup-only lines of <paramref name="xml"/>, as spans of file lines: 1-based in the XML
    /// text, moved down by <paramref name="offset"/> for a file whose XML begins after a header.
    /// </summary>
    /// <param name="except">Spans (already offset, as the parser's rows carry them) whose lines are never framing.</param>
    /// <param name="within">When given, only lines inside this span (already offset) are considered: an object inside a larger export.</param>
    public static IReadOnlyList<LineSpan> Structural(string xml, IEnumerable<LineSpan> except, int offset = 0, LineSpan? within = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(except);

        var excluded = except.ToList();
        var lines = xml.Replace("\r\n", "\n").Split('\n');
        var spans = new List<LineSpan>();
        int? start = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = i + 1 + offset;
            var framing = (within is null || within.Value.Contains(line))
                          && !excluded.Any(s => s.Contains(line))
                          && IsMarkupOnly(lines[i]);
            if (framing)
            {
                start ??= line;
                continue;
            }

            if (start is { } s)
            {
                spans.Add(new LineSpan(s, line - 1));
                start = null;
            }
        }

        if (start is { } last)
        {
            spans.Add(new LineSpan(last, lines.Length + offset));
        }

        return spans;
    }

    /// <summary>Whether a line is tags, spacing and at most a GUID: nothing a drawing could have a cell for.</summary>
    public static bool IsMarkupOnly(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var rest = Tags().Replace(line, "").Trim();
        return rest.Length == 0 || Guid().IsMatch(rest);
    }

    [GeneratedRegex(@"<[^<>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"^\{?[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\}?$")]
    private static partial Regex Guid();
}
