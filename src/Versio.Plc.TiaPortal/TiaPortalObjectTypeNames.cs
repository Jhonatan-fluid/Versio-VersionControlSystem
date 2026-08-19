using Versio.Core.Plc;
using Versio.Core.Projects;

namespace Versio.Plc.TiaPortal;

/// <summary>
/// How TIA Portal's own object kinds are written on screen.
/// </summary>
/// <remarks>
/// These are one vendor's model of its own project tree. FB/FC/OB/DB are this vendor's
/// abbreviations, not IEC 61131-3's; the standard says POU. Repeating them in the IEC
/// table would hand every other vendor a vocabulary it does not use.
/// </remarks>
public static class TiaPortalObjectTypeNames
{
    private static readonly Dictionary<string, ObjectTypeName> Table =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["OB"] = new("OB", "organization block", "organization blocks"),
            ["FB"] = new("FB", "function block", "function blocks"),
            ["FC"] = new("FC", "function", "functions"),
            ["DB"] = new("DB", "data block", "data blocks"),
            ["UDT"] = new("UDT", "PLC data type", "PLC data types"),
            ["TAG_TABLE"] = new("Tag table", "tag table", "tag tables"),
            ["DEVICE"] = new("Device", "device", "devices"),
            ["PLC"] = new("PLC", "PLC", "PLCs"),
            ["FOLDER"] = new("Folder", "folder", "folders"),
        };

    public static ObjectTypeName? TryFor(string? type) =>
        type is not null && Table.TryGetValue(type, out var name) ? name : null;
}
