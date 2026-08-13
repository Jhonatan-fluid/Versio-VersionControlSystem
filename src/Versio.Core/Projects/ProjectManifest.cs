using System.Text.Json;
using System.Text.Json.Serialization;

namespace Versio.Core.Projects;

/// <summary>One object as the export recorded it.</summary>
/// <remarks>
/// <b><see cref="Guid"/> is the identity, not <see cref="Path"/>.</b> A single object can
/// produce several files — a GVL exports both <c>.gvl.st</c> and <c>.gvl.xml</c> — so the
/// manifest holds one record per file and several records can share a GUID. Treating the path
/// as the identity would show one object as two, and would turn a rename into a delete and an
/// add (plan R29).
/// </remarks>
public sealed record ProjectObject
{
    [JsonPropertyName("guid")] public string Guid { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    /// <summary>CODESYS object type, e.g. <c>POU</c>, <c>GVL</c>, <c>VISUALISATION</c>.</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;

    /// <summary>How this file represents the object: <c>ST_DECL</c>, <c>NATIVE</c>, …</summary>
    [JsonPropertyName("form")] public string Form { get; init; } = string.Empty;

    /// <summary>Path within the export directory, forward slashes.</summary>
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;

    /// <summary>
    /// The GUID of the object that owns this one, for a sub-POU member. Empty otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Methods, actions, transitions and properties belong to a POU, and a property's
    /// accessors belong to the property — so this is a chain, not one level. The export writes
    /// a member <i>beside</i> its owner as <c>clsLight.Light.xml</c>, which means the on-disk
    /// layout cannot express ownership: the panel drew a function block's methods as its
    /// siblings, and four <c>InitRoom</c> methods on four different rooms as four rows in one
    /// folder.
    /// </para>
    /// <para>
    /// A GUID rather than the owner's name or path, because a rename must not re-parent
    /// anything and a name is free to contain a dot. Written from manifest schema 4 —
    /// <b>an older manifest has no parents, which reads as a flat project rather than as an
    /// error</b>, and starts nesting when the project is next exported.
    /// </para>
    /// </remarks>
    [JsonPropertyName("parent")] public string Parent { get; init; } = string.Empty;

    [JsonPropertyName("hash")] public string Hash { get; init; } = string.Empty;
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
}

/// <summary>An object the export deliberately did not write, and why.</summary>
/// <remarks>
/// <see cref="Reason"/> is a stable machine string produced by the exporter, not prose:
/// <c>SERVICE_OBJECT</c>, <c>TEMPLATE_ONLY</c>, <c>COVERED_ELSEWHERE</c> and
/// <c>NOT_IMPLEMENTED</c>. Only the last of those is a hole; the others are decisions whose
/// content is carried somewhere else. Collapsing them into one "skipped" count is how a hole
/// stays invisible.
/// </remarks>
public sealed record SkippedObject
{
    /// <summary>
    /// Deliberately absent: a skipped object records no GUID.
    /// </summary>
    /// <remarks>
    /// CODESYS re-mints the project-level <c>__VisualizationStyle</c>'s GUID on every open, so
    /// recording GUIDs here made the manifest differ on every export. A skipped object has
    /// nothing pointing at it, so <see cref="Path"/> and <see cref="Type"/> are identity
    /// enough — unlike an exported object, where the GUID is what makes a move a move.
    /// </remarks>
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
}

/// <summary>Something the export noticed and could not act on.</summary>
/// <remarks>
/// <para>
/// A record rather than a sentence, because <see cref="Code"/> is a stable machine string the
/// exporter, the manifest, <c>_coverage.md</c> and the tests all agree on — the same rule
/// <see cref="SkippedObject.Reason"/> follows. Prose would be reworded and silently break
/// every reader of it.
/// </para>
/// <para>
/// <b>This shape was already what <c>versio_export.py</c> wrote.</b> The model declared these
/// as plain strings, and the mismatch discarded the whole manifest for any project that
/// produced a single warning — see <c>ProjectManifestTests</c>.
/// </para>
/// </remarks>
public sealed record ExportWarning
{
    /// <summary>Stable machine string, e.g. <c>UNKNOWN_TYPE_GUID</c>.</summary>
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;

    /// <summary>The object it concerns, as the exporter walked to it.</summary>
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;

    /// <summary>Whatever the code needs to be actionable — for a type GUID, the GUID.</summary>
    [JsonPropertyName("detail")] public string Detail { get; init; } = string.Empty;
}

/// <summary>The contents of an export's <c>_manifest.json</c>.</summary>
public sealed record ProjectManifest
{
    /// <summary>
    /// Manifest format version. 2 added <see cref="Skipped"/>; 3 exports library references;
    /// 4 records each member's owner in <see cref="ProjectObject.Parent"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read rather than ignored, because an empty <see cref="Skipped"/> means two different
    /// things: nothing was skipped, or nobody was counting yet. Treating a schema-1 manifest
    /// as complete coverage would be the most comfortable possible lie.
    /// </para>
    /// <para>
    /// 3 is the same ambiguity one level along. Library references were skipped as
    /// <c>NOT_IMPLEMENTED</c> up to schema 2 and are exported from 3, so "no records with form
    /// <c>LIBRARIES</c>" means <i>nobody was writing them yet</i> on a schema-2 manifest and
    /// <i>this project has no library manager</i> on a schema-3 one. Reading the first as the
    /// second reports full coverage over the exact hole that work closed.
    /// </para>
    /// </remarks>
    [JsonPropertyName("schema")] public int Schema { get; init; } = 1;
    [JsonPropertyName("bindingId")] public string BindingId { get; init; } = string.Empty;
    [JsonPropertyName("project")] public string Project { get; init; } = string.Empty;
    [JsonPropertyName("objectCount")] public int ObjectCount { get; init; }
    [JsonPropertyName("objects")] public IReadOnlyList<ProjectObject> Objects { get; init; } = [];
    /// <summary>What the export noticed and could not act on.</summary>
    /// <seealso cref="ExportWarning"/>
    [JsonPropertyName("warnings")] public IReadOnlyList<ExportWarning> Warnings { get; init; } = [];

