using System.Text.Json;
using Versio.Core.Abstractions;
using Versio.Core.Plc.Codesys;

namespace Versio.Infrastructure.Plc.Codesys;

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
/// </remarks>
public sealed class UnavailableToolchainRunner(string explanation) : ICodesysRunner
{
    public Task<CodesysResult> RunAsync(
        CodesysJob job,
        IProgress<CodesysProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.FromResult(new CodesysResult
        {
            ExitCode = -1,
            TimedOut = false,
            Duration = TimeSpan.Zero,

            // Reported as a fatal script event rather than a bare exit code, because that is
            // the channel CodesysResult.FailureReason prefers — so the user sees the actual
            // sentence instead of "CODESYS exited with code -1".
            Output = new ScriptOutput
            {
                Events =
                [
                    new ScriptEvent
                    {
                        Type = "fatal",
                        Fields = new Dictionary<string, JsonElement>
                        {
                            ["msg"] = JsonSerializer.SerializeToElement(explanation),
                        },
                    },
                ],
            },
        });
}
