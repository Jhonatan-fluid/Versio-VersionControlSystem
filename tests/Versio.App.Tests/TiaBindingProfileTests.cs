using System.IO;
using Versio.Core.Plc;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// What adoption writes into the committed <c>.versio/repo.json</c> for a project whose
/// file names its own release.
/// </summary>
/// <remarks>
/// <b>Review, 2026-10-29.</b> Adoption pinned the vendor probe's profile — the newest Portal
/// on the machine — on every new binding, so a V20 project adopted on a machine with Portal
/// V20 and V21 was committed and shared as a V21 binding. Nothing acted on it yet (the TIA
/// runner reads the extension, not the profile), the log said <c>[V21]</c> on every export of
/// it, and the first guard to read <c>binding.Profile</c> as the release would have acted on
/// a falsehood. The format decides now, through <c>IPlcProjectFormat.ProfileFor</c>; this
/// drives the real adoption through the shell with a probe that says something else.
/// </remarks>
public sealed class TiaBindingProfileTests
{
    [Fact]
    public async Task A_V20_project_is_pinned_as_V20_whatever_the_probe_calls_newest()
    {
        using var repo = new TempRepo();
        var station = Path.Combine(repo.Path, "Station");
        Directory.CreateDirectory(Path.Combine(station, "System"));
        File.WriteAllText(Path.Combine(station, "Station.ap20"), "pointer");
        File.WriteAllText(Path.Combine(station, "System", "PEData.plf"), "program v1");

        // The probe's profile is not a Portal release at all, which is the point: whatever
        // the vendor-wide probe says, the file says V20.
        var toolchain = new CapturingToolchain();
        using var shell = TestShell.Build(out var sink, toolchain: toolchain);
        await shell.OpenAsync(repo.Path);

        string State() =>
            $"exports={toolchain.Jobs.Count(j => j.Operation == PlcOperation.Export)}\nlog:\n" +
            string.Join("\n", sink.Snapshot().Select(e => $"{e.Level} {e.Category} {e.Message}"));

        // Adoption is done once the catch-up export has been asked for.
        await Eventually.Until(
            () => toolchain.Jobs.Any(j => j.Operation == PlcOperation.Export),
            "the catch-up export ran", State);

        var config = File.ReadAllText(Path.Combine(repo.Path, ".versio", "repo.json"));
        Assert.Contains("\"projectPath\": \"Station/Station.ap20\"", config, StringComparison.Ordinal);
        Assert.Contains("\"profile\": \"V20\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("CODESYS V3.5 SP22 Patch 2", config, StringComparison.Ordinal);

        // And the job the runner was handed carries the same release.
        Assert.Equal("V20", toolchain.Jobs.First(j => j.Operation == PlcOperation.Export).Profile);
    }
}
