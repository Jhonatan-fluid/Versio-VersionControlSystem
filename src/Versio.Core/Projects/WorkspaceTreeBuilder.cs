using Versio.Core.Git;

namespace Versio.Core.Projects;

/// <summary>One tracked project, as the workspace tree needs to see it.</summary>
/// <remarks>
/// The manifest is handed in already parsed rather than read here, because reading it is IO
/// and this assembly does none. A null manifest is the ordinary state of a project that is
/// tracked and has never been exported, not an error.
/// </remarks>
public sealed record WorkspaceProject
{
    /// <summary>Repo-relative path of the <c>.project</c> file.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Repo-relative root of its export tree.</summary>
    public required string ExportPath { get; init; }

    /// <summary>The export's manifest, or null when it has never been exported.</summary>
    public ProjectManifest? Manifest { get; init; }

    /// <summary>
    /// The project's parsed library catalogs, one per manager. Empty when none were read.
    /// </summary>
    /// <remarks>
    /// Same contract as <see cref="Manifest"/>: parsed by the caller, because reading them is
    /// IO. Empty draws the managers without reference rows, which is what every mirror showed
    /// before the catalogs existed — a degraded view, never an error.
    /// </remarks>
    public IReadOnlyList<LibrarySnapshot> Libraries { get; init; } = [];

    /// <summary>The project's parsed task catalogs. Same contract as <see cref="Libraries"/>.</summary>
    public IReadOnlyList<TaskSnapshot> Tasks { get; init; } = [];

    /// <summary>Export state in a few words, shown on the project row instead of a diff.</summary>
    public string ExportState { get; init; } = string.Empty;

    /// <summary>
    /// A toolchain run is in flight for this project right now.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ExportState"/> rather than inferred from its wording, because
    /// the wording is a sentence for a human and matching on it would make the row's colour
    /// depend on a string somebody is free to rephrase. The state says <i>what</i>; this says
    /// <i>whether it is happening now</i>, which is what decides whether the row reads as an
    /// activity or as a fact.
    /// </remarks>
    public bool Busy { get; init; }

    /// <summary>
    /// Show this project even when nothing under it changed.
    /// </summary>
    /// <remarks>
    /// For the states a clean working tree cannot express and the user still has to see: a
    /// project tracked but never exported, one whose mirror is behind, one holding a direction
    /// lock, one that failed. Hiding those because git happens to report no changed file would
    /// be hiding exactly the projects that need attention — git has nothing to say about a
    /// project whose mirror was never written.
    /// </remarks>
    public bool NeedsAttention { get; init; }
}

/// <summary>
/// Assembles the one tree the left panel shows: every tracked project with its objects
/// underneath it, and every other changed file beside them.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the sync panel and the changed-files panel were answering the same
/// question twice and disagreeing about the answer. A changed <c>.st</c> appeared in the file
/// list as <c>fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/Simulation.st</c>
/// and in the sync panel as <c>Simulation</c> under <c>Application</c>, and nothing said they
/// were the same edit. One tree, and the question does not arise.
/// </para>
/// <para>
/// <b>The object tree is full, not filtered to what changed.</b> The changed-only filter made
/// sense when the tree was a secondary view somebody opened to answer "what did I change";
/// as the primary view it has to answer "where in the machine am I", which needs every object
/// present and the changed ones marked. <see cref="ProjectTreeOptions.ChangedOnly"/> still
/// works and is still tested — the shell simply does not ask for it.
/// </para>
/// <para>
/// Pure, so the rules below are tested without a repository, an export or a UI.
/// </para>
/// </remarks>
public static class WorkspaceTreeBuilder
{
    /// <param name="projects">Every tracked project, with whatever manifest it has.</param>
    /// <param name="changes">What git reports for the working tree.</param>
    /// <param name="options">Search and, if anyone still wants it, the changed-only filter.</param>
    public static IReadOnlyList<ProjectTreeNode> Build(
        IReadOnlyList<WorkspaceProject> projects,
        IReadOnlyList<FileChange> changes,
        ProjectTreeOptions? options = null)
    {
        var opts = options ?? new ProjectTreeOptions { ChangedOnly = false };

        var status = new Dictionary<string, ChangeKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            status[Normalize(change.Path)] = change.Effective;
        }

        var roots = new List<ProjectTreeNode>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var searching = !string.IsNullOrWhiteSpace(opts.Search);

        foreach (var project in projects.OrderBy(p => p.ProjectPath, StringComparer.OrdinalIgnoreCase))
        {
            var node = BuildProjectNode(project, changes, opts, claimed, status);

            // Built first and dropped after, never skipped: building it is what claims the
            // project's files, and a project skipped before that would leak its own exported
            // paths back into the panel as loose rows.
            if (opts.HideUnchangedProjects &&
                !node.HasChanges &&
                !project.NeedsAttention &&
                !(searching && node.Children.Count > 0))
            {
                continue;
            }

            roots.Add(node);
        }

