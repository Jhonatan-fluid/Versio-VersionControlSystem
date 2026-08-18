namespace Versio.Core.Diff;

/// <summary>A character range within one diff line's text.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// Marks, within a changed pair of lines, the part that actually changed.
/// </summary>
/// <remarks>
/// <para>
/// The diffs this application shows are generated XML and Structured Text, where a long line
/// routinely differs by one identifier or one attribute value — the case the deferral note
/// named when it argued this feature had the strongest claim. A whole-row tint answers
/// "this line changed"; the emphasis answers "here", which on a 400-character native line is
/// the difference between reading and hunting.
/// </para>
/// <para>
/// <b>Common prefix and suffix, trimmed at word boundaries — deliberately not an LCS.</b>
/// A word-level LCS is quadratic in line length, and the lines this most helps on are the
/// longest ones in the corpus; a prefix/suffix trim is linear, and for "one identifier
/// changed in the middle" the two produce the same answer. What LCS would add is emphasis on
/// lines with several separated edits, at the price of an algorithm whose worst case sits
/// exactly on the biggest inputs. Trimmed at token boundaries rather than characters so that
/// <c>Counter1</c> → <c>Counter2</c> emphasises the whole identifier, not the one digit —
/// half a name is not a shorter version of it.
/// </para>
/// <para>
/// <b>A pair with nothing in common gets no emphasis.</b> Emphasising the entire line would
/// paint every character of both rows and say less than the row tint already does.
/// </para>
/// </remarks>
public static class IntralineEmphasis
{
    /// <summary>
    /// The document with <see cref="DiffLine.Emphasis"/> filled in on changed pairs.
    /// </summary>
    /// <remarks>
    /// Pairing follows the unified format's own shape: within one change run, git prints
    /// every removed line then every added line, and the i-th of each block are the same
    /// line edited. An unbalanced block leaves its unmatched tail unemphasised — a line with
    /// no counterpart has nothing to be compared against.
    /// </remarks>
    public static DiffDocument Annotate(DiffDocument document)
    {
        if (document.IsEmpty || document.IsBinary)
        {
            return document;
        }

        var lines = document.Lines.ToArray();

        for (var i = 0; i < lines.Length;)
        {
            if (lines[i].Kind != DiffLineKind.Removed)
            {
                i++;
                continue;
            }

            var removedStart = i;
            while (i < lines.Length && lines[i].Kind == DiffLineKind.Removed)
            {
                i++;
            }

            var addedStart = i;
            while (i < lines.Length && lines[i].Kind == DiffLineKind.Added)
            {
                i++;
            }

            var pairs = Math.Min(i - addedStart, addedStart - removedStart);
            for (var p = 0; p < pairs; p++)
            {
                var old = lines[removedStart + p];
                var @new = lines[addedStart + p];
                var (oldSpan, newSpan) = Compare(old.Text, @new.Text);

                if (oldSpan is { } o)
                {
                    lines[removedStart + p] = old with { Emphasis = [o] };
                }

                if (newSpan is { } n)
                {
                    lines[addedStart + p] = @new with { Emphasis = [n] };
                }
            }
        }

        return document with { Lines = lines };
    }

    /// <summary>The changed middle of each side, or null when a side has none to mark.</summary>
    public static (TextSpan? Old, TextSpan? New) Compare(string oldText, string newText)
    {
        if (oldText.Length == 0 || newText.Length == 0 || oldText == newText)
        {
            return (null, null);
        }

        var limit = Math.Min(oldText.Length, newText.Length);

        var prefix = 0;
        while (prefix < limit && oldText[prefix] == newText[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < limit - prefix
               && oldText[^(suffix + 1)] == newText[^(suffix + 1)])
        {
            suffix++;
        }

        // Nothing in common: the row tint already says everything this could.
        if (prefix == 0 && suffix == 0)
        {
            return (null, null);
        }

        // Widen to token boundaries. "Counter1" → "Counter2" shares "Counter" as a prefix,
        // and emphasising only the digit invites reading half an identifier as the change.
        prefix = BackToBoundary(oldText, prefix);
        suffix = ForwardToBoundary(oldText, newText, prefix, suffix);

        return (Middle(oldText, prefix, suffix), Middle(newText, prefix, suffix));
    }

    private static TextSpan? Middle(string text, int prefix, int suffix)
    {
        var length = text.Length - prefix - suffix;
        return length <= 0 ? null : new TextSpan(prefix, length);
    }

    /// <summary>Move a prefix split leftwards until it sits between tokens.</summary>
    private static int BackToBoundary(string text, int prefix)
    {
        while (prefix > 0 && prefix < text.Length
               && IsWord(text[prefix - 1]) && IsWord(text[prefix]))
        {
            prefix--;
        }

        return prefix;
    }

    /// <summary>Move a suffix split rightwards until it sits between tokens, on both sides.</summary>
    private static int ForwardToBoundary(string oldText, string newText, int prefix, int suffix)
    {
        bool Splits(string text) =>
            text.Length - suffix <= prefix
            || suffix == 0
            || !IsWord(text[text.Length - suffix - 1])
            || !IsWord(text[text.Length - suffix]);

        while (suffix > 0 && (!Splits(oldText) || !Splits(newText)))
        {
            suffix--;
        }

        return suffix;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
}
