using Versio.Core.Projects;
using Versio.Web.Rendering;
using Versio.Web.Sources;
using ChangeKind = Versio.Core.Git.ChangeKind;
using FileChange = Versio.Core.Git.FileChange;

namespace Versio.Web.Repo;

/// <summary>One file of an object as a commit touched it, or left it alone.</summary>
/// <param name="Status">GitHub's word for the file's change (added, modified, removed, renamed, copied, changed); "unmodified" for a layer the commit did not touch.</param>
/// <param name="Changed">Whether the commit lists this file at all.</param>
public sealed record TouchedLayer(string Path, string Status, int Additions, int Deletions, string? PreviousPath, bool Changed);

/// <summary>
/// One thing a commit changed as the engineer thinks of it: an object with every one of its
/// files, or a file outside any export.
/// </summary>
/// <param name="Key">What the page addresses the entry by: the object's tree path, or the file's path. Unique within one commit.</param>
/// <param name="Kind">"object" or "file".</param>
/// <param name="Name">The object's name, or the file's name.</param>
/// <param name="Path">The layer to draw first: the object's readable file when the commit touched it, else the first touched layer; the file itself for a plain file.</param>
/// <param name="From">The drawn layer's old path when the commit renamed it, for the comparison to follow the rename.</param>
/// <param name="Type">The object's type as the vendor names it; null for a file.</param>
/// <param name="TypeName">The type as the tree's chip says it; null for a file.</param>
/// <param name="Toolchain">The vendor pill of the project the object belongs to; empty for a file.</param>
/// <param name="Status">The tree's word for the whole entry: added, modified, deleted, renamed.</param>
/// <param name="Layers">Every file of the object, touched or not, in the manifest's order; the one file for a plain file.</param>
public sealed record TouchedEntry(
    string Key, string Kind, string Name, string Path, string? From, string? Type, string? TypeName, string Toolchain, string Status,
    int Additions, int Deletions, IReadOnlyList<TouchedLayer> Layers);

/// <summary>
/// Which object a changed file belongs to, and a commit's file list regrouped by object — the
/// project format the Code and Compare views draw in, for the commit page and history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the commit page needed this.</b> Until 2026-10-27 <c>/commit</c> answered GitHub's
/// raw file list and the page drew one section per file: a GVL that is both <c>.gvl.st</c> and
/// <c>.gvl.xml</c> was two sections headed by two paths, with no object name, no type chip, no
/// vendor pill, and a manifest edit sat between them as a third. The tree beside it already drew
/// the object once with its marks; the main column did not agree with it.
/// </para>
/// <para>
/// <b>The status is read from the marked tree, never recomputed.</b> An object's state is the
/// strongest of its layers' (<c>ProjectTreeBuilder.Stronger</c>, which is Core's and internal),
/// and the tree the page shows beside the sections is built by that rule; grouping here reads the
/// row the tree built rather than restating the rule, so the section and the tree row cannot
/// disagree. The fallback for a row the tree does not have is the drawn layer's own status, and
/// a test pins that the fallback is not what answers for an ordinary object.
/// </para>
/// </remarks>
public static class TouchedObjects
{
    /// <summary>
    /// The object a path is a layer of, looked up at the newer commit first and at the older one
    /// for a file the commit removed; null for a file that is not an object's.
    /// </summary>
    /// <remarks>
    /// The older model is the only place a deleted object still exists, which is why both are
    /// asked: without the fallback a removed POU is a nameless file. This was written twice, in
    /// <c>/compare</c> and in <c>/pulls/{n}/files</c>, before the commit page became the third place.
    /// </remarks>
    public static ProjectTreeNode? ObjectFor(RepoModel newer, RepoModel older, string path)
    {
        ArgumentNullException.ThrowIfNull(newer);
        ArgumentNullException.ThrowIfNull(older);
        return (newer.NodeFor(path) ?? older.NodeFor(path)) is { Kind: ProjectNodeKind.Object } node ? node : null;
    }

