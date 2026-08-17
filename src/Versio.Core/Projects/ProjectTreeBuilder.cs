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

    /// <summary>
    /// One library reference, read from an exported catalog and drawn under its manager.
    /// </summary>
    /// <remarks>
    /// Not an <see cref="Object"/>: it has no GUID, no files of its own and no diff — its
    /// facts live inside the manager's one YAML file, which is what selecting it opens. It
    /// exists as a row so a version bump reads as <i>that library</i> rather than as "the
    /// library manager changed".
    /// </remarks>
    Library,

    /// <summary>
    /// One task, read from an exported task catalog and drawn under its configuration.
    /// </summary>
    /// <remarks>
    /// Same construction as <see cref="Library"/>, one file over: the facts live in the
    /// configuration's <c>.tasks.yaml</c> — which, unlike a library catalog, is the
    /// importable half, so the row a reviewer reads here is exactly what the next import
    /// applies.
    /// </remarks>
    Task,
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

    /// <summary>Something is running for this node right now — a toolchain job, today.</summary>
    /// <remarks>
    /// Only a project node ever sets this. It is on the node rather than on a view model so
    /// the tree snapshot handed to the UI is complete on its own: the rows are rebuilt from
    /// the snapshot, and a flag living beside it would be lost on every rebuild.
    /// </remarks>
    public bool Busy { get; init; }

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

/// <summary>
/// One exported library catalog, with the copy to compare it against.
/// </summary>
/// <remarks>
/// <para>
/// The catalogs are handed in already parsed rather than read here, for the same reason the
/// manifest is: reading them is IO and this assembly does none. Whoever builds the tree reads
/// each <c>*.libraries.yaml</c> the manifest names and, when git reports that file changed,
/// fetches HEAD's copy too — that pair is what lets the tree say <i>which</i> library moved
/// instead of only that the manager's file did.
/// </para>
/// <para>
/// <see cref="Baseline"/> is null when there is nothing to compare against: the file is
/// unchanged, or HEAD has no copy. Null means "mark nothing", not "everything is new" — a
/// first export adds the manager's file, and drawing its twenty references as twenty additions
/// would bury the one line git actually has to say about it.
/// </para>
/// </remarks>
public sealed record LibrarySnapshot
{
    /// <summary>Repo-relative path of the catalog's YAML file, forward slashes.</summary>
    /// <remarks>
    /// The join key: it matches the manager object's own file path in the tree, which is how
    /// the references find the node they hang under. The catalog's <c>path</c> header names
    /// the manager's position instead of its file and cannot serve here.
    /// </remarks>
    public required string Path { get; init; }

    /// <summary>The catalog as the working tree has it.</summary>
    public required LibraryCatalog Current { get; init; }

    /// <summary>The catalog as HEAD has it, or null when there is nothing to compare.</summary>
    public LibraryCatalog? Baseline { get; init; }
}

/// <summary>
/// One exported task catalog, with the copy to compare it against.
/// </summary>
/// <remarks>
/// <see cref="LibrarySnapshot"/>'s twin, with the same contract throughout: parsed by the
/// caller because this assembly does no IO, joined to its configuration by the YAML file's
/// repo-relative path, and a null <see cref="Baseline"/> means "mark nothing", never
/// "everything is new".
/// </remarks>
public sealed record TaskSnapshot
{
    /// <summary>Repo-relative path of the catalog's YAML file, forward slashes.</summary>
    public required string Path { get; init; }

    /// <summary>The catalog as the working tree has it.</summary>
    public required TaskCatalog Current { get; init; }

    /// <summary>The catalog as HEAD has it, or null when there is nothing to compare.</summary>
    public TaskCatalog? Baseline { get; init; }
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

    /// <summary>
    /// Drop a project from the workspace tree when nothing under it changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read by <see cref="WorkspaceTreeBuilder"/> only. A project with a clean mirror is a
    /// project with nothing to review, and listing it anyway makes the panel a directory
    /// listing that happens to mark changes rather than a view of what changed.
    /// </para>
    /// <para>
    /// It does <b>not</b> hide a project that needs attention — never exported, behind,
    /// locked, failed — because those are exactly the projects git has nothing to say about.
    /// Nor does it apply while a search is running: the box searches the whole project, and a
    /// filter that quietly restricted it to changed objects would be a search that lies.
    /// </para>
    /// </remarks>
    public bool HideUnchangedProjects { get; init; } = true;
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
    /// <param name="libraries">Parsed library catalogs, one per manager, keyed by their file's
    /// repo-relative path. Empty is the ordinary state of a project whose catalogs have not
    /// been read, not an error.</param>
    /// <param name="tasks">Parsed task catalogs, same contract.</param>
    public static ProjectTreeNode Build(
        ProjectManifest manifest,
        string exportPath,
        IReadOnlyList<FileChange> changes,
        ProjectTreeOptions? options = null,
        IReadOnlyList<LibrarySnapshot>? libraries = null,
        IReadOnlyList<TaskSnapshot>? tasks = null)
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
            .ToList();

