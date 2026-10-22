using Versio.Plc.Codesys;
using Versio.Plc.Codesys.Infrastructure;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// The runner refuses a pinned profile that its install does not have, before it launches
/// anything.
/// </summary>
/// <remarks>
/// The install here is fake: a <c>CODESYS\Common\CODESYS.exe</c> that is an empty file, with a
/// <c>Profiles</c> folder beside it. If the preflight were missing, the runner would try to
/// start that "executable" and fail with an exception rather than a refusal. So a missing
/// preflight turns this test red without needing a real CODESYS.
/// </remarks>
public sealed class RunnerProfilePreflightTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "Versio", "preflight-" + Guid.NewGuid().ToString("N"));

    private string Executable => Path.Combine(_root, "CODESYS", "Common", "CODESYS.exe");

    public RunnerProfilePreflightTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Executable)!);
        File.WriteAllBytes(Executable, []);

        var profiles = Directory.CreateDirectory(Path.Combine(_root, "CODESYS", "Profiles"));
        File.WriteAllText(
            Path.Combine(profiles.FullName, "CODESYS V3.5 SP22 Patch 4.profile.xml"), "<x/>");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp clean-up only; a leftover folder fails nothing.
        }
    }

    [Fact]
    public void The_profiles_beside_an_executable_are_the_install_profiles() =>
        Assert.Equal(["CODESYS V3.5 SP22 Patch 4"], CodesysLocator.ProfilesBeside(Executable));

    [Fact]
    public async Task A_pin_this_install_lacks_is_refused_without_launching_CODESYS()
    {
        var runner = new LocalCodesysRunner(Executable, new ScriptExtractor());

        var result = await runner.RunAsync(new CodesysJob
        {
            Operation = CodesysOperation.Export,
            Profile = "CODESYS V3.5 SP22 Patch 2",
            Projects = [new ProjectJob { BindingId = "x", ProjectPath = "p.project", Path = "p" }],
            Timeout = TimeSpan.FromMinutes(1),
        }, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TimeSpan.Zero, result.Duration);
        Assert.NotNull(result.ProfileNotInstalled);
        Assert.Contains("'CODESYS V3.5 SP22 Patch 2', which is not installed", result.FailureReason);
        Assert.Contains("CODESYS V3.5 SP22 Patch 4", result.FailureReason);
    }
}
