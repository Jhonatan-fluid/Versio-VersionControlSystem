namespace Versio.Core.Sync;

/// <summary>
/// Asks what git currently reports as unmerged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Narrow on purpose.</b> The orchestrator has no git client and should not grow one for a
/// single question: everything else it needs about the working tree it gets from
/// <see cref="Abstractions.IFileSystem"/> and its own hashes. Handing it an
/// <see cref="Abstractions.IGitClient"/> would put branches, commits, diffs and the remote
/// within reach of a class whose whole discipline is that it cannot touch any of them.
/// </para>
/// <para>
/// Optional wherever it is accepted, exactly as the occupancy probe is: without one, nothing is
/// ever reported as conflicted and the behaviour is what it was before this existed. That keeps
/// the several hundred orchestrator tests that predate it constructing it without mention.
/// </para>
/// </remarks>
public interface IConflictProbe
{
    /// <summary>
    /// Repo-relative paths git reports as unmerged, or empty when the tree is not mid-merge.
    /// </summary>
    /// <remarks>
    /// The whole tree rather than a per-binding question, because that is the shape git answers
    /// in — one <c>status</c> covers every binding, and asking per binding would spawn a process
    /// each. Deciding which of them belongs to which binding is the orchestrator's job, and it
    /// already knows how to do it.
    /// </remarks>
    Task<IReadOnlyList<string>> UnmergedPathsAsync(CancellationToken ct);
}

/// <summary>A probe for a tree that is never mid-merge. The default when none is supplied.</summary>
/// <remarks>
/// A null object rather than a null check at the call site, for the same reason
/// <c>NullLogger.Instance</c> is: the one place that would have to remember the check is the
/// place where forgetting it is silent.
/// </remarks>
public sealed class NoConflicts : IConflictProbe
{
    public static NoConflicts Instance { get; } = new();

    public Task<IReadOnlyList<string>> UnmergedPathsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
