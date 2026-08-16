using Versio.Core.Abstractions;
using Versio.Core.Sync;

namespace Versio.Infrastructure.Git;

/// <summary>
/// <see cref="IHistoricContentProbe"/> over the real git CLI.
/// </summary>
/// <remarks>
/// The adapter that keeps <c>SyncOrchestrator</c> off <see cref="IGitClient"/>, exactly as
/// <see cref="GitConflictProbe"/> does for unmerged paths. Everything interesting is in
/// <c>GitCliClient.ShowAtHeadAsync</c>; this is the seam, not the work.
/// </remarks>
public sealed class GitHistoricContent(IGitClient git) : IHistoricContentProbe
{
    public Task<string> AtHeadAsync(string repoRelativePath, CancellationToken ct) =>
        git.ShowAtHeadAsync(repoRelativePath, ct);
}
