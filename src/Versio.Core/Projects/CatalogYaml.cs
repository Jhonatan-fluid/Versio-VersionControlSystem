namespace Versio.Core.Projects;

/// <summary>
/// The shared half of the hand-written YAML-subset readers.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LibraryCatalog"/>, <see cref="TaskCatalog"/> and <see cref="DeviceCatalog"/>
/// read documents written by the same emitter — <c>versio_yaml.py</c> — so the quoting, the
/// <c>key: value</c> split and the flag rule are one contract, not three. They lived inside
/// <see cref="LibraryCatalog"/> while it was the only reader; a second reader copying them
/// would mean the day the emitter's quoting changed, one reader would be fixed and the other
/// would corrupt names quietly.
/// </para>
/// <para>
/// What is deliberately NOT shared is the document walk itself: the formats nest
/// differently — libraries and devices carry nested mappings (<c>parameters</c>,
/// <c>slots</c>), tasks carry nested scalar lists (<c>pous</c>) — and a walk generic over
/// them would be harder to read than three short concrete ones.
/// </para>
/// </remarks>
internal static class CatalogYaml
{
    /// <summary>Split <c>key: value</c> at the first colon that is not inside quotes.</summary>
    /// <remarks>
    /// A colon inside a quoted <b>value</b> was always safe, because the split happened
    /// before the value was unquoted. A colon inside a quoted <b>key</b> was not: the
    /// first <c>IndexOf(':')</c> cut <c>"Mode: DC-Synchron": "1"</c> into key
    /// <c>"Mode</c>. That bound held while keys were IEC identifiers (library parameters).
    /// Device parameter names are free vendor text and the emitter quotes a colon, so the
    /// separator has to skip quoted spans. Unclosed quotes yield no split rather than a
    /// corrupted key.
    /// </remarks>
    internal static bool Split(string text, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var inQuotes = false;
        var escape = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (escape)
            {
                escape = false;
                continue;
            }

            if (inQuotes)
            {
                if (ch == '\\')
                {
                    escape = true;
                }
                else if (ch == '"')
                {
                    inQuotes = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inQuotes = true;
                continue;
            }

            if (ch == ':')
            {
                key = Unquote(text[..i].Trim());
                value = text[(i + 1)..].Trim();
                return key.Length > 0;
            }
        }

        return false;
    }

    internal static string Value(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var v) ? Unquote(v) : string.Empty;

    /// <summary>A flag is only true when it says <c>true</c>.</summary>
    /// <remarks>
    /// Absent must not read as true: every flag these files carry weakens a check —
    /// <c>optional</c> suppresses a missing-library error, <c>systemLibrary</c> sorts a row
    /// away, <c>watchdogEnabled</c> is a safety feature — so the failure of guessing would be
    /// quiet by construction.
    /// </remarks>
    internal static bool Flag(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var v) &&
        string.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Inverse of the emitter's quoting.
    /// </summary>
    /// <remarks>
    /// Only the escapes <c>versio_yaml.quote</c> can produce are recognised, and anything
    /// else is left as written. A reader that invented escapes the writer never emits would
    /// eventually corrupt a library name containing a backslash, and library names really do
    /// contain punctuation — <c>"Standard, * (System)"</c> is why quoting exists here at all.
    /// </remarks>
    internal static string Unquote(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
        {
            return text;
        }

        var body = text[1..^1];
        if (!body.Contains('\\'))
        {
            return body;
        }

        var sb = new System.Text.StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\' || i == body.Length - 1)
            {
                sb.Append(body[i]);
                continue;
            }

            var next = body[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'x' when i + 2 < body.Length &&
                              int.TryParse(body.Substring(i + 1, 2),
                                  System.Globalization.NumberStyles.HexNumber,
                                  System.Globalization.CultureInfo.InvariantCulture,
                                  out var code):
                    sb.Append((char)code);
                    i += 2;
                    break;
                default:
                    sb.Append('\\').Append(next);
                    break;
            }
        }

        return sb.ToString();
    }
}
