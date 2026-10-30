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
    private readonly bool _expandTopLevelFolders;

    /// <param name="expandTopLevelFolders">
    /// Explorer default: open the first level of the clone so the listing is a listing, not
    /// a single closed chevron named <c>src</c>. The Project panel leaves this off — its
    /// roots are projects, which already open themselves.
    /// </param>
    public TreeExpansion(bool expandTopLevelFolders = false) =>
        _expandTopLevelFolders = expandTopLevelFolders;

    /// <summary>Whether a freshly built row should start open.</summary>
    public bool ShouldExpand(ProjectTreeNode node, int depth = 0)
    {
        if (_closed.Contains(node.Path))
        {
            return false;
        }

        if (_opened.Contains(node.Path))
        {
            return true;
        }

        if (_expandTopLevelFolders && depth == 0 && node.Children.Count > 0)
        {
            return true;
        }

        return DefaultFor(node);
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
        ArgumentNullException.ThrowIfNull(state);
        Clear();

        // Either list is null when the file on disk left it out: System.Text.Json builds the
        // positional record from `{}` or `{"opened":null}` with nulls, and UnionWith(null)
        // threw ArgumentNullException out of the attach — past the catch that only expected
        // a torn file — before the orchestrator was built or the watcher started (the
        // 2026-10-24 review's item 12). A list that is not there is a list that is empty.
        _opened.UnionWith(state.Opened ?? []);
        _closed.UnionWith(state.Closed ?? []);
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
    /// <para>
    /// A project opens, because it is what the panel is about and a closed one shows nothing.
    /// Everything under it stays shut until somebody opens it.
    /// </para>
    /// <para>
    /// Until 2026-10-30 anything with children opened when there was a change under it, so
    /// the four objects somebody edited were on screen without burying them under the four
    /// thousand they did not. On a real project that rule opened the whole tree: after a
    /// first export every object is a change, and after one save on LBB101 the Program blocks
    /// of every CPU unfolded to the block. The owner asked for the project row alone, with
    /// the rest by hand — and the dot on every row with a change below it
    /// (<see cref="TreeRow.HasChangesBelow"/>) is what now says where to open, at every level,
    /// so an edited method behind a closed block or a bumped library behind its manager is
    /// still pointed at, just not unfolded. Selecting an object from elsewhere (a type link,
    /// a changed file) still opens the way to it: that is the shell's reveal, not this default.
    /// </para>
    /// </remarks>
    private static bool DefaultFor(ProjectTreeNode node) =>
        node.Kind == ProjectNodeKind.Project;
}
