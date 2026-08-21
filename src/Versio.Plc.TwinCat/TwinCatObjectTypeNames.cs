using Versio.Core.Projects;

namespace Versio.Plc.TwinCat;

/// <summary>
/// How TwinCAT's own object kinds are written on screen.
/// </summary>
/// <remarks>
/// IEC names (POU, METHOD, PROPERTY, ACTION, INTERFACE, TASK) stay on
/// <see cref="Iec.IecObjectTypeNames"/>. These are this vendor's tree words.
/// DUT and GVL are TwinCAT/CODESYS abbreviations, not the standard's, so they
/// live here rather than being inherited by every vendor.
/// </remarks>
public static class TwinCatObjectTypeNames
{
    private static readonly Dictionary<string, ObjectTypeName> Table =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["DUT"] = new("DUT", "data type", "data types"),
            ["GVL"] = new("GVL", "global variable list", "global variable lists"),
            ["FOLDER"] = new("Folder", "folder", "folders"),
            ["PLC"] = new("PLC", "PLC", "PLCs"),
            ["TIPC"] = new("PLC", "PLC", "PLCs"),
            ["TIID"] = new("I/O", "I/O", "I/O"),
            ["TINC"] = new("NC", "NC", "NC"),
            ["LIBRARY_MANAGER"] = new("Libraries", "library manager", "library managers"),
        };

    public static ObjectTypeName? TryFor(string? type) =>
        type is not null && Table.TryGetValue(type, out var name) ? name : null;
}
