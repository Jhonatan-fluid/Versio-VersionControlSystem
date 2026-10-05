using System.Text;
using System.Text.RegularExpressions;

namespace Versio.Core.Text;

/// <summary>How a run of inline text is drawn. Combinable: <c>**`x`**</c> is both.</summary>
[Flags]
public enum MdStyle
{
    None = 0,
    Strong = 1,
    Emphasis = 2,
    Code = 4,
    Strike = 8,
}

/// <summary>One run of inline text with one style, and the link it belongs to if any.</summary>
public sealed record MdRun(string Text, MdStyle Style, string? Link = null);

/// <summary>Column alignment from a table's delimiter row.</summary>
public enum MdAlign
{
    None,
    Left,
    Center,
    Right,
}

/// <summary>One block of a parsed document.</summary>
public abstract record MdBlock;

public sealed record MdHeading(int Level, IReadOnlyList<MdRun> Runs) : MdBlock;

public sealed record MdParagraph(IReadOnlyList<MdRun> Runs) : MdBlock;

/// <summary>A fenced or indented code block: kept verbatim, never parsed for inlines.</summary>
public sealed record MdCode(string Language, string Text) : MdBlock;

public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;

/// <summary>A list: each item is its own sequence of blocks, so lists nest.</summary>
public sealed record MdList(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<MdBlock>> Items) : MdBlock;

public sealed record MdTable(
    IReadOnlyList<IReadOnlyList<MdRun>> Header,
    IReadOnlyList<MdAlign> Align,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<MdRun>>> Rows) : MdBlock;

public sealed record MdRule : MdBlock;

