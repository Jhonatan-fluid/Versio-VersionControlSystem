namespace Versio.Core.Diff;

/// <summary>
/// How alike two texts are, as a fraction from 0 to 1.
/// </summary>
/// <remarks>
/// <para>
/// <b>For deciding whether a departed file and an arrived one are the same object.</b> A rename
/// that also edits the body matches no hash — that is what a hash is for — so the only thing
/// left is "are these mostly the same lines". Used by the import planner's last matching pass,
/// after exact content and after the implementation hash have both failed.
/// </para>
/// <para>
/// <b>Multiset of lines, not a diff.</b> A real longest-common-subsequence diff would be more
/// accurate and is not worth it here: the answer feeds a threshold, the inputs are source files
/// of a few hundred lines, and an LCS on every candidate pair is quadratic work for a decision
/// that a line multiset already makes correctly. Counting lines with their multiplicity rather
/// than as a set matters for ST specifically — <c>END_IF</c> and <c>END_VAR</c> repeat, and a
/// plain set would call two very different files similar because both contain the same handful
/// of distinct closing keywords.
/// </para>
/// <para>
/// <b>Blank lines and surrounding whitespace are dropped.</b> Re-indenting a block is not
/// evidence that a file became a different object, and a file whose only change is spacing
/// should score 1.
/// </para>
/// <para>
/// Vendor-neutral on purpose: this is text, not Structured Text. It sits in <c>Diff/</c> beside
/// the other things that compare content, and a second vendor gets it for free.
/// </para>
/// </remarks>
public static class TextSimilarity
{
    /// <summary>
    /// The fraction of lines the two texts share, 0 when nothing matches and 1 when the
    /// multisets are equal.
    /// </summary>
    /// <remarks>
    /// <b>Two empty texts score 0, not 1.</b> Mathematically they are identical and the answer
    /// is useless: an empty file is similar to every other empty file, so returning 1 would let
    /// a threshold pair any two of them. The caller wants "is there evidence these are the same
    /// object", and there is none here. Both-empty is the one case where the honest answer and
    /// the arithmetic disagree, so it is stated rather than left to fall out.
    /// </remarks>
    public static double Of(string? left, string? right)
    {
        var a = Lines(left);
        var b = Lines(right);

        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        // Multiset intersection: each line counts as many times as both sides have it.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in a)
        {
            counts[line] = counts.GetValueOrDefault(line) + 1;
        }

        var shared = 0;
        foreach (var line in b)
        {
            if (counts.TryGetValue(line, out var left_) && left_ > 0)
            {
                counts[line] = left_ - 1;
                shared++;
            }
        }

        // Over the LARGER side, so appending a hundred lines to a file lowers the score. Over
        // the smaller side, a file that is a prefix of another would score 1 and a rename would
        // pair an object with something that merely contains it.
        return (double)shared / Math.Max(a.Count, b.Count);
    }

    private static List<string> Lines(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : [.. text.Replace("\r\n", "\n").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)];
}
