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
/// Look-alike letters (a Cyrillic <c>U+0430</c> in a Latin identifier) are marked too, since
/// 2026-10-11: a name mixing scripts gets a wavy underline (<see cref="MixedScripts"/>).
/// </para>
/// </remarks>
public static class CodeHtml
{
    /// <summary>
    /// Appends <paramref name="text"/> as highlighted, escaped HTML, and returns how many hidden
    /// characters it replaced with markers plus how many names it marked as mixing scripts.
    /// </summary>
    /// <param name="emphasis">Changed-word spans (<see cref="IntralineEmphasis"/>), or null.</param>
    /// <param name="state">The block-comment state carried between the lines of one file, or null for a line on its own.</param>
    /// <param name="links">
    /// What an identifier opens, or null for none (docs/web/FINISH.md, Step 3c): asked for every
    /// plain word — a name the tokenizer did not class as a keyword, a type or a comment — and
    /// answered with the target the host understands (a repository path on the web, a guid on the
    /// desktop), carried as <c>data-link</c> like a member table's type link. Only words: a name
    /// inside a comment or a string is prose, and linking it would make a remark read as code.
    /// </param>
    public static int AppendLine(
        StringBuilder html,
        string? text,
        SyntaxLanguage language,
        IReadOnlyList<TextSpan>? emphasis = null,
        Func<string, string?>? links = null,
        SyntaxTokenizer.TokenState? state = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(language);

        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var hidden = 0;
        // With a state the caller is drawing a whole file in order, and a block comment's body
        // stays a comment on its second line (no link, no colour of code); without one each line
        // stands alone, as a diff hunk must.
        foreach (var token in SyntaxTokenizer.Tokenize(text, language, state))
        {
            // A word after a dot is a member of whatever precedes it — pump.Start names pump's
            // method, GVL.Running the list's variable — never the object of that name. The index
            // keys methods and properties by their bare name, so without this every instance call
            // linked to the first object called Start in path order (found in review, 2026-10-24).
            var link = links is not null && token.Kind == TokenKind.Plain && IsWord(text, token.Start, token.Length) && !IsMember(text, token.Start)
                ? links(text.Substring(token.Start, token.Length))
                : null;
            if (link is not null)
            {
                html.Append("<span class=\"cd-t-link\" data-link=\"").Append(A(link)).Append("\" title=\"Open ").Append(A(link)).Append("\">");
            }

            hidden += AppendToken(html, text, token.Start, token.Length, token.Kind, emphasis);
            if (link is not null)
            {
                html.Append("</span>");
            }
        }

        return hidden;
    }

    /// <summary>
    /// Whether the word at <paramref name="start"/> follows a dot: a member access, which no
    /// object name is. Shared with the "used by" search (<c>ObjectLinks.NamesReferenced</c>), so
    /// what one side links the other side counts, and nothing else.
    /// </summary>
    public static bool IsMember(string text, int start)
    {
        ArgumentNullException.ThrowIfNull(text);
        return start > 0 && text[start - 1] == '.';
    }

    /// <summary>A plain token that is one identifier: letters, digits and underscores, starting with a letter or underscore.</summary>
    private static bool IsWord(string text, int start, int length)
    {
        if (length == 0 || !(char.IsLetter(text[start]) || text[start] == '_'))
        {
            return false;
        }

        for (var i = start + 1; i < start + length; i++)
        {
            if (!(char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            {
                return false;
            }
        }

        return true;
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
            .Append(hidden == 1 ? " hidden or look-alike character" : " hidden or look-alike characters")
            .Append(" that can make code read differently from how it compiles. Each is marked: a U+ marker for a hidden one, a wavy underline for a name mixing scripts.</p>\n");
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

    /// <summary>
    /// Text from a file for an attribute (a tooltip, a <c>data-path</c>): escaped, and each hidden
    /// character spelled as <c>U+XXXX</c>, since an attribute cannot hold a marker element.
    /// </summary>
    /// <remarks>
    /// The rule for elements holds here too: a hidden character from a file is never handed to
    /// the browser as itself, even where it would only show on hover or reach a script.
    /// </remarks>
    public static string A(string? text) => E(Spelled(text));

    /// <summary>
    /// Text with each hidden character spelled as <c>U+XXXX</c> and nothing else changed: for a
    /// name or message shown as plain text by something that escapes it itself (the web page's
    /// tree, titles, commit messages).
    /// </summary>
    /// <remarks>
    /// Security rule 9 was applied to file contents only. Git allows a right-to-left override in
    /// a file or branch name, and a commit message is anyone's text, so a tree row could read as
    /// one name and be another (found in review, 2026-10-12). One spelling for both uses, so the
    /// list of hidden characters stays <see cref="IsHidden"/>'s alone.
    /// </remarks>
    public static string Spelled(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Any(IsHidden))
        {
            return text ?? "";
        }

        var spelled = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (IsHidden(c))
            {
                spelled.Append("U+").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                spelled.Append(c);
            }
        }

        return spelled.ToString();
    }

    /// <summary>
    /// The scripts a name mixes, as "Latin and Cyrillic", or null when it uses one (or none of
    /// the three this checks).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Look-alike letters</b> (docs/web/PLAN.md, security rule 9). <c>Compressor</c> spelled with
    /// a Cyrillic first letter (U+0421) reads as <c>Compressor</c> and is a different variable. A full
    /// confusables table would flag a lot of honest text; a <i>name</i> that mixes Latin with
    /// Cyrillic or Greek is the attack's practical form and almost never honest, so that is what
    /// is marked. Only identifiers are checked, never comments or strings, where a sentence in
    /// Russian beside an English word is ordinary.
    /// </para>
    /// <para>Written as code points, for <see cref="IsHidden"/>'s reason.</para>
    /// </remarks>
    public static string? MixedScripts(string text, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool latin = false, greek = false, cyrillic = false;
        for (var i = start; i < start + length; i++)
        {
            switch ((int)text[i])
            {
                case (>= 0x41 and <= 0x5A) or (>= 0x61 and <= 0x7A) or (>= 0xC0 and <= 0x24F and not 0xD7 and not 0xF7):
                    latin = true;
                    break;
                case >= 0x370 and <= 0x3FF:
                    greek = true;
                    break;
                case >= 0x400 and <= 0x52F:
                    cyrillic = true;
                    break;
            }
        }

        var scripts = new[] { latin ? "Latin" : null, greek ? "Greek" : null, cyrillic ? "Cyrillic" : null }.OfType<string>().ToList();
        return scripts.Count > 1 ? string.Join(" and ", scripts) : null;
    }

    private static int AppendToken(
        StringBuilder html, string text, int start, int length, TokenKind kind, IReadOnlyList<TextSpan>? emphasis)
    {
        if (length == 0)
        {
            return 0;
        }

        var mixed = kind is TokenKind.Plain or TokenKind.Keyword or TokenKind.Type && (char.IsLetter(text[start]) || text[start] == '_')
            ? MixedScripts(text, start, length)
            : null;
        if (mixed is not null)
        {
            html.Append("<span class=\"cd-confusable\" data-scripts=\"").Append(E(mixed))
                .Append("\" title=\"This name mixes ").Append(E(mixed)).Append(" letters, which can look identical\">");
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

        if (mixed is not null)
        {
            html.Append("</span>");
            hidden++;
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
