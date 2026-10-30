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
    private bool _isSelected;
    private ProjectTreeNode _node;

    public TreeRow(ProjectTreeNode node, int depth, TreeExpansion? expansion = null, object? context = null)
    {
        _node = node;
        Depth = depth;
        _expansion = expansion;
        Context = context;

        // Only a project opens itself; everything under it is opened by hand. The path to
        // each change used to open too, and on a real project that was the whole tree:
        // after a first export every object is a change, and after a save Program blocks
        // under every CPU unfolded down to the block (the owner's LBB101, 2026-10-30). The
        // dot on every row with a change below it is what says where to open now, and it
        // says so at every level, so nothing is lost by leaving the folders shut. The same
        // default as TreeExpansion.DefaultFor, for a row built without one.
        _isExpanded = expansion?.ShouldExpand(node, depth)
                      ?? node.Kind == ProjectNodeKind.Project;
    }

    public ProjectTreeNode Node => _node;

    /// <summary>
    /// Which tree this row belongs to, when the node alone cannot say.
    /// </summary>
    /// <remarks>
    /// The only identity a row has beyond its path. The Project and Explorer trees need none:
    /// their rows are the working tree's, and the shell reads the disk. The History panel hangs
    /// the commit on it (<c>CommitTreeContext</c>), because a row realised three levels down by
    /// <see cref="Realised"/> cannot be told by anyone else which commit it belongs to — and the
    /// shell, handed the row, must read the file at that commit rather than from disk.
    /// Opaque here so this class stays what it is: a row, not a history row.
    /// </remarks>
    public object? Context { get; }

    /// <summary>This row's children, built the first time anything asks for them.</summary>
    /// <remarks>
    /// Observable so a later export can add and remove rows here without the level being
    /// rebuilt. The declared type stays read-only: nothing outside this class may add to it,
    /// and <see cref="Reconcile"/> is the only thing that does.
    /// </remarks>
    public IReadOnlyList<TreeRow> Children => Realised;

    private ObservableCollection<TreeRow> Realised =>
        _children ??= [.. _node.Children.Select(c => new TreeRow(c, Depth + 1, _expansion, Context))];

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
            Reconcile(_children, fresh.Children, Depth + 1, _expansion, Context);
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
    /// <para>
    /// <b>A row whose path is further down is moved up, not inserted again.</b> The pass used
    /// to insert on every mismatch and never take the stale row out, so a level whose order
    /// changed — [A,B] rebuilt as [B,A] — came out as [B,A,B]: a duplicate row that nothing
    /// trimmed, holding a selection that pointed at the wrong one of the two. Reachable,
    /// because this level's order is not the path's: the library level sorts system
    /// libraries first and the rest by display name, and tasks follow the order the user
    /// gave them in the IDE (the 2026-10-24 review's item 37). Moved rather than replaced
    /// for the same reason <see cref="Adopt"/> exists — the instance is what WPF's selection
    /// and expander state hang off.
    /// </para>
    /// </remarks>
    public static void Reconcile(
        ObservableCollection<TreeRow> rows,
        IReadOnlyList<ProjectTreeNode> fresh,
        int depth,
        TreeExpansion? expansion,
        object? context = null)
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
                rows.Add(new TreeRow(fresh[i], depth, expansion, context));
                continue;
            }

            if (!string.Equals(rows[i].Node.Path, fresh[i].Path,
                               StringComparison.OrdinalIgnoreCase))
            {
                var later = IndexOf(rows, fresh[i].Path, from: i + 1);
                if (later < 0)
                {
                    rows.Insert(i, new TreeRow(fresh[i], depth, expansion, context));
                    continue;
                }

                rows.Move(later, i);
            }

            rows[i].Adopt(fresh[i]);
        }

        // Nothing past the fresh list belongs to it: a path listed twice by a builder would
        // otherwise leave its second row standing, since the removal pass above keeps every
        // row whose path is live.
        while (rows.Count > fresh.Count)
        {
            rows.RemoveAt(rows.Count - 1);
        }
    }

    private static int IndexOf(ObservableCollection<TreeRow> rows, string path, int from)
    {
        for (var j = from; j < rows.Count; j++)
        {
            if (string.Equals(rows[j].Node.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return j;
            }
        }

        return -1;
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

    /// <summary>True when this row's own object or file was deleted. Its name is drawn struck.</summary>
    /// <remarks>
    /// The red D alone was one letter in a column of M and ?, and a deleted property read as one
    /// more property of the block (the owner, 2026-10-22). Own status, not the roll-up: a folder
    /// holding one deleted method still exists.
    /// </remarks>
    public bool IsDeleted => Node.Status == ChangeKind.Deleted;

    /// <summary>
    /// The resource key of the glyph drawn before the name: what KIND of row this is.
    /// </summary>
    /// <remarks>
    /// The tree drew every row in the same face with nothing before the name, so a folder, an
    /// object and a library manager differed only by whether a chevron happened to sit beside
    /// them. Seen in the running window on 2026-09-21. By kind, never by object type: which
    /// glyph a POU deserves is a vendor's word, and the kinds are what every vendor's tree is
    /// built from.
    /// </remarks>
    public string IconKey => Node.Kind switch
    {
        ProjectNodeKind.Folder => "Icon.Folder",
        ProjectNodeKind.Project => "Icon.Project",
        ProjectNodeKind.Library => "Icon.Library",
        ProjectNodeKind.Task => "Icon.Task",
        ProjectNodeKind.Device => "Icon.Device",
        ProjectNodeKind.File => "Icon.Document",
        _ => "Icon.Code",
    };

    /// <summary>The object's type, shown beside the name. Empty for anything else.</summary>
    /// <remarks>
    /// The vendor's own word for it (see <see cref="ProjectTreeNode.TypeName"/>). The raw type
    /// is the fallback only for a node built without one, which is a test's hand-made node.
    /// </remarks>
    public string TypeLabel =>
        Node.Kind == ProjectNodeKind.Object
            ? Node.TypeName ?? Node.Type ?? string.Empty
            : string.Empty;

    /// <summary>Export state for a project row, empty elsewhere.</summary>
    public string Detail => Node.Detail;

    /// <summary>The Explorer's toolchain pill — <c>TIA Portal V21</c> — or empty.</summary>
    public string Toolchain => Node.Toolchain;

    /// <summary>What the last export could not write for this object, or null.</summary>
    public string? ExportWarning => Node.ExportWarning;

    /// <summary>Drawn in the warning colour by the panel, with the word on the annotation.</summary>
    public bool HasExportWarning => Node.ExportWarning is not null;

    /// <summary>True while a toolchain run is in flight for this project.</summary>
    public bool IsBusy => Node.Busy;

    /// <summary>
    /// The one phrase shown to the right of the name.
    /// </summary>
    /// <remarks>
    /// A project reports its export state; a library its version and a task its schedule,
    /// which are the facts a reviewer opens the catalog to check; an object reports its
    /// type, so a POU and a visualisation with the same name are told apart. One
    /// property rather than several columns competing for the same space, since no row ever
    /// has more than one.
    /// </remarks>
    public string Annotation => Node.Kind switch
    {
        ProjectNodeKind.Project or ProjectNodeKind.Library or ProjectNodeKind.Task
            or ProjectNodeKind.Device => Detail,
        // Only the Explorer sets a folder's Detail: the state of the directory project it is.
        ProjectNodeKind.Folder => Detail,
        // An object the export could not write says so beside its type, in words: the type
        // alone reads as "here it is", and what is here is the previous export. The detail
        // is the pane's, above the file; the colour is the panel's.
        _ when Node.ExportWarning is not null =>
            TypeLabel.Length == 0 ? "export refused" : TypeLabel + " · export refused",
        _ => TypeLabel,
    };

    /// <summary>
    /// Whether the context menu offers Stage and Discard for this row.
    /// </summary>
    /// <remarks>
    /// Only for a row that stands for files of its own with changes of its own — an object
    /// or a loose file. A library, task or device row shares its catalog's file, and staging
    /// "one reference" would silently stage every change in the file beside it; the manager's
    /// own row is where that operation honestly lives. Folders and projects group things,
    /// and per-file is the discard rule everywhere else in the app.
    /// </remarks>
    public bool CanStageOrDiscard =>
        Node.Kind is ProjectNodeKind.Object or ProjectNodeKind.File
        && Node.Status != ChangeKind.Unmodified;

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

    /// <summary>Whether the tree shows this row as the selected one.</summary>
    /// <remarks>
    /// Bound two-way to the container so the view model can <i>deselect</i>, which WPF's
    /// read-only <c>TreeView.SelectedItem</c> cannot be asked to do. Needed the moment the
    /// pane started clearing when the rail leaves a panel: a row left highlighted raises no
    /// selection change when it is clicked again, so the pane would stay blank on the way
    /// back. Letting go of the row is what makes the second click a click.
    /// </remarks>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
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

    /// <summary>The brush for this node's own letter.</summary>
    /// <remarks>
    /// Own change only. It fell back to the roll-up until 2026-10-30, which was dead once the
    /// count went: the only thing painted with it is <see cref="Glyph"/>, and a folder's glyph
    /// is blank. What a parent says about the changes below it is <see cref="HasChangesBelow"/>.
    /// </remarks>
    public string BrushKey => BrushFor(Node.Status);

    /// <summary>
    /// True when a change sits somewhere under this row: the dot beside its name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row used to carry a count of changed descendants — "91" on a folder, "1357" on a
    /// project — and after a first export every object is a change, so the owner read the
    /// numbers as counts of items and had them removed on every vendor (2026-10-29). The web
    /// tree then coloured every folder's name on the way down to an edit, and the desktop
    /// coloured nothing a folder actually draws, so a shut folder with a change under it looked
    /// like one without. Shown three treatments on 2026-10-30, the owner first picked a dot on
    /// a collapsed row only, then, with the tree no longer opening the way to each change,
    /// asked for the dot on every level that has a change below it, open or shut: it is the
    /// one mark a parent carries, in the strongest state's colour below
    /// (<see cref="ChangesBelowBrushKey"/>), and a shut folder with nothing changed under it
    /// carries nothing. The web tree draws the same rule.
    /// </para>
    /// <para>
    /// On the descendants' count, not on the roll-up, so that a deleted folder whose children
    /// all went with it still says so. The facts pane still states the number.
    /// </para>
    /// </remarks>
    public bool HasChangesBelow => Node.ChangedDescendants > 0;

    /// <summary>The dot's brush: the strongest change below this node.</summary>
    public string ChangesBelowBrushKey => BrushFor(Node.RollUp);

    /// <summary>The file the diff pane opens for this node, or null when it opens none.</summary>
    /// <remarks>
    /// The rule and its reasons live on <see cref="ProjectTreeNode.PrimaryFile"/> since
    /// 2026-10-11, so the web viewer opens the same file for the same node. Only the default:
    /// the diff pane's header offers the other layers as chips (<see cref="DiffLayerChoice"/>),
    /// so the native half is never unreachable.
    /// </remarks>
    public string? PrimaryFile => Node.PrimaryFile;

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