    /// <summary>Objects the export did not write. Empty on a schema-1 manifest.</summary>
    /// <seealso cref="Schema"/>
    [JsonPropertyName("skipped")] public IReadOnlyList<SkippedObject> Skipped { get; init; } = [];

    public static readonly ProjectManifest Empty = new();

    /// <summary>
    /// Read a manifest, or <see cref="Empty"/> if it cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never throws.</b> This is read to draw a tree, and a manifest can legitimately be
    /// mid-write, truncated by a killed export, or written by a newer Versio. Refusing to draw
    /// anything — or worse, taking the window down — over a file that is only ever a
    /// convenience is the wrong trade: the export tree on disk is the truth, and the manifest
    /// is how it is read quickly.
    /// </para>
    /// <para>
    /// Parsing lives in Core and takes a string, so it stays testable without a filesystem;
    /// the caller does the reading.
    /// </para>
    /// <para>
    /// <b>And never throws away more than it has to.</b> Every field is read on its own, so a
    /// field this build cannot make sense of costs that field and leaves the rest standing.
    /// The whole-document <c>Deserialize</c> this replaced did the opposite: <c>warnings</c>
    /// arriving as records rather than strings — which is what the exporter had always
    /// written — discarded 56 objects, drew the panel as a flat list of raw paths and reported
    /// coverage as unknown, with nothing said anywhere.
    /// </para>
    /// </remarks>
    public static ProjectManifest Parse(string json) => Parse(json, out _);

    /// <summary>
    /// Read a manifest, naming anything in it that could not be read.
    /// </summary>
    /// <param name="problems">
    /// One sentence per field or element that was skipped; empty when the file was read whole.
    /// </param>
    /// <remarks>
    /// The overload exists because <see cref="Empty"/> is indistinguishable from a manifest
    /// that genuinely describes nothing, and that ambiguity is what let the drift above live
    /// through a whole feature: the panel drew an empty tree and had nothing to say about why.
    /// A caller that can report — the shell, which has a log — takes this one.
    /// </remarks>
    public static ProjectManifest Parse(string json, out IReadOnlyList<string> problems)
    {
        var found = new List<string>();
        problems = found;

        // An absent or empty manifest is a project that has never been exported, which is a
        // state rather than a fault. Nothing to report.
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            found.Add($"the file is not valid JSON: {ex.Message}");
            return Empty;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                found.Add($"the file holds {root.ValueKind}, not a JSON object");
                return Empty;
            }

            return new ProjectManifest
            {
                Schema = Scalar(root, "schema", Empty.Schema, found),
                BindingId = Scalar(root, "bindingId", Empty.BindingId, found),
                Project = Scalar(root, "project", Empty.Project, found),
                ObjectCount = Scalar(root, "objectCount", Empty.ObjectCount, found),
                Objects = Items<ProjectObject>(root, "objects", found),
                Skipped = Items<SkippedObject>(root, "skipped", found),
                Warnings = Items<ExportWarning>(root, "warnings", found),
            };
        }
    }

    /// <summary>One value, or the default with a note saying the value was not usable.</summary>
    private static T Scalar<T>(
        JsonElement root, string name, T fallback, List<string> problems)
    {
        if (!TryProperty(root, name, out var value))
        {
            return fallback;
        }

        try
        {
            return value.Deserialize<T>(Options) ?? fallback;
        }
        catch (JsonException ex)
        {
            problems.Add($"'{name}' could not be read: {ex.Message}");
            return fallback;
        }
    }

    /// <summary>
    /// One array, element by element.
    /// </summary>
    /// <remarks>
    /// Deserialized per element rather than as a list, so one malformed record costs one row
    /// of the tree instead of all of them. An export killed mid-write is the case that makes
    /// this worth the loop.
    /// </remarks>
    private static IReadOnlyList<T> Items<T>(
        JsonElement root, string name, List<string> problems)
    {
        if (!TryProperty(root, name, out var value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"'{name}' holds {value.ValueKind}, not an array");
            return [];
        }

        var items = new List<T>();
        var index = 0;

        foreach (var element in value.EnumerateArray())
        {
            try
            {
                if (element.Deserialize<T>(Options) is { } item)
                {
                    items.Add(item);
                }
            }
            catch (JsonException ex)
            {
                problems.Add($"'{name}[{index}]' could not be read: {ex.Message}");
            }

            index++;
        }

        return items;
    }

    /// <summary>Look a property up without caring how it was cased.</summary>
    /// <remarks>
    /// The exact name first, because that is what the exporter writes and it is the fast path;
    /// the scan is only reached by a producer that cased something differently. Kept because
    /// the parser this replaced set <c>PropertyNameCaseInsensitive</c>, and quietly dropping
    /// that alongside a bug fix is how a second bug gets attributed to the first.
    /// </remarks>
    private static bool TryProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value))
        {
            return value.ValueKind != JsonValueKind.Null;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return value.ValueKind != JsonValueKind.Null;
            }
        }

        value = default;
        return false;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
