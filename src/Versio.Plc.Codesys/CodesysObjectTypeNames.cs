using Versio.Core.Plc;
using Versio.Core.Projects;

namespace Versio.Plc.Codesys;

/// <summary>
/// How CODESYS's own object kinds are written on screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are one vendor's model of its own project tree, not PLC vocabulary.</b>
/// <c>PLC_LOGIC</c>, <c>APPLICATION</c>, <c>CALL_TO_POU</c>, <c>LIBRARY_MANAGER</c>,
/// <c>TASK_CONFIGURATION</c> and the four visualisation kinds describe how CODESYS organises a
/// project. TwinCAT organises the same machine differently and would need its own table; a
/// standard-looking table containing all of these would have handed the next vendor a
/// vocabulary it does not use and cannot correct.
/// </para>
/// <para>
/// <b><c>DUT</c> and <c>GVL</c> are here rather than in the IEC table, which is the entry most
/// likely to be argued with.</b> They read as universal and are not: IEC 61131-3 says "derived
/// data type" and <c>VAR_GLOBAL</c>. Both abbreviations are CODESYS's, and a term wrongly called
/// standard is one the next vendor inherits and cannot get out of.
/// </para>
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
/// </remarks>
public static class CodesysObjectTypeNames
{
    private static readonly Dictionary<string, ObjectTypeName> Table =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["DUT"] = new("DUT", "DUT", "DUTs"),
            ["GVL"] = new("GVL", "GVL", "GVLs"),
            ["GVL_PERSISTENT"] = new("Persistent GVL", "persistent GVL", "persistent GVLs"),
            ["EVC"] = new("EVC", "EVC", "EVCs"),

            // CODESYS's own spelling of "a method that returns nothing". Still a method to
            // anybody reading a column of changed objects, which is why it renders identically
            // to the IEC METHOD above it.
            ["METHOD_NORET"] = new("Method", "method", "methods"),

            ["PROPERTY_METHOD"] = new("Accessor", "property accessor", "property accessors"),
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

    /// <summary>How this type is written, or null when CODESYS does not name it.</summary>
    public static ObjectTypeName? TryFor(string? type) =>
        type is not null && Table.TryGetValue(type, out var name) ? name : null;

    /// <summary>Every type this vendor names. Ordered for a stable test.</summary>
    public static IReadOnlyList<string> Known =>
        [.. Table.Keys.OrderBy(k => k, StringComparer.Ordinal)];
}
