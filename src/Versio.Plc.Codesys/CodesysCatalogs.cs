using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Versio.Core.Projects;
using Versio.Plc.Codesys.FileBased;
using Versio.Plc.Codesys.Ladder;

namespace Versio.Plc.Codesys;

/// <summary>
/// CODESYS's own files read into the neutral library, task and device records, and the text list
/// and project settings into member tables, so the views the classic mirror's YAML feeds draw a
/// File-Based Storage project too.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The classic export writes <c>*.libraries.yaml</c>,
/// <c>*.tasks.yaml</c> and <c>*.devices.yaml</c> through the ScriptEngine, and the Rendered tab
/// draws those. A File-Based Storage folder holds the same facts in CODESYS's own form:
/// <c>Libraries.json</c>, and archive XML for tasks, devices, text lists and project settings.
/// With nothing to read them, every one of those objects drew as raw source (the owner,
/// 2026-10-10: "libraries, device, task configuration, global text list, project settings ...
/// not rendering at all").
/// </para>
/// <para>
/// <b>The same record the YAML gives, field for field, where both say it.</b> A project moved to
/// File-Based Storage must draw the same table it drew before, or every reviewer sees a change
/// that is not one. The tests compare each reader with the classic mirror's YAML for the same
/// fixture. Where the classic YAML leaves a field out (an empty watchdog time, a sensitivity
/// nobody set) these leave it out too.
/// </para>
/// <para>
/// The archive XML readers find the object with <see cref="CodesysLadderParser.ObjectOf"/>, so
/// they read a classic native export (<c>GlobalTextList.xml</c>) as readily as an FBS
/// <c>.xml.v3</c>.
/// </para>
/// </remarks>
public static class CodesysCatalogs
{
    private const string TaskType = "{98a2708a-9b18-4f31-82ed-a1465b24fa2d}";
    private const string DeviceType = "{225bfe47-7336-4dbc-9419-4105a7c831fa}";
    private const string TextListType = "{63784cbb-9ba0-45e6-9d69-babf3f040511}";
    private const string ProjectSettingsType = "{8753fe6f-4a22-4320-8103-e553c4fc8e04}";

    /// <summary>The library manager's fixed File-Based Storage file name.</summary>
    private const string LibrariesFile = "Libraries.json";

    /// <summary>
    /// A File-Based Storage <c>Libraries.json</c> as the library catalog, in the classic YAML's
    /// terms: a placeholder reference is named <c>#placeholder</c>, its redirection is the
    /// project's override for that placeholder, and "qualified only" is the inverse of the
    /// JSON's <c>allowUnqualifiedAccess</c>.
    /// </summary>
    public static LibraryCatalog? Libraries(string relativePath, string content)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (string.IsNullOrWhiteSpace(content)
            || !relativePath.EndsWith(LibrariesFile, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(content.TrimStart('\uFEFF'));
        }
        catch (JsonException)
        {
            return null;
        }

        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var overrides = Strings(root, "placeholderOverrides");
            var parameters = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            if (root.TryGetProperty("libraryParameters", out var all) && all.ValueKind == JsonValueKind.Object)
            {
                foreach (var library in all.EnumerateObject())
                {
                    parameters[library.Name] = Strings(all, library.Name);
                }
            }

            var references = new List<LibraryReference>();
            if (root.TryGetProperty("references", out var refs) && refs.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in refs.EnumerateObject())
                {
                    references.Add(Reference(entry.Value, entry.Name, system: false, overrides, parameters));
                }
            }

