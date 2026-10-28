using Versio.Core.Git;
using Versio.Core.Projects;

namespace Versio.App.ViewModels;

/// <summary>
/// One commit, read as a project: what its tree held, what it changed, and the rows the
/// History panel draws under it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built from the commit's own manifest, never from today's index.</b> The history's rows
/// were deliberately bare paths until 2026-10-27, because the object index describes the
/// current export and naming a historical path from it would put today's name on
/// yesterday's file. That objection was right; the answer is to read yesterday's manifest.
/// <see cref="Repo"/> is <see cref="ProjectSnapshotLoader"/>'s reading of <c>repo.json</c>,
/// the manifests and the catalogs <i>as of this commit</i>, through <c>git show</c>; the
/// same loader the web viewer reads GitHub with.
/// </para>
/// <para>
/// Immutable, like the commit: its hash is its content, so a snapshot can be held for the
/// life of the session with no risk of going stale — the same argument that made the old
/// file-list cache safe. The patches are still not held (see <see cref="CommitRow"/>).
/// </para>
/// </remarks>
/// <param name="Changes">What the commit did to each path (<c>--name-status</c>); empty for a merge, whose combined diff git suppresses.</param>
/// <param name="Nodes">The changed-only project tree: every changed object or file under its project and folders, from <see cref="RepoExplorerBuilder"/>.</param>
/// <param name="GitCalls">How many git processes the snapshot cost, for the log line that holds the budget still.</param>
public sealed record CommitSnapshot(
    string Id,
    string ShortId,
    ProjectSnapshot Repo,
    IReadOnlyList<CommitFile> Changes,
    IReadOnlyList<ProjectTreeNode> Nodes,
    int GitCalls)
{
    /// <summary>The commit's own object index, for the commit heading and for type links.</summary>
    public ObjectIndex Objects => Repo.Objects;
}

/// <summary>
/// What a <see cref="TreeRow"/> under a commit carries in <see cref="TreeRow.Context"/>: the
/// commit it belongs to, and the snapshot its node came from.
/// </summary>
/// <remarks>
/// The shell reads it to know <i>which</i> commit a picked row means — the same path exists
/// in every commit that ever touched it — and therefore which revision to read the file at.
/// </remarks>
public sealed record CommitTreeContext(CommitRow Commit, CommitSnapshot Snapshot);
