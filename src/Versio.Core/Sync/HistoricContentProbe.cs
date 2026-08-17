namespace Versio.Core.Sync;

/// <summary>
/// Reads a file as the last commit has it — the only copy of something the working tree has
/// deleted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Narrow on purpose, exactly like <see cref="IConflictProbe"/>.</b> The orchestrator does
/// not take an <c>IGitClient</c> and should not start now: it needs one question answered, and
/// a one-method probe is what keeps the sync engine off a forty-member interface it would then
/// be free to reach into. The implementation lives in <c>Versio.Infrastructure</c> beside
/// <c>GitConflictProbe</c>.
/// </para>
/// <para>
/// <b>What it is for.</b> Planning a rename pairs a departed file with the one it became. While
/// the content is unchanged a hash settles it, and the manifest records one — but a file that
/// was renamed <i>and</i> edited matches no hash, and the manifest keeps a hash rather than the
/// bytes. HEAD is the only place the old content still exists.
/// </para>
/// <para>
/// <b>Absence is an ordinary answer, not a failure.</b> A mirror that has never been committed,
/// a repository with no commits, a path HEAD never had — all mean "no old copy", and the caller
/// falls back to what it did before this existed: a delete and a create.
/// </para>
/// </remarks>
public interface IHistoricContentProbe
{
    /// <summary>
    /// The file's content at HEAD, or empty when HEAD does not have it.
    /// </summary>
    /// <param name="repoRelativePath">Repository-relative, forward slashes.</param>
    Task<string> AtHeadAsync(string repoRelativePath, CancellationToken ct);
}

/// <summary>The answer for a repository nobody supplied a probe for: nothing is in HEAD.</summary>
/// <remarks>
/// A null-object rather than a null check at the call site, matching <c>NoConflicts</c>. The
/// planner then sees no departed content and behaves as it did before the probe existed, which
/// is the correct fallback rather than a degraded one.
/// </remarks>
public sealed class NoHistory : IHistoricContentProbe
{
    public static readonly NoHistory Instance = new();

    private NoHistory()
    {
    }

    public Task<string> AtHeadAsync(string repoRelativePath, CancellationToken ct) =>
        Task.FromResult(string.Empty);
}
