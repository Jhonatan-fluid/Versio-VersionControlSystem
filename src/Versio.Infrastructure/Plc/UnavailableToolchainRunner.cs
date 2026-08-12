using Versio.Core.Plc;

namespace Versio.Infrastructure.Plc;

/// <summary>
/// Stands in for a toolchain that is not installed, and fails every job with the reason.
/// </summary>
/// <remarks>
/// <para>
/// A repository full of CODESYS projects is a perfectly good git repository on a machine
/// with no CODESYS, so the sync engine is still built there: the direction lock, the
/// persisted state and recovery all work, and the panel can show what each project is doing.
/// Only the export itself is impossible.
/// </para>
/// <para>
/// The alternative — leaving <see cref="Versio.Core.Sync.SyncOrchestrator"/> unbuilt — makes
/// the whole panel go dark for a reason that has nothing to do with the state it was showing,
/// and turns a missing install into a null check in every caller.
/// </para>
/// <para>
/// Vendor-neutral: it stands in for any absent toolchain, not just CODESYS, and carries only
/// the sentence its caller supplied.
/// </para>
/// </remarks>
public sealed class UnavailableToolchainRunner(string explanation) : IPlcRunner
{
    public Task<PlcResult> RunAsync(
        PlcJob job,
        IProgress<PlcProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PlcResult
        {
            Succeeded = false,
            FailureReason = explanation,
            Duration = TimeSpan.Zero,
        });
}
