using Versio.Core.Plc;

namespace Versio.App.Tests;

/// <summary>
/// A toolchain that refuses with a fix on offer, and becomes available once the fix has been
/// applied — the shape of the TIA Portal Openness group, without Windows.
/// </summary>
/// <remarks>
/// The vendor's words are tested in the vendor's own project; this is the neutral flow —
/// the status carries a remedy, the shell draws it in three places, the button applies it,
/// the probe is asked again. <see cref="Applied"/> counts the presses; <see cref="Refuse"/>
/// makes the fix fail, for the notification that says so.
/// </remarks>
internal sealed class RemedyToolchain : IPlcToolchain
{
    public IPlcProjectFormat Format => Versio.Plc.Codesys.CodesysProjectFormat.Instance;

    public int Applied { get; private set; }

    public int Probes { get; private set; }

    /// <summary>When set, the fix fails with this sentence and the state stays refused.</summary>
    public string? Refuse { get; init; }

    /// <summary>When set, the fix answers uncertain with this sentence: still running, outcome unknown.</summary>
    public string? Unsure { get; init; }

    public bool Fixed { get; private set; }

    public static readonly PlcToolchainRemedy Remedy = new()
    {
        Action = "Add this account to the Fake group…",
        Explanation = "Adds the account to the local group 'Fake'. Windows asks for approval first.",
        Afterwards = "Sign out and back in, then try again.",
    };

    public Task<PlcToolchainStatus> ProbeAsync(CancellationToken ct)
    {
        Probes++;
        return Task.FromResult(Fixed
            ? new PlcToolchainStatus
            {
                Available = true,
                Version = "1.0",
                Profile = "fake",
                ExecutablePath = @"C:\nowhere\fake.exe",
                Explanation = @"C:\nowhere — 1.0",
            }
            : new PlcToolchainStatus
            {
                Available = false,
                Version = "1.0",
                Explanation = "Fake IDE is installed, but this account is not in the group 'Fake'.",
                Remedy = Remedy,
            });
    }

    /// <summary>Jobs run once the fix has made the toolchain available; empty before.</summary>
    public List<PlcJob> Jobs { get; } = [];

    public IPlcRunner CreateRunner(PlcToolchainStatus status) =>
        status.Available
            ? new Runner(this)
            : new Versio.Infrastructure.Plc.UnavailableToolchainRunner(status.Explanation ?? "unavailable");

    private sealed class Runner(RemedyToolchain owner) : IPlcRunner
    {
        public Task<PlcResult> RunAsync(PlcJob job, IProgress<PlcProgress>? progress, CancellationToken ct)
        {
            owner.Jobs.Add(job);
            return Task.FromResult(new PlcResult { Succeeded = true, Duration = TimeSpan.FromSeconds(1) });
        }
    }

    public Task<PlcRemedyOutcome> ApplyRemedyAsync(PlcToolchainStatus status, CancellationToken ct)
    {
        Applied++;
        if (Refuse is { } why)
        {
            return Task.FromResult(PlcRemedyOutcome.Refused(why));
        }

        if (Unsure is { } still)
        {
            return Task.FromResult(PlcRemedyOutcome.Unknown(still));
        }

        Fixed = true;
        return Task.FromResult(PlcRemedyOutcome.Done("added the account to 'Fake'. Sign out and back in, then try again."));
    }
}
