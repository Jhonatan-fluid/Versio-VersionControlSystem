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
    /// <summary>A folder in the project structure. Carries no diff of its own.</summary>
    Folder,

    /// <summary>One exported object, with one or more layers.</summary>
    Object,

    /// <summary>A tracked project file. Reports its export, never a diff.</summary>
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

    /// <summary>
    /// One connector, parameter or empty slot, read from an exported device catalog and
    /// drawn under the device that owns it.
    /// </summary>
    /// <remarks>
    /// Same construction as <see cref="Library"/>: the facts live in the device's
    /// <c>.devices.yaml</c>, which is export-only. It exists as a row so a cycle-time
    /// change reads as <i>that parameter</i> rather than as "the device file changed",
    /// and so eight empty IO-Link slots are eight rows instead of one collided file.
    /// </remarks>
    Device,
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
    /// <see cref="Type"/> as a reader writes it — <c>Persistent GVL</c>, not
    /// <c>gvl_persistent</c> — in the vocabulary of the vendor that exported it. Null for a
    /// folder.
    /// </summary>
    /// <remarks>
    /// Resolved here, where the manifest's vendor is in hand, because a tree row has no other
    /// way to learn it. The tree drew the raw type lowercased in a code font
    /// (<c>library_manager</c>, <c>task_configuration</c>) while
    /// <see cref="ObjectTypeNames"/> — which the git panel already used — sat unasked beside
    /// it. Seen in the running window on 2026-09-21.
    /// </remarks>
    public string? TypeName { get; init; }

    /// <summary>
    /// A short phrase shown beside the name, when the node has one.
    /// </summary>
    /// <remarks>
    /// Carries the export state of a <see cref="ProjectNodeKind.Project"/> row — the thing a
    /// project row reports instead of a diff, because a <c>.project</c> is a binary compound
    /// document and there is no diff to show. In the Explorer, a folder that <i>is</i> a
    /// directory project carries the same state, so it reads like a one-file project's row.
    /// </remarks>
    public string Detail { get; init; } = string.Empty;

    /// <summary>
    /// Which toolchain opens the PLC project this node holds or is, e.g. <c>TIA Portal V21</c>.
    /// Empty almost everywhere.
    /// </summary>
    /// <remarks>
    /// Set only by <see cref="RepoExplorerBuilder"/>, in the format's own words
    /// (<c>IPlcProjectFormat.ProjectLabelFor</c>), so this record carries a vendor's name as
    /// data without the tree knowing one. On the folder for a project that is a directory,
    /// and on the project's own row for one that is a single file — see the builder for why.
    /// </remarks>
    public string Toolchain { get; init; } = string.Empty;

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

    /// <summary>The file a pane opens for this node, or null when it opens none.</summary>
    /// <remarks>
    /// <para>
    /// The readable layer wins. An object exporting to both <c>.st</c> / <c>.scl</c> and
    /// <c>.xml</c> opens the Structured Text, because that is the one somebody can review;
    /// the native XML is there for completeness and reads like a machine wrote it, which
    /// it did. Matching by suffix rather than sort order is load-bearing for TIA: a data
    /// block's <c>.scl</c> is the review copy, and falling through to "first file" used
    /// to open SimaticML whenever the textual suffix was not <c>.st</c>. This is only
    /// the <i>default</i>: the desktop's diff pane offers the other layers as chips.
    /// </para>
    /// <para>
    /// Null for a project row. A <c>.project</c> is a binary compound document - git can store
    /// it and cannot diff it, which is the entire reason this tool exists - so the row reports
    /// its export instead, and the pane says so rather than showing an empty diff.
    /// </para>
    /// <para>
    /// Moved here from the desktop's <c>TreeRow</c> on 2026-10-11 so the web viewer opens the
    /// same file for the same node; the desktop row now asks this.
    /// </para>
    /// </remarks>
    public string? PrimaryFile =>
        Kind is ProjectNodeKind.Project or ProjectNodeKind.Folder
            ? null
            : Files.FirstOrDefault(IsTextualLayer) ?? Files.FirstOrDefault();

    /// <remarks>
    /// Structured Text by whoever claims the file: the standard's <c>.st</c> from the catalog, a
    /// vendor's own extensions from its format. This listed <c>.st</c>, <c>.scl</c> and
    /// <c>.udt</c> by name until 2026-10-11, two of them Siemens', in a namespace that may not
    /// learn a vendor (found in review; the seam tests looked for brand names and saw none).
    /// </remarks>
    private static bool IsTextualLayer(string path) =>
        Plc.PlcFormats.LanguageFor(path) == Diff.LanguageCatalog.StructuredText;
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

