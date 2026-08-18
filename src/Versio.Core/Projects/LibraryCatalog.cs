namespace Versio.Core.Projects;

/// <summary>
/// One library a project depends on, as the project declares it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Vendor-neutral on purpose.</b> Every PLC toolchain has some notion of a dependency on a
/// versioned, third-party block of code — CODESYS libraries, TwinCAT libraries, Rockwell
/// Add-On Instructions, Siemens global libraries — and all of them answer the same three
/// questions a reviewer asks: what is it, which version, and did anybody pin it. So this type
/// lives beside the manifest in <c>Projects/</c> rather than under <c>Plc/Codesys/</c>, and
/// nothing in it names a vendor. What is vendor-specific is the code that *produces* the file:
/// <c>versio_libraries.py</c> for CODESYS, and its equivalent for whatever comes second.
/// </para>
/// <para>
/// <b>What a reference declares is not what it resolves to.</b> A placeholder can ask for
/// <c>Standard, *</c> and resolve to <c>3.5.22.0</c> only because that version happens to be
/// installed on the machine that exported. The resolved value is therefore deliberately absent
/// from the file and from this type: recording it would make the mirror differ between two
/// engineers sharing one repository, and neither of them would be wrong. <see cref="Redirection"/>
/// is the exception, and it belongs here precisely because it is stored in the project.
/// </para>
/// </remarks>
public sealed record LibraryReference
{
    /// <summary>The reference as the toolchain names it. Never empty in a valid file.</summary>
    public required string Name { get; init; }

    /// <summary>The namespace IEC code uses to reach into the library.</summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>Placeholder name without its sigil, when the reference is a placeholder.</summary>
    public string Placeholder { get; init; } = string.Empty;

    /// <summary>What the placeholder asks for, which may carry a wildcard.</summary>
    public string DefaultResolution { get; init; } = string.Empty;

    /// <summary>
    /// A fixed version the <b>project</b> pins the placeholder to, when it does.
    /// </summary>
    /// <remarks>
    /// Project state, unlike the effective resolution: this is what somebody chose in the
    /// placeholder dialog, and a change to it is a change a reviewer needs to see.
    /// </remarks>
    public string Redirection { get; init; } = string.Empty;

    /// <summary>Fixed version, for a reference that names one rather than a placeholder.</summary>
    public string Version { get; init; } = string.Empty;

