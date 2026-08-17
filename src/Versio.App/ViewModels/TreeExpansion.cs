using Versio.Core.Projects;

namespace Versio.App.ViewModels;

/// <summary>
/// Which nodes are open, kept outside the rows so it survives a rebuild.
/// </summary>
/// <remarks>
/// <para>
/// The tree is rebuilt from scratch whenever git's status moves, which - now that it is the
/// only view - is several times a minute while somebody works. Rebuilding used to be free
/// because the tree was a secondary panel nobody was looking at; as the primary view, a
/// rebuild that collapsed everything the user had opened would be the same class of
/// regression as the changed-files list that dropped its selection on every refresh.
/// </para>
/// <para>
/// Keyed by node path, because that is the only identity that survives a rebuild - the rows
/// themselves are new objects every time. Both sets are needed rather than one: a folder the
/// user deliberately closed has to stay closed even though the default rule would open it,
/// and a set of open paths alone cannot express that.
/// </para>
/// </remarks>
public sealed class TreeExpansion
{
    private readonly HashSet<string> _opened = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _closed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a freshly built row should start open.</summary>
    public bool ShouldExpand(ProjectTreeNode node)
    {
        if (_closed.Contains(node.Path))
        {
            return false;
        }

        return _opened.Contains(node.Path) || DefaultFor(node);
    }

    /// <summary>Remember what the user just did to a row.</summary>
    public void Record(ProjectTreeNode node, bool expanded)
    {
        // Recorded on both sides rather than only the deviation, so the answer does not
        // depend on whether the default rule has since changed its mind about this node -
        // which it does, every time the node's change state moves.
        if (expanded)
        {
            _closed.Remove(node.Path);
            _opened.Add(node.Path);
        }
        else
        {
            _opened.Remove(node.Path);
            _closed.Add(node.Path);
        }
    }

    /// <summary>Forget everything. Called when the repository changes.</summary>
    public void Clear()
    {
        _opened.Clear();
        _closed.Clear();
    }

    /// <summary>Both sets, for persisting when the repository closes.</summary>
    public State Snapshot() => new([.. _opened], [.. _closed]);

    /// <summary>
    /// Put back what a previous session recorded. Replaces, never merges: the state on disk
    /// is the whole answer for its repository, and merging would resurrect rows the previous
    /// session deliberately overwrote.
    /// </summary>
    public void Restore(State state)
    {
        Clear();
        _opened.UnionWith(state.Opened);
        _closed.UnionWith(state.Closed);
    }

    /// <summary>
    /// The two sets as one value, shaped for a JSON file under <c>.versio/local/</c>.
    /// </summary>
    /// <remarks>
    /// Per repository by construction — the paths are repo-relative, and the file lives in
    /// the repository they are relative to.
    /// </remarks>
    public sealed record State(IReadOnlyList<string> Opened, IReadOnlyList<string> Closed)
    {
        public static readonly State Empty = new([], []);
    }

    /// <summary>
    /// What a node does when nobody has touched it.
    /// </summary>
    /// <remarks>
    /// A project opens, because it is what the panel is about and a closed one shows nothing.
    /// Anything else with children opens only when there is a change under it: with the full
    /// tree rendered rather than the changed-only filter, opening everything would bury the
    /// four objects somebody edited under the four thousand they did not. Tested on
    /// <see cref="ProjectTreeNode.Children"/> rather than <see cref="ProjectTreeNode.IsFolder"/>
    /// — the same correction <c>PruneUnchanged</c> already carries — because an object owns
    /// rows now: a function block owns its methods and a library manager its references, and
    /// the folder-only rule left an edited method and a bumped library version behind a
    /// closed chevron with only the badge to hint at them.
    /// </remarks>
    private static bool DefaultFor(ProjectTreeNode node) =>
        node.Kind == ProjectNodeKind.Project ||
        (node.Children.Count > 0 && node.HasChanges);
}
