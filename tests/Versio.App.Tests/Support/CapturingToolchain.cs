using Versio.Core.Plc;

namespace Versio.App.Tests;

/// <summary>
/// A toolchain that reports itself available and records every job instead of running one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The app suite had no way to accept an import until this existed.</b> Every test in
/// <see cref="ImportConfirmationTests"/> either declines or never presses, because
/// <c>TestShell</c> wires <c>toolchains: (_, _) =&gt; null</c> — so the confirmed path, the one
/// that actually writes into somebody's project, was reachable only through
/// <c>Versio.Integration.Tests</c> and a real 17–31 s CODESYS launch against a real project.
/// </para>
/// <para>
/// That gap hid a specific thing: what the view model hands the orchestrator. A plan dropped
/// on that step is replaced by one the orchestrator computes, so the import still runs and
/// still succeeds — see <see cref="ImportPlanHandoverTests"/> for the assertion that can tell
/// those two apart.
/// </para>
/// <para>
/// It fails nothing and writes nothing. The point is the <see cref="Jobs"/> list.
/// </para>
/// </remarks>
internal sealed class CapturingToolchain : IPlcToolchain, IPlcRunner
{
    public IPlcProjectFormat Format => Versio.Plc.Codesys.CodesysProjectFormat.Instance;

    /// <summary>Every job the application asked to be run, in order.</summary>
    public List<PlcJob> Jobs { get; } = [];

    /// <summary>When set, every job fails with this reason instead of succeeding.</summary>
    public string? FailWith { get; init; }

    /// <summary>When set, the probe is available with this notice for the next run (a vendor's own dialog ahead).</summary>
    public string? Notice { get; init; }

    public Task<PlcToolchainStatus> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(new PlcToolchainStatus
        {
            Available = true,
            Version = "3.5.22.20",
            ExecutablePath = @"C:\nowhere\CODESYS.exe",
            Profile = "CODESYS V3.5 SP22 Patch 2",
            Notice = Notice,
        });

    public IPlcRunner CreateRunner(PlcToolchainStatus status) => this;

    /// <summary>
    /// Runs inside each job, after it is recorded and before it completes — the moment a
    /// save lands "while the last run was in flight". The same hook the Core fake has, for
    /// the same reason: nothing else can put the view model into that moment.
    /// </summary>
    public Func<PlcJob, Task>? BeforeRun { get; set; }

    public async Task<PlcResult> RunAsync(
        PlcJob job, IProgress<PlcProgress>? progress, CancellationToken ct)
    {
        Jobs.Add(job);

        if (BeforeRun is not null)
        {
            await BeforeRun(job).ConfigureAwait(false);
        }

        return new PlcResult
        {
            Succeeded = FailWith is null,
            FailureReason = FailWith,
            Duration = TimeSpan.FromSeconds(18),
        };
    }
}
