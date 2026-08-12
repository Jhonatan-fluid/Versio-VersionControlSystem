using Versio.App.Mvvm;
using Versio.Core.Git;
using Versio.Core.Projects;

namespace Versio.App.ViewModels;

/// <summary>
/// One row of the project tree, as the view binds to it.
/// </summary>
/// <remarks>
/// A thin wrapper over the immutable <see cref="ProjectTreeNode"/> the builder produced. The
/// node carries the facts; this carries what the view additionally needs and the node has no
/// business knowing - whether it is expanded, and how to phrase its badge.
/// </remarks>
public sealed class TreeRow : ObservableObject
{
    private bool _isExpanded;

    public TreeRow(ProjectTreeNode node, int depth)
    {
        Node = node;
        Depth = depth;
        Children = [.. node.Children.Select(c => new TreeRow(c, depth + 1))];

        // Folders on the path to a change open themselves. Somebody who filtered to "changed
        // only" and then had to expand five levels by hand would reasonably wonder what the
        // filter was for.
        _isExpanded = node.IsFolder && node.HasChanges;
    }

    public ProjectTreeNode Node { get; }

    public IReadOnlyList<TreeRow> Children { get; }

    /// <summary>Nesting level, for the indent.</summary>
    public int Depth { get; }

    public string Name => Node.Name;

    public bool IsFolder => Node.IsFolder;

    /// <summary>The object's CODESYS type, shown beside the name. Empty for a folder.</summary>
    public string TypeLabel => Node.IsFolder ? string.Empty : Node.Type?.ToLowerInvariant() ?? "";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
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
    /// Shown on folders only. On a leaf the glyph already says everything, and a "0" beside
    /// every unchanged object would be noise in the one view meant to be scannable.
    /// </remarks>
    public string Badge =>
        Node.IsFolder && Node.ChangedDescendants > 0
            ? Node.ChangedDescendants.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

    public string BrushKey => BrushFor(
        Node.Status != ChangeKind.Unmodified ? Node.Status : Node.RollUp);

    /// <summary>The file the diff pane opens for this node.</summary>
    /// <remarks>
    /// The readable layer wins. An object exporting to both <c>.st</c> and <c>.xml</c> opens
    /// the Structured Text, because that is the one somebody can review; the native XML is
    /// there for completeness and reads like a machine wrote it, which it did. A proper layer
    /// switcher belongs in the diff pane and is not built yet.
    /// </remarks>
    public string? PrimaryFile =>
        Node.Files.FirstOrDefault(f => f.EndsWith(".st", StringComparison.OrdinalIgnoreCase))
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
