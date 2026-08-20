namespace Versio.Core.Projects;

/// <summary>
/// One connector on a device, as the project declares it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Vendor-neutral on purpose.</b> Every PLC toolchain has a device tree whose connectors
/// carry named parameters a reviewer diffs — CODESYS host parameters, TwinCAT TIID, TIA
/// hardware. The questions are the same: which connector, which parameter, what value.
/// What is vendor-specific is the code that <i>produces</i> the file.
/// </para>
/// <para>
/// Duplicate parameter names are real (a coupler's eight IO-Link ports reuse "Cycle Time").
/// The writer disambiguates them; this type just stores the keys it is given.
/// </para>
/// </remarks>
public sealed record DeviceConnector
{
    /// <summary>The toolchain's connector identity, when it has one.</summary>
    public string ConnectorId { get; init; } = string.Empty;

    /// <summary>The interface name the toolchain shows, when it has one.</summary>
    public string Interface { get; init; } = string.Empty;

    /// <summary>Module type, when the toolchain reports one.</summary>
    public string ModuleType { get; init; } = string.Empty;

    /// <summary>Parameter name (or disambiguated key) to value.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>What the row is called: the interface a person reads, falling back to the id.</summary>
    public string DisplayName =>
        !string.IsNullOrEmpty(Interface) ? Interface
        : !string.IsNullOrEmpty(ConnectorId) ? $"connector {ConnectorId}"
        : "connector";

    /// <summary>
    /// Identity used in the tree path and the detail-pane lookup.
    /// </summary>
    /// <remarks>
    /// Id, then interface, then <see cref="DisplayName"/>. The pane used to match only id
    /// or interface, so a connector kept because it had a module type or parameters but
    /// neither of those fields opened with its facts dropped. One property, both callers.
    /// </remarks>
    public string Key =>
        !string.IsNullOrEmpty(ConnectorId) ? ConnectorId
        : !string.IsNullOrEmpty(Interface) ? Interface
        : DisplayName;

    /// <summary>Whether two connectors record the same facts.</summary>
    /// <remarks>
    /// Same construction as <see cref="LibraryReference.ContentEquals"/>: record equality
    /// compares <see cref="Parameters"/> by reference, and a comparison written as record
    /// equality over clones covers a field added later without anyone extending a list.
    /// A hand-written comparison that silently ignored a new field would mark a changed
    /// cycle time as unchanged.
    /// </remarks>
    public bool ContentEquals(DeviceConnector other)
    {
        if (this with { Parameters = NoParameters } != other with { Parameters = NoParameters })
        {
            return false;
        }

        return Parameters.Count == other.Parameters.Count &&
               Parameters.All(p =>
                   other.Parameters.TryGetValue(p.Key, out var value) &&
                   string.Equals(p.Value, value, StringComparison.Ordinal));
    }

    private static readonly IReadOnlyDictionary<string, string> NoParameters =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// The contents of one <c>*.devices.yaml</c> file.
/// </summary>
/// <remarks>
/// <para>
/// Written by the export, read here, and <b>never applied back</b>. A wrong mapping can
/// brick a machine, and the write API has not been measured. The file exists so a cycle-time
/// change diffs under the device that owns it rather than vanishing into native XML — or,
/// for empty IO-Link slots that share a name, colliding onto one file.
/// </para>
/// <para>
/// Empty slots are listed here rather than written as files. Their identity is the parent's
/// sibling index, not a path: eight children named <c>&lt;Empty&gt;</c> are eight keys in
/// <see cref="Slots"/>, not eight writes to the same <c>%3CEmpty%3E.xml</c>.
/// </para>
/// </remarks>
public sealed record DeviceCatalog
{
    /// <summary>
    /// What every device catalog's file name ends with, whichever vendor exported it.
    /// </summary>
    /// <remarks>
    /// Neutral for the same reason <see cref="LibraryCatalog.FileSuffix"/> is, and used the
    /// same way: the shell recognises a changed catalog by suffix before any manifest is
    /// parsed. The writing side is one vendor's exporter; a parity test pins the two together.
    /// </remarks>
    public const string FileSuffix = ".devices.yaml";

    /// <summary>Tree-path marker for a grafted connector row. Shared with the shell pane.</summary>
    public const string ConnectorRowMarker = "::connector::";

    /// <summary>Tree-path marker for a grafted parameter row.</summary>
    public const string ParamRowMarker = "::param::";

    /// <summary>Tree-path marker for a grafted empty-slot row.</summary>
    public const string SlotRowMarker = "::slot::";

    /// <summary>Document format version. Zero means the file could not be read.</summary>
    public int Schema { get; init; }

    /// <summary>Which toolchain produced this file, lower-case.</summary>
    public string Vendor { get; init; } = string.Empty;

