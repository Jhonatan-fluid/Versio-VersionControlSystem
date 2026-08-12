using System.Security.Cryptography;
using System.Text;

namespace Versio.Core.Serialization;

/// <summary>
/// Packs a CODESYS object's declaration and implementation into one reviewable .st file
/// and takes it apart again.
/// </summary>
/// <remarks>
/// This is the highest-consequence pure code in the product: a bug here silently corrupts
/// PLC source on import. The round-trip invariant <c>Split(Join(d, i)) == (d, i)</c> is
/// asserted by property-based tests over adversarial input.
/// <para>Must stay in lockstep with <c>versio_text.py</c>.</para>
/// </remarks>
public static class StTextCodec
{
    /// <summary>Separator between declaration and implementation. A legal ST comment, so
    /// the file stays valid and readable in any editor.</summary>
    public const string ImplementationDelimiter = "// --- BEGIN IMPLEMENTATION ---";

    /// <summary>
    /// Escape marker used when the delimiter appears inside the content itself.
    /// </summary>
    /// <remarks>
    /// Found by a randomised round-trip test: a declaration containing the delimiter made
    /// <see cref="Split"/> cut in the wrong place and silently move code from the
    /// declaration into the implementation. Rare, but silent corruption of PLC source is
    /// not something to leave to luck. codescribe has the same shape of bug — its
    /// unpacking <c>split()</c> raises <c>ValueError</c> on a second delimiter.
    /// <para>
    /// Normal files never contain either marker, so both replacements are no-ops and the
    /// on-disk text is untouched.
    /// </para>
    /// </remarks>
    private const string EscapeMarker = "//!VERSIO-ESC!";

    /// <summary>Combine declaration and implementation into a single .st payload.</summary>
    /// <remarks>
    /// The trim-and-single-trailing-newline normalisation is what makes
    /// export → import → export byte-identical rather than accumulating whitespace on
    /// every cycle.
    /// </remarks>
    public static string Join(string? declaration, string? implementation)
    {
        var decl = Escape(Normalize(declaration));
        var impl = Escape(Normalize(implementation));
        return decl + "\n\n" + ImplementationDelimiter + "\n\n" + impl + "\n";
    }

    /// <summary>Inverse of <see cref="Join"/>.</summary>
    /// <remarks>
    /// Splits on the FIRST unescaped delimiter. Any delimiter that came from the content
    /// was escaped by <see cref="Join"/>, so the first one found is always the real
    /// separator.
    /// </remarks>
    public static (string Declaration, string Implementation) Split(string? content)
    {
        var text = (content ?? string.Empty).Replace("\r\n", "\n");
        var idx = text.IndexOf(ImplementationDelimiter, StringComparison.Ordinal);
        if (idx < 0)
        {
            // No delimiter: declaration-only file (DUT, GVL).
            return (Unescape(text.Trim()), string.Empty);
        }

        var decl = text[..idx];
        var impl = text[(idx + ImplementationDelimiter.Length)..];
        return (Unescape(decl.Trim()), Unescape(impl.Trim()));
    }

    // Order matters: escape the marker itself first, so decoding can distinguish a
    // literal marker in the source from one we added.
    private static string Escape(string s) =>
        s.Replace(EscapeMarker, EscapeMarker + "E", StringComparison.Ordinal)
         .Replace(ImplementationDelimiter, EscapeMarker + "D", StringComparison.Ordinal);

    private static string Unescape(string s) =>
        s.Replace(EscapeMarker + "D", ImplementationDelimiter, StringComparison.Ordinal)
         .Replace(EscapeMarker + "E", EscapeMarker, StringComparison.Ordinal);

    /// <summary>For declaration-only artifacts (DUTs, GVL declarations).</summary>
    public static string DeclarationOnly(string? declaration) => Normalize(declaration) + "\n";

    private static string Normalize(string? s) =>
        (s ?? string.Empty).Replace("\r\n", "\n").Trim();

    /// <summary>Stable content hash: SHA-256 over UTF-8 bytes, no BOM.</summary>
    public static string ContentHash(string text)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>Stable content hash over raw bytes.</summary>
    public static string ContentHash(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