            if (root.TryGetProperty("v3SystemLibraries", out var system) && system.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in system.EnumerateArray())
                {
                    if (entry.TryGetProperty("libraryReference", out var reference))
                    {
                        references.Add(Reference(reference, Text(entry, "namespace"), system: true, overrides, parameters));
                    }
                }
            }

            var manager = CodesysV3File.ObjectName(relativePath);
            return new LibraryCatalog
            {
                Schema = 1,
                Vendor = "codesys",
                Manager = manager,
                Path = relativePath,
                References = references,
            };
        }
    }

    private static LibraryReference Reference(
        JsonElement reference,
        string namespaceName,
        bool system,
        IReadOnlyDictionary<string, string> overrides,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> parameters)
    {
        var placeholder = Text(reference, "placeholder");
        var name = placeholder.Length > 0 ? "#" + placeholder : Text(reference, "library");
        if (name.Length == 0)
        {
            name = Text(reference, "defaultResolution");
        }

        return new LibraryReference
        {
            Name = name,
            Namespace = namespaceName.Length > 0 ? namespaceName : placeholder,
            Placeholder = placeholder,
            DefaultResolution = Text(reference, "defaultResolution"),
            Redirection = placeholder.Length > 0 ? overrides.GetValueOrDefault(placeholder) ?? "" : "",
            SystemLibrary = system,
            Optional = Flag(reference, "optional"),
            // Written only when true: a reference with no allowUnqualifiedAccess is qualified-only,
            // which the parity test with the classic YAML caught on CAA Device Diagnosis.
            QualifiedOnly = !(reference.TryGetProperty("allowUnqualifiedAccess", out var open) && open.ValueKind == JsonValueKind.True),
            PublishSymbols = Flag(reference, "publishSymbols"),
            Parameters = parameters.GetValueOrDefault(name) ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    /// <summary>One task from its archive XML, in the classic YAML's fields.</summary>
    public static TaskRecord? Task(string relativePath, string content)
    {
        if (Object(relativePath, content, TaskType) is not var (task, name))
        {
            return null;
        }

        var watchdog = Named(task, "Watchdog");
        var interval = Named(task, "Interval");
        return new TaskRecord
        {
            Name = name,
            Priority = Value(task, "Priority"),
            Interval = Value(interval, "Time"),
            IntervalUnit = Value(interval, "Unit"),
            CoreBinding = Value(task, "Core2"),
            WatchdogEnabled = Value(watchdog, "Enabled") == "True",
            WatchdogTime = Value(Named(watchdog, "Time"), "Time"),
            WatchdogSensitivity = Value(watchdog, "Sensitivity"),
            Pous = Named(task, "PouList")?.Elements().Select(p => Value(p, "Name")).Where(n => n.Length > 0).ToList() ?? [],
        };
    }

    /// <summary>
    /// A device's connectors and their parameters from its archive XML.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A parameter is named as the IDE shows it (its visible name), and a name used twice on one
    /// connector, or no name at all, gets its id after a <c>#</c>, the rule the classic YAML
    /// follows. A structured value is written <c>{a, b, …}</c>, its elements in order.
    /// </para>
    /// <para>
    /// <b>Where the names can differ from the classic YAML.</b> The ScriptEngine names a parameter
    /// from the installed device description, in the IDE's language. The project file stores only
    /// the description's fallback text. So the same parameter can read "Clear Statistics" there
    /// and "Clear statistics" here, and a description's own spelling (<c>ShpwDCStatistik</c>) is not
    /// in the file at all. The values always agree; this reader shows what the file says.
    /// </para>
    /// <para>
    /// Not here: the slots of a modular device. In File-Based Storage each slot is its own
    /// object, drawn as a child of the device in the tree, so the device file does not carry
    /// them.
    /// </para>
    /// </remarks>
    public static DeviceCatalog? Device(string relativePath, string content)
    {
        if (Object(relativePath, content, DeviceType) is not var (device, name))
        {
            return null;
        }

        var connectors = new List<DeviceConnector>();
        // ConnectorList/InnerList, not the top-level "Connectors" list beside it: that one is
        // written empty, and reading it drew every device with no connectors.
        foreach (var connector in Named(Named(device, "ConnectorList"), "InnerList")?.Elements() ?? [])
        {
            var raw = new List<(string Name, string Id, string Value)>();
            foreach (var parameter in Named(Named(connector, "HostParameterSet"), "Params")?.Elements() ?? [])
            {
                var element = Named(parameter, "DalaElement");
                raw.Add((Value(Named(element, "VisibleName"), "Default"), Value(parameter, "Id"), ValueOf(element)));
            }

            var counts = raw.GroupBy(r => r.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (parameterName, id, value) in raw)
            {
                // An unnamed parameter is named by its id alone: an empty key says nothing.
                var key = counts[parameterName] > 1 || parameterName.Length == 0 ? $"{parameterName}#{id}" : parameterName;
                parameters.TryAdd(key, value);
            }

            connectors.Add(new DeviceConnector
            {
                ConnectorId = Value(connector, "ConnectorId"),
                Interface = Value(connector, "Interface"),
                ModuleType = Value(connector, "ModuleType"),
                Parameters = parameters,
            });
        }

        return new DeviceCatalog
        {
            Schema = 1,
            Vendor = "codesys",
            Name = name,
            Path = relativePath,
            Connectors = connectors,
        };
    }

    /// <summary>A global text list: one row per text, its id, its default text and its translations.</summary>
    public static MemberTable? TextList(string relativePath, string content)
    {
        if (Object(relativePath, content, TextListType) is not var (list, name))
        {
            return null;
        }

        var rows = new List<MemberRow>();
        foreach (var text in Named(list, "TextList")?.Elements() ?? [])
        {
            var id = Value(text, "TextID");
            var translations = Named(text, "LanguageTexts")?.Elements()
                .Select(t => (Language: Value(t, "LanguageId") is { Length: > 0 } l ? l : Value(t, "Language"), Text: Value(t, "Text")))
                .Where(t => t.Text.Length > 0)
                .Select(t => t.Language.Length > 0 ? $"{t.Language}: {t.Text}" : t.Text)
                .ToList() ?? [];
            rows.Add(new MemberRow
            {
                Path = id,
                Name = id,
                StartValue = Value(text, "TextDefault"),
                Comment = string.Join("; ", translations),
            });
        }

        return new MemberTable
        {
            Name = name,
            Members = rows,
            NameLabel = "TEXT ID",
            ValueLabel = "DEFAULT TEXT",
            Note = "Read-only view of the text list. Translations are in the comment column.",
        };
    }

    /// <summary>
    /// The project settings: CODESYS stores them as one base64 blob of UTF-16 XML, a tree of keys
    /// holding name/value pairs. Each key with values becomes a section, named as CODESYS names
    /// it, which for many is a GUID; the settings under it are readable whatever the key is called.
    /// </summary>
    public static MemberTable? ProjectSettings(string relativePath, string content)
    {
        if (Object(relativePath, content, ProjectSettingsType) is not var (settings, name))
        {
            return null;
        }

        XDocument options;
        try
        {
            var bytes = Convert.FromBase64String(Value(settings, "OptionData").Trim());
            options = Versio.Core.Text.SafeXml.Parse(Encoding.Unicode.GetString(bytes).TrimStart('\uFEFF'));
        }
        catch (Exception e) when (e is FormatException or XmlException)
        {
            return null;
        }

        var rows = new List<MemberRow>();
        if (options.Root is { } root)
        {
            Walk(root, rows, prefix: "");
        }

        return new MemberTable
        {
            Name = name,
            Members = rows,
            NameLabel = "SETTING",
            ValueLabel = "VALUE",
            Note = "Read-only view of the project settings, by the key CODESYS files each under.",
        };
    }

    private static void Walk(XElement key, List<MemberRow> rows, string prefix)
    {
        var keyName = Value(key, "Name");
        var path = keyName is "<ProjectRoot>" or "" ? prefix : prefix.Length == 0 ? keyName : prefix + "/" + keyName;

        var values = Named(key, "Values")?.Elements().Select(Pair).Where(p => p.Name.Length > 0).ToList() ?? [];
        if (values.Count > 0)
        {
            var section = path.Length == 0 ? "Project" : path;
            rows.Add(new MemberRow { Path = section, Name = section, Section = section, IsSectionHeading = true });
            rows.AddRange(values.Select(v => new MemberRow
            {
                Path = section + "/" + v.Name,
                Name = v.Name,
                Section = section,
                StartValue = v.Value,
            }));
        }

        foreach (var sub in Named(key, "SubKeys")?.Elements().Select(e => e.Descendants().FirstOrDefault(d => Named(d, "SubKeys") is not null)).OfType<XElement>() ?? [])
        {
            Walk(sub, rows, path);
        }
    }

    private static (string Name, string Value) Pair(XElement entry) =>
        (entry.Elements().FirstOrDefault(e => e.Name.LocalName == "Key")?.Value.Trim() ?? "",
         entry.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value.Trim() ?? "");

    /// <summary>A parameter's value, or its elements' values in braces when it is structured.</summary>
    private static string ValueOf(XElement? element)
    {
        if (Named(Named(element, "SubElements"), "elements") is { } elements)
        {
            return "{" + string.Join(", ", elements.Elements().Select(ValueOf)) + "}";
        }

        // An enumerated element (an EtherCAT init command's Transition) keeps its value only on its
        // base type; every other kind repeats it on the element itself. Caught by the parity test
        // with the classic YAML, where it read "0" and this read nothing.
        var own = Value(element, "Value");
        return own.Length > 0 || Named(element, "BaseType") is not { } baseType ? own : Value(baseType, "Value");
    }

    /// <summary>The object element and its name when the file is an archive of the given type.</summary>
    private static (XElement Owner, string Name)? Object(string relativePath, string content, string type) =>
        CodesysLadderParser.ObjectOf(relativePath, content) is var (owner, name)
        && string.Equals(owner.Attribute("Type")?.Value, type, StringComparison.OrdinalIgnoreCase)
            ? (owner, name)
            : null;

    private static XElement? Named(XElement? parent, string name) => CodesysLadderParser.Named(parent, name);

    private static string Value(XElement? parent, string name) =>
        Named(parent, name) is { HasElements: false } leaf ? leaf.Value : "";

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static Dictionary<string, string> Strings(JsonElement element, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty(name, out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in obj.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.ToString();
            }
        }

        return result;
    }
}
