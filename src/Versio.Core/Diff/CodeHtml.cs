using System.Globalization;
using System.Net;
using System.Text;

namespace Versio.Core.Diff;

/// <summary>
/// One line of code as HTML: syntax-coloured spans, changed words marked, every character from
/// the file escaped, and the characters that hide what code does made visible.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is here and not in each renderer.</b> The rendered source, the text diff and the
/// code under a member table all draw lines of code. One line renderer means one escaping rule
/// and one answer to "what does a hidden character look like", where three would drift. The
/// pages it serves are the Versio Web viewer and the Outpost page (docs/web/PLAN.md Step 1,
/// docs/outpost/PLAN.md Step 2b), the same reason <c>LadderHtml</c> lives in Core.
/// </para>
/// <para>
/// <b>Emphasis is cut into the tokens, not laid over them</b>, as <c>DiffView.Highlighted</c>
/// does in WPF and for the same reason: the change and the syntax are two facts about the same
/// characters, and losing either to show the other would be a regression. A token straddling an
/// emphasis boundary is split at it.
/// </para>
/// <para>
/// <b>Nothing from the file is dropped, because the tokenizer covers every character.</b> This
/// draws exactly the spans <see cref="SyntaxTokenizer"/> returns, as the desktop does, so a gap
/// between them would show a reviewer code that is not the file. The first version filled gaps
/// here defensively; a deliberate mutation showed no input could reach that branch, so the
/// promise moved to where it belongs: <c>SyntaxTokenizerCoverageTests</c> pins that the spans
/// run from the first character to the last with no gap and no overlap, over seeded random
/// lines, for every catalog language. It guards the desktop's drawing too.
/// </para>
/// <para>
/// <b>Hidden characters are drawn, never passed through</b> (docs/web/PLAN.md, security rule 9).
/// A right-to-left override or a zero-width space in Structured Text makes the line read one way
/// and compile another ("Trojan Source"). Passing the character to the browser would carry the
/// attack to the reader intact, so each is replaced by a visible <c>U+XXXX</c> marker naming it,
/// and the caller is told how many there were so the page can say so above the code. Raw C0
/// controls are treated the same way: they are invalid in HTML and invisible in a browser.
/// Homoglyphs (a Cyrillic <c>U+0430</c> in a Latin identifier) are not caught here yet.
/// </para>
/// </remarks>
public static class CodeHtml
{
    /// <summary>
    /// Appends <paramref name="text"/> as highlighted, escaped HTML, and returns how many hidden
    /// characters it replaced with markers.
    /// </summary>
    /// <param name="emphasis">Changed-word spans (<see cref="IntralineEmphasis"/>), or null.</param>
    public static int AppendLine(
        StringBuilder html,
        string? text,
        SyntaxLanguage language,
        IReadOnlyList<TextSpan>? emphasis = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(language);

        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var hidden = 0;
        foreach (var token in SyntaxTokenizer.Tokenize(text, language))
        {
            hidden += AppendToken(html, text, token.Start, token.Length, token.Kind, emphasis);
        }

        return hidden;
    }

    /// <summary>Rows per <c>.cd-block</c>, the unit the browser may skip laying out.</summary>
    public const int BlockRows = 256;

    /// <summary>
    /// The notice above code that holds hidden characters, or nothing when it holds none.
    /// </summary>
    /// <remarks>
    /// Above the code, not only inside it: one marker in a long file is easy to scroll past, and
    /// the point is that the reader is told before trusting what they read. One wording for every
    /// code renderer, so the source and the diff of one file cannot describe it differently.
    /// </remarks>
    public static void AppendNotice(StringBuilder html, int hidden)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (hidden <= 0)
        {
            return;
        }

