using Versio.Core.Git;

namespace Versio.Core.Projects;

/// <summary>What a node in the tree actually is.</summary>
/// <remarks>
/// <para>
/// Split out because the tree stopped being only the export's objects. The one panel now
/// carries the project file, the objects underneath it and every other changed file in the
/// repository, and each of those behaves differently when it is selected — an object opens a
/// diff, a project reports whether its export succeeded.
/// </para>
/// <para>
/// It also removes a latent bug: "folder" used to mean "has no GUID", and a manifest record
/// that arrived without one therefore rendered as a folder with no children.
/// </para>
/// </remarks>
public enum ProjectNodeKind
{
    /// <summary>A folder in the CODESYS structure. Carries no diff of its own.</summary>
    Folder,

    /// <summary>One CODESYS object, with one or more exported layers.</summary>
    Object,

    /// <summary>A tracked <c>.project</c> file. Reports its export, never a diff.</summary>
    Project,

    /// <summary>A changed file that is not part of any export tree.</summary>
    File,
}

/// <summary>One node of the project as the engineer thinks about it.</summary>
/// <remarks>
/// Immutable, and built away from the UI thread. The tree is rebuilt on every status refresh,
/// and a mutable graph being edited while WPF walks it is the kind of bug that shows up as a
/// rendering glitch nobody can reproduce.
/// </remarks>
public sealed record ProjectTreeNode
{
    /// <summary>What the engineer sees: the object or folder name, never a path.</summary>
    public required string Name { get; init; }

    /// <summary>Path within the export tree, forward slashes. Identity for a folder.</summary>
    public required string Path { get; init; }

    /// <summary>What this node is. Decides how it renders and what selecting it does.</summary>
    public ProjectNodeKind Kind { get; init; } = ProjectNodeKind.Folder;

    /// <summary>The CODESYS object GUID, or null for a folder.</summary>
    public string? Guid { get; init; }

    /// <summary>CODESYS object type, or null for a folder.</summary>
    public string? Type { get; init; }

    /// <summary>
    /// A short phrase shown beside the name, when the node has one.
    /// </summary>
    /// <remarks>
    /// Carries the export state of a <see cref="ProjectNodeKind.Project"/> row — the thing a
    /// project row reports instead of a diff, because a <c>.project</c> is a binary compound
    /// document and there is no diff to show.
    /// </remarks>
    public string Detail { get; init; } = string.Empty;

    /// <summary>
    /// Every file this object exports to, repo-relative.
    /// </summary>
    /// <remarks>
    /// One object, several layers: a GVL is both <c>.gvl.st</c> and <c>.gvl.xml</c>. These are
    /// layers of one node rather than extra rows, because the engineer edited one object and
    /// showing them two would invite the question of which is real.
    /// </remarks>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>This node's own change state.</summary>
    public ChangeKind Status { get; init; } = ChangeKind.Unmodified;

    /// <summary>How many descendants changed. Zero on a leaf.</summary>
    public int ChangedDescendants { get; init; }

    /// <summary>Strongest change state among the descendants, for the folder badge.</summary>
    public ChangeKind RollUp { get; init; } = ChangeKind.Unmodified;

    public IReadOnlyList<ProjectTreeNode> Children { get; init; } = [];

    /// <summary>Whether this node groups others rather than standing for one thing.</summary>
    public bool IsFolder => Kind is ProjectNodeKind.Folder;

    /// <summary>True when this node or anything under it changed.</summary>
    public bool HasChanges => Status != ChangeKind.Unmodified || ChangedDescendants > 0;
}

/// <summary>What to include when building the tree.</summary>
public sealed record ProjectTreeOptions
{
    /// <summary>
    /// Show only objects that changed, with the folders needed to reach them.
    /// </summary>
    /// <remarks>
    /// The default, because the common question is "what did I change" and a real project has
    /// thousands of objects. The full tree answers the other question — <i>where</i> in the
    /// machine did this change — and is one toggle away.
    /// </remarks>
    public bool ChangedOnly { get; init; } = true;

    /// <summary>Match against object and folder names. Empty matches everything.</summary>
    public string Search { get; init; } = string.Empty;
}

/// <summary>
/// Builds the CODESYS object tree from the manifest and git's view of the working tree.
/// </summary>
/// <remarks>
/// <para>
/// The export mirrors the CODESYS structure, so the left pane can show the project as the
/// engineer thinks about it rather than as paths on disk. A flat list of
/// <c>plc/Machine/Device/Plc Logic/Application/Main.st</c> is a regression from what CODESYS
/// itself shows them all day.
/// </para>
/// <para>
/// Pure: the manifest and the status are handed in, and a snapshot comes out. That is what
/// lets the roll-up rules — the part most likely to be quietly wrong — be tested without an
/// export, a repository or a UI.
/// </para>
/// </remarks>
public static class ProjectTreeBuilder
{
    /// <param name="manifest">The export's manifest.</param>
    /// <param name="exportPath">Repo-relative export root, so manifest paths can be matched
    /// against git's repo-relative ones.</param>
    /// <param name="changes">What git reports for the working tree.</param>
    public static ProjectTreeNode Build(
        ProjectManifest manifest,
        string exportPath,
        IReadOnlyList<FileChange> changes,
        ProjectTreeOptions? options = null)
    {
        var opts = options ?? new ProjectTreeOptions();
        var root = exportPath.Replace('\\', '/').Trim('/');

        var status = new Dictionary<string, ChangeKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            status[change.Path.Replace('\\', '/')] = change.Effective;
        }