/// <summary>
/// Markdown text into blocks and styled runs, for the diff pane's Rendered tab.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> A <c>.md</c> file selected in the Project panel offered a Rendered tab
/// that drew exactly what Raw drew: the rendered half had an arm for ST, catalogs and member
/// tables, and Markdown fell through to the ST source layout. Seen on 2026-10-04 with
/// <c>docs/outpost/PLAN.md</c> - a repository whose documentation is its engineering log, read
/// as pipes and asterisks.
/// </para>
/// <para>
/// <b>Why it is hand-written rather than Markdig.</b> This is a reading view, not a publisher:
/// what it has to get right is what this repository's own documents use - ATX and setext
/// headings, hard-wrapped paragraphs, nested lists with lazy continuation, GFM pipe tables,
/// fences, quotes, rules, and the inline set of strong, emphasis, code, strikethrough and links.
/// That is a few hundred lines with no dependency, it lives where the rest of the text handling
/// lives, and it keeps <c>Versio.Core</c> free of a package for one tab. What it does NOT do is
/// listed so nobody mistakes the gap for a bug: reference-style links, HTML (shown as text),
/// footnotes, and the full CommonMark delimiter-run algorithm - emphasis here is matched
/// nearest-first, which differs from the spec only on pathological nesting.
/// </para>
/// <para>
/// Pure, like the rest of Core: a string in, records out, no I/O. Anything it cannot parse
/// degrades to paragraph text, never to an exception - a broken document still has to open.
/// </para>
/// </remarks>
public static partial class Markdown
{
    /// <summary>Parse a whole document.</summary>
    public static IReadOnlyList<MdBlock> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace('\r', '\n')
                        .Replace("\t", "    ", StringComparison.Ordinal)
                        .Split('\n');
        return ParseBlocks(lines);
    }

    // ------------------------------------------------------------------ blocks

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ ]+(.*?))?(?:[ ]+#+)?[ ]*$")]
    private static partial Regex AtxHeading();

    [GeneratedRegex(@"^ {0,3}(?:(?:-[ ]*){3,}|(?:\*[ ]*){3,}|(?:_[ ]*){3,})$")]
    private static partial Regex ThematicBreak();

    [GeneratedRegex(@"^( {0,3})(`{3,}|~{3,})[ ]*([^`\s]*)")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^( {0,3})([-*+]|(\d{1,9})[.)])( +|$)")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"^ {0,3}(=+|-+)[ ]*$")]
    private static partial Regex SetextUnderline();

    [GeneratedRegex(@"^ *\|? *:?-+:? *(\| *:?-+:? *)*\|? *$")]
    private static partial Regex TableDelimiter();

    private static IReadOnlyList<MdBlock> ParseBlocks(IReadOnlyList<string> lines)
    {
        var blocks = new List<MdBlock>();
        var paragraph = new List<string>();
        var i = 0;

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new MdParagraph(Inline(JoinParagraph(paragraph))));
                paragraph.Clear();
            }
        }

        while (i < lines.Count)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                i++;
                continue;
            }

            // Setext: a paragraph's last line underlined. Checked before the thematic break,
            // because "---" under text is a heading in CommonMark and a rule only after a blank.
            if (paragraph.Count > 0 && SetextUnderline().Match(line) is { Success: true } setext)
            {
                var level = setext.Groups[1].Value[0] == '=' ? 1 : 2;
                blocks.Add(new MdHeading(level, Inline(JoinParagraph(paragraph))));
                paragraph.Clear();
                i++;
                continue;
            }

            if (Fence().Match(line) is { Success: true } fence)
            {
                FlushParagraph();
                i = ReadFence(lines, i, fence, blocks);
                continue;
            }

            if (AtxHeading().Match(line) is { Success: true } atx)
            {
                FlushParagraph();
                blocks.Add(new MdHeading(atx.Groups[1].Length, Inline(atx.Groups[2].Value.Trim())));
                i++;
                continue;
            }

            if (ThematicBreak().IsMatch(line))
            {
                FlushParagraph();
                blocks.Add(new MdRule());
                i++;
                continue;
            }

            // An HTML comment is the author talking to the next editor, not to the reader.
            if (paragraph.Count == 0 && line.TrimStart().StartsWith("<!--", StringComparison.Ordinal))
            {
                while (i < lines.Count && !lines[i].Contains("-->", StringComparison.Ordinal))
                {
                    i++;
                }

                i++;
                continue;
            }

            if (IsQuote(line))
            {
                FlushParagraph();
                var quoted = new List<string>();
                while (i < lines.Count && IsQuote(lines[i]))
                {
                    quoted.Add(StripQuote(lines[i]));
                    i++;
                }

                blocks.Add(new MdQuote(ParseBlocks(quoted)));
                continue;
            }

            if (IsTableStart(lines, i))
            {
                FlushParagraph();
                i = ReadTable(lines, i, blocks);
                continue;
            }

            if (ListMarker().Match(line) is { Success: true } marker
                && (paragraph.Count == 0 || CanInterruptParagraph(marker)))
            {
                FlushParagraph();
                i = ReadList(lines, i, blocks);
                continue;
            }

            // Indented code only where it cannot be a paragraph's continuation.
            if (paragraph.Count == 0 && line.StartsWith("    ", StringComparison.Ordinal))
            {
                var code = new List<string>();
                while (i < lines.Count
                       && (lines[i].StartsWith("    ", StringComparison.Ordinal)
                           || string.IsNullOrWhiteSpace(lines[i])))
                {
                    code.Add(lines[i].Length >= 4 ? lines[i][4..] : string.Empty);
                    i++;
                }

                while (code.Count > 0 && string.IsNullOrWhiteSpace(code[^1]))
                {
                    code.RemoveAt(code.Count - 1);
                }

                blocks.Add(new MdCode(string.Empty, string.Join('\n', code)));
                continue;
            }

            paragraph.Add(line);
            i++;
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>
    /// CommonMark's rule: a bullet may interrupt a paragraph, a numbered item only when it is
    /// 1. A hard-wrapped sentence that happens to put "2026." at the start of a line is
    /// prose, not a list starting at 2026.
    /// </summary>
    private static bool CanInterruptParagraph(Match marker) =>
        !marker.Groups[3].Success || marker.Groups[3].Value == "1";

    private static int ReadFence(IReadOnlyList<string> lines, int start, Match fence, List<MdBlock> blocks)
    {
        var indent = fence.Groups[1].Length;
        var token = fence.Groups[2].Value;
        var body = new List<string>();
        var i = start + 1;

        while (i < lines.Count)
        {
            var trimmed = lines[i].TrimStart(' ');
            if (lines[i].Length - trimmed.Length <= 3
                && trimmed.StartsWith(token, StringComparison.Ordinal)
                && trimmed.TrimEnd().All(c => c == token[0]))
            {
                i++;
                break;
            }

            // The fence's own indent is removed from each line, as far as each line has it.
            var line = lines[i];
            var strip = 0;
            while (strip < indent && strip < line.Length && line[strip] == ' ')
            {
                strip++;
            }

            body.Add(line[strip..]);
            i++;
        }

        blocks.Add(new MdCode(fence.Groups[3].Value, string.Join('\n', body)));
        return i;
    }

    private static bool IsQuote(string line)
    {
        var trimmed = line.TrimStart(' ');
        return line.Length - trimmed.Length <= 3 && trimmed.StartsWith('>');
    }

    private static string StripQuote(string line)
    {
        var trimmed = line.TrimStart(' ')[1..];
        return trimmed.StartsWith(' ') ? trimmed[1..] : trimmed;
    }

    private static bool IsTableStart(IReadOnlyList<string> lines, int i) =>
        i + 1 < lines.Count
        && lines[i].Contains('|', StringComparison.Ordinal)
        && lines[i + 1].Contains('-', StringComparison.Ordinal)
        && TableDelimiter().IsMatch(lines[i + 1])
        && SplitRow(lines[i + 1]).Count == SplitRow(lines[i]).Count;

    private static int ReadTable(IReadOnlyList<string> lines, int start, List<MdBlock> blocks)
    {
        var header = SplitRow(lines[start]);
        var align = SplitRow(lines[start + 1]).Select(c =>
        {
            var cell = c.Trim();
            var left = cell.StartsWith(':');
            var right = cell.EndsWith(':');
            return (left, right) switch
            {
                (true, true) => MdAlign.Center,
                (true, false) => MdAlign.Left,
                (false, true) => MdAlign.Right,
                _ => MdAlign.None,
            };
        }).ToList();

        var rows = new List<IReadOnlyList<IReadOnlyList<MdRun>>>();
        var i = start + 2;
        while (i < lines.Count && !string.IsNullOrWhiteSpace(lines[i])
               && lines[i].Contains('|', StringComparison.Ordinal))
        {
            // A short row is padded and a long one cut, as GFM does: the header decides
            // how many columns there are.
            var cells = SplitRow(lines[i]);
            rows.Add(Enumerable.Range(0, header.Count)
                .Select(c => Inline(c < cells.Count ? cells[c].Trim() : string.Empty))
                .ToList());
            i++;
        }

        blocks.Add(new MdTable(header.Select(h => Inline(h.Trim())).ToList(), align, rows));
        return i;
    }

    /// <summary>
    /// Split a table row on its pipes - except an escaped <c>\|</c>, and a pipe inside a code
    /// span, which in this repository's tables is usually a shell pipeline or an ST operator.
    /// </summary>
    private static List<string> SplitRow(string line)
    {
        var row = line.Trim();
        if (row.StartsWith('|'))
        {
            row = row[1..];
        }

        if (row.EndsWith('|') && !row.EndsWith("\\|", StringComparison.Ordinal))
        {
            row = row[..^1];
        }

        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;
        for (var i = 0; i < row.Length; i++)
        {
            var c = row[i];
            if (c == '\\' && i + 1 < row.Length && row[i + 1] == '|')
            {
                cell.Append('|');
                i++;
                continue;
            }

            if (c == '`')
            {
                inCode = !inCode;
            }

            if (c == '|' && !inCode)
            {
                cells.Add(cell.ToString());
                cell.Clear();
                continue;
            }

            cell.Append(c);
        }

        cells.Add(cell.ToString());
        return cells;
    }

    /// <summary>
    /// Read one list, every item, and each item's own nested content.
    /// </summary>
    /// <remarks>
    /// An item owns every following line indented at least as far as its text starts, any
    /// blank line followed by such a line, and - the lazy rule - an unindented line that
    /// simply continues the item's last paragraph. That last one is what hard-wrapped prose
    /// in a numbered list needs, and this repository's STATUS is written that way.
    /// </remarks>
    private static int ReadList(IReadOnlyList<string> lines, int start, List<MdBlock> blocks)
    {
        var first = ListMarker().Match(lines[start]);
        var ordered = first.Groups[3].Success;
        var startNumber = ordered && int.TryParse(first.Groups[3].Value, out var n) ? n : 1;
        var bullet = first.Groups[2].Value[^1];

        var items = new List<IReadOnlyList<MdBlock>>();
        var i = start;

        while (i < lines.Count && ListMarker().Match(lines[i]) is { Success: true } marker
               && marker.Groups[3].Success == ordered
               && marker.Groups[2].Value[^1] == bullet)
        {
            var spaces = marker.Groups[4].Length;

            // Five or more spaces after the marker means an indented code block inside the
            // item, so the content starts one space in. An empty item has no spaces at all.
            var contentIndent = marker.Groups[1].Length + marker.Groups[2].Length
                                + (spaces is 0 or > 4 ? 1 : spaces);

            var item = new List<string> { lines[i].Length > contentIndent ? lines[i][contentIndent..] : string.Empty };
            i++;

            while (i < lines.Count)
            {
                var line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                {
                    // A blank line stays in the item only if the item goes on after it.
                    var next = i + 1;
                    while (next < lines.Count && string.IsNullOrWhiteSpace(lines[next]))
                    {
                        next++;
                    }

                    if (next < lines.Count && Indent(lines[next]) >= contentIndent)
                    {
                        item.Add(string.Empty);
                        i++;
                        continue;
                    }

                    break;
                }

                if (Indent(line) >= contentIndent)
                {
                    item.Add(line[contentIndent..]);
                    i++;
                    continue;
                }

                if (ListMarker().IsMatch(line) || StartsBlock(line)
                    || string.IsNullOrWhiteSpace(item[^1]))
                {
                    break;
                }

                // Lazy continuation of the item's paragraph.
                item.Add(line.TrimStart());
                i++;
            }

            items.Add(ParseBlocks(item));

            // Blank lines between items keep the list going; anything else after them ends it.
            var after = i;
            while (after < lines.Count && string.IsNullOrWhiteSpace(lines[after]))
            {
                after++;
            }

            if (after < lines.Count && ListMarker().Match(lines[after]) is { Success: true } again
                && again.Groups[3].Success == ordered && again.Groups[2].Value[^1] == bullet)
            {
                i = after;
            }
        }

        blocks.Add(new MdList(ordered, startNumber, items));
        return i;
    }

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    private static bool StartsBlock(string line) =>
        AtxHeading().IsMatch(line) || Fence().IsMatch(line) || ThematicBreak().IsMatch(line)
        || IsQuote(line);

    /// <summary>
    /// Hard-wrapped lines become one line again. A line ending in two spaces or a backslash
    /// is a deliberate break, and stays one.
    /// </summary>
    private static string JoinParagraph(List<string> lines)
    {
        var text = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].TrimStart();
            var last = i == lines.Count - 1;

            if (!last && line.EndsWith("  ", StringComparison.Ordinal))
            {
                text.Append(line.TrimEnd()).Append('\n');
            }
            else if (!last && line.EndsWith('\\'))
            {
                text.Append(line[..^1]).Append('\n');
            }
            else
            {
                text.Append(line.TrimEnd());
                if (!last)
                {
                    text.Append(' ');
                }
            }
        }

        return text.ToString();
    }

    // ------------------------------------------------------------------ inlines

    /// <summary>Parse one block's inline text into styled runs.</summary>
    public static IReadOnlyList<MdRun> Inline(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var runs = new List<MdRun>();
        ParseInline(text, MdStyle.None, null, runs);
        return Merge(runs);
    }

    private static void ParseInline(string text, MdStyle style, string? link, List<MdRun> runs)
    {
        var plain = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (plain.Length > 0)
            {
                runs.Add(new MdRun(plain.ToString(), style, link));
                plain.Clear();
            }
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length
                && (char.IsPunctuation(text[i + 1]) || char.IsSymbol(text[i + 1])))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`' && CodeSpan(text, i) is (var code, var codeEnd))
            {
                Flush();
                runs.Add(new MdRun(code, style | MdStyle.Code, link));
                i = codeEnd;
                continue;
            }

            if (link is null && (c == '[' || (c == '!' && i + 1 < text.Length && text[i + 1] == '['))
                && LinkAt(text, c == '!' ? i + 1 : i) is (var label, var url, var linkEnd))
            {
                Flush();

                // An image cannot be drawn from a relative path in a reading view; its alt
                // text, in italics, says what was there.
                if (c == '!')
                {
                    ParseInline(label, style | MdStyle.Emphasis, null, runs);
                }
                else
                {
                    ParseInline(label, style, url, runs);
                }

                i = linkEnd;
                continue;
            }

            if (link is null && c == '<' && AutoLink(text, i) is (var auto, var autoEnd))
            {
                Flush();
                runs.Add(new MdRun(auto, style, auto));
                i = autoEnd;
                continue;
            }

            if (c is '*' or '_' or '~' && Delimited(text, i) is (var inner, var added, var delimEnd))
            {
                Flush();
                ParseInline(inner, style | added, link, runs);
                i = delimEnd;
                continue;
            }

            plain.Append(c);
            i++;
        }

        Flush();
    }

    /// <summary>A code span: a run of backticks closed by a run of the same length.</summary>
    private static (string Code, int End)? CodeSpan(string text, int start)
    {
        var ticks = 0;
        while (start + ticks < text.Length && text[start + ticks] == '`')
        {
            ticks++;
        }

        var fence = new string('`', ticks);
        var search = start + ticks;
        while (search < text.Length)
        {
            var close = text.IndexOf(fence, search, StringComparison.Ordinal);
            if (close < 0)
            {
                return null;
            }

            var closeEnd = close + ticks;
            if (closeEnd < text.Length && text[closeEnd] == '`')
            {
                // A longer run is not this span's closer.
                while (closeEnd < text.Length && text[closeEnd] == '`')
                {
                    closeEnd++;
                }

                search = closeEnd;
                continue;
            }

            var code = text[(start + ticks)..close];
            if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0)
            {
                code = code[1..^1];
            }

            return (code, closeEnd);
        }

        return null;
    }

    /// <summary><c>[label](url)</c>, with brackets nested in the label and an optional title.</summary>
    private static (string Label, string Url, int End)? LinkAt(string text, int open)
    {
        var depth = 0;
        var close = -1;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '`' && CodeSpan(text, i) is (_, var codeEnd))
            {
                i = codeEnd - 1;
                continue;
            }

            if (text[i] == '[')
            {
                depth++;
            }
            else if (text[i] == ']' && --depth == 0)
            {
                close = i;
                break;
            }
        }

        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
        {
            return null;
        }

        var parens = 0;
        for (var i = close + 1; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                parens++;
            }
            else if (text[i] == ')' && --parens == 0)
            {
                var target = text[(close + 2)..i].Trim();

                // A title is quoted after a space; only the destination is the link.
                var space = target.IndexOf(' ', StringComparison.Ordinal);
                if (space > 0)
                {
                    target = target[..space];
                }

                target = target.TrimStart('<').TrimEnd('>');
                return (text[(open + 1)..close], target, i + 1);
            }
        }

        return null;
    }

    private static (string Url, int End)? AutoLink(string text, int start)
    {
        var close = text.IndexOf('>', start + 1);
        if (close < 0)
        {
            return null;
        }

        var inner = text[(start + 1)..close];
        return inner.Contains(' ', StringComparison.Ordinal)
               || !(inner.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || inner.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || inner.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            ? null
            : (inner, close + 1);
    }

    /// <summary>
    /// Strong, emphasis or strikethrough opened at <paramref name="start"/>, matched to the
    /// nearest closer that can close it.
    /// </summary>
    /// <remarks>
    /// The underscore rules are the ones that matter in a PLC repository: <c>FB_Motor_Speed</c>
    /// is an identifier, not an emphasised <c>Motor</c>, so an underscore opens only where no
    /// letter or digit precedes it and closes only where none follows. Code spans are skipped
    /// while searching, so a delimiter inside backticks never closes one outside them.
    /// </remarks>
    private static (string Inner, MdStyle Style, int End)? Delimited(string text, int start)
    {
        var c = text[start];
        var run = 0;
        while (start + run < text.Length && text[start + run] == c)
        {
            run++;
        }

        if (c == '~' && run != 2)
        {
            return null;
        }

        var width = c == '~' ? 2 : Math.Min(run, 3);
        var open = start + width;

        // Opening must be followed by text, not space: "a * b" is arithmetic.
        if (open >= text.Length || char.IsWhiteSpace(text[open]))
        {
            return null;
        }

        if (c == '_' && start > 0 && char.IsLetterOrDigit(text[start - 1]))
        {
            return null;
        }

        var token = new string(c, width);
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '`' && CodeSpan(text, i) is (_, var codeEnd))
            {
                i = codeEnd - 1;
                continue;
            }

            if (string.CompareOrdinal(text, i, token, 0, width) != 0
                || char.IsWhiteSpace(text[i - 1]))
            {
                continue;
            }

            var after = i + width;
            if (c == '_' && after < text.Length && char.IsLetterOrDigit(text[after]))
            {
                continue;
            }

            // A longer run here belongs to a different delimiter, unless this one is the
            // longest kind; "**a*b**" must not close the strong at the single star.
            if (after < text.Length && text[after] == c && width < 3)
            {
                i = after;
                while (i + 1 < text.Length && text[i + 1] == c)
                {
                    i++;
                }

                continue;
            }

            var style = c == '~'
                ? MdStyle.Strike
                : width switch
                {
                    1 => MdStyle.Emphasis,
                    2 => MdStyle.Strong,
                    _ => MdStyle.Strong | MdStyle.Emphasis,
                };

            return (text[open..i], style, after);
        }

        return null;
    }

    /// <summary>Adjacent runs with the same style and link become one.</summary>
    private static List<MdRun> Merge(List<MdRun> runs)
    {
        var merged = new List<MdRun>(runs.Count);
        foreach (var run in runs)
        {
            if (merged.Count > 0 && merged[^1].Style == run.Style && merged[^1].Link == run.Link)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + run.Text };
            }
            else
            {
                merged.Add(run);
            }
        }

        return merged;
    }
}
