using System.Security.Cryptography;
using System.Text;
using Versio.Core.Text;

namespace Versio.Core.Diff;

/// <summary>
/// A bounded fingerprint of a text's distinct lines, and the estimate of how alike two of
/// them are.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fallback for when git cannot help.</b> Pairing a renamed-and-edited file with the
/// one it became needs the departed file's content, and the mirror no longer has it. HEAD has
/// it when the mirror was committed — and an uncommitted mirror, or one whose file was never
/// tracked, has no old copy anywhere. This is what the manifest carries instead, recorded at
/// export time while the content still exists.
/// </para>
/// <para>
/// <b>Bottom-k over 32-bit line hashes.</b> Two sketches estimate the Jaccard similarity of
/// the two line <i>sets</i>: coarser than <see cref="TextSimilarity"/>, which sees the texts
/// themselves and counts repeats. A sketch cannot see line order or multiplicity. That is the
/// price of not keeping the text, and it is exactly why git is preferred whenever HEAD has the
/// file — an estimate is a fallback, not an equal.
/// </para>
/// <para>
/// <b>The size is about the manifest, not about accuracy.</b> Recording every line's hash
/// would estimate far better and grow with the file: a 400-line POU would add 3 KB to a file
/// people read in pull requests. A sketch is 128 characters whatever the POU does.
/// </para>
/// <para>
/// The mirror of <c>line_sketch</c> / <c>sketch_similarity</c> in <c>versio_text.py</c>,
/// compared by output in <c>PythonFileNameParityTests</c>. The exporter writes the sketch and
/// this reads it, so a disagreement between the two would not fail — it would silently
/// estimate every similarity as zero and quietly stop detecting renames.
/// </para>
/// </remarks>
public static class LineSketch
{
    /// <summary>How many line hashes a sketch keeps — the k smallest.</summary>
    public const int Size = 16;

    /// <summary>The sketch of a text, as the comma-separated form the manifest stores.</summary>
    public static string Of(string? text, int size = Size)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            // PayloadWhitespace, not Trim(): python's line_sketch strips four ASCII
            // separators .NET does not call whitespace, and a line whose edge is trimmed
            // differently hashes differently - so the two sketches would share no digest
            // for that line and the similarity would be under-reported, losing the rename
            // this sketch exists to find. See PayloadWhitespace.
            var line = PayloadWhitespace.Trim(raw);
            if (line.Length == 0)
            {
                continue;
            }

            // First 4 bytes of SHA-256, lowercase hex. SHA-256 because both languages already
            // have it and agree byte for byte; a runtime's own string hash does not, and
            // IronPython's differs from CPython's inside one family.
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(line));
            seen.Add(Convert.ToHexString(digest.AsSpan(0, 4)).ToLowerInvariant());
        }

        return string.Join(",", seen.Take(size));
    }

    /// <summary>
    /// Estimated Jaccard similarity of two sketches, 0 to 1.
    /// </summary>
    /// <remarks>
    /// Both empty scores 0, not 1 — the same decision <see cref="TextSimilarity.Of"/> states:
    /// an empty file resembles every other empty file, so 1 would let a threshold pair any two
    /// of them, and the caller is asking whether there is <i>evidence</i> two files are the
    /// same object.
    /// </remarks>
    public static double Similarity(string? left, string? right, int size = Size)
    {
        var a = Split(left);
        var b = Split(right);

        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        // Bottom-k estimator: compared over the k smallest of the union, because past that
        // neither sketch is a complete record and their disagreement carries no information.
        var union = a.Union(b, StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .Take(size)
            .ToList();

        if (union.Count == 0)
        {
            return 0;
        }

        return (double)union.Count(v => a.Contains(v) && b.Contains(v)) / union.Count;
    }

    private static HashSet<string> Split(string? sketch) =>
        string.IsNullOrEmpty(sketch)
            ? []
            : [.. sketch.Split(',').Where(v => v.Length > 0)];
}
