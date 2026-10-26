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

    /// <summary>
    /// Which vendor's vocabulary <see cref="Type"/> is written in.
    /// </summary>
    /// <remarks>
    /// Carried per object rather than looked up, because one repository can hold projects from
    /// two toolchains and the type string alone does not say whose word it is. Defaults to
    /// CODESYS for a manifest written before schema 8 recorded it, which is a fact about those
    /// files rather than a guess.
    /// </remarks>
    public Plc.PlcVendor Vendor { get; init; } = Plc.PlcVendor.Codesys;

    /// <summary>The type as it is written beside the name.</summary>
    public string TypeLabel => ObjectTypeNames.Chip(Vendor, Type);
}

/// <summary>One object found by name: its identity and every file it was exported to.</summary>
public sealed record ObjectFiles(string Guid, IReadOnlyList<string> Paths);

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

    /// <summary>
    /// Project and object name to every file of that object - what a type reference resolves
    /// through. Keyed per project: two projects may each have their own <c>MotL</c>, and a
    /// member can only mean the one in its own project.
    /// </summary>
    private readonly Dictionary<(string Project, string Name), ObjectFiles> _byName;

    private ObjectIndex(Dictionary<string, NamedObject> byPath)
    {
        _byPath = byPath;
        _byName = byPath
            .GroupBy(e => (e.Value.Project, e.Value.Name), new ProjectNameComparer())
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    // One object per name: the first guid in path order. A second object
                    // with the same name in one project (possible across folders in some
                    // vendors' trees) is ambiguous, and resolving to the first is stable
                    // rather than right - say so if it ever matters.
                    var guid = g.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).First().Value.Guid;
                    return new ObjectFiles(
                        guid,
                        g.Where(e => e.Value.Guid == guid)
                         .Select(e => e.Key)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                         .ToList());
                },
                new ProjectNameComparer());
    }

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
                        Vendor = manifest.Vendor,
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
    /// The object named <paramref name="name"/> in <paramref name="project"/>, with every file
    /// it was exported to, or null when the export wrote no such object.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, as IEC identifiers are. A miss is the ordinary answer for a library or
    /// system type: those are referenced by name and never exported into the project.
    /// </remarks>
    public ObjectFiles? FindByName(string project, string name) =>
        _byName.GetValueOrDefault((project, name));

    /// <summary>
    /// Every object of <paramref name="project"/>, once each, with its files: what a "used by"
    /// search reads through (<see cref="ObjectLinks"/>). Ordered by name, so an answer is stable.
    /// </summary>
    public IReadOnlyList<(NamedObject Object, ObjectFiles Files)> ObjectsIn(string project) =>
        _byName
            .Where(e => string.Equals(e.Key.Project, project, StringComparison.OrdinalIgnoreCase))
            .Select(e => (_byPath[e.Value.Paths[0]], e.Value))
            .OrderBy(e => e.Item1.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private sealed class ProjectNameComparer : IEqualityComparer<(string Project, string Name)>
    {
        public bool Equals((string Project, string Name) x, (string Project, string Name) y) =>
            string.Equals(x.Project, y.Project, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Project, string Name) key) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Project),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
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
