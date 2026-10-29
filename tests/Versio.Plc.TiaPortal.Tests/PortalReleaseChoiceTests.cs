using Versio.Core.Logging;
using Versio.Plc.TiaPortal.Infrastructure;
using Xunit;

namespace Versio.Plc.TiaPortal.Tests;

/// <summary>
/// Which Portal opens which project, on a machine with more than one — against folders laid
/// out like the real installs, never against the real installs. Runs on every machine.
/// </summary>
/// <remarks>
/// <b>Found 2026-10-29, on the first export of a V20 project on a machine with Portal V20
/// and V21.</b> The locator knew only V21's modular <c>PublicAPI\V21\net48\</c> layout, so
/// Portal V20 — one <c>Siemens.Engineering.dll</c> under <c>PublicAPI\V20\</c>, as every
/// release before V21 ships it — was skipped at debug level and the machine looked like a
/// V21-only machine. The runner then opened the V20 project with the only install it could
/// see and Portal answered with a compatibility error. Three things had to change, and each
/// has a test here: the locator reads both layouts; the runner picks the install from the
/// project's extension and refuses a release that is not installed instead of falling to the
/// newest; and the toolchain no longer goes unavailable over several releases, because the
/// choice it was refusing to make is made per project now.
/// </remarks>
public sealed class PortalReleaseChoiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "versio-tia-installs-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp tree the next run will not see; nothing here depends on it.
        }
    }

    [Fact]
    public void The_locator_reads_the_modular_layout_and_the_single_assembly_layout()
    {
        ModularInstall("V21");
        SingleAssemblyInstall("V20");

        var installs = new TiaPortalLocator([_root]).FindInstalls();

        Assert.Equal(["V21", "V20"], installs.Select(i => i.Version));
        var v21 = installs[0];
        Assert.Equal(TiaPortalInstall.ModularApi, v21.ApiShape);
        Assert.EndsWith(Path.Combine("PublicAPI", "V21", "net48"), v21.ApiFolder);
        var v20 = installs[1];
        Assert.Equal(TiaPortalInstall.SingleAssemblyApi, v20.ApiShape);
        Assert.EndsWith(Path.Combine("PublicAPI", "V20"), v20.ApiFolder);
    }

    [Fact]
    public void An_older_release_api_shipped_for_compatibility_is_not_mistaken_for_the_install()
    {
        // Portal V20 carries PublicAPI\V17..V20. Only its own folder counts: an install whose
        // own folder is missing is not quietly driven through an older API, because the API
        // release is what decides how the project is opened.
        var folder = Path.Combine(_root, "Portal V20", "PublicAPI", "V19");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Siemens.Engineering.dll"), string.Empty);

        Assert.Empty(new TiaPortalLocator([_root]).FindInstalls());
    }

    [Fact]
    public void A_project_is_opened_by_the_release_its_extension_names()
    {
        ModularInstall("V21");
        SingleAssemblyInstall("V20");
        var installs = new TiaPortalLocator([_root]).FindInstalls();

        var (forV20, _) = LocalTiaPortalRunner.ChooseInstall(installs, "plc/ProjectInitial.ap20", "V21");
        Assert.Equal("V20", forV20?.Version);

        // The pinned profile does not override the file.
        var (forV21, _) = LocalTiaPortalRunner.ChooseInstall(installs, @"plc\Station.ap21", "V20");
        Assert.Equal("V21", forV21?.Version);
    }

    [Fact]
    public void A_release_that_is_not_installed_is_refused_and_never_upgraded()
    {
        ModularInstall("V21");
        var installs = new TiaPortalLocator([_root]).FindInstalls();

        var (install, refusal) = LocalTiaPortalRunner.ChooseInstall(
            installs, "JV_WorkingSpace/ProjectInitial/ProjectInitial.ap20", "V21");

        Assert.Null(install);
        Assert.NotNull(refusal);
        Assert.Contains("ProjectInitial.ap20 is a TIA Portal V20 project", refusal);
        Assert.Contains("Portal V20 is not installed", refusal);
        Assert.Contains("installed: V21", refusal);
        Assert.Contains("upgrade the project yourself in Portal V21", refusal);
        Assert.Contains("never upgrades", refusal);
    }

    [Fact]
    public void A_project_newer_than_every_installed_release_is_not_told_to_upgrade()
    {
        // An older Portal cannot open a newer project, so "upgrade it in Portal V20" would
        // point at a dialog that does not exist. The way out is the release the file names.
        SingleAssemblyInstall("V20");
        var installs = new TiaPortalLocator([_root]).FindInstalls();

        var (install, refusal) = LocalTiaPortalRunner.ChooseInstall(installs, "plc/Station.ap21", "V20");

        Assert.Null(install);
        Assert.Contains("Station.ap21 is a TIA Portal V21 project", refusal);
        Assert.Contains("installed: V20", refusal);
        Assert.Contains("Install Portal V21; an older Portal cannot open a newer project.", refusal);
        Assert.DoesNotContain("upgrade", refusal);
    }

    [Fact]
    public void A_path_with_no_release_in_its_extension_falls_back_to_the_profile()
    {
        ModularInstall("V21");
        SingleAssemblyInstall("V20");
        var installs = new TiaPortalLocator([_root]).FindInstalls();

        var (byProfile, _) = LocalTiaPortalRunner.ChooseInstall(installs, "plc/Line.apx", "V20");
        Assert.Equal("V20", byProfile?.Version);

        var (newest, _) = LocalTiaPortalRunner.ChooseInstall(installs, "plc/Line.apx", "V99");
        Assert.Equal("V21", newest?.Version);
    }

    [Fact]
    public void No_install_at_all_is_the_old_sentence()
    {
        var (install, refusal) = LocalTiaPortalRunner.ChooseInstall([], "plc/Line.ap21", "V21");
        Assert.Null(install);
        Assert.Equal("TIA Portal is not installed on this machine.", refusal);
    }

    [SkippableFact]
    public async Task Two_releases_side_by_side_leave_the_toolchain_available()
    {
        Skip.If(LocalTiaPortalRunner.FindHost() is null,
            "TIA Portal host was not copied to the output directory");
        ModularInstall("V21");
        SingleAssemblyInstall("V20");

        var status = await new TiaPortalToolchain(NullLogger.Instance, [_root])
            .ProbeAsync(CancellationToken.None);

        Assert.True(status.Available, status.Explanation);
        Assert.Equal("V21", status.Profile);
        Assert.Equal("V21, V20", status.Version);
        Assert.Equal(["V21", "V20"], status.Candidates);
        Assert.Contains("extension picks which one opens it", status.Explanation);
    }

    private void ModularInstall(string version)
    {
        var api = Path.Combine(_root, $"Portal {version}", "PublicAPI", version, "net48");
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "Siemens.Engineering.Base.dll"), string.Empty);
        File.WriteAllText(Path.Combine(api, "Siemens.Engineering.Step7.dll"), string.Empty);
    }

    private void SingleAssemblyInstall(string version)
    {
        // The real V20 install also carries V17, V18 and V19 beside its own folder.
        foreach (var older in new[] { "V17", "V18", "V19" })
        {
            var compat = Path.Combine(_root, $"Portal {version}", "PublicAPI", older);
            Directory.CreateDirectory(compat);
            File.WriteAllText(Path.Combine(compat, "Siemens.Engineering.dll"), string.Empty);
        }

        var api = Path.Combine(_root, $"Portal {version}", "PublicAPI", version);
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "Siemens.Engineering.dll"), string.Empty);
        File.WriteAllText(Path.Combine(api, "Siemens.Engineering.Hmi.dll"), string.Empty);
    }
}
