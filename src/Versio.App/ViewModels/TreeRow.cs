using System.Collections.ObjectModel;
using Versio.App.Mvvm;
using Versio.Core.Git;
using Versio.Core.Projects;

namespace Versio.App.ViewModels;

/// <summary>
/// One row of the project tree, as the view binds to it.
/// </summary>
/// <remarks>
/// <para>
/// A thin wrapper over the immutable <see cref="ProjectTreeNode"/> the builder produced. The
/// node carries the facts; this carries what the view additionally needs and the node has no
/// business knowing - whether it is expanded, and how to phrase its badge.
/// </para>
/// <para>
/// <b>Children are built on first access, not in the constructor.</b> They used to be built
/// eagerly, which was affordable only because the tree was filtered to what had changed.
/// Rendering the full CODESYS structure that way would construct a row per object in the
/// project - thousands on a real machine - before the window drew anything, which is the
/// same mistake the diff view made when its virtualization turned out to be decorative.
/// </para>
/// <para>
/// This makes the cost proportional to what is <i>reachable</i> rather than to what exists.
/// WPF evaluates <see cref="Children"/> when it realises a container, because it cannot know
/// whether to draw an expander without asking - so a visible collapsed folder still costs one
/// row per child, and its grandchildren cost nothing. Turning O(objects in the project) into
/// O(visible rows x branching) is the whole of the win, and it is worth being precise that it
/// is not zero.
/// </para>
/// </remarks>
public sealed class TreeRow : ObservableObject
{
    private readonly TreeExpansion? _expansion;
    private ObservableCollection<TreeRow>? _children;
    private bool _isExpanded;
    private ProjectTreeNode _node;

    public TreeRow(ProjectTreeNode node, int depth, TreeExpansion? expansion = null)
    {
        _node = node;
        Depth = depth;
        _expansion = expansion;

        // Anything on the path to a change opens itself, and so does a project. Somebody who
        // had to expand five levels by hand to find the object they just edited would
        // reasonably wonder what the roll-up badges were for.
        //
        // Written as "has changed descendants" rather than "is a folder with changes",
        // because a function block owns its methods now: an edited method sat behind a
        // collapsed block, which is the same complaint one level down. The two are equivalent
        // for a folder - a folder has no state of its own to change - so this is the same
        // rule stated in the terms that now cover both.
        _isExpanded = expansion?.ShouldExpand(node)
                      ?? (node.Kind == ProjectNodeKind.Project || node.ChangedDescendants > 0);
    }

    public ProjectTreeNode Node => _node;

    /// <summary>This row's children, built the first time anything asks for them.</summary>
    /// <remarks>
    /// Observable so a later export can add and remove rows here without the level being
    /// rebuilt. The declared type stays read-only: nothing outside this class may add to it,
    /// and <see cref="Reconcile"/> is the only thing that does.
    /// </remarks>
    public IReadOnlyList<TreeRow> Children => Realised;

    private ObservableCollection<TreeRow> Realised =>
        _children ??= [.. _node.Children.Select(c => new TreeRow(c, Depth + 1, _expansion))];

    /// <summary>
    /// Take fresh facts for the node this row already represents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row object survives, which is the entire point: WPF keeps its container, its
    /// expander state and — critically — the selection, because <c>SelectedItem</c> is a
    /// reference to this instance. Replacing the row would drop all three, which is what
    /// clearing and refilling the collection used to do on every export.
    /// </para>
    /// <para>
    /// Everything the view shows is derived from <see cref="Node"/>, so one empty-name
    /// notification re-reads the lot. Enumerating a dozen property names here would be a list
    /// to forget to extend the next time the row grows a badge.
    /// </para>
    /// <para>
    /// <b>Children are reconciled only if they were ever realised.</b> Touching
    /// <see cref="Children"/> on an unrealised row would build it — and doing that from a
    /// refresh would construct a row per object in the project, thousands on a real machine,
    /// undoing the laziness this class exists for.
    /// </para>
    /// </remarks>
    public void Adopt(ProjectTreeNode fresh)
    {
        _node = fresh;

        OnPropertyChanged(string.Empty);

        if (_children is not null)
        {
            Reconcile(_children, fresh.Children, Depth + 1, _expansion);
        }
    }

    /// <summary>
    /// Bring a level of rows into line with freshly built nodes, touching only what moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same single forward pass the changed-files list uses, for the same reason: an
    /// export that lands while somebody is reading the tree must appear as the objects that
    /// actually changed appearing and disappearing, not as the tree vanishing and coming back.
    /// </para>
    /// <para>
    /// Identity is the node's path. A row never changes path — an object that moved is a
    /// different row — which is what makes a positional pass safe once the removals are done.
    /// </para>
    /// </remarks>
    public static void Reconcile(
        ObservableCollection<TreeRow> rows,
        IReadOnlyList<ProjectTreeNode> fresh,
        int depth,
        TreeExpansion? expansion)
    {
        var live = fresh.Select(n => n.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!live.Contains(rows[i].Node.Path))
            {
                rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < fresh.Count; i++)
        {
            if (i >= rows.Count)
            {
                rows.Add(new TreeRow(fresh[i], depth, expansion));
            }
            else if (!string.Equals(rows[i].Node.Path, fresh[i].Path,
                                    StringComparison.OrdinalIgnoreCase))
            {
                rows.Insert(i, new TreeRow(fresh[i], depth, expansion));
            }
            else
            {
                rows[i].Adopt(fresh[i]);
            }
        }
    }