    /// <summary>The device object's own name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Path of the device within the export tree, forward slashes.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Empty slots under this device, keyed by the toolchain's sibling index.
    /// </summary>
    /// <remarks>
    /// Keys are the index as text ("0".."7" on the fixture). Values are the slot's name,
    /// almost always <c>&lt;Empty&gt;</c>. A plugged-in module is a real child device with
    /// its own catalog, not a slot here.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Slots { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<DeviceConnector> Connectors { get; init; } = [];

    public static readonly DeviceCatalog Empty = new();

    /// <summary>True when the file parsed and described a real device.</summary>
    public bool IsValid => Schema > 0;

    /// <summary>
    /// Parse one <c>*.devices.yaml</c> document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-written, and reads only the subset <c>versio_yaml.py</c> emits — a header
    /// mapping (including one nested scalar map for slots), then a list of mappings with
    /// nested single-level mappings for parameters. Never throws: same rule as
    /// <see cref="ProjectManifest.Parse"/>.
    /// </para>
    /// <para>
    /// Nested mappings are stored INTO the item as they are found rather than held in one
    /// pending slot — the same lesson <see cref="LibraryCatalog.Parse"/> recorded. Holding
    /// one loses the map the moment a sibling key follows it.
    /// </para>
    /// </remarks>
    public static DeviceCatalog Parse(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return Empty;
        }

        var top = new Dictionary<string, string>(StringComparer.Ordinal);
        var slots = new Dictionary<string, string>(StringComparer.Ordinal);
        var connectors = new List<DeviceConnector>();

        Dictionary<string, string>? item = null;
        Dictionary<string, Dictionary<string, string>>? nestedMaps = null;
        Dictionary<string, string>? nested = null;
        var itemIndent = 0;
        var inConnectors = false;
        var inSlots = false;

        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart(' ').Length;
            var text = line.TrimStart(' ');

            if (text.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush(item, nestedMaps, connectors);
                item = new Dictionary<string, string>(StringComparer.Ordinal);
                nestedMaps = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                nested = null;
                inSlots = false;
                text = text[2..].TrimStart();
                indent += 2;
                itemIndent = indent;
            }

            if (!CatalogYaml.Split(text, out var key, out var value))
            {
                continue;
            }

            if (indent == 0)
            {
                Flush(item, nestedMaps, connectors);
                item = null;
                nestedMaps = null;
                nested = null;

                if (key == "connectors")
                {
                    inConnectors = true;
                    inSlots = false;
                    continue;
                }

                if (key == "slots")
                {
                    inSlots = true;
                    inConnectors = false;
                    continue;
                }

                inConnectors = false;
                inSlots = false;
                top[key] = value;
                continue;
            }

            if (inSlots && item is null)
            {
                slots[CatalogYaml.Unquote(key)] = CatalogYaml.Unquote(value);
                continue;
            }

            if (!inConnectors || item is null || nestedMaps is null)
            {
                continue;
            }

            if (nested is not null && indent > itemIndent)
            {
                nested[CatalogYaml.Unquote(key)] = CatalogYaml.Unquote(value);
                continue;
            }

            if (value.Length == 0)
            {
                nested = new Dictionary<string, string>(StringComparer.Ordinal);
                nestedMaps[key] = nested;
                continue;
            }

            item[key] = value;
        }

        Flush(item, nestedMaps, connectors);

        var schema = int.TryParse(CatalogYaml.Value(top, "schema"), out var s) ? s : 0;
        return new DeviceCatalog
        {
            Schema = schema,
            Vendor = CatalogYaml.Value(top, "vendor"),
            Name = CatalogYaml.Value(top, "name"),
            Path = CatalogYaml.Value(top, "path"),
            Slots = slots,
            Connectors = connectors,
        };
    }

    private static void Flush(
        Dictionary<string, string>? item,
        Dictionary<string, Dictionary<string, string>>? nestedMaps,
        List<DeviceConnector> into)
    {
        if (item is null)
        {
            return;
        }

        var parameters =
            nestedMaps is not null &&
            nestedMaps.TryGetValue("parameters", out var read) &&
            read.Count > 0
                ? read
                : new Dictionary<string, string>(StringComparer.Ordinal);

        var connectorId = CatalogYaml.Value(item, "connectorId");
        var iface = CatalogYaml.Value(item, "interface");
        var moduleType = CatalogYaml.Value(item, "moduleType");
        if (string.IsNullOrEmpty(connectorId) &&
            string.IsNullOrEmpty(iface) &&
            string.IsNullOrEmpty(moduleType) &&
            parameters.Count == 0)
        {
            return;
        }

        into.Add(new DeviceConnector
        {
            ConnectorId = connectorId,
            Interface = iface,
            ModuleType = moduleType,
            Parameters = parameters,
        });
    }
}
