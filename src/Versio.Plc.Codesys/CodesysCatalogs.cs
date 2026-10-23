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
    private const string SymbolConfigurationType = "{21d4fe94-4123-4e23-9091-ead220afbd1f}";
    private const string SymbolSetType = "{3cda6efc-db0e-4ab2-9926-6ef9cbceea87}";

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

        // A fixed-version reference ("$type": "ManagedLibrary") names itself in libraryId, and
        // the reader once knew only placeholders: FsLibrary.fbslib's four OPC UA and
        // IecVarAccess interface libraries came out with no name at all, all four shared the
        // empty key, and the comparison paired them with each other into three "changed" rows
        // on a tree with no changes (the owner, 2026-10-23).
        var managed = Text(reference, "libraryId");
        var name = placeholder.Length > 0 ? "#" + placeholder
            : managed.Length > 0 ? managed
            : Text(reference, "library");
        if (name.Length == 0)
        {
            name = Text(reference, "defaultResolution");
        }

        var parsed = placeholder.Length == 0 ? ManagedName(name) : null;

        return new LibraryReference
        {
            Name = name,
            Title = parsed?.Title ?? "",
            Version = parsed?.Version ?? "",
            Company = parsed?.Company ?? "",
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

    /// <summary>
    /// A managed reference's own name, <c>"Title, Version (Company)"</c>, split the way the
    /// classic export's <c>parse_managed_name</c> splits it, or null when it is not that shape.
    /// </summary>
    /// <remarks>
    /// <b>The same strict parse, on purpose.</b> A project moved to File-Based Storage must draw
    /// the row it drew before (see the class remarks), and the python's reasoning carries over:
    /// split on the last <c>" ("</c> and the last <c>", "</c>, and accept only a version of
    /// ASCII digits and dots with at least one digit. So <c>"CmpOPCUAServer Interfaces, *
    /// (System)"</c> gets no version rather than <c>"*"</c>: an absent version is a gap somebody
    /// can see, and a wrong one is a diff somebody acts on. The name still says it whole.
    /// </remarks>
    private static (string Title, string Version, string Company)? ManagedName(string name)
    {
        if (!name.EndsWith(')'))
        {
            return null;
        }

        var head = name[..^1];
        var bracket = head.LastIndexOf(" (", StringComparison.Ordinal);
        if (bracket <= 0 || bracket + 2 >= head.Length)
        {
            return null;
        }

        var company = head[(bracket + 2)..];
        head = head[..bracket];
        var comma = head.LastIndexOf(", ", StringComparison.Ordinal);
        if (comma <= 0 || comma + 2 >= head.Length)
        {
            return null;
        }

        var version = head[(comma + 2)..];
        return version.All(c => c is (>= '0' and <= '9') or '.') && version.Any(char.IsAsciiDigit)
            ? (head[..comma], version, company)
            : null;
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

    /// <summary>
    /// The symbol configuration's settings, one row per option as CODESYS names it, and how
    /// many entries each of its lists holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It drew as raw archive XML (the owner, 2026-10-23), which buries the handful of switches
    /// a reviewer checks — OPC UA support, comment export, direct I/O access — among GUIDs.
    /// </para>
    /// <para>
    /// <b>GUID-typed values are left out.</b> They are CODESYS's references to its own
    /// generated objects (the symbol GVL and POU), they change when it regenerates them, and a
    /// row nobody can read is churn in every comparison. <b>The lists are counted, not
    /// listed</b>: the only configuration in hand (<c>FsLibrary.fbslib</c>) selects nothing, so
    /// there was no selected variable to read the shape of, and guessing one would draw a
    /// table that looks complete and is not. The note says so.
    /// </para>
    /// </remarks>
    public static MemberTable? SymbolConfiguration(string relativePath, string content)
    {
        if (Object(relativePath, content, SymbolConfigurationType) is not var (config, name))
        {
            return null;
        }

        var rows = new List<MemberRow>();
        foreach (var option in config.Elements())
        {
            var key = option.Attribute("Name")?.Value ?? "";
            var type = option.Attribute("Type")?.Value ?? "";
            if (key.Length == 0 || type == "System.Guid")
            {
                continue;
            }

            string value;
            if (option.Name.LocalName is "List" or "List2" or "Dictionary" or "Dictionary2")
            {
                value = Count(option.Elements().Count());
            }
            else if (option.Name.LocalName == "Null")
            {
                value = "(none)";
            }
            else if (!option.HasElements)
            {
                value = option.Value.Trim();
            }
            else if (option.Elements().SingleOrDefault() is { Name.LocalName: "List" or "List2" or "Dictionary" or "Dictionary2" } inner)
            {
                // An object wrapping one table (LibraryIds holds a Dictionary2 named Table): the
                // wrapper says nothing, the count is the content.
                value = Count(inner.Elements().Count());
            }
            else
            {
                continue;
            }

            rows.Add(new MemberRow { Path = key, Name = key, StartValue = value });
        }

        return new MemberTable
        {
            Name = name,
            Members = rows,
            NameLabel = "SETTING",
            ValueLabel = "VALUE",
            Note = "Read-only view of the symbol configuration's settings. Lists are counted, not listed; Raw has every entry.",
        };

        static string Count(int n) => n == 1 ? "1 entry" : $"{n} entries";
    }

    /// <summary>
    /// An OPC UA server's symbol set: each published variable, its members under it as the
    /// symbol editor nests them, with the access each one is published at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It drew as 779 lines of archive XML (the owner, 2026-10-23, on
    /// <c>Communication Manager/OPC UA Server/Symbol Set</c>), and what a reviewer asks of it is
    /// two questions: what is published, and can a client write it.
    /// </para>
    /// <para>
    /// <b>Three lists, joined by id and by type name.</b> <c>SymbolItems</c> are the roots, with
    /// their full path. <c>TypeSet/TypeEntries</c> give each type its members' ids, and
    /// <c>TypeSet/AllEntries</c> holds each member: name, type, access, and whether it is
    /// ticked. A member of a structured type finds its own members through the type entry of
    /// the same name. The walk keeps a visited set of type names on the current branch, so a
    /// type that contains itself through a pointer cannot recurse forever in a background
    /// render.
    /// </para>
    /// <para>
    /// An unticked member is still drawn, with "not published" in its comment, because the
    /// difference between ticked and unticked is exactly what a change to this file is.
    /// </para>
    /// </remarks>
    public static MemberTable? SymbolSet(string relativePath, string content)
    {
        if (Object(relativePath, content, SymbolSetType) is not var (set, name))
        {
            return null;
        }

        var typeSet = Named(set, "TypeSet");
        var childrenOfType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var entry in Named(typeSet, "TypeEntries")?.Elements() ?? [])
        {
            childrenOfType[Value(entry, "SymbolType")] =
                Named(entry, "ChildIds")?.Elements().Select(e => e.Value.Trim()).ToList() ?? [];
        }

        var members = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var entry in Named(typeSet, "AllEntries")?.Elements() ?? [])
        {
            var key = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "Key")?.Value.Trim();
            var value = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Elements().FirstOrDefault();
            if (key is { Length: > 0 } && value is not null)
            {
                members[key] = value;
            }
        }

        var rows = new List<MemberRow>();
        foreach (var item in Named(Named(set, "SymbolSet"), "SymbolItems")?.Elements() ?? [])
        {
            var path = Value(item, "FullPath") is { Length: > 0 } full ? full : Value(item, "Name");
            var type = Value(item, "SymbolType");
            rows.Add(new MemberRow
            {
                Path = path,
                Name = path,
                Datatype = type,
                ExternalAccess = Value(item, "AccessRights"),
                Comment = Value(item, "Comment"),
            });
            AddMembers(type, path, depth: 1, [type]);
        }

        return new MemberTable
        {
            Name = name,
            Members = rows,
            NameLabel = "SYMBOL",
            Note = "Read-only view of what this OPC UA server publishes, and the access each symbol has.",
        };

        void AddMembers(string type, string parentPath, int depth, HashSet<string> onBranch)
        {
            foreach (var id in childrenOfType.GetValueOrDefault(type) ?? [])
            {
                if (!members.TryGetValue(id, out var member))
                {
                    continue;
                }

                var memberName = Value(member, "Name");
                var memberType = Value(member, "SymbolType");
                var path = parentPath + "." + memberName;
                rows.Add(new MemberRow
                {
                    Path = path,
                    Name = memberName,
                    Depth = depth,
                    Datatype = memberType,
                    ExternalAccess = Value(member, "AccessRights"),
                    Comment = Value(member, "Checked") == "False" ? "not published" : "",
                });

                if (onBranch.Add(memberType))
                {
                    AddMembers(memberType, path, depth + 1, onBranch);
                    onBranch.Remove(memberType);
                }
            }
        }
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
