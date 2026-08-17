using Versio.Core.Plc;

namespace Versio.Core.Projects;

/// <summary>
/// How one object type is written on screen: as a chip, and in a counted sentence.
/// </summary>
/// <remarks>
/// Three forms rather than one, because English needs three and deriving them from each other
/// gets them wrong. <c>POU</c> is an acronym and stays capitalised in a sentence; "visualisation"
/// is a word and does not; and "POUs" pluralises where "library manager" becomes "library
/// managers". A rule that lowercased the chip would produce "3 pous", and one that appended an
/// <c>s</c> to a lowercased "Persistent GVL" would produce "persistent gvls".
/// </remarks>
/// <param name="Chip">Beside a row's name: <c>POU</c>, <c>Visualisation</c>.</param>
/// <param name="One">In a sentence, singular: <c>1 POU</c>, <c>1 visualisation</c>.</param>
/// <param name="Many">In a sentence, plural: <c>3 POUs</c>, <c>3 visualisations</c>.</param>
public readonly record struct ObjectTypeName(string Chip, string One, string Many);

/// <summary>
/// Renders an object's type, in whichever vendor's vocabulary wrote it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This was one table of thirty entries, and it read as a domain vocabulary that it was
/// not.</b> Roughly a quarter of it is IEC 61131-3 — <c>POU</c>, <c>ACTION</c>,
/// <c>TRANSITION</c>, <c>METHOD</c>, <c>PROPERTY</c>, <c>INTERFACE</c>, <c>TASK</c> — and means
/// the same thing whoever implements the standard. The rest is one vendor's model of its own
/// project tree: <c>PLC_LOGIC</c>, <c>CALL_TO_POU</c>, <c>LIBRARY_MANAGER</c>,
/// <c>TASK_CONFIGURATION</c>, four kinds of visualisation. TwinCAT organises the same machine
/// differently, and would have arrived to find a "neutral" table full of somebody else's words
/// that it could neither use nor correct.
/// </para>
/// <para>
/// So this asks three things in order: <b>the vendor that wrote the manifest</b>, then
/// <see cref="Iec.IecObjectTypeNames"/>, then nothing — an unrecognised type is shown exactly as
/// the exporter wrote it. Falling back to IEC rather than to raw matters because the standard's
/// words are the ones a second vendor is most likely to share.
/// </para>
/// <para>
/// <b>The vendor's half is asked of its own <see cref="IPlcProjectFormat"/></b> rather than of a
/// second registry beside <see cref="PlcFormats"/>. The format object already is the thing a
/// vendor registers, and it already carries knowledge of this kind — the export-path convention,
/// the template rule, which files a plan needs text for. One registry, asked a new question.
/// </para>
/// <para>
/// <b>An unknown type is shown as the exporter wrote it, never guessed at.</b> A build that met
/// a type added after it shipped would otherwise invent an English word for something it has
/// never seen, and the raw <c>WEB_VISUALISATION</c> is more honest than a plausible
/// mistranslation. This is the same rule the git panel applies to a path with no object behind
/// it: fall back, never fabricate.
/// </para>
/// </remarks>
public static class ObjectTypeNames
{
    /// <summary>How this type is written, or the raw string when nobody names it.</summary>
    public static ObjectTypeName For(PlcVendor vendor, string? type)
    {
        var raw = type ?? string.Empty;

        return PlcFormats.ForVendor(vendor)?.DisplayNameFor(raw)
               ?? Iec.IecObjectTypeNames.TryFor(raw)
               ?? new ObjectTypeName(raw, raw, raw);
    }

    /// <summary>The chip shown beside an object's name.</summary>
    public static string Chip(PlcVendor vendor, string? type) => For(vendor, type).Chip;

    /// <summary>"3 POUs", "1 visualisation" — the count and the type in one phrase.</summary>
    public static string Counted(PlcVendor vendor, string? type, int count)
    {
        var name = For(vendor, type);
        return $"{count} {(count == 1 ? name.One : name.Many)}";
    }
}