    public string Company { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Added implicitly by the toolchain rather than by a person.
    /// </summary>
    /// <remarks>
    /// Worth surfacing separately: on the fixture, 18 of 21 references in the application's
    /// manager are system libraries pulled in by the visualisation plug-in. Listing them beside
    /// the three somebody actually chose, with nothing to tell them apart, is how the three get
    /// lost.
    /// </remarks>
    public bool SystemLibrary { get; init; }

    /// <summary>Missing this library is not reported as an error.</summary>
    public bool Optional { get; init; }

    /// <summary>Access requires the namespace prefix.</summary>
    public bool QualifiedOnly { get; init; }

    /// <summary>The library's symbols are published in the containing project.</summary>
    public bool PublishSymbols { get; init; }

    /// <summary>
    /// Library parameters, name to IEC expression.
    /// </summary>
    /// <remarks>
    /// Real, diffable project state rather than metadata: the fixture pins Util's
    /// <c>IBLOCKSIZE</c> to 22800, and changing that number changes what the machine does.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Parameters { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>What to show as the version, whichever way the reference expresses it.</summary>
    /// <remarks>
    /// A placeholder has no version of its own: the pin is the answer when there is one, and
    /// the request — wildcard and all — when there is not. Returning an empty string for a
    /// perfectly well-specified placeholder would make the tree row look broken.
    /// </remarks>
    public string DisplayVersion =>
        !string.IsNullOrEmpty(Version) ? Version
        : !string.IsNullOrEmpty(Redirection) ? Redirection
        : DefaultResolution;

    /// <summary>Whether two references record the same facts.</summary>
    /// <remarks>
    /// <para>
    /// Not <c>Equals</c>, because the generated record equality compares
    /// <see cref="Parameters"/> by reference — two references with identical parameter pairs
    /// read as different, and every fresh parse allocates a fresh dictionary.
    /// </para>
    /// <para>
    /// Written as record equality over clones whose dictionaries are swapped for one shared
    /// instance, rather than as a field-by-field comparison, so that a field added to this
    /// type later is compared without anyone remembering to extend a list here. A hand-written
    /// comparison that silently ignored a new field would mark a changed library as unchanged,
    /// which is the failure this method exists to report.
    /// </para>
    /// </remarks>
    public bool ContentEquals(LibraryReference other)
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
/// The contents of one <c>*.libraries.yaml</c> file.
/// </summary>
/// <remarks>
/// <para>
/// Written by the export, read here, and <b>never applied back</b>. Libraries are changed in
/// the IDE (plan §3.10, Tier 3): adding one through a scripting API is a different operation
/// from importing an object, and getting it wrong rewires what the machine compiles against.
/// The exporter's <c>_coverage.md</c> says so in the tree, and the importer skips these files
/// by suffix rather than handing YAML to a toolchain that expects XML.
/// </para>
/// </remarks>
public sealed record LibraryCatalog
{
    /// <summary>
    /// What every catalog file's name ends with, whichever vendor exported it.
    /// </summary>
    /// <remarks>
    /// Part of the neutral format, not a vendor detail: the file is
    /// <c>&lt;manager&gt;.libraries.yaml</c> for the same reason its fields are vendor-neutral,
    /// and the shell needs the suffix to know which changed files have a HEAD copy worth
    /// fetching <i>before</i> any manifest has been parsed. The CODESYS path mapper carries
    /// the same string on the writing side; a parity test pins the two together.
    /// </remarks>
    public const string FileSuffix = ".libraries.yaml";

    /// <summary>
    /// Document format version, so an older file is not mistaken for a complete one.
    /// </summary>
    /// <remarks>
    /// Zero means the file could not be read, or was written before the schema existed. The
    /// distinction that matters is the manifest's: an empty reference list on a file that
    /// declares its schema means the project has no libraries, while an empty list on a file
    /// with no schema means nobody was recording them yet.
    /// </remarks>
    public int Schema { get; init; }

    /// <summary>Which toolchain produced this file, lower-case.</summary>
    public string Vendor { get; init; } = string.Empty;

    /// <summary>The library manager object's own name.</summary>
    public string Manager { get; init; } = string.Empty;

    /// <summary>Path of the manager within the export tree, forward slashes.</summary>
    public string Path { get; init; } = string.Empty;

    public IReadOnlyList<LibraryReference> References { get; init; } = [];

    public static readonly LibraryCatalog Empty = new();

    /// <summary>True when the file parsed and described a real manager.</summary>
    public bool IsValid => Schema > 0;

    /// <summary>
    /// Parse one <c>*.libraries.yaml</c> document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hand-written, and reads only the subset <c>versio_yaml.py</c> emits</b> — a header
    /// mapping, then a list of mappings, with nested single-level mappings for parameters. No
    /// anchors, flow style, multi-line scalars or second documents. That is what makes a YAML
    /// reader affordable inside a project with no third-party dependencies: the format is
    /// small because both halves of it are ours.
    /// </para>
    /// <para>
    /// <b>Never throws.</b> Same rule as <see cref="ProjectManifest.Parse"/>: this is read to
    /// draw a tree, the file can be mid-write or truncated by a killed export, and taking the
    /// window down over it would be the wrong trade.
    /// </para>
    /// </remarks>
    public static LibraryCatalog Parse(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return Empty;
        }

        var top = new Dictionary<string, string>(StringComparer.Ordinal);
        var references = new List<LibraryReference>();

        // The item currently being filled, its nested mappings by owning key, and the
        // mapping still being read.
        //
        // Nested mappings are stored INTO the item as they are found rather than held in
        // one pending slot. Holding one was the first design and it lost the map the
        // moment a sibling key followed it — invisible against the fixture, because the
        // emitter writes `parameters` last, and caught only by breaking the reader on
        // purpose and watching every fixture test pass anyway.
        Dictionary<string, string>? item = null;
        Dictionary<string, Dictionary<string, string>>? nestedMaps = null;
        Dictionary<string, string>? nested = null;
        var itemIndent = 0;
        var inReferences = false;

        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart(' ').Length;
            var text = line.TrimStart(' ');

            // A new list item. Its first key rides on the same line as the dash, so the
            // dash's own width is what sets the indent every later key of this item lines
            // up against.
            if (text.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush(item, nestedMaps, references);
                item = new Dictionary<string, string>(StringComparer.Ordinal);
                nestedMaps = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                nested = null;
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
                // Back at the top level: whatever list we were in has ended.
                Flush(item, nestedMaps, references);
                item = null;
                nestedMaps = null;
                nested = null;

                if (key == "references")
                {
                    inReferences = true;
                    continue;
                }

                inReferences = false;
                top[key] = value;
                continue;
            }

            if (!inReferences || item is null || nestedMaps is null)
            {
                continue;
            }

            // Deeper than this item's own keys: it belongs to the mapping opened above.
            // Checked before the empty-value case, so a nested key whose value is empty
            // stays in the mapping instead of opening another one.
            if (nested is not null && indent > itemIndent)
            {
                nested[CatalogYaml.Unquote(key)] = CatalogYaml.Unquote(value);
                continue;
            }

            // No "close the open mapping" step here, deliberately. Indent is the only
            // thing that decides where a key belongs, and adding a second mechanism that
            // agreed with it would be code no test could ever fail on — which is exactly
            // how it was found: deleting the line changed nothing anywhere.
            if (value.Length == 0)
            {
                // A key with nothing after the colon opens a nested mapping — but it is
                // also how the emitter writes a field whose value is absent. Which one it
                // is only becomes clear on the next line, so open one and let it stay
                // empty; an empty mapping and an absent value mean the same thing here.
                nested = new Dictionary<string, string>(StringComparer.Ordinal);
                nestedMaps[key] = nested;
                continue;
            }

            item[key] = value;
        }

        Flush(item, nestedMaps, references);

        var schema = int.TryParse(CatalogYaml.Value(top, "schema"), out var s) ? s : 0;
        return new LibraryCatalog
        {
            Schema = schema,
            Vendor = CatalogYaml.Value(top, "vendor"),
            Manager = CatalogYaml.Value(top, "manager"),
            Path = CatalogYaml.Value(top, "path"),
            References = references,
        };
    }