    /// <summary>Nesting level, for the indent.</summary>
    public int Depth { get; }

    public string Name => Node.Name;

    public bool IsFolder => Node.IsFolder;

    /// <summary>True for the <c>.project</c> row, which reports its export instead of a diff.</summary>
    public bool IsProject => Node.Kind == ProjectNodeKind.Project;

    /// <summary>True for a changed file that belongs to no object — a document, a script.</summary>
    /// <remarks>
    /// Drawn in the monospaced face, because its name is a whole repo-relative path rather
    /// than an object name. It has no folder above it to give it context, so the path is the
    /// context.
    /// </remarks>
    public bool IsFile => Node.Kind == ProjectNodeKind.File;

    /// <summary>The object's CODESYS type, shown beside the name. Empty for anything else.</summary>
    public string TypeLabel =>
        Node.Kind == ProjectNodeKind.Object ? Node.Type?.ToLowerInvariant() ?? "" : string.Empty;

    /// <summary>Export state for a project row, empty elsewhere.</summary>
    public string Detail => Node.Detail;

    /// <summary>True while a toolchain run is in flight for this project.</summary>
    public bool IsBusy => Node.Busy;

    /// <summary>
    /// The one phrase shown to the right of the name.
    /// </summary>
    /// <remarks>
    /// A project reports its export state; a library its version and a task its schedule,
    /// which are the facts a reviewer opens the catalog to check; an object reports its
    /// CODESYS type, so a POU and a visualisation with the same name are told apart. One
    /// property rather than several columns competing for the same space, since no row ever
    /// has more than one.
    /// </remarks>
    public string Annotation => Node.Kind switch
    {
        ProjectNodeKind.Project or ProjectNodeKind.Library or ProjectNodeKind.Task => Detail,
        _ => TypeLabel,
    };

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                // Recorded so the next rebuild - one per git status refresh - puts the tree
                // back the way the user left it rather than the way the defaults want it.
                _expansion?.Record(_node, value);
            }
        }
    }

    /// <summary>One letter for this node's own state, blank when it did not change.</summary>
    public string Glyph => Node.Status switch
    {
        ChangeKind.Added => "A",
        ChangeKind.Modified => "M",
        ChangeKind.Deleted => "D",
        ChangeKind.Renamed => "R",
        ChangeKind.Copied => "C",
        ChangeKind.Untracked => "?",
        ChangeKind.Conflicted => "!",
        _ => " ",
    };

    /// <summary>How many descendants changed, blank on a leaf or a quiet folder.</summary>
    /// <remarks>
    /// Shown on folders and projects only. On a leaf the glyph already says everything, and a
    /// "0" beside every unchanged object would be noise in the one view meant to be scannable
    /// - which matters more now that every object is drawn rather than only the changed ones.
    /// </remarks>
    public string Badge =>
        Node.Children.Count > 0 && Node.ChangedDescendants > 0
            ? Node.ChangedDescendants.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

    public string BrushKey => BrushFor(
        Node.Status != ChangeKind.Unmodified ? Node.Status : Node.RollUp);

    /// <summary>The file the diff pane opens for this node, or null when it opens none.</summary>
    /// <remarks>
    /// <para>
    /// The readable layer wins. An object exporting to both <c>.st</c> and <c>.xml</c> opens
    /// the Structured Text, because that is the one somebody can review; the native XML is
    /// there for completeness and reads like a machine wrote it, which it did. This is only
    /// the <i>default</i>: the diff pane's header offers the other layers as chips
    /// (<see cref="DiffLayerChoice"/>), so the native half stopped being unreachable.
    /// </para>
    /// <para>
    /// Null for a project row. A <c>.project</c> is a binary compound document - git can store
    /// it and cannot diff it, which is the entire reason this tool exists - so the row reports
    /// its export instead, and the pane says so rather than showing an empty diff.
    /// </para>
    /// </remarks>
    public string? PrimaryFile =>
        IsProject || IsFolder
            ? null
            : Node.Files.FirstOrDefault(f => f.EndsWith(".st", StringComparison.OrdinalIgnoreCase))
              ?? Node.Files.FirstOrDefault();

    private static string BrushFor(ChangeKind kind) => kind switch
    {
        ChangeKind.Added or ChangeKind.Untracked => "Brush.Status.Added",
        ChangeKind.Modified => "Brush.Status.Modified",
        ChangeKind.Deleted => "Brush.Status.Deleted",
        ChangeKind.Renamed or ChangeKind.Copied => "Brush.Status.Renamed",
        ChangeKind.Conflicted => "Brush.Status.Conflict",
        _ => "Brush.Status.Neutral",
    };
}
