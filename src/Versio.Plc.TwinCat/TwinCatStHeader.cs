using Versio.Core.Iec;

namespace Versio.Plc.TwinCat;

/// <summary>
/// The object's name as the ST declaration states it, not as the file is named.
/// </summary>
/// <remarks>
/// TwinCAT ST is IEC, so <see cref="StDeclarationParser"/> already answers. GVL is
/// <c>VAR_GLOBAL</c> rather than a POU keyword; that is this vendor's extra, not a
/// reason to fork the parser.
/// </remarks>
public static class TwinCatStHeader
{
    public static string? ObjectName(string? text)
    {
        var info = StDeclarationParser.Parse(text);
        if (!string.IsNullOrEmpty(info.Name))
        {
            return info.Name;
        }

        return GvlName(text);
    }

    public static string? ObjectType(string? text)
    {
        var info = StDeclarationParser.Parse(text);
        return info.Kind switch
        {
            PouKind.Program => "POU",
            PouKind.FunctionBlock => "POU",
            PouKind.Function => "POU",
            PouKind.Interface => "INTERFACE",
            PouKind.Dut => "DUT",
            _ => GvlName(text) is null ? null : "GVL",
        };
    }

    public static bool CanCreate(string? text)
    {
        var info = StDeclarationParser.Parse(text);
        return info.CanCreate || GvlName(text) is not null;
    }

    private static string? GvlName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // A GVL file is VAR_GLOBAL without a PROGRAM/FB header. The file stem is
        // then the only name; the planner still refuses a stray .st with neither.
        var stripped = text.Replace('\r', '\n');
        foreach (var raw in stripped.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)
                || line.StartsWith("(*", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("VAR_GLOBAL", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            return null;
        }

        return null;
    }
}