        objects = AttachLibraries(objects, libraries);
        objects = AttachTasks(objects, tasks);

        // Members hung off their owner before anything is placed in a folder, because a
        // method's folder is its owner's folder — the export writes clsLight.Light.xml beside
        // clsLight.st — so placing by path alone is what made them siblings.
        //
        // Filtered after nesting rather than before it: a search that matched a method and
        // dropped the function block holding it would leave the match unreachable.
        var placed = new List<Leaf>();
        foreach (var leaf in Nest(objects))
        {
            if (ForSearch(leaf.Node, opts.Search) is { } kept)
            {
                placed.Add(leaf with { Node = kept });
            }
        }

        var tree = Assemble(placed, name: RootName(manifest, exportPath), path: root);

        return opts.ChangedOnly ? PruneUnchanged(tree) ?? EmptyRoot(tree) : tree;
    }

    private static string RootName(ProjectManifest manifest, string exportPath) =>
        !string.IsNullOrEmpty(manifest.Project)
            ? manifest.Project
            : exportPath.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() ?? "project";

    private readonly record struct Leaf(ProjectTreeNode Node, string[] Folders, string Parent);

    /// <summary>
    /// Hang every member off the object that owns it, and hand back what is left.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An owner is named by GUID (manifest schema 4). A member whose owner is <b>not in this
    /// manifest</b> stays where its path puts it rather than being dropped: a truncated
    /// export, or one written by a build that skipped the owner's type, would otherwise lose
    /// objects that are sitting right there on disk — and losing an object silently is the one
    /// failure this panel exists to prevent.
    /// </para>
    /// <para>
    /// The walk is depth-limited by a visited set, not by trust. A manifest is a file on disk
    /// and a cycle in it — an object claiming its own descendant as its owner — would
    /// otherwise be an infinite recursion inside a background build, which presents as the
    /// panel simply never appearing again.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<Leaf> Nest(IReadOnlyList<Leaf> leaves)
    {
        var owners = new Dictionary<string, Leaf>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            if (leaf.Node.Guid is { Length: > 0 } guid)
            {
                owners[guid] = leaf;
            }
        }

        var members = leaves
            .Where(l => l.Parent.Length > 0 && owners.ContainsKey(l.Parent))
            .ToLookup(l => l.Parent, StringComparer.Ordinal);

        if (members.Count == 0)
        {
            return leaves;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);

        ProjectTreeNode Attach(Leaf leaf)
        {
            if (leaf.Node.Guid is not { Length: > 0 } guid || !visited.Add(guid))
            {
                return leaf.Node;
            }

            var children = members[guid]
                .Select(Attach)
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return children.Count == 0 ? leaf.Node : WithChildren(leaf.Node, children);
        }

        return leaves
            .Where(l => l.Parent.Length == 0 || !owners.ContainsKey(l.Parent))
            .Select(l => l with { Node = Attach(l) })
            .ToList();
    }

    /// <summary>An object node that owns others, with its badges counted over them.</summary>
    /// <remarks>
    /// The same roll-up an <see cref="Assemble"/>d folder gets, and for the same reason: an
    /// edited method inside a collapsed function block has to raise the block's badge, or a
    /// change hides behind a chevron.
    /// </remarks>
    private static ProjectTreeNode WithChildren(
        ProjectTreeNode node, IReadOnlyList<ProjectTreeNode> children) =>
        node with
        {
            Children = children,
            ChangedDescendants = children.Sum(
                c => c.ChangedDescendants + (c.Status != ChangeKind.Unmodified ? 1 : 0)),
            RollUp = children
                .Select(c => Stronger(c.Status, c.RollUp))
                .Aggregate(ChangeKind.Unmodified, Stronger),
        };

    /// <summary>
    /// This object as the search leaves it, or null when nothing in it matches.
    /// </summary>
    /// <remarks>
    /// A node that matches keeps <b>all</b> its members, because a function block whose name
    /// was typed and which then appeared with no methods reads as a function block that lost
    /// them. A node that does not match survives only for the members that do.
    /// </remarks>
    private static ProjectTreeNode? ForSearch(ProjectTreeNode node, string search)
    {
        if (Matches(node.Name, search))
        {
            return node;
        }

        var kept = node.Children
            .Select(c => ForSearch(c, search))
            .OfType<ProjectTreeNode>()
            .ToList();

        return kept.Count == 0 ? null : WithChildren(node, kept);
    }

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
            folders,

            // Taken from the first record because every layer of one object has the same
            // owner — a GVL's .st and .xml cannot belong to different function blocks.
            first.Parent);
    }

    /// <summary>Hang each catalog's references under the manager that owns them.</summary>
    /// <remarks>
    /// Joined on the manager's file path rather than on its name or GUID, because the snapshot
    /// was produced by reading a file and the file's path is the one fact both sides hold. A
    /// snapshot whose path matches no leaf is dropped silently: the manifest is the authority
    /// on what exists, and a stray catalog must not conjure a manager the export never wrote.
    /// </remarks>
    private static List<Leaf> AttachLibraries(
        List<Leaf> leaves, IReadOnlyList<LibrarySnapshot>? libraries)
    {
        if (libraries is null || libraries.Count == 0)
        {
            return leaves;
        }

        var byPath = new Dictionary<string, LibrarySnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in libraries)
        {
            byPath[snapshot.Path.Replace('\\', '/').Trim('/')] = snapshot;
        }

        return leaves
            .Select(l =>
                byPath.TryGetValue(l.Node.Path, out var snapshot) &&
                snapshot.Current.IsValid &&
                LibraryNodes(l.Node, snapshot) is { Count: > 0 } children
                    ? l with { Node = WithChildren(l.Node, children) }
                    : l)
            .ToList();
    }

    /// <summary>One row per library reference, marked against the baseline when there is one.</summary>
    /// <remarks>
    /// <para>
    /// <b>A null or unreadable baseline marks nothing.</b> The manager's own row already
    /// carries the file's git state; without an old copy to compare against, per-library marks
    /// would be guesses, and a fresh export's twenty references drawn as twenty additions
    /// would bury the one thing that happened.
    /// </para>
    /// <para>
    /// <b>A reference deleted from the catalog still gets a row</b>, from the baseline's copy
    /// of it — a library that silently vanished from the panel is indistinguishable from one
    /// that was never referenced, and removal is exactly the change a reviewer must see.
    /// </para>
    /// <para>
    /// <b>Chosen libraries sort before system ones.</b> On the fixture, 18 of 21 references
    /// are system libraries pulled in by the visualisation plug-in; listed alphabetically
    /// beside them, the three somebody actually chose are lost in the middle of the list.
    /// </para>
    /// </remarks>
    private static List<ProjectTreeNode> LibraryNodes(
        ProjectTreeNode manager, LibrarySnapshot snapshot)
    {
        var baseline = snapshot.Baseline is { IsValid: true } b
            ? b.References
                .GroupBy(r => r.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
            : null;

        var rows = new List<(LibraryReference Reference, ChangeKind Status)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reference in snapshot.Current.References)
        {
            if (!seen.Add(reference.Name))
            {
                continue;
            }

            var status =
                baseline is null ? ChangeKind.Unmodified
                : !baseline.TryGetValue(reference.Name, out var old) ? ChangeKind.Added
                : reference.ContentEquals(old) ? ChangeKind.Unmodified
                : ChangeKind.Modified;

            rows.Add((reference, status));
        }

        if (baseline is not null)
        {
            rows.AddRange(baseline.Values
                .Where(old => !seen.Contains(old.Name))
                .Select(old => (old, ChangeKind.Deleted)));
        }

        return rows
            .OrderBy(r => r.Reference.SystemLibrary)
            .ThenBy(r => DisplayName(r.Reference), StringComparer.OrdinalIgnoreCase)
            .Select(r => new ProjectTreeNode
            {
                Name = DisplayName(r.Reference),

                // Identity for the row across rebuilds, never shown and never a file. The
                // raw reference name, not the display name, because the display name is
                // free to change its mind about which field reads best.
                Path = $"{manager.Path}::{r.Reference.Name}",
                Kind = ProjectNodeKind.Library,
                Detail = Describe(r.Reference),

                // The manager's own file: a library has no file of its own, and selecting
                // it opens the one place its facts are recorded.
                Files = manager.Files,
                Status = r.Status,
            })
            .ToList();
    }

    /// <summary>What the row is called: the name a person chose, not the toolchain's spelling.</summary>
    private static string DisplayName(LibraryReference reference) =>
        !string.IsNullOrEmpty(reference.Placeholder) ? reference.Placeholder
        : !string.IsNullOrEmpty(reference.Title) ? reference.Title
        : reference.Name;

    /// <summary>The version phrase beside the name, with the system flag when it applies.</summary>
    private static string Describe(LibraryReference reference) =>
        reference.SystemLibrary && reference.DisplayVersion.Length > 0
            ? $"{reference.DisplayVersion} · system"
            : reference.SystemLibrary
                ? "system"
                : reference.DisplayVersion;

    /// <summary>Hang each task catalog's tasks under the configuration that owns them.</summary>
    /// <remarks>
    /// The same join as <see cref="AttachLibraries"/> — the YAML's path against the leaf's —
    /// with one wrinkle inherited from the export: a task configuration is TWO files sharing
    /// one GUID, so its leaf's <see cref="ProjectTreeNode.Path"/> is whichever file sorts
    /// first. That is the <c>.tasks.yaml</c> today by ordinal accident of the suffixes, so
    /// the match is made against every file of the leaf rather than its path — the accident
    /// is not load-bearing.
    /// </remarks>
    private static List<Leaf> AttachTasks(
        List<Leaf> leaves, IReadOnlyList<TaskSnapshot>? tasks)
    {
        if (tasks is null || tasks.Count == 0)
        {
            return leaves;
        }

        var byPath = new Dictionary<string, TaskSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in tasks)
        {
            byPath[snapshot.Path.Replace('\\', '/').Trim('/')] = snapshot;
        }

        return leaves
            .Select(l =>
                l.Node.Files.Select(f => byPath.TryGetValue(f, out var s) ? s : null)
                        .FirstOrDefault(s => s is not null) is { } snapshot &&
                snapshot.Current.IsValid &&
                TaskNodes(l.Node, snapshot) is { Count: > 0 } children
                    ? l with { Node = WithChildren(l.Node, children) }
                    : l)
            .ToList();
    }

    /// <summary>One row per task, marked against the baseline when there is one.</summary>
    /// <remarks>
    /// The marking rules are <see cref="LibraryNodes"/>'s — a null or unreadable baseline
    /// marks nothing, a task deleted from the catalog still gets a row from the baseline's
    /// copy — but the order is not: tasks keep the catalog's order, which is the
    /// configuration's own, because that is how the IDE shows them and two tasks have no
    /// alphabet a reviewer would expect. Removed tasks are appended at the end, where a row
    /// that no longer exists reads as an afterthought rather than as the schedule.
    /// </remarks>
    private static List<ProjectTreeNode> TaskNodes(
        ProjectTreeNode configuration, TaskSnapshot snapshot)
    {
        var baseline = snapshot.Baseline is { IsValid: true } b
            ? b.Tasks
                .GroupBy(t => t.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
            : null;

        var rows = new List<(TaskRecord Task, ChangeKind Status)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var task in snapshot.Current.Tasks)
        {
            if (!seen.Add(task.Name))
            {
                continue;
            }

            var status =
                baseline is null ? ChangeKind.Unmodified
                : !baseline.TryGetValue(task.Name, out var old) ? ChangeKind.Added
                : task.ContentEquals(old) ? ChangeKind.Unmodified
                : ChangeKind.Modified;

            rows.Add((task, status));
        }

        if (baseline is not null)
        {
            rows.AddRange(baseline.Values
                .Where(old => !seen.Contains(old.Name))
                .OrderBy(old => old.Name, StringComparer.OrdinalIgnoreCase)
                .Select(old => (old, ChangeKind.Deleted)));
        }

        return rows
            .Select(r => new ProjectTreeNode
            {
                Name = r.Task.Name,
                Path = $"{configuration.Path}::{r.Task.Name}",
                Kind = ProjectNodeKind.Task,
                Detail = Describe(r.Task),
                Files = configuration.Files,
                Status = r.Status,
            })
            .ToList();
    }

    /// <summary>The schedule phrase beside the task's name.</summary>
    /// <remarks>
    /// Interval and priority, because they are the two numbers a reviewer checks a schedule
    /// for. The watchdog and the core binding stay in the YAML the row opens — a row phrase
    /// that carried four facts would carry none of them legibly.
    /// </remarks>
    private static string Describe(TaskRecord task)
    {
        var schedule = task.DisplaySchedule;
        return schedule.Length > 0 && task.Priority.Length > 0
            ? $"{schedule} · prio {task.Priority}"
            : schedule.Length > 0
                ? schedule
                : task.Priority.Length > 0
                    ? $"prio {task.Priority}"
                    : string.Empty;
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
    /// <remarks>
    /// Tested on <see cref="ProjectTreeNode.Children"/> rather than on
    /// <see cref="ProjectTreeNode.IsFolder"/>, because an object owns objects now. An
    /// unchanged function block holding an edited method is not a leaf, and treating it as one
    /// dropped the block and the edit with it.
    /// </remarks>
    private static ProjectTreeNode? PruneUnchanged(ProjectTreeNode node)
    {
        if (node.Children.Count == 0)
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