    /// <summary>Turn the key/value pairs collected for one item into a reference.</summary>
    private static void Flush(
        Dictionary<string, string>? item,
        Dictionary<string, Dictionary<string, string>>? nestedMaps,
        List<LibraryReference> into)
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

        var name = CatalogYaml.Value(item, "name");
        if (string.IsNullOrEmpty(name))
        {
            // A reference with no name is not a reference. Dropping it is better than
            // showing a nameless row: the file is machine-written, so this only happens
            // when it was truncated mid-write, and half an entry is not information.
            return;
        }

        into.Add(new LibraryReference
        {
            Name = name,
            Namespace = CatalogYaml.Value(item, "namespace"),
            Placeholder = CatalogYaml.Value(item, "placeholder"),
            DefaultResolution = CatalogYaml.Value(item, "defaultResolution"),
            Redirection = CatalogYaml.Value(item, "redirection"),
            Version = CatalogYaml.Value(item, "version"),
            Company = CatalogYaml.Value(item, "company"),
            Title = CatalogYaml.Value(item, "title"),
            SystemLibrary = CatalogYaml.Flag(item, "systemLibrary"),
            Optional = CatalogYaml.Flag(item, "optional"),
            QualifiedOnly = CatalogYaml.Flag(item, "qualifiedOnly"),
            PublishSymbols = CatalogYaml.Flag(item, "publishSymbols"),
            Parameters = parameters,
        });
    }
}