        html.Append("<p class=\"cd-notice\">This file contains ")
            .Append(hidden.ToString(CultureInfo.InvariantCulture))
            .Append(hidden == 1 ? " hidden character" : " hidden characters")
            .Append(" that can make code read differently from how it compiles. Each is shown as a U+ marker.</p>\n");
    }

    /// <summary>Opens a new <c>.cd-block</c> every <see cref="BlockRows"/> rows.</summary>
    /// <remarks>
    /// The browser's stand-in for the desktop's virtualisation: <c>content-visibility: auto</c>
    /// on each block lets it skip laying out the ones nobody can see (<see cref="CodePageAssets"/>).
    /// </remarks>
    public sealed class Blocks(StringBuilder html)
    {
        private int _rows;

        /// <summary>Call before writing each row.</summary>
        public void Next()
        {
            if (_rows % BlockRows == 0)
            {
                if (_rows > 0)
                {
                    html.Append("</div>\n");
                }

                html.Append("<div class=\"cd-block\">\n");
            }

            _rows++;
        }

        /// <summary>Closes the last block, if any row was written.</summary>
        public void End()
        {
            if (_rows > 0)
            {
                html.Append("</div>\n");
            }
        }
    }

    /// <summary>The CSS class a token kind is drawn with, or null for plain text.</summary>
    public static string? TokenClass(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => "cd-t-keyword",
        TokenKind.Type => "cd-t-type",
        TokenKind.Comment => "cd-t-comment",
        TokenKind.String => "cd-t-string",
        TokenKind.Number => "cd-t-number",
        TokenKind.Directive => "cd-t-directive",
        _ => null,
    };

    /// <summary>
    /// Whether a character hides or reorders text when shown as itself: bidirectional controls,
    /// zero-width characters, line and paragraph separators, and C0 controls other than tab.
    /// </summary>
    /// <remarks>
    /// Written as code points, never as the characters or as char escapes: a source file that
    /// guards against invisible characters must not contain any, and an escape is one careless
    /// tool away from becoming the character itself (it happened while this was written).
    /// </remarks>
    public static bool IsHidden(char c) => (int)c switch
    {
        0x09 => false,
        < 0x20 or 0x7F => true,
        0x061C or 0x200E or 0x200F => true, // Arabic letter mark, LRM, RLM
        >= 0x202A and <= 0x202E => true,    // LRE, RLE, PDF, LRO, RLO
        >= 0x2066 and <= 0x2069 => true,    // LRI, RLI, FSI, PDI
        >= 0x200B and <= 0x200D => true,    // zero-width space, non-joiner, joiner
        0x2060 or 0xFEFF => true,           // word joiner, BOM / zero-width no-break space
        0x2028 or 0x2029 => true,           // line and paragraph separators
        _ => false,
    };

    /// <summary>Escapes text for an HTML attribute or element; the one escaping rule here.</summary>
    public static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static int AppendToken(
        StringBuilder html, string text, int start, int length, TokenKind kind, IReadOnlyList<TextSpan>? emphasis)
    {
        if (length == 0)
        {
            return 0;
        }

        var css = TokenClass(kind);
        if (css is not null)
        {
            html.Append("<span class=\"").Append(css).Append("\">");
        }

        var hidden = 0;
        foreach (var (s, l, emphasised) in SplitAtEmphasis(start, length, emphasis))
        {
            if (emphasised)
            {
                html.Append("<mark class=\"cd-em\">");
            }

            hidden += AppendText(html, text, s, l);

            if (emphasised)
            {
                html.Append("</mark>");
            }
        }

        if (css is not null)
        {
            html.Append("</span>");
        }

        return hidden;
    }

    /// <summary>Escaped text, with each hidden character replaced by a marker that names it.</summary>
    private static int AppendText(StringBuilder html, string text, int start, int length)
    {
        var hidden = 0;
        var run = start;
        var end = start + length;
        for (var i = start; i < end; i++)
        {
            if (!IsHidden(text[i]))
            {
                continue;
            }

            if (i > run)
            {
                html.Append(E(text[run..i]));
            }

            var code = ((int)text[i]).ToString("X4", CultureInfo.InvariantCulture);
            html.Append("<span class=\"cd-hidden\" data-char=\"U+").Append(code)
                .Append("\" title=\"").Append(E(HiddenName(text[i]))).Append("\">U+").Append(code).Append("</span>");
            hidden++;
            run = i + 1;
        }

        if (end > run)
        {
            html.Append(E(text[run..end]));
        }

        return hidden;
    }

    private static string HiddenName(char c) => (int)c switch
    {
        0x202A => "Left-to-right embedding: changes the order text is shown in",
        0x202B => "Right-to-left embedding: changes the order text is shown in",
        0x202C => "Pop directional formatting: ends a direction change",
        0x202D => "Left-to-right override: changes the order text is shown in",
        0x202E => "Right-to-left override: changes the order text is shown in",
        0x2066 => "Left-to-right isolate: changes the order text is shown in",
        0x2067 => "Right-to-left isolate: changes the order text is shown in",
        0x2068 => "First strong isolate: changes the order text is shown in",
        0x2069 => "Pop directional isolate: ends a direction change",
        0x200E => "Left-to-right mark (invisible)",
        0x200F => "Right-to-left mark (invisible)",
        0x061C => "Arabic letter mark (invisible)",
        0x200B => "Zero-width space (invisible)",
        0x200C => "Zero-width non-joiner (invisible)",
        0x200D => "Zero-width joiner (invisible)",
        0x2060 => "Word joiner (invisible)",
        0xFEFF => "Zero-width no-break space or byte-order mark (invisible)",
        0x2028 => "Line separator",
        0x2029 => "Paragraph separator",
        _ => "Control character (invisible)",
    };

    /// <summary>Cut one token at the emphasis boundaries crossing it.</summary>
    private static IEnumerable<(int Start, int Length, bool Emphasised)> SplitAtEmphasis(
        int start, int length, IReadOnlyList<TextSpan>? emphasis)
    {
        if (emphasis is not { Count: > 0 })
        {
            yield return (start, length, false);
            yield break;
        }

        var at = start;
        var end = start + length;
        foreach (var span in emphasis)
        {
            if (span.End <= at || span.Start >= end)
            {
                continue;
            }

            if (span.Start > at)
            {
                yield return (at, span.Start - at, false);
                at = span.Start;
            }

            var stop = Math.Min(span.End, end);
            yield return (at, stop - at, true);
            at = stop;
        }

        if (at < end)
        {
            yield return (at, end - at, false);
        }
    }
}
