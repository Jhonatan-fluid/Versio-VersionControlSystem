namespace Versio.Core.Projects;

/// <summary>
/// How one CODESYS object type is written on screen: as a chip, and in a counted sentence.
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

/// <summary>The display names for the type strings the exporter writes.</summary>
/// <remarks>
/// <para>
/// <b>A table, not an algorithm.</b> The obvious alternative — lowercase, replace underscores,
/// capitalise the first letter — is wrong for over half of these: it yields "Gvl", "Textlist",
/// "Imagepool" and "Pou". Engineers read those every day and would read a tool that renamed them
/// as a tool that did not know the domain.
/// </para>
/// <para>
/// <b>An unknown type is shown as the exporter wrote it, never guessed at.</b> The type strings
/// come from <c>versio_types.py</c> and that table grows — <c>APPLICATION</c> and
/// <c>PLC_LOGIC</c> were both added by probing a real project. A build that met a type added
/// after it shipped would otherwise invent an English word for something it has never seen, and
/// the raw <c>WEB_VISUALISATION</c> is more honest than a plausible mistranslation of it. This
/// is the same rule the git panel applies to a path with no object behind it: fall back, never
/// fabricate.
/// </para>
/// <para>
/// Kept beside the manifest that produces the strings rather than in the App, because it is a
/// rule about the domain and it is tested without a window.
/// </para>
/// </remarks>
public static class ObjectTypeNames
{
    private static readonly Dictionary<string, ObjectTypeName> Table =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["POU"] = new("POU", "POU", "POUs"),
            ["DUT"] = new("DUT", "DUT", "DUTs"),
            ["GVL"] = new("GVL", "GVL", "GVLs"),
            ["GVL_PERSISTENT"] = new("Persistent GVL", "persistent GVL", "persistent GVLs"),
            ["EVC"] = new("EVC", "EVC", "EVCs"),
            ["INTERFACE"] = new("Interface", "interface", "interfaces"),
            ["TASK"] = new("Task", "task", "tasks"),

            // A method with no return value is still a method to anybody reading the list. The
            // distinction matters to the exporter, which has to write a different ST header for
            // it, and to nobody scanning a column of changed objects.
            ["METHOD"] = new("Method", "method", "methods"),
            ["METHOD_NORET"] = new("Method", "method", "methods"),

            ["PROPERTY"] = new("Property", "property", "properties"),
            ["PROPERTY_METHOD"] = new("Accessor", "property accessor", "property accessors"),
            ["ACTION"] = new("Action", "action", "actions"),
            ["TRANSITION"] = new("Transition", "transition", "transitions"),
            ["IMAGEPOOL"] = new("Image pool", "image pool", "image pools"),
            ["TEXTLIST"] = new("Text list", "text list", "text lists"),
            ["GLOBAL_TEXTLIST"] = new("Global text list", "global text list", "global text lists"),
            ["LIBRARY_MANAGER"] = new("Library manager", "library manager", "library managers"),
            ["TASK_CONFIGURATION"] = new("Task configuration", "task configuration", "task configurations"),
            ["PROJECT_INFORMATION"] = new("Project information", "project information", "project information"),
            ["PROJECT_SETTINGS"] = new("Project settings", "project settings", "project settings"),
            ["DEVICE"] = new("Device", "device", "devices"),
            ["FOLDER"] = new("Folder", "folder", "folders"),
            ["CALL_TO_POU"] = new("POU call", "POU call", "POU calls"),
            ["VISUALISATION"] = new("Visualisation", "visualisation", "visualisations"),
            ["VISUALISATION_MANAGER"] = new("Visualisation manager", "visualisation manager", "visualisation managers"),
            ["TARGET_VISUALISATION"] = new("Target visualisation", "target visualisation", "target visualisations"),
            ["WEB_VISUALISATION"] = new("Web visualisation", "web visualisation", "web visualisations"),
            ["VISUALISATION_STYLE"] = new("Visualisation style", "visualisation style", "visualisation styles"),
            ["PLC_LOGIC"] = new("PLC logic", "PLC logic", "PLC logic"),
            ["APPLICATION"] = new("Application", "application", "applications"),
        };

    /// <summary>How this type is written, or the raw string when it is not one we know.</summary>
    public static ObjectTypeName For(string? type)
    {
        var raw = type ?? string.Empty;
        return Table.TryGetValue(raw, out var name) ? name : new ObjectTypeName(raw, raw, raw);
    }

    /// <summary>The chip shown beside an object's name.</summary>
    public static string Chip(string? type) => For(type).Chip;

    /// <summary>"3 POUs", "1 visualisation" — the count and the type in one phrase.</summary>
    public static string Counted(string? type, int count)
    {
        var name = For(type);
        return $"{count} {(count == 1 ? name.One : name.Many)}";
    }
}
