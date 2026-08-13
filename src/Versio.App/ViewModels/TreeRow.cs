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
    private IReadOnlyList<TreeRow>? _children;
    private bool _isExpanded;

    public TreeRow(ProjectTreeNode node, int depth, TreeExpansion? expansion = null)
    {
        Node = node;
        Depth = depth;
        _expansion = expansion;

        // Folders on the path to a change open themselves, and so does a project. Somebody
        // who had to expand five levels by hand to find the object they just edited would
        // reasonably wonder what the roll-up badges were for.
        _isExpanded = expansion?.ShouldExpand(node)
                      ?? (node.Kind == ProjectNodeKind.Project || (node.IsFolder && node.HasChanges));
    }

    public ProjectTreeNode Node { get; }

    /// <summary>This row's children, built the first time anything asks for them.</summary>
    public IReadOnlyList<TreeRow> Children =>
        _children ??= [.. Node.Children.Select(c => new TreeRow(c, Depth + 1, _expansion))];

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
    /// A project reports its export state; an object reports its CODESYS type, so a POU and a
    /// visualisation with the same name are told apart. One property rather than two columns
    /// competing for the same space, since no row ever has both.
    /// </remarks>
    public string Annotation => IsProject ? Detail : TypeLabel;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                // Recorded so the next rebuild - one per git status refresh - puts the tree
                // back the way the user left it rather than the way the defaults want it.
                _expansion?.Record(Node, value);
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
    /// there for completeness and reads like a machine wrote it, which it did. A proper layer
    /// switcher belongs in the diff pane and is not built yet.
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
