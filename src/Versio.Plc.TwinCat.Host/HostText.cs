namespace Versio.Plc.TwinCat.Host;

/// <summary>
/// Packs TwinCAT declaration + implementation the same way the IEC codec does.
/// </summary>
/// <remarks>
/// The host cannot reference Versio.Core. The delimiter string is therefore duplicated,
/// not imported — a drift here would split a file in the wrong place on import. Keep
/// it identical to <c>StTextCodec.ImplementationDelimiter</c>.
/// </remarks>
internal static class HostText
{
    public const string ImplementationDelimiter = "// --- BEGIN IMPLEMENTATION ---";

    private const string EscapeMarker = "//!VERSIO-ESC!";

    private static readonly char[] TrimChars =
    [
        ' ', '\t', '\n', '\r', '\f', '\v', '\u00a0',
        '\u001c', '\u001d', '\u001e', '\u001f',
    ];

    public static string Join(string? declaration, string? implementation)
    {
        var decl = Escape(Normalize(declaration));
        var impl = Escape(Normalize(implementation));
        if (string.IsNullOrEmpty(impl))
        {
            return decl + "\n";
        }

        return decl + "\n\n" + ImplementationDelimiter + "\n\n" + impl + "\n";
    }

    public static (string Declaration, string Implementation) Split(string? content)
    {
        var text = (content ?? string.Empty).Replace("\r\n", "\n");
        var idx = text.IndexOf(ImplementationDelimiter, StringComparison.Ordinal);
        if (idx < 0)
        {
            return (Unescape(text.Trim(TrimChars)), string.Empty);
        }

        var decl = text[..idx];
        var impl = text[(idx + ImplementationDelimiter.Length)..];
        return (Unescape(decl.Trim(TrimChars)), Unescape(impl.Trim(TrimChars)));
    }

    private static string Escape(string s) =>
        s.Replace(EscapeMarker, EscapeMarker + "E", StringComparison.Ordinal)
         .Replace(ImplementationDelimiter, EscapeMarker + "D", StringComparison.Ordinal);

    private static string Unescape(string s) =>
        s.Replace(EscapeMarker + "D", ImplementationDelimiter, StringComparison.Ordinal)
         .Replace(EscapeMarker + "E", EscapeMarker, StringComparison.Ordinal);

    private static string Normalize(string? s) =>
        (s ?? string.Empty).Replace("\r\n", "\n").Trim(TrimChars);
}
