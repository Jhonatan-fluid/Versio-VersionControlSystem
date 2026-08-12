namespace Versio.Core.Diff;

/// <summary>
/// Splits one line into coloured spans.
/// </summary>
/// <remarks>
/// <para>
/// Line-at-a-time and stateless by design. A diff shows fragments of a file, so there is no
/// reliable way to know whether line 213 sits inside a block comment that opened in a hunk
/// git never printed. Attempting to carry state across hunks produces confident, wrong
/// colouring; treating each line independently is occasionally incomplete but never
/// misleading.
/// </para>
/// <para>
/// <b>Highlighting is decoration; the diff is the information.</b> The spans always cover
/// the line exactly once with no gaps or overlaps, so a tokenizer bug can never hide or
/// duplicate content — asserted by tests over every supported language.
/// </para>
/// </remarks>
public static class SyntaxTokenizer
{
    public static IReadOnlyList<TokenSpan> Tokenize(string? line, SyntaxLanguage language)
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

        // A directive prefix claims the whole line: a pragma, a preprocessor line or a
        // markdown heading is one unit, not a sequence of keywords.
        var trimmedStart = 0;
        while (trimmedStart < line.Length && char.IsWhiteSpace(line[trimmedStart]))
        {
            trimmedStart++;
        }

        foreach (var prefix in language.DirectivePrefixes)
        {
            if (trimmedStart < line.Length &&
                line.AsSpan(trimmedStart).StartsWith(prefix, StringComparison.Ordinal))
            {
                return [new TokenSpan(0, line.Length, TokenKind.Directive)];
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

    private static (bool Matched, int Length) MatchBlockComment(
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
            // given, and it cannot swallow anything that is not on this line.
            return (true, end < 0 ? line.Length - i : end + close.Length - i);
        }

        return (false, 0);
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
