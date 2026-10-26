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

    /// <summary>What writing this implementation onto the live object means.</summary>
    public enum ImplementationWrite
    {
        /// <summary>Nothing to write and nothing live: a DUT, or a body already empty.</summary>
        Leave,

        /// <summary>The file has a body; set it.</summary>
        Set,

        /// <summary>The file has no body and the live object still has one: clear it.</summary>
        Clear,
    }

    /// <summary>
    /// Whether an empty implementation in the file means "leave the object alone" or "take
    /// the body away".
    /// </summary>
    /// <remarks>
    /// The first version skipped the write whenever the file's implementation was empty,
    /// for the objects that have none (a DUT). A function block whose body the engineer had
    /// deleted in the mirror therefore kept its old logic in the project, the declaration
    /// check passed, and the import reported success with the previous code still running
    /// (the 2026-10-24 review's item 23). The live text is the only thing that tells the two
    /// cases apart, so it is read first: <c>null</c> here is the host's "no text" — the
    /// property is absent or empty — and nothing is written then either.
    /// </remarks>
    public static ImplementationWrite ImplementationWriteFor(string wanted, string? live) =>
        wanted.Length > 0 ? ImplementationWrite.Set
        : string.IsNullOrWhiteSpace(live) ? ImplementationWrite.Leave
        : ImplementationWrite.Clear;

    /// <summary>
    /// After the write: does the live text say what the file says? A non-empty body must
    /// contain its first code line; an empty one must have left nothing behind.
    /// </summary>
    public static bool ImplementationTook(string wanted, string? live)
    {
        var first = FirstNonComment(wanted);
        if (first.Length > 0)
        {
            return (live ?? string.Empty).Contains(first, StringComparison.Ordinal);
        }

        // A body of comments only, or none: either way nothing of the old code may remain
        // past what the file holds. Comments-only is checked by length rather than content
        // because the Automation Interface rewrites comment spacing on the way in.
        return wanted.Length > 0 || string.IsNullOrWhiteSpace(live);
    }

    /// <summary>The first line that is code rather than a comment or blank — what a
    /// read-back can be asked to contain.</summary>
    public static string FirstNonComment(string text)
    {
        foreach (var raw in text.Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)
                || line.StartsWith("(*", StringComparison.Ordinal))
            {
                continue;
            }

            return line;
        }

        return string.Empty;
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
