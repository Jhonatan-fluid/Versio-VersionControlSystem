using Versio.Core.Projects;

namespace Versio.Core.Iec;

/// <summary>
/// How the IEC 61131-3 object kinds are written on screen. Shared by every vendor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Split out of a single table that mixed two vocabularies.</b> `ObjectTypeNames` held
/// thirty entries and read as one domain vocabulary, which it is not: <c>POU</c>, <c>ACTION</c>,
/// <c>TRANSITION</c>, <c>METHOD</c>, <c>PROPERTY</c>, <c>INTERFACE</c> and <c>TASK</c> come from
/// the standard and mean the same thing whoever implements it, while <c>PLC_LOGIC</c>,
/// <c>CALL_TO_POU</c>, <c>GVL_PERSISTENT</c> and <c>LIBRARY_MANAGER</c> are one vendor's model
/// of its own project tree.
/// </para>
/// <para>
/// <b>The partition is deliberately conservative, and two entries are worth arguing about.</b>
/// <c>DUT</c> and <c>GVL</c> read as universal PLC vocabulary and are not: IEC 61131-3 says
/// "derived data type" and <c>VAR_GLOBAL</c>, and both abbreviations come from CODESYS. They are
/// on the vendor side. Anything genuinely ambiguous went there too — a term wrongly called
/// standard is a term the next vendor inherits and has to live with, which is the more expensive
/// mistake of the two.
/// </para>
/// <para>
/// <b>Three forms rather than one, because English needs three</b> and deriving them from each
/// other gets them wrong. <c>POU</c> is an acronym and stays capitalised in a sentence;
/// "interface" is a word and does not. A rule that lowercased the chip would produce "3 pous".
/// </para>
/// </remarks>
public static class IecObjectTypeNames
{
    private static readonly Dictionary<string, ObjectTypeName> Table =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["POU"] = new("POU", "POU", "POUs"),
            ["INTERFACE"] = new("Interface", "interface", "interfaces"),
            ["TASK"] = new("Task", "task", "tasks"),

            // A method with no return value is still a method to anybody reading the list. The
            // distinction matters to the exporter, which has to write a different ST header for
            // it, and to nobody scanning a column of changed objects.
            //
            // METHOD_NORET is CODESYS's spelling of the distinction rather than the standard's,
            // so it lives on the vendor side; METHOD itself is IEC 61131-3 third-edition OOP.
            ["METHOD"] = new("Method", "method", "methods"),

            ["PROPERTY"] = new("Property", "property", "properties"),
            ["ACTION"] = new("Action", "action", "actions"),
            ["TRANSITION"] = new("Transition", "transition", "transitions"),
        };

    /// <summary>How this type is written, or null when the standard does not name it.</summary>
    public static ObjectTypeName? TryFor(string? type) =>
        type is not null && Table.TryGetValue(type, out var name) ? name : null;

    /// <summary>Every type the standard names here. Ordered for a stable test.</summary>
    public static IReadOnlyList<string> Known =>
        [.. Table.Keys.OrderBy(k => k, StringComparer.Ordinal)];
}