        // One node per OBJECT, not per file: several manifest records can share a GUID
        // because one object exports to several layers.
        var objects = manifest.Objects
            .Where(o => !string.IsNullOrEmpty(o.Path))
            .GroupBy(o => string.IsNullOrEmpty(o.Guid) ? o.Path : o.Guid, StringComparer.Ordinal)
            .Select(g => BuildLeaf(g.ToList(), root, status))
            .Where(leaf => Matches(leaf.Node.Name, opts.Search))
            .ToList();

        var tree = Assemble(objects, name: RootName(manifest, exportPath), path: root);

        return opts.ChangedOnly ? PruneUnchanged(tree) ?? EmptyRoot(tree) : tree;
    }

    private static string RootName(ProjectManifest manifest, string exportPath) =>
        !string.IsNullOrEmpty(manifest.Project)
            ? manifest.Project
            : exportPath.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() ?? "project";

    private readonly record struct Leaf(ProjectTreeNode Node, string[] Folders);

    /// <summary>Turn every file of one object into a single node with its layers.</summary>
    private static Leaf BuildLeaf(
        List<ProjectObject> files, string root, IReadOnlyDictionary<string, ChangeKind> status)
    {
        // Deterministic layer order, so the diff pane's default does not depend on the order
        // the export happened to walk the project in.
        var ordered = files
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        var first = ordered[0];
        var segments = first.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var folders = segments.Take(Math.Max(0, segments.Length - 1)).ToArray();

        var paths = ordered
            .Select(f => Combine(root, f.Path))
            .ToList();

        return new Leaf(
            new ProjectTreeNode
            {
                Name = string.IsNullOrEmpty(first.Name)
                    ? segments.LastOrDefault() ?? first.Path
                    : first.Name,
                Path = Combine(root, first.Path),
                Kind = ProjectNodeKind.Object,
                Guid = string.IsNullOrEmpty(first.Guid) ? null : first.Guid,
                Type = first.Type,
                Files = paths,

                // The strongest state across the object's layers. Editing the .st and leaving
                // the .xml alone still means the object changed, and a node reporting the
                // quieter of its two files would hide it.
                Status = paths
                    .Select(p => status.TryGetValue(p, out var k) ? k : ChangeKind.Unmodified)
                    .Aggregate(ChangeKind.Unmodified, Stronger),
            },
            folders);
    }

    /// <summary>Hang every leaf off its folder chain, then roll the badges up.</summary>
    private static ProjectTreeNode Assemble(IReadOnlyList<Leaf> leaves, string name, string path)
    {
        var children = new List<ProjectTreeNode>();

        foreach (var group in leaves
                     .Where(l => l.Folders.Length > 0)
                     .GroupBy(l => l.Folders[0], StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var descended = group
                .Select(l => l with { Folders = l.Folders[1..] })
                .ToList();

            children.Add(Assemble(descended, group.Key, Combine(path, group.Key)));
        }

        children.AddRange(leaves
            .Where(l => l.Folders.Length == 0)
            .Select(l => l.Node)
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase));

        return new ProjectTreeNode
        {
            Name = name,
            Path = path,
            Kind = ProjectNodeKind.Folder,
            Children = children,

            // Counted over descendants, so a change is never hidden behind a collapsed node.
            ChangedDescendants = children.Sum(
                c => c.ChangedDescendants + (c.Status != ChangeKind.Unmodified ? 1 : 0)),
            RollUp = children
                .Select(c => Stronger(c.Status, c.RollUp))
                .Aggregate(ChangeKind.Unmodified, Stronger),
        };
    }

    /// <summary>Drop everything with no changes under it.</summary>
    private static ProjectTreeNode? PruneUnchanged(ProjectTreeNode node)
    {
        if (!node.IsFolder)
        {
            return node.Status == ChangeKind.Unmodified ? null : node;
        }

        var kept = node.Children
            .Select(PruneUnchanged)
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

        // A folder survives only for what is under it. Keeping empty folders would rebuild
        // the full tree one level at a time and defeat the filter.
        return kept.Count == 0 && node.Status == ChangeKind.Unmodified
            ? null
            : node with { Children = kept };
    }

    private static ProjectTreeNode EmptyRoot(ProjectTreeNode tree) =>
        tree with { Children = [], ChangedDescendants = 0, RollUp = ChangeKind.Unmodified };

    internal static bool Matches(string name, string search) =>
        string.IsNullOrWhiteSpace(search) ||
        name.Contains(search, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which of two states a badge should show.
    /// </summary>
    /// <remarks>
    /// Ordered by how much attention it deserves, not alphabetically. A conflict outranks
    /// everything because nothing else can proceed until it is resolved; structural changes
    /// — an object appearing, vanishing or moving — outrank an edit to one that stayed put,
    /// because they are the ones somebody might not have meant.
    /// </remarks>
    internal static ChangeKind Stronger(ChangeKind a, ChangeKind b) =>
        Rank(a) >= Rank(b) ? a : b;

    private static int Rank(ChangeKind kind) => kind switch
    {
        ChangeKind.Conflicted => 6,
        ChangeKind.Deleted => 5,
        ChangeKind.Renamed => 4,
        ChangeKind.Copied => 4,
        ChangeKind.Added => 3,
        ChangeKind.Untracked => 3,
        ChangeKind.Modified => 2,
        _ => 0,
    };

    internal static string Combine(string left, string right) =>
        string.IsNullOrEmpty(left) ? right : $"{left.TrimEnd('/')}/{right.TrimStart('/')}";
}
