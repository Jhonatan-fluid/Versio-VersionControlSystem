using System.Text.RegularExpressions;

namespace Versio.Plc.TiaPortal;

/// <summary>
/// The object's name as SCL states it, not as the file is named.
/// </summary>
/// <remarks>
/// <para>
/// G3 on this vendor: the importer used to name a created object from its file stem.
/// SCL headers quote names (<c>FUNCTION_BLOCK "Motor"</c>) and the IEC declaration
/// parser does not, so stretching that parser across this syntax would silently
/// create the wrong object.
/// </para>
/// </remarks>
public static class TiaPortalSclHeader
{
    private static readonly Regex Header = new(
        @"^\s*(FUNCTION_BLOCK|FUNCTION|ORGANIZATION_BLOCK|DATA_BLOCK|TYPE)\s+""?([^\s"";:]+)""?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The declared name, or null when the text is not an SCL header.</summary>
    public static string? ObjectName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("(*", StringComparison.Ordinal))
            {
                continue;
            }

            var match = Header.Match(trimmed);
            return match.Success ? match.Groups[2].Value : null;
        }

        return null;
    }

    /// <summary>The vendor type token for a header, or null.</summary>
    public static string? ObjectType(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var match = Header.Match(trimmed);
            if (!match.Success)
            {
                return null;
            }

            return match.Groups[1].Value.ToUpperInvariant() switch
            {
                "FUNCTION_BLOCK" => "FB",
                "FUNCTION" => "FC",
                "ORGANIZATION_BLOCK" => "OB",
                "DATA_BLOCK" => "DB",
                "TYPE" => "UDT",
                _ => null,
            };
        }

        return null;
    }
}
