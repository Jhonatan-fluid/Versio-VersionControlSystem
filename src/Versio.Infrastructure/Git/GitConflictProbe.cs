using Versio.Core.Abstractions;
using Versio.Core.Sync;

namespace Versio.Infrastructure.Git;

/// <summary>
/// Answers the sync engine's conflict question out of <c>git status</c>.
/// </summary>
/// <remarks>
/// <para>
/// The porcelain parser already recognises unmerged entries and
/// <see cref="Core.Git.FileChange.IsConflicted"/> already reports them — they were simply read
/// by nothing. This is the wire between the two, and it is deliberately the whole of the
/// implementation: no caching, no cleverness, one question asked of git at the moment the
/// answer is needed.
/// </para>
/// <para>
/// <b>Not cached, on purpose.</b> A merge is resolved while Versio is running, and an answer
/// held from a minute ago would keep refusing syncs after the user had finished — which reads
/// as the tool being stuck. The orchestrator asks only once a change has passed the hash gate,
/// so this costs a process spawn per real change rather than per file event.
/// </para>
/// </remarks>
public sealed class GitConflictProbe(IGitClient git) : IConflictProbe
{
    public async Task<IReadOnlyList<string>> UnmergedPathsAsync(CancellationToken ct)
    {
        var status = await git.StatusAsync(ct).ConfigureAwait(false);

        if (!status.HasConflicts)
        {
            return [];
        }

        return [.. status.Changes.Where(c => c.IsConflicted).Select(c => c.Path)];
    }
}