        // Everything git reports that no project accounted for. Sorted by path because that
        // is the only ordering a flat list of unrelated files has.
        foreach (var change in changes
                     .Where(c => !claimed.Contains(Normalize(c.Path)))
                     .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            var path = Normalize(change.Path);
            if (!ProjectTreeBuilder.Matches(path, opts.Search))
            {
                continue;
            }

            roots.Add(new ProjectTreeNode
            {
                Name = path,
                Path = path,
                Kind = ProjectNodeKind.File,
                Files = [path],
                Status = change.Effective,
            });
        }

        return roots;
    }

    /// <summary>
    /// One project row: the <c>.project</c> itself, with its export tree beneath it.
    /// </summary>
    /// <remarks>
    /// Public because the repo explorer grafts this same node into a folder tree. A second
    /// object-tree builder would be a second answer to "what is this project", and the two
    /// would drift — which is the leak this method exists to prevent.
    /// </remarks>
    public static ProjectTreeNode BuildProjectNode(
        WorkspaceProject project,
        IReadOnlyList<FileChange> changes,
        ProjectTreeOptions? options = null,
        HashSet<string>? claimed = null,
        IReadOnlyDictionary<string, ChangeKind>? status = null)
    {
        var opts = options ?? new ProjectTreeOptions { ChangedOnly = false };
        claimed ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (status is null)
        {
            var map = new Dictionary<string, ChangeKind>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes)
            {
                map[Normalize(change.Path)] = change.Effective;
            }

            status = map;
        }

        var projectPath = Normalize(project.ProjectPath);
        var exportRoot = Normalize(project.ExportPath).TrimEnd('/');

        claimed.Add(projectPath);

        var manifest = project.Manifest ?? ProjectManifest.Empty;
        var tree = ProjectTreeBuilder.Build(
            manifest, exportRoot, changes, opts, project.Libraries, project.Tasks);

        // Everything the manifest accounted for is now represented by an object node.
        foreach (var file in manifest.Objects
                     .Where(o => !string.IsNullOrEmpty(o.Path))
                     .Select(o => ProjectTreeBuilder.Combine(exportRoot, Normalize(o.Path))))
        {
            claimed.Add(file);
        }

        var children = new List<ProjectTreeNode>(tree.Children);
        children.AddRange(UnmappedExportFiles(changes, exportRoot, opts, claimed));

        // Counted over the whole subtree rather than taken from the export tree alone, so a
        // stray file under the export root still raises the project's badge.
        var changedDescendants = children.Sum(
            c => c.ChangedDescendants + (c.Status != ChangeKind.Unmodified ? 1 : 0));

        return new ProjectTreeNode
        {
            Name = FileName(projectPath),
            Path = projectPath,
            Kind = ProjectNodeKind.Project,
            Detail = project.ExportState,
            Busy = project.Busy,

            // The .project is binary, so this is never opened as a diff — but it is still a
            // changed file, and a project row that did not say so would be the one place in
            // the panel where a change was invisible.
            Status = status.TryGetValue(projectPath, out var own) ? own : ChangeKind.Unmodified,
            Files = [projectPath],
            Children = children,
            ChangedDescendants = changedDescendants,
            RollUp = children
                .Select(c => ProjectTreeBuilder.Stronger(c.Status, c.RollUp))
                .Aggregate(ChangeKind.Unmodified, ProjectTreeBuilder.Stronger),
        };
    }

    /// <summary>
    /// Changed files under the export root that the manifest does not describe.
    /// </summary>
    /// <remarks>
    /// <c>_manifest.json</c> is the obvious one — it changes on every export and describes the
    /// others rather than appearing among them. Dropping these would be worse than showing
    /// them: a file git reports as changed, in a directory Versio owns, that appears nowhere
    /// in the panel is indistinguishable from Versio having lost it.
    /// </remarks>
    private static IEnumerable<ProjectTreeNode> UnmappedExportFiles(
        IReadOnlyList<FileChange> changes,
        string exportRoot,
        ProjectTreeOptions options,
        HashSet<string> claimed)
    {
        var prefix = exportRoot + "/";

        return changes
            .Select(c => (Path: Normalize(c.Path), c.Effective))
            .Where(c => c.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(c => claimed.Add(c.Path))
            .Select(c => (Name: c.Path[prefix.Length..], c.Path, c.Effective))
            .Where(c => ProjectTreeBuilder.Matches(c.Name, options.Search))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ProjectTreeNode
            {
                Name = c.Name,
                Path = c.Path,
                Kind = ProjectNodeKind.File,
                Files = [c.Path],
                Status = c.Effective,
            })
            .ToList();
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string FileName(string path) =>
        path.Split('/').LastOrDefault() is { Length: > 0 } name ? name : path;
}