/// <summary>
/// One exported device catalog, with the copy to compare it against.
/// </summary>
/// <remarks>
/// <see cref="LibrarySnapshot"/>'s twin for the third catalog type: parsed by the caller
/// because this assembly does no IO, joined to its device by any of the leaf's
/// <see cref="ProjectTreeNode.Files"/> rather than <see cref="ProjectTreeNode.Path"/> —
/// after the folder+yaml merge the path is the folder and the yaml lives in Files.
/// A null <see cref="Baseline"/> means "mark nothing", never "everything is new".
/// </remarks>
public sealed record DeviceSnapshot
{
    /// <summary>Repo-relative path of the catalog's YAML file, forward slashes.</summary>
    public required string Path { get; init; }

    /// <summary>The catalog as the working tree has it.</summary>
    public required DeviceCatalog Current { get; init; }

    /// <summary>The catalog as HEAD has it, or null when there is nothing to compare.</summary>
    public DeviceCatalog? Baseline { get; init; }
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
/// Builds the object tree from the manifest and git's view of the working tree.
/// </summary>
/// <remarks>
/// <para>
/// The export mirrors the toolchain's structure, so the left pane can show the project as the
/// engineer thinks about it rather than as paths on disk. A flat list of
/// <c>plc/Machine/Device/Plc Logic/Application/Main.st</c> is a regression from what the IDE
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
    /// <param name="devices">Parsed device catalogs, same contract. Optional so existing
    /// callers keep compiling — null is "not read", not "no devices".</param>
    public static ProjectTreeNode Build(
        ProjectManifest manifest,
        string exportPath,
        IReadOnlyList<FileChange> changes,
        ProjectTreeOptions? options = null,
        IReadOnlyList<LibrarySnapshot>? libraries = null,
        IReadOnlyList<TaskSnapshot>? tasks = null,
        IReadOnlyList<DeviceSnapshot>? devices = null)
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
            .Select(g => BuildLeaf(g.ToList(), root, status, manifest.Vendor))
            .ToList();

        objects = AttachLibraries(objects, libraries);
        objects = AttachTasks(objects, tasks);
        objects = AttachDevices(objects, devices);

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

        // A manifest that names its panes is grouped by them, and the pool guess below is not
        // made at all: the guess is what put a library's device tree under POUs.
        placed = placed.Any(l => l.View.Length > 0) ? GroupByView(placed) : GroupPoolObjects(placed);

        var tree = Assemble(placed, name: RootName(manifest, exportPath), path: root);
        tree = MergeDeviceFolders(tree);

