namespace Versio.Core.Projects;

/// <summary>A changed file, named as the object the export wrote it from.</summary>
/// <remarks>
/// <see cref="Guid"/> rather than a path is the identity, for the same reason
/// <see cref="ProjectObject"/> says so: a GVL exports to both <c>.gvl.st</c> and
/// <c>.gvl.xml</c>, so one edited object is two changed files. Anything that counts objects
/// counts distinct GUIDs, or it reports one edit twice.
/// </remarks>
public sealed record NamedObject
{
    /// <summary>The object's name in the project — <c>FB_Motor</c>, never a filename.</summary>
    public required string Name { get; init; }

    /// <summary>The manifest's own type string, e.g. <c>POU</c>.</summary>
    /// <remarks>
    /// Kept raw and translated at the edge by <see cref="ObjectTypeNames"/>. A display string
    /// stored here would be one more thing that has to agree with the exporter's table.
    /// </remarks>
    public required string Type { get; init; }

    /// <summary>The object's identity. Several files of one object share it.</summary>
    public required string Guid { get; init; }

    /// <summary>The project it belongs to, without the <c>.project</c> extension.</summary>
    public required string Project { get; init; }

    /// <summary>
    /// Exported for review and never applied back.
    /// </summary>
    /// <remarks>
    /// Library references and the native half of a task configuration, and the panel is where
    /// it matters: the file is in the changed list, it looks exactly like an editable one, and
    /// staging an edit to it commits a change that will never reach the project — the next
    /// export rewrites the file from the project and the edit is gone. <c>STATUS.md</c> carried
    /// that as a known limitation for a long time with "the panel does not say so yet" beside
    /// it; this is the flag that closed it, and the source of truth is
    /// <see cref="ExportCoverage.IsExportOnly(string)"/> rather than a list of filenames.
    /// <para>
    /// <b>Asking the predicate rather than comparing to one constant is the point.</b> This
    /// read <c>form == "LIBRARIES"</c> until 2026-08-15, so the day a second export-only form
    /// arrived the glyph was silently right for one file and silently wrong for another.
    /// </para>
    /// </remarks>
    public bool IsExportOnly { get; init; }

    /// <summary>The type as it is written beside the name.</summary>
    public string TypeLabel => ObjectTypeNames.Chip(Type);
}

/// <summary>
/// Every exported file in the workspace, by the path git reports it under.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one thing a generic git client cannot do.</b> Git reports
/// <c>plc/Device/Plc Logic/Application/FB_Motor.st</c>; Versio knows that is the function block
/// <c>FB_Motor</c>, because the export wrote both the file and the manifest that describes it.
/// The panel had all of that in the same process and threw it away at the list boundary — every
/// row a raw path, in the column with the least room for one.
/// </para>
/// <para>
/// <b>Built from what the tree already loads.</b> <c>SyncViewModel</c> parses each manifest to
/// build the project tree and holds the result in a <see cref="WorkspaceProject"/>; this takes
/// the same list. No second read, no second cadence, and no way for the two views to disagree
/// about what an object is called — which is the defect the workspace tree was itself created
/// to fix.
/// </para>
/// <para>
/// <b>A path with no object behind it is not an object.</b> <c>Lookup</c> returns null for
/// <c>_manifest.json</c>, for a stray file under an export root, and for every ordinary file in
/// the repository. Inferring a type from an extension would label <c>notes.st</c> as a POU, and
/// a wrong object name is worse than an honest path: the path is at least what git will commit.
/// </para>
/// <para>
/// Immutable and rebuilt with the tree, so it can be handed across threads like the tree
/// snapshot beside it.
/// </para>
/// </remarks>
public sealed class ObjectIndex
{
    /// <summary>No projects, or none exported yet. Every lookup misses.</summary>
    public static readonly ObjectIndex Empty = new([]);

    private readonly Dictionary<string, NamedObject> _byPath;

    private ObjectIndex(Dictionary<string, NamedObject> byPath) => _byPath = byPath;

    /// <summary>How many exported files are indexed.</summary>
    public int Count => _byPath.Count;

    /// <summary>Index every exported file of every project.</summary>
    /// <param name="projects">The same list the workspace tree is built from.</param>
    public static ObjectIndex Of(IReadOnlyList<WorkspaceProject> projects)
    {
        var byPath = new Dictionary<string, NamedObject>(StringComparer.OrdinalIgnoreCase);

        // Ordered so that the winner of a collision is stable rather than whichever project the
        // caller happened to list first. Two projects claiming one path means overlapping export
        // roots, which is a misconfiguration the orphan scanner reports; the index's job is to
        // not throw in front of somebody while that is true.
        foreach (var project in projects
                     .Where(p => p.Manifest is not null)
                     .OrderBy(p => p.ProjectPath, StringComparer.OrdinalIgnoreCase))
        {
            var manifest = project.Manifest!;
            var root = Normalize(project.ExportPath).TrimEnd('/');
            var name = ProjectName(manifest, project.ProjectPath);

            foreach (var obj in manifest.Objects)
            {
                if (string.IsNullOrEmpty(obj.Path))
                {
                    continue;
                }

                byPath.TryAdd(
                    Normalize(ProjectTreeBuilder.Combine(root, Normalize(obj.Path))),
                    new NamedObject
                    {
                        // An object the exporter wrote with no name is shown by its filename
                        // rather than as an empty chip with nothing beside it — the same
                        // fallback the tree leaf makes, for the same reason.
                        Name = string.IsNullOrEmpty(obj.Name) ? FileName(obj.Path) : obj.Name,
                        Type = obj.Type,
                        Guid = obj.Guid,
                        Project = name,
                        IsExportOnly = ExportCoverage.IsExportOnly(obj.Form),
                    });
            }
        }

        return byPath.Count == 0 ? Empty : new ObjectIndex(byPath);
    }

    /// <summary>The object this path was exported from, or null if it was not.</summary>
    /// <param name="repoRelativePath">A path exactly as git reports it.</param>
    public NamedObject? Lookup(string? repoRelativePath)
    {
        if (string.IsNullOrEmpty(repoRelativePath))
        {
            return null;
        }

        return _byPath.GetValueOrDefault(Normalize(repoRelativePath));
    }

    /// <summary>
    /// The project's name for a sentence: the manifest's, without the extension.
    /// </summary>
    /// <remarks>
    /// The manifest records <c>RefrigeratorControl.project</c>, which is the filename and reads
    /// as one. "3 POUs changed in RefrigeratorControl" is the sentence an engineer would write.
    /// Falls back to the binding's own path for a manifest written before the field existed.
    /// </remarks>
    private static string ProjectName(ProjectManifest manifest, string projectPath)
    {
        var raw = !string.IsNullOrEmpty(manifest.Project)
            ? manifest.Project
            : FileName(Normalize(projectPath));

        var dot = raw.LastIndexOf('.');
        return dot > 0 ? raw[..dot] : raw;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string FileName(string path) =>
        path.Replace('\\', '/').Split('/').LastOrDefault() is { Length: > 0 } name ? name : path;
}