    /// <summary>
    /// The tree at <paramref name="newer"/> marked with what changed since <paramref name="older"/>,
    /// as the desktop's Project panel marks a working-tree diff: A/M/D on each row, roll-ups on
    /// folders, deleted objects struck. With <paramref name="changedOnly"/>, only what changed and
    /// the folders that hold it.
    /// </summary>
    public static IReadOnlyList<ProjectTreeNode> Marked(RepoModel older, RepoModel newer, bool changedOnly)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);
        var changes = ViewService.ChangedPaths(older, newer)
            .Select(c => new FileChange
            {
                Path = c.Path,
                Staged = c.Change switch { "added" => ChangeKind.Added, "deleted" => ChangeKind.Deleted, _ => ChangeKind.Modified },
                Unstaged = ChangeKind.Unmodified,
            })
            .ToList();
        return RepoExplorerBuilder.Build(newer.Files.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList(), newer.Projects,
            new ProjectTreeOptions { ChangedOnly = changedOnly, HideUnchangedProjects = changedOnly }, changes: changes);
    }

    /// <summary>
    /// A commit's files regrouped: one entry per object the files belong to, carrying every layer
    /// with the touched ones flagged, then one entry per file outside any export. GitHub's order
    /// is kept — an entry sits where its first file was listed.
    /// </summary>
    /// <param name="files">The commit's files as GitHub listed them.</param>
    /// <param name="head">The commit.</param>
    /// <param name="parent">Its first parent, where a removed object still has a node.</param>
    /// <param name="markedTree"><see cref="Marked"/> from the parent to the commit: the rows the statuses and pills are read from.</param>
    public static IReadOnlyList<TouchedEntry> Group(IReadOnlyList<CommitFile> files, RepoModel head, RepoModel parent, IReadOnlyList<ProjectTreeNode> markedTree)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(markedTree);

        var rows = new Dictionary<string, (ProjectTreeNode Node, string Toolchain)>(StringComparer.Ordinal);
        Index(markedTree, string.Empty, rows);

        // Each file's object first, so an object's files are gathered from the whole list and not
        // from the one that happened to come first. GitHub lists at most 300 files for a commit,
        // so the quadratic gather below is a few thousand comparisons at the very worst.
        var owners = files.Select(f => (File: f, Node: ObjectFor(head, parent, f.Path))).ToList();
        var entries = new List<TouchedEntry>();
        var grouped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, node) in owners)
        {
            if (node is null)
            {
                entries.Add(new TouchedEntry(file.Path, "file", file.Path[(file.Path.LastIndexOf('/') + 1)..], file.Path, file.PreviousPath, null, null, string.Empty,
                    TreeWord(file.Status), file.Additions, file.Deletions, [Layer(file)]));
                continue;
            }

            if (!grouped.Add(node.Path))
            {
                continue;
            }

            var touched = owners.Where(o => o.Node?.Path == node.Path).Select(o => o.File).ToDictionary(f => f.Path, StringComparer.Ordinal);
            var layers = node.Files.Select(path => touched.TryGetValue(path, out var hit) ? Layer(hit) : new TouchedLayer(path, "unmodified", 0, 0, null, false)).ToList();
            // A listed file the index mapped to this object by its path rather than by a layer (a
            // row whose path is not one of its files) is the object's all the same; kept, not lost.
            layers.AddRange(touched.Values.Where(f => !node.Files.Contains(f.Path, StringComparer.Ordinal)).Select(Layer));

            // The readable layer is drawn when the commit touched it; a change to the native XML
            // alone draws the XML, since the primary file would compare as unchanged and the
            // section would show nothing of what the commit did. Scrolling the tree's pick to a
            // section id made of the primary file found no section at all in that case.
            var changed = layers.Where(l => l.Changed).ToList();
            var drawn = changed.FirstOrDefault(l => l.Path == node.PrimaryFile) ?? changed[0];
            var row = rows.GetValueOrDefault(node.Path);
            entries.Add(new TouchedEntry(node.Path, "object", node.Name, drawn.Path, drawn.PreviousPath, node.Type, node.TypeName, row.Toolchain ?? string.Empty,
                row.Node is { } marked ? marked.Status.ToString().ToLowerInvariant() : TreeWord(drawn.Status),
                changed.Sum(l => l.Additions), changed.Sum(l => l.Deletions), layers));
        }

        return entries;
    }

    private static TouchedLayer Layer(CommitFile file) => new(file.Path, file.Status, file.Additions, file.Deletions, file.PreviousPath, true);

    /// <summary>GitHub's status in the tree's words, so a plain file's mark matches an object's.</summary>
    private static string TreeWord(string gitHubStatus) => gitHubStatus switch
    {
        "removed" => "deleted",
        "renamed" => "renamed",
        "added" => "added",
        _ => "modified",
    };

    /// <summary>
    /// Every row that stands for something, by its path, with the pill of the nearest ancestor
    /// that carries one. The pill sits on a project's own row for a one-file project and on the
    /// folder for a directory project (<see cref="RepoExplorerBuilder"/>), and an object row
    /// never carries its own, so the walk hands each row the last pill seen on the way down.
    /// </summary>
    /// <remarks>
    /// Not only object rows: an object the commit removed is gone from the commit's manifest, so
    /// the marked tree draws it as a struck file row at its first layer's path — which is the
    /// object's path too (<c>ProjectTreeBuilder.BuildLeaf</c>), so the tree's row, this index and
    /// the page's section ids all meet on one key. Folders have no path a file could share.
    /// </remarks>
    private static void Index(IReadOnlyList<ProjectTreeNode> nodes, string toolchain, Dictionary<string, (ProjectTreeNode, string)> rows)
    {
        foreach (var node in nodes)
        {
            var pill = node.Toolchain.Length > 0 ? node.Toolchain : toolchain;
            if (node.Kind != ProjectNodeKind.Folder)
            {
                rows.TryAdd(node.Path, (node, pill));
            }

            Index(node.Children, pill, rows);
        }
    }
}
