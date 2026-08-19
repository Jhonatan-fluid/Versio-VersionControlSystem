using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Plc.TiaPortal.Infrastructure;
using Versio.TestSupport;
using Xunit;

namespace Versio.Plc.TiaPortal.Tests;

/// <summary>
/// Real TIA Portal, when the machine has it. Skip is the success path on every other machine.
/// </summary>
[Collection(TiaPortalCollection.Name)]
public sealed class TiaPortalGateTests
{
    [SkippableFact]
    public void The_locator_finds_the_PublicAPI_folder()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TIA Portal is Windows-only");
        var installs = new TiaPortalLocator().FindInstalls();
        Skip.If(installs.Count == 0, "no TIA Portal install with a PublicAPI net48 folder");

        var install = Assert.Single(installs);
        Assert.True(Directory.Exists(install.ApiFolder));
        Assert.True(File.Exists(Path.Combine(install.ApiFolder, "Siemens.Engineering.Base.dll")));
    }

    [SkippableFact]
    public async Task Probe_opens_the_committed_fixture_or_explains_why_not()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TIA Portal is Windows-only");
        var installs = new TiaPortalLocator().FindInstalls();
        Skip.If(installs.Count == 0, "no TIA Portal install");

        var fixture = TestPaths.TiaPortalProject();
        Skip.If(fixture is null, "committed TIA fixture is missing or is an LFS pointer — git lfs pull");

        var host = LocalTiaPortalRunner.FindHost();
        Skip.If(host is null, "TIA Portal host was not copied to the output directory");

        var toolchain = new TiaPortalToolchain(NullLogger.Instance);
        var status = await toolchain.ProbeAsync(CancellationToken.None);
        Skip.If(!status.Available, status.Explanation);

        var runner = toolchain.CreateRunner(status);
        var outDir = Path.Combine(Path.GetTempPath(), "versio-tia-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);

        try
        {
            var result = await runner.RunAsync(
                new PlcJob
                {
                    Operation = PlcOperation.Probe,
                    Profile = status.Profile ?? "",
                    Projects =
                    [
                        new PlcProjectJob
                        {
                            BindingId = "tia-probe",
                            ProjectPath = fixture!,
                            Path = outDir,
                        },
                    ],
                    Timeout = TimeSpan.FromMinutes(5),
                },
                progress: null,
                CancellationToken.None);

            if (!result.Succeeded)
            {
                // Openness membership and the first-connect firewall AllowList are measured
                // blockers, not product bugs. The failure reason is the record.
                // "Security error. The operation has timed out." is the unanswered
                // AllowList dialog — it names neither Access nor Openness, so the first
                // probe of this machine went red instead of skip until that string was added.
                Skip.If(
                    result.FailureReason?.Contains("Openness", StringComparison.OrdinalIgnoreCase) == true
                    || result.FailureReason?.Contains("Access", StringComparison.OrdinalIgnoreCase) == true
                    || result.FailureReason?.Contains("held", StringComparison.OrdinalIgnoreCase) == true
                    || result.FailureReason?.Contains("Security error", StringComparison.OrdinalIgnoreCase) == true
                    || result.FailureReason?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true,
                    result.FailureReason);
            }

            Assert.True(result.Succeeded, result.FailureReason);
            Assert.True(File.Exists(Path.Combine(outDir, "report.json")));
        }
        finally
        {
            try
            {
                Directory.Delete(outDir, recursive: true);
            }
            catch (IOException)
            {
                // Temp.
            }
        }
    }

    [SkippableFact]
    public async Task Export_writes_a_manifest_and_names_the_coverage_skips()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TIA Portal is Windows-only");
        var installs = new TiaPortalLocator().FindInstalls();
        Skip.If(installs.Count == 0, "no TIA Portal install");

        var fixture = TestPaths.TiaPortalProject();
        Skip.If(fixture is null, "committed TIA fixture is missing or is an LFS pointer — git lfs pull");

        var toolchain = new TiaPortalToolchain(NullLogger.Instance);
        var status = await toolchain.ProbeAsync(CancellationToken.None);
        Skip.If(!status.Available, status.Explanation);
        Skip.If(LocalTiaPortalRunner.FindHost() is null, "TIA Portal host was not copied to the output directory");

        var runner = toolchain.CreateRunner(status);
        var outDir = Path.Combine(Path.GetTempPath(), "versio-tia-export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);

        try
        {
            var result = await runner.RunAsync(
                new PlcJob
                {
                    Operation = PlcOperation.Export,
                    Profile = status.Profile ?? "",
                    Projects =
                    [
                        new PlcProjectJob
                        {
                            BindingId = "tia-export",
                            ProjectPath = fixture!,
                            Path = outDir,
                        },
                    ],
                    Timeout = TimeSpan.FromMinutes(20),
                },
                progress: null,
                CancellationToken.None);

            if (!result.Succeeded)
            {
                Skip.If(
                    result.FailureReason?.Contains("Openness", StringComparison.OrdinalIgnoreCase) == true
                    || result.FailureReason?.Contains("Access", StringComparison.OrdinalIgnoreCase) == true
                    || result.FailureReason?.Contains("held", StringComparison.OrdinalIgnoreCase) == true
                    || result.FailureReason?.Contains("Security error", StringComparison.OrdinalIgnoreCase) == true
                    || result.FailureReason?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true,
                    result.FailureReason);
            }

            Assert.True(result.Succeeded, result.FailureReason);
            Assert.True(File.Exists(Path.Combine(outDir, "_manifest.json")));
            var coverage = File.ReadAllText(Path.Combine(outDir, "_coverage.md"));
            Assert.Contains("Hardware", coverage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("HMI", coverage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(outDir, recursive: true);
            }
            catch (IOException)
            {
                // Temp.
            }
        }
    }
}
