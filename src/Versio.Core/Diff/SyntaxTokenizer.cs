namespace Versio.Core.Diff;

/// <summary>
/// Splits one line into coloured spans.
/// </summary>
/// <remarks>
/// <para>
/// Line-at-a-time and stateless by default. A diff shows fragments of a file, so there is no
/// reliable way to know whether line 213 sits inside a block comment that opened in a hunk
/// git never printed. Attempting to carry state across hunks produces confident, wrong
/// colouring; treating each line independently is occasionally incomplete but never
/// misleading.
/// </para>
/// <para>
/// <b>A caller that sees the whole file carries the state itself</b> (<see cref="TokenState"/>):
/// the second line of a <c>(* … *)</c> block is a comment, not code, and once an identifier in
/// code became a link (docs/web/FINISH.md, Step 3c) the difference mattered — a method named in
/// commented-out code was linked, and counted as a use, on a real fixture (review, 2026-10-24).
/// The state is reset wherever lines are hidden (a fold), which is the stateless answer again.
/// </para>
/// <para>
/// <b>Highlighting is decoration; the diff is the information.</b> The spans always cover
/// the line exactly once with no gaps or overlaps, so a tokenizer bug can never hide or
/// duplicate content — asserted by tests over every supported language.
/// </para>
/// </remarks>
public static class SyntaxTokenizer
{
    /// <summary>
    /// What one line leaves for the next: the block comment it ended inside, if any. One per
    /// file side; <see cref="Reset"/> where lines are hidden.
    /// </summary>
    public sealed class TokenState
    {
        /// <summary>The open comment's delimiters while inside one; null in code.</summary>
        public (string Open, string Close)? OpenComment { get; internal set; }

        /// <summary>Whether the next line starts inside a block comment.</summary>
        public bool InComment => OpenComment is not null;

        /// <summary>Back to "in code": what a line after a fold is taken to be.</summary>
        public void Reset() => OpenComment = null;
    }

    public static IReadOnlyList<TokenSpan> Tokenize(string? line, SyntaxLanguage language) => Tokenize(line, language, null);

    /// <summary>
    /// The spans of <paramref name="line"/>, continuing a block comment <paramref name="state"/>
    /// says the previous line left open, and recording one this line leaves open.
    /// </summary>
    public static IReadOnlyList<TokenSpan> Tokenize(string? line, SyntaxLanguage language, TokenState? state)
    {
        if (string.IsNullOrEmpty(line))
        {
            return [];
        }

        if (ReferenceEquals(language, SyntaxLanguage.PlainText))
        {
            return [new TokenSpan(0, line.Length, TokenKind.Plain)];
        }

        var spans = new List<TokenSpan>();
        var i = 0;

        // Inside a block comment the previous line left open: a comment up to its close, or the
        // whole line when it does not close here (and the next line starts inside it too). A line
        // inside a comment is never a directive.
        if (state?.OpenComment is { } open)
        {
            var end = line.IndexOf(open.Close, StringComparison.Ordinal);
            if (end < 0)
            {
                return [new TokenSpan(0, line.Length, TokenKind.Comment)];
            }

            i = end + open.Close.Length;
            spans.Add(new TokenSpan(0, i, TokenKind.Comment));
            state.OpenComment = null;
            if (i >= line.Length)
            {
                return spans;
            }
        }

        // A directive prefix claims the whole line: a pragma, a preprocessor line or a
        // markdown heading is one unit, not a sequence of keywords.
        var trimmedStart = i;
        while (trimmedStart < line.Length && char.IsWhiteSpace(line[trimmedStart]))
        {
            trimmedStart++;
        }

        foreach (var prefix in language.DirectivePrefixes)
        {
            if (trimmedStart < line.Length &&
                line.AsSpan(trimmedStart).StartsWith(prefix, StringComparison.Ordinal))
            {
                spans.Add(new TokenSpan(i, line.Length - i, TokenKind.Directive));
                // The directive claims the line's colour, but a block comment opened after it
                // ({attribute 'x'} (* why: …) still runs on: a caller carrying state is told, or
                // the next line was code again — the defect of 2026-10-24 through the other door.
                if (state is not null)
                {
                    state.OpenComment = TrailingOpenComment(line, trimmedStart, language);
                }

                return spans;
            }
        }

        while (i < line.Length)
        {
            // --- comments ---------------------------------------------------------
            var comment = MatchLineComment(line, i, language);
            if (comment)
            {
                spans.Add(new TokenSpan(i, line.Length - i, TokenKind.Comment));
                break;
            }

            var block = MatchBlockComment(line, i, language);
            if (block.Matched)
            {
                spans.Add(new TokenSpan(i, block.Length, TokenKind.Comment));
                i += block.Length;
                if (state is not null && block.Unclosed is { } unclosed)
                {
                    state.OpenComment = unclosed;
                }

                continue;
            }

            // --- strings ----------------------------------------------------------
            if (language.StringDelimiters.Contains(line[i]))
            {
                var length = ReadString(line, i, line[i]);
                spans.Add(new TokenSpan(i, length, TokenKind.String));
                i += length;
                continue;
            }

            // --- numbers ----------------------------------------------------------
            if (language.HighlightNumbers && char.IsDigit(line[i]) && !IsWordChar(Previous(line, i)))
            {
                var length = ReadNumber(line, i);
                spans.Add(new TokenSpan(i, length, TokenKind.Number));
                i += length;
                continue;
            }

            // --- words ------------------------------------------------------------
            if (IsWordStart(line[i]))
            {
                var start = i;
                while (i < line.Length && IsWordChar(line[i]))
                {
                    i++;
                }

                var word = line[start..i];
                var kind = Classify(word, language);
                spans.Add(new TokenSpan(start, i - start, kind));
                continue;
            }

            // --- everything else --------------------------------------------------
            var plainStart = i;
            while (i < line.Length &&
                   !IsWordStart(line[i]) &&
                   !language.StringDelimiters.Contains(line[i]) &&
                   !(language.HighlightNumbers && char.IsDigit(line[i])) &&
                   !MatchLineComment(line, i, language) &&
                   !MatchBlockComment(line, i, language).Matched)
            {
                i++;
            }

            if (i == plainStart)
            {
                i++;    // guarantee progress; a stuck tokenizer would hang the UI
            }

            spans.Add(new TokenSpan(plainStart, i - plainStart, TokenKind.Plain));
        }

        return spans;
    }