        return opts.ChangedOnly ? PruneUnchanged(tree) ?? EmptyRoot(tree) : tree;
    }

    private static string RootName(ProjectManifest manifest, string exportPath) =>
        !string.IsNullOrEmpty(manifest.Project)
            ? manifest.Project
            : exportPath.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() ?? "project";

    private readonly record struct Leaf(ProjectTreeNode Node, string[] Folders, string Parent, string View = "");

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
    /// <summary>
    /// How many generations of ownership are nested before the rest is laid flat.
    /// </summary>
    /// <remarks>
    /// Far above anything a project has — a property's accessor under a method under a block
    /// is three — and far below the roughly four thousand generations at which the recursive
    /// walkers over the finished tree overflow a 1 MB stack. See <c>Nest</c>.
    /// </remarks>
    public const int MaxNesting = 64;

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

        // An ownership chain must end at ground — a leaf with no parent, or one whose
        // owner the manifest does not contain. A chain that loops (an object claiming
        // itself, or two claiming each other) has no end, and nesting by it would not
        // hang — the visited set below guards that — it would do something quieter and
        // worse: no member of the loop qualifies as a root, so Attach never runs for any
        // of them and every object in the loop vanishes from the panel. That is the one
        // failure this method's own remarks forswear, and the opposite of the
        // missing-owner case, which deliberately keeps the leaf. The edge is what lies,
        // so the edge is what goes: an ungrounded leaf is excluded from the members
        // lookup AND admitted to the root set below — flat, which is where an object
        // with no honest owner belongs. Both halves, deliberately spelled out: the first
        // fix guarded only the lookup, and its tests stayed green through the
        // members.Count == 0 early return because every fixture was all-cycle — beside
        // one honest member, the loop still vanished. Caught by a re-review that built
        // the mixed manifest; that shape is the pin now.
        //
        // Memoised per guid as a DEPTH (generations below ground; -1 for a loop), so one
        // deep honest chain costs one walk, not one per member — and so the cap below is a
        // fact about each leaf rather than about whichever chain happened to walk through
        // it first.
        //
        // The cap: an honest chain four thousand long passes the cycle guard, and Attach
        // below recurses once per generation — on a 600 KB manifest that overflowed the
        // stack and took the process down (the 2026-10-24 review's item 14). A leaf deeper
        // than MaxNesting is treated as ungrounded: laid flat at the root with everything
        // under it, nothing lost, nothing recursing past the cap. Attach alone on an
        // explicit stack would not have been enough: MergeDeviceFolders, PruneUnchanged,
        // ForSearch and the WPF tree all recurse over the same depth.
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);

        int DepthOf(Leaf leaf)
        {
            var chain = new List<string>();
            var onChain = new HashSet<string>(StringComparer.Ordinal);
            if (leaf.Node.Guid is { Length: > 0 } own)
            {
                if (depths.TryGetValue(own, out var knownOwn))
                {
                    return knownOwn;
                }

                chain.Add(own);
                onChain.Add(own);
            }

            // Walk up to ground or to a guid already measured; `below` is the depth of
            // whatever the walk stopped at (0 for ground, -1 for a loop).
            var current = leaf;
            var below = 0;
            while (current.Parent.Length > 0 && owners.TryGetValue(current.Parent, out var owner))
            {
                if (owner.Node.Guid is not { Length: > 0 } guid || !onChain.Add(guid))
                {
                    below = -1;
                    break;
                }

                if (depths.TryGetValue(guid, out var known))
                {
                    below = known;
                    break;
                }

                chain.Add(guid);
                current = owner;
            }

            // The chain was collected leaf-first; depths are assigned from the top down.
            var depth = below;
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                depth = depth < 0 ? -1 : depth + 1;
                depths[chain[i]] = depth;
            }

            return chain.Count == 0 ? below : depths[chain[0]];
        }

        bool Grounded(Leaf leaf)
        {
            var depth = DepthOf(leaf);
            return depth >= 0 && depth <= MaxNesting;
        }

        var members = leaves
            .Where(l => l.Parent.Length > 0 && owners.ContainsKey(l.Parent) && Grounded(l))
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

            // A member's own folders, rebuilt inside its owner.
            //
            // FROM SCHEMA 9 a member lives in a directory named after its owner, so anything
            // BELOW that directory is a folder the engineer made inside the POU —
            // `FB_Alarm/Private/_M_Reset.st` is FB_Alarm's method, shown in its folder
            // `Private`. Attaching by GUID alone drew both of FB_Alarm's folders' contents as
            // if they were loose methods, which is the half of the owner's report that the
            // export fix did not answer.
            //
            // Counted from the owner rather than parsed: the member's directory minus the
            // owner's, minus one more for the owner's own directory. A pre-schema-9 mirror
            // puts the member in the SAME directory as its owner, so the count comes out zero
            // or negative and this is exactly what it was before — no folders, no change.
            var placed = members[guid]
                .Select(m => m with
                {
                    Node = Attach(m),
                    Folders = FoldersInsideOwner(leaf, m),
                })
                .ToList();

            if (placed.Count == 0)
            {
                return leaf.Node;
            }

            return WithChildren(leaf.Node, ChildrenOf(placed, OwnerDirectory(leaf, placed)));
        }

        return leaves
            .Where(l => l.Parent.Length == 0 || !owners.ContainsKey(l.Parent) || !Grounded(l))
            .Select(l => l with { Node = Attach(l) })
            .ToList();
    }

    /// <summary>
    /// The folder segments between an owner's own directory and one of its members.
    /// </summary>
    /// <remarks>
    /// Empty for a member sitting beside its owner, which is every mirror below schema 9 and
    /// every member that is in no folder. The <c>+ 1</c> is the owner's own directory —
    /// <c>FB_Alarm/</c> — which is not a folder anybody made and must not become a row.
    /// </remarks>
    private static string[] FoldersInsideOwner(Leaf owner, Leaf member)
    {
        var below = member.Folders.Length - (owner.Folders.Length + 1);
        return below <= 0 ? [] : member.Folders[^below..];
    }

    /// <summary>
    /// The directory an owner's members live in, for naming the folder rows inside it.
    /// </summary>
    /// <remarks>
    /// Trimmed off a member's real path rather than built from the owner's name, because the
    /// directory on disk is a percent-encoded stem and this file has no business re-deriving
    /// that: <see cref="ProjectObject.Path"/> is what the exporter actually wrote. Any member
    /// gives the same answer once its own folders are trimmed away.
    /// </remarks>
    private static string OwnerDirectory(Leaf owner, IReadOnlyList<Leaf> placed)
    {
        var first = placed[0];
        var path = DirectoryPart(first.Node.Path);

        for (var i = 0; i < first.Folders.Length; i++)
        {
            path = DirectoryPart(path);
        }

        return path.Length == 0 ? DirectoryPart(owner.Node.Path) : path;
    }

    private static string DirectoryPart(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
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
        List<ProjectObject> files,
        string root,
        IReadOnlyDictionary<string, ChangeKind> status,
        Plc.PlcVendor vendor)
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
                TypeName = ObjectTypeNames.Chip(vendor, first.Type),
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
            first.Parent,

            // The same argument for the pane: one object is in one pane, whatever its layers.
            first.View ?? string.Empty);
    }

    /// <summary>The group project-level objects sit under, when there are any.</summary>
    /// <remarks>
    /// "POUs" is the IEC standard's own word — Program Organization Units — and it is also
    /// the name of the pane CODESYS shows these objects in, which is what makes the label
    /// legible to the person this exists for. Kept in the neutral builder rather than behind
    /// the vendor seam on that IEC ground; a vendor with no project-level pool simply never
    /// exports a root-level object beside foldered ones, and the group never appears.
    /// </remarks>
    public const string PoolGroupName = "POUs";

    /// <summary>
    /// Put project-level objects under an explicit <see cref="PoolGroupName"/> node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An object at the mirror's root is a <b>pool</b> object — the project-level Library
    /// Manager, a GlobalTextList — and CODESYS itself never shows those beside the device
    /// tree: they live in a pane of their own, named POUs. Rendered flat at the root, the
    /// pool's Library Manager read as a duplicate of the application's; the product owner
    /// read it exactly that way, twice, before the CODESYS comparison settled it. The group
    /// is the same statement CODESYS makes, made where Versio's one tree can make it.
    /// </para>
    /// <para>
    /// <b>Only when foldered objects exist beside them.</b> A manifest from before schema 4
    /// is flat — every object at the root — and grouping those would file the entire
    /// project under POUs. Mixed depth is what says the flat ones are genuinely pool-level
    /// rather than merely unplaced.
    /// </para>
    /// <para>
    /// The node's path is the root's <c>POUs</c>, which a real export directory of that
    /// name would collide with — accepted and stated: the exporter writes objects into the
    /// CODESYS folder structure, and a CODESYS folder named POUs at the project root would
    /// be an object folder whose contents belong in exactly this group anyway.
    /// </para>
    /// </remarks>
    private static List<Leaf> GroupPoolObjects(List<Leaf> placed)
    {
        if (!placed.Any(l => l.Folders.Length > 0) || !placed.Any(l => l.Folders.Length == 0))
        {
            return placed;
        }

        return placed
            .Select(l => l.Folders.Length == 0 ? l with { Folders = [PoolGroupName] } : l)
            .ToList();
    }

    /// <summary>
    /// Put each top-level object under the pane its manifest names, with its own folders
    /// inside that pane.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The vendor's word, not this builder's: see <see cref="ProjectObject.View"/>. Run after
    /// nesting, so only the objects still at the top are moved — a member is wherever its owner
    /// is, and a member naming a different pane from its owner would otherwise be torn out of
    /// the function block it belongs to.
    /// </para>
    /// <para>
    /// A top-level object that names no pane in a manifest that names some stays where its
    /// path puts it. Filing it under a pane the vendor did not name would be this builder
    /// guessing again, which is the thing the field exists to stop.
    /// </para>
    /// </remarks>
    private static List<Leaf> GroupByView(List<Leaf> placed) =>
        placed
            .Select(l => l.View.Length > 0 ? l with { Folders = [l.View, .. l.Folders] } : l)
            .ToList();

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

    /// <summary>Hang each device catalog's connectors, parameters and slots under the device.</summary>
    /// <remarks>
    /// Joined on any of the leaf's <see cref="ProjectTreeNode.Files"/>, not on
    /// <see cref="ProjectTreeNode.Path"/>. After <see cref="MergeDeviceFolders"/> the path
    /// is the folder and the yaml lives in Files; joining on Path only would silently drop
    /// every grafted row. The same wrinkle <see cref="AttachTasks"/> already records.
    /// Grafted rows are concatenated onto the existing children: replacing them is what
    /// libraries and tasks can do, and on a device it would hide EtherCAT and Plc Logic.
    /// </remarks>
    private static List<Leaf> AttachDevices(
        List<Leaf> leaves, IReadOnlyList<DeviceSnapshot>? devices)
    {
        if (devices is null || devices.Count == 0)
        {
            return leaves;
        }

        var byPath = new Dictionary<string, DeviceSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in devices)
        {
            byPath[snapshot.Path.Replace('\\', '/').Trim('/')] = snapshot;
        }

        return leaves
            .Select(l =>
                l.Node.Files.Select(f => byPath.TryGetValue(f, out var s) ? s : null)
                        .FirstOrDefault(s => s is not null) is { } snapshot &&
                snapshot.Current.IsValid &&
                DeviceNodes(l.Node, snapshot) is { Count: > 0 } grafted
                    // KEEP the structural children (EtherCAT, Plc Logic). Replacing them
                    // with the graft is what libraries and tasks can do — those nodes have
                    // no other children — and on a device it would hide the I/O tree the
                    // moment SyncViewModel actually passed the catalogs.
                    ? l with { Node = WithChildren(l.Node, [.. l.Node.Children, .. grafted]) }
                    : l)
            .ToList();
    }

    /// <summary>
    /// Connector rows, then parameter rows under them, then empty-slot rows.
    /// </summary>
    /// <remarks>
    /// Flat 55 + 193 parameters would bury CycleTime. Connectors first, parameters hanging
    /// off the connector that owns them, slots last — they are absences, not settings.
    /// Marking rules match <see cref="LibraryNodes"/>: a null baseline marks nothing; a
    /// connector or slot deleted from the catalog still gets a row from the baseline.
    /// </remarks>
    private static List<ProjectTreeNode> DeviceNodes(
        ProjectTreeNode device, DeviceSnapshot snapshot)
    {
        var current = snapshot.Current;
        var baseline = snapshot.Baseline is { IsValid: true } b ? b : null;

        // KeyedConnectors, not Key: two id-less connectors sharing an interface collide,
        // and a HashSet-and-continue here rendered one row for two connectors — the
        // second's parameters never got a diff row anywhere. The #2/#3 keys are stable
        // per file, so a current "X#2" pairs with the baseline's "X#2" by position.
        var oldConnectors = baseline?.KeyedConnectors()
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var rows = new List<ProjectTreeNode>();
        var seenConnectors = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, connector) in current.KeyedConnectors())
        {
            seenConnectors.Add(key);

            DeviceConnector? old = null;
            var connectorStatus =
                oldConnectors is null ? ChangeKind.Unmodified
                : !oldConnectors.TryGetValue(key, out old) ? ChangeKind.Added
                : connector.ContentEquals(old) ? ChangeKind.Unmodified
                : ChangeKind.Modified;

            var paramRows = ParameterNodes(device, key, connector, old, hasBaseline: oldConnectors is not null);
            rows.Add(new ProjectTreeNode
            {
                Name = connector.DisplayName,
                Path = $"{device.Path}{DeviceCatalog.ConnectorRowMarker}{key}",
                Kind = ProjectNodeKind.Device,
                Detail = connector.Parameters.Count == 0
                    ? string.Empty
                    : $"{connector.Parameters.Count} param(s)",
                Files = device.Files,
                Status = connectorStatus,
                Children = paramRows,
                ChangedDescendants = paramRows.Count(p => p.Status != ChangeKind.Unmodified),
                RollUp = paramRows
                    .Select(p => p.Status)
                    .Aggregate(ChangeKind.Unmodified, Stronger),
            });
        }

        if (oldConnectors is not null)
        {
            foreach (var (key, old) in oldConnectors
                         .Where(kv => !seenConnectors.Contains(kv.Key))
                         .OrderBy(kv => kv.Value.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var paramRows = ParameterNodes(device, key, old, old, hasBaseline: true)
                    .Select(p => p with { Status = ChangeKind.Deleted })
                    .ToList();
                rows.Add(new ProjectTreeNode
                {
                    Name = old.DisplayName,
                    Path = $"{device.Path}{DeviceCatalog.ConnectorRowMarker}{key}",
                    Kind = ProjectNodeKind.Device,
                    Detail = old.Parameters.Count == 0
                        ? string.Empty
                        : $"{old.Parameters.Count} param(s)",
                    Files = device.Files,
                    Status = ChangeKind.Deleted,
                    Children = paramRows,
                    ChangedDescendants = paramRows.Count,
                    RollUp = ChangeKind.Deleted,
                });
            }
        }

        rows.AddRange(SlotNodes(device, current, baseline));
        return rows;
    }

    private static List<ProjectTreeNode> ParameterNodes(
        ProjectTreeNode device,
        string key,
        DeviceConnector connector,
        DeviceConnector? previous,
        bool hasBaseline)
    {
        // hasBaseline is "HEAD had a catalog", not "this connector existed". An added
        // connector used to pass previous=null and then treat that as "mark nothing",
        // so its parameter rows stayed Unmodified: no badge, ChangedOnly pruned them,
        // and the row promised "N param(s)" with zero children. A deleted connector
        // forced Deleted on every param. Same comparison, both directions.
        //
        // `key` is the caller's uniquified connector key, not connector.Key: recomputing
        // it here would fold two colliding connectors' parameter rows onto one path.
        var oldParams = hasBaseline ? previous?.Parameters : null;
        var rows = new List<ProjectTreeNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in connector.Parameters)
        {
            if (!seen.Add(pair.Key))
            {
                continue;
            }

            var status =
                !hasBaseline ? ChangeKind.Unmodified
                : oldParams is null || !oldParams.TryGetValue(pair.Key, out var old)
                    ? ChangeKind.Added
                : string.Equals(old, pair.Value, StringComparison.Ordinal) ? ChangeKind.Unmodified
                : ChangeKind.Modified;

            rows.Add(new ProjectTreeNode
            {
                Name = pair.Key,
                Path = $"{device.Path}{DeviceCatalog.ParamRowMarker}{key}::{pair.Key}",
                Kind = ProjectNodeKind.Device,
                Detail = pair.Value,
                Files = device.Files,
                Status = status,
            });
        }

        if (hasBaseline && oldParams is not null)
        {
            rows.AddRange(oldParams
                .Where(p => !seen.Contains(p.Key))
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => new ProjectTreeNode
                {
                    Name = p.Key,
                    Path = $"{device.Path}{DeviceCatalog.ParamRowMarker}{key}::{p.Key}",
                    Kind = ProjectNodeKind.Device,
                    Detail = p.Value,
                    Files = device.Files,
                    Status = ChangeKind.Deleted,
                }));
        }

        return rows;
    }

    private static List<ProjectTreeNode> SlotNodes(
        ProjectTreeNode device, DeviceCatalog current, DeviceCatalog? baseline)
    {
        var oldSlots = baseline?.Slots;
        var rows = new List<ProjectTreeNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in current.Slots.OrderBy(p => p.Key, SlotKeyComparer))
        {
            if (!seen.Add(pair.Key))
            {
                continue;
            }

            var status =
                oldSlots is null ? ChangeKind.Unmodified
                : !oldSlots.TryGetValue(pair.Key, out var old) ? ChangeKind.Added
                : string.Equals(old, pair.Value, StringComparison.Ordinal) ? ChangeKind.Unmodified
                : ChangeKind.Modified;

            rows.Add(new ProjectTreeNode
            {
                Name = pair.Value,
                Path = $"{device.Path}{DeviceCatalog.SlotRowMarker}{pair.Key}",
                Kind = ProjectNodeKind.Device,
                Detail = pair.Key,
                Files = device.Files,
                Status = status,
            });
        }

        if (oldSlots is not null)
        {
            rows.AddRange(oldSlots
                .Where(p => !seen.Contains(p.Key))
                .OrderBy(p => p.Key, SlotKeyComparer)
                .Select(p => new ProjectTreeNode
                {
                    Name = p.Value,
                    Path = $"{device.Path}{DeviceCatalog.SlotRowMarker}{p.Key}",
                    Kind = ProjectNodeKind.Device,
                    Detail = p.Key,
                    Files = device.Files,
                    Status = ChangeKind.Deleted,
                }));
        }

        return rows;
    }

    /// <summary>
    /// Numeric slot indices in numeric order ("2" before "10"), everything else ordinal.
    /// </summary>
    private static readonly IComparer<string> SlotKeyComparer = Comparer<string>.Create((a, b) =>
        int.TryParse(a, out var na) && int.TryParse(b, out var nb)
            ? na.CompareTo(nb)
            : string.Compare(a, b, StringComparison.Ordinal));

    /// <summary>
    /// Promote a folder that contains this folder's <c>*.devices.yaml</c> object into that
    /// object, keeping the other children (EtherCAT, Plc Logic).
    /// </summary>
    /// <remarks>
    /// Without this, <c>Device/Device.devices.yaml</c> draws as a folder named Device
    /// containing a second object also named Device, and grafted parameters hang only on
    /// the inner object. Tests in <c>ProjectTreeDevicesTests</c> must go red if this merge
    /// is deleted — the same class of silent-green as a dropped subscription.
    /// <para>
    /// Joined on the encoded path, not on <see cref="ProjectTreeNode.Name"/>. The folder
    /// segment is <c>safe_file_stem</c> (percent-encoded, reserved-name <c>%20</c>, digest
    /// tail); the manifest name is verbatim. Ordinal name equality never merged a device
    /// named <c>COM1</c> or <c>foo&lt;bar&gt;</c>, and Core must not grow a vendor decoder
    /// to fix it.
    /// </para>
    /// </remarks>
    private static ProjectTreeNode MergeDeviceFolders(ProjectTreeNode node)
    {
        var children = node.Children.Select(MergeDeviceFolders).ToList();
        var yaml = children.FirstOrDefault(c => YamlFileIsThisFolder(c, node.Path));

        if (node.Kind != ProjectNodeKind.Folder || yaml is null)
        {
            return children.Count == node.Children.Count &&
                   children.Zip(node.Children, (a, b) => ReferenceEquals(a, b)).All(x => x)
                ? node
                : WithChildren(node, children);
        }

        var others = children.Where(c => !ReferenceEquals(c, yaml)).ToList();
        return WithChildren(yaml with { Path = node.Path }, [.. others, .. yaml.Children]);
    }

    /// <summary>
    /// True when <paramref name="child"/> is the <c>{folderStem}.devices.yaml</c> that
    /// lives directly in <paramref name="folderPath"/>.
    /// </summary>
    private static bool YamlFileIsThisFolder(ProjectTreeNode child, string folderPath)
    {
        if (child.Kind != ProjectNodeKind.Object)
        {
            return false;
        }

        var folder = folderPath.Replace('\\', '/').Trim('/');
        var slash = folder.LastIndexOf('/');
        var stem = slash < 0 ? folder : folder[(slash + 1)..];
        if (stem.Length == 0)
        {
            return false;
        }

        var expected = $"{folder}/{stem}{DeviceCatalog.FileSuffix}";
        return child.Files.Any(f =>
            string.Equals(
                f.Replace('\\', '/').Trim('/'),
                expected,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One level of the tree: the folders under it, then the objects in it.
    /// </summary>
    /// <remarks>
    /// <b>Folders first, which is what the IDE does</b> — measured against CODESYS 3.5.22.20,
    /// where <c>FB_Alarm</c> shows <c>Private</c> and <c>Protected</c> above its methods. It
    /// is not what <c>get_children()</c> returns, which is storage order: that hands back
    /// <c>M_Reset, Private, Protected, P_Faulted</c> and puts <c>FB_Alarm</c> eleventh of the
    /// application's twelve children where the IDE shows it second. So the order here is the
    /// panel's own rule rather than a recording of the toolchain's.
    /// <para>
    /// Shared with <see cref="Nest"/> so that a folder <i>inside</i> an owner is built the
    /// same way as one at project level. Before that they were different code paths and only
    /// one of them existed.
    /// </para>
    /// <para>
    /// <b>Properties after the other objects.</b> A block's members were one alphabetical run,
    /// so a renamed property's old and new rows (<c>Busy</c>, then <c>P_Busy</c> further down)
    /// sat between methods, and the methods were split around them (the owner, 2026-10-22:
    /// methods first, then properties). Keyed on the standard's own word, so no vendor's type
    /// list is learned here; a vendor's other method spellings (<c>METHOD_NORET</c>) sort with
    /// the methods by not being a property. A level with no properties — every project-level
    /// one — orders exactly as before.
    /// </para>
    /// </remarks>
    private static List<ProjectTreeNode> ChildrenOf(IReadOnlyList<Leaf> leaves, string path)
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
            .OrderBy(n => string.Equals(n.Type, Iec.IecObjectTypeNames.Property, StringComparison.OrdinalIgnoreCase))
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase));

        return children;
    }

    /// <summary>Hang every leaf off its folder chain, then roll the badges up.</summary>
    private static ProjectTreeNode Assemble(IReadOnlyList<Leaf> leaves, string name, string path)
    {
        var children = ChildrenOf(leaves, path);

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
