using System.Text;
using System.Text.RegularExpressions;
using Versio.Core.Plc.Codesys;

namespace Versio.Core.Serialization;

/// <summary>
/// Maps a CODESYS object to its file name and back.
/// </summary>
/// <remarks>
/// CODESYS object names are far freer than Windows file names: they may contain characters
/// Windows forbids, may collide with reserved device names, and a visualisation frequently
/// shares a name with a POU. Encoding is percent-based and therefore reversible, so two
/// distinct objects can never collapse onto one file.
/// <para>
/// The file name is a convenience for humans. The manifest's GUID is the source of truth
/// for identity (plan R12).
/// </para>
/// </remarks>
public static partial class PathMapper
{
    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex InvalidWindowsChars();

    [GeneratedRegex("%([0-9A-Fa-f]{2})")]
    private static partial Regex PercentEscape();

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Type-specific infix that keeps same-named objects of different types apart.
    /// A "Main" visualisation and a "Main" POU are a common real collision.</summary>
    private static readonly Dictionary<CodesysObjectType, string> Infix = new()
    {
        [CodesysObjectType.Visualisation] = ".vis",
        [CodesysObjectType.Gvl] = ".gvl",
        [CodesysObjectType.GvlPersistent] = ".gvl",
        [CodesysObjectType.Action] = ".action",
        [CodesysObjectType.Transition] = ".transition",
    };

    /// <summary>Types whose file name is prefixed with their owner, so a flat folder can
    /// still reconstruct ownership on import.</summary>
    private static readonly HashSet<CodesysObjectType> ParentPrefixed =
    [
        CodesysObjectType.Method,
        CodesysObjectType.MethodNoReturn,
        CodesysObjectType.Property,
        CodesysObjectType.Action,
        CodesysObjectType.Transition,
    ];

    /// <summary>Percent-encode anything Windows will not accept in a file name.</summary>
    public static string EncodeStem(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "_unnamed";
        }

        var encoded = InvalidWindowsChars().Replace(name, m => $"%{(int)m.Value[0]:X2}");

        // Windows silently strips trailing spaces and dots, which would make the mapping
        // lossy. Encode them instead.
        var sb = new StringBuilder();
        for (var i = 0; i < encoded.Length; i++)
        {
            var c = encoded[i];
            var isTrailing = i == encoded.Length - 1;
            if (isTrailing && (c == ' ' || c == '.'))
            {
                sb.Append($"%{(int)c:X2}");
            }
            else
            {
                sb.Append(c);
            }
        }

        var stem = sb.ToString();
        if (ReservedWindowsNames.Contains(stem))
        {
            stem += "%20";
        }

        return stem.Length == 0 ? "_unnamed" : stem;
    }

    /// <summary>Inverse of <see cref="EncodeStem"/>.</summary>
    public static string DecodeStem(string? stem)
    {
        if (string.IsNullOrEmpty(stem))
        {
            return string.Empty;
        }

        var s = stem;
        if (s.EndsWith("%20", StringComparison.OrdinalIgnoreCase) &&
            ReservedWindowsNames.Contains(s[..^3]))
        {
            s = s[..^3];
        }

        return PercentEscape().Replace(s, m =>
            ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    }

    public static string SuffixFor(ExportForm form) => form switch
    {
        ExportForm.StFull or ExportForm.StDeclaration or ExportForm.StImplementation => ".st",
        _ => ".xml",
    };

    /// <summary>Build the on-disk file name for an object.</summary>
    public static string FileNameFor(string name, CodesysObjectType type, ExportForm form,
                                     string? parentName = null)
    {
        var stem = EncodeStem(name);
        if (parentName is not null && ParentPrefixed.Contains(type))
        {
            stem = EncodeStem(parentName) + "." + stem;
        }

        var infix = Infix.TryGetValue(type, out var i) ? i : string.Empty;
        return stem + infix + SuffixFor(form);
    }

    /// <summary>Normalise a repo-relative path for storage and comparison: forward slashes,
    /// no leading separator. Git records forward slashes regardless of platform.</summary>
    public static string NormalizeRelative(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}