    private static TokenKind Classify(string word, SyntaxLanguage language)
    {
        if (language.Keywords.Contains(word))
        {
            return TokenKind.Keyword;
        }

        return language.Types.Contains(word) ? TokenKind.Type : TokenKind.Plain;
    }

    private static bool MatchLineComment(string line, int i, SyntaxLanguage language)
    {
        foreach (var prefix in language.LineComments)
        {
            if (line.AsSpan(i).StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The block comment still open at the end of <paramref name="line"/> from <paramref name="from"/>,
    /// walking strings and comments as the tokenizer would, for a line whose colour something else
    /// claimed (a directive). Null when the line ends in code.
    /// </summary>
    private static (string Open, string Close)? TrailingOpenComment(string line, int from, SyntaxLanguage language)
    {
        var i = from;
        while (i < line.Length)
        {
            if (MatchLineComment(line, i, language))
            {
                return null;
            }

            var block = MatchBlockComment(line, i, language);
            if (block.Matched)
            {
                if (block.Unclosed is { } unclosed)
                {
                    return unclosed;
                }

                i += block.Length;
                continue;
            }

            if (language.StringDelimiters.Contains(line[i]))
            {
                i += ReadString(line, i, line[i]);
                continue;
            }

            i++;
        }

        return null;
    }

    private static (bool Matched, int Length, (string Open, string Close)? Unclosed) MatchBlockComment(
        string line, int i, SyntaxLanguage language)
    {
        foreach (var (open, close) in language.BlockComments)
        {
            if (!line.AsSpan(i).StartsWith(open, StringComparison.Ordinal))
            {
                continue;
            }

            var end = line.IndexOf(close, i + open.Length, StringComparison.Ordinal);

            // An unterminated block runs to end of line. Correct for the fragment we were
            // given, and it cannot swallow anything that is not on this line; a caller carrying
            // state is told which comment stays open.
            return end < 0 ? (true, line.Length - i, (open, close)) : (true, end + close.Length - i, null);
        }

        return (false, 0, null);
    }

    private static int ReadString(string line, int start, char delimiter)
    {
        var i = start + 1;
        while (i < line.Length)
        {
            if (line[i] == '\\' && i + 1 < line.Length)
            {
                i += 2;
                continue;
            }

            if (line[i] == delimiter)
            {
                return i - start + 1;
            }

            i++;
        }

        return line.Length - start;   // unterminated: colour to end of line
    }

    private static int ReadNumber(string line, int start)
    {
        var i = start;

        // C-style radix prefix: 0xFF, 0b1010.
        if (line[i] == '0' && i + 1 < line.Length && line[i + 1] is 'x' or 'X' or 'b' or 'B')
        {
            i += 2;
            while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
            {
                i++;
            }

            return i - start;
        }

        while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '_'))
        {
            i++;
        }

        // IEC 61131-3 writes the base FIRST: 16#FF, 2#1010, 8#777. The '#' therefore sits
        // after the leading digits rather than at position 1 like 0xFF, which is why a
        // C-style prefix check alone stops at "16" and leaves "#FF" behind.
        if (i < line.Length && line[i] == '#')
        {
            i++;
            while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
            {
                i++;
            }

            return i - start;
        }

        // Decimal part and exponent.
        while (i < line.Length &&
               (char.IsDigit(line[i]) || line[i] == '.' || line[i] == '_' ||
                line[i] is 'e' or 'E' ||
                ((line[i] == '+' || line[i] == '-') && i > start && line[i - 1] is 'e' or 'E')))
        {
            i++;
        }

        // Type suffixes: 10L, 3.5f, 100u.
        while (i < line.Length && char.IsLetter(line[i]))
        {
            i++;
        }

        return i - start;
    }

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static char Previous(string line, int i) => i > 0 ? line[i - 1] : ' ';
}
