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
    [JsonPropertyName("guid")] public string Guid { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
}

/// <summary>The contents of an export's <c>_manifest.json</c>.</summary>
public sealed record ProjectManifest
{
    /// <summary>
    /// Manifest format version. 2 added <see cref="Skipped"/>.
    /// </summary>
    /// <remarks>
    /// Read rather than ignored, because an empty <see cref="Skipped"/> means two different
    /// things: nothing was skipped, or nobody was counting yet. Treating a schema-1 manifest
    /// as complete coverage would be the most comfortable possible lie.
    /// </remarks>
    [JsonPropertyName("schema")] public int Schema { get; init; } = 1;
    [JsonPropertyName("bindingId")] public string BindingId { get; init; } = string.Empty;
    [JsonPropertyName("project")] public string Project { get; init; } = string.Empty;
    [JsonPropertyName("objectCount")] public int ObjectCount { get; init; }
    [JsonPropertyName("objects")] public IReadOnlyList<ProjectObject> Objects { get; init; } = [];
    [JsonPropertyName("warnings")] public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Objects the export did not write. Empty on a schema-1 manifest.</summary>
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
    /// </remarks>
    public static ProjectManifest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<ProjectManifest>(json, Options) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
