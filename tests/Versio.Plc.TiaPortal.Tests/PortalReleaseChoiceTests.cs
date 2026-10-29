using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Plc.TiaPortal.Infrastructure;
using Versio.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Plc.TiaPortal.Tests;

/// <summary>
/// Which Portal opens which project, on a machine with more than one — against folders laid
/// out like the real installs, never against the real installs. Runs on every machine.
/// </summary>
/// <remarks>
/// <para>
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
/// </para>
/// <para>
/// The same day's review found the headline fix proven only as a pure function — swapping
/// the project path out of the <c>RunAsync</c> call left every test green — and four other
/// branches nothing watched. Each has a test below now; the wiring one drives the real
/// <c>RunAsync</c> with a host path that does not exist, which is safe because the refusal
/// happens before any process is started.
/// </para>
/// </remarks>
public sealed class PortalReleaseChoiceTests(ITestOutputHelper output) : IDisposable
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
        // release is what decides how the project is opened. Both layouts: the modular lookup
        // used to fall back to any *\net48 under PublicAPI, which would have reported a
        // "Portal V22" carrying only V21\net48 as V22 and driven it through V21 (review,
        // 2026-10-29).
        var single = Path.Combine(_root, "Portal V20", "PublicAPI", "V19");
        Directory.CreateDirectory(single);
        File.WriteAllText(Path.Combine(single, "Siemens.Engineering.dll"), string.Empty);

        var modular = Path.Combine(_root, "Portal V22", "PublicAPI", "V21", "net48");
        Directory.CreateDirectory(modular);
        File.WriteAllText(Path.Combine(modular, "Siemens.Engineering.Base.dll"), string.Empty);

        // And a Portal folder with no PublicAPI at all must not throw.
        Directory.CreateDirectory(Path.Combine(_root, "Portal V23"));

        Assert.Empty(new TiaPortalLocator([_root]).FindInstalls());
    }

    [Fact]
    public void A_skipped_install_is_said_at_info_level_with_the_folder_and_why()
    {
        // Raised from Debug on 2026-10-29 because at Debug the one line that explained a
        // missing release was invisible while the toolchain reported itself ready. Nothing
        // pinned the level or the line until review said so.
        Directory.CreateDirectory(Path.Combine(_root, "Portal V20", "PublicAPI", "V20"));
        var log = new TestLogger(output);

        new TiaPortalLocator([_root], log).FindInstalls();

        var skipped = Assert.Single(log.Entries, e => e.Message.StartsWith("skipped ", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, skipped.Level);
        Assert.Contains(Path.Combine(_root, "Portal V20"), skipped.Message);
        Assert.Contains("no Openness API under PublicAPI\\V20", skipped.Message);
    }

    [Fact]
    public void The_release_is_the_V_number_in_the_folder_name_whatever_follows_it()
    {
        // "Portal V21 Update 2", should Siemens ever name one so, is still V21: the version
        // used to be everything after "Portal ", which no project extension could ever
        // match, and the single-assembly lookup then looked for PublicAPI\V21 Update 2 and
        // found nothing (review, 2026-10-29). Lower case is fine too — the release match is
        // case-insensitive end to end, and the fakes were all upper case until now.
        ModularInstall("V21", folder: "Portal V21 Update 2");
        SingleAssemblyInstall("V20", folder: "Portal v20");

        var installs = new TiaPortalLocator([_root]).FindInstalls();

        Assert.Equal(["V21", "V20"], installs.Select(i => i.Version));
        Assert.EndsWith("Portal V21 Update 2", installs[0].InstallFolder);
        var (forV21, _) = LocalTiaPortalRunner.ChooseInstall(installs, "plc/Station.AP21", "");
        Assert.Equal("V21", forV21?.Version);
        var (forV20, _) = LocalTiaPortalRunner.ChooseInstall(installs, "plc/Line.ap20", "");
        Assert.Equal("V20", forV20?.Version);
    }

    [Fact]
    public void Installs_are_ordered_by_release_number_and_a_release_is_reported_once()
    {
        // Program Files and Program Files (x86) are both searched; a release present under
        // both is one install, from the first root. And V9 sorts under V21, which a string
        // order got wrong (harmless for V13–V99, still wrong).
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        ModularInstall("V21", root: first);
        SingleAssemblyInstall("V20", root: second);
        SingleAssemblyInstall("V21", root: second);
        SingleAssemblyInstall("V9", root: second);
        var log = new TestLogger(output);

        var installs = new TiaPortalLocator([first, second], log).FindInstalls();

        Assert.Equal(["V21", "V20", "V9"], installs.Select(i => i.Version));
        Assert.Equal(TiaPortalInstall.ModularApi, installs[0].ApiShape);
        Assert.StartsWith(first, installs[0].InstallFolder);
        Assert.Contains(log.Entries, e => e.Message.Contains("Portal V21 was already found at", StringComparison.Ordinal));
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
    public void The_upgrade_named_is_the_nearest_newer_portal_not_the_newest()
    {
        // An upgrade cannot be taken back, so the smallest step is the one to name: a V19
        // project on a V20 + V21 machine is pointed at V20.
        ModularInstall("V21");
        SingleAssemblyInstall("V20");
        var installs = new TiaPortalLocator([_root]).FindInstalls();

        var (_, refusal) = LocalTiaPortalRunner.ChooseInstall(installs, "plc/Old.ap19", "V21");

        Assert.Contains("upgrade the project yourself in Portal V20", refusal);
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

    [Fact]
    public async Task RunAsync_refuses_from_the_project_path_before_any_host_is_started()
    {
        // The wiring, not the function: with a V21-only machine a .ap20 job must come back
        // refused from RunAsync itself. Swapping the project path out of the ChooseInstall
        // call left every other test here green (review, 2026-10-29). The host path does
        // not exist, which proves the refusal came before a process was spawned — a run
        // that got that far would fail on the missing executable instead.
        ModularInstall("V21");
        var log = new TestLogger(output);
        var runner = new LocalTiaPortalRunner(
            Path.Combine(_root, "no-such-host.exe"), "V21", log, [_root]);

        var result = await runner.RunAsync(
            new PlcJob
            {
                Operation = PlcOperation.Export,
                Profile = "V21",
                Projects =
                [
                    new PlcProjectJob
                    {
                        BindingId = "b",
                        ProjectPath = Path.Combine(_root, "plc", "ProjectInitial.ap20"),
                        Path = Path.Combine(_root, "out"),
                    },
                ],
            },
            progress: null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Portal V20 is not installed", result.FailureReason);
        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("refused: ProjectInitial.ap20 is a TIA Portal V20 project", StringComparison.Ordinal));
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

    [SkippableFact]
    public async Task One_release_has_no_candidates_and_the_plain_explanation()
    {
        // The single-install shape the Settings rows were written for: no list, one folder.
        Skip.If(LocalTiaPortalRunner.FindHost() is null,
            "TIA Portal host was not copied to the output directory");
        SingleAssemblyInstall("V20");

        var status = await new TiaPortalToolchain(NullLogger.Instance, [_root])
            .ProbeAsync(CancellationToken.None);

        Assert.True(status.Available, status.Explanation);
        Assert.Equal("V20", status.Profile);
        Assert.Equal("V20", status.Version);
        Assert.Empty(status.Candidates);
        Assert.EndsWith("Portal V20 — V20", status.Explanation);
    }

    private void ModularInstall(string version, string? folder = null, string? root = null)
    {
        var api = Path.Combine(root ?? _root, folder ?? $"Portal {version}", "PublicAPI", version, "net48");
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "Siemens.Engineering.Base.dll"), string.Empty);
        File.WriteAllText(Path.Combine(api, "Siemens.Engineering.Step7.dll"), string.Empty);
    }

    private void SingleAssemblyInstall(string version, string? folder = null, string? root = null)
    {
        var install = Path.Combine(root ?? _root, folder ?? $"Portal {version}");

        // The real V20 install also carries V17, V18 and V19 beside its own folder.
        foreach (var older in new[] { "V17", "V18", "V19" })
        {
            var compat = Path.Combine(install, "PublicAPI", older);
            Directory.CreateDirectory(compat);
            File.WriteAllText(Path.Combine(compat, "Siemens.Engineering.dll"), string.Empty);
        }

        var api = Path.Combine(install, "PublicAPI", version);
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "Siemens.Engineering.dll"), string.Empty);
        File.WriteAllText(Path.Combine(api, "Siemens.Engineering.Hmi.dll"), string.Empty);
    }
}
