using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.Platform;
using Versio.Plc.TiaPortal.Infrastructure;
using Versio.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Plc.TiaPortal.Tests;

/// <summary>
/// Portal's Openness access list, read before the host is launched, and the notice it puts
/// on the card. The list's shape against the real registry; the notice against a fake list.
/// </summary>
/// <remarks>
/// <b>2026-10-29.</b> The owner asked whether the <i>Openness access</i> dialog would come
/// back every time. Once per Portal release per host build, it turned out — three entries for
/// one exe name on their machine, three hashes, three updates — and the alternative of writing
/// the entry ourselves was rejected as the auto-updater trusting its own next build
/// (<c>OpennessAccessList</c> says why). So the list is read, never written, and the card says
/// a dialog is coming instead of a nine-minute silence saying nothing.
/// </remarks>
public sealed class OpennessAccessListTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "versio-tia-access-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp.
        }
    }

    private static TiaPortalInstall Install(string version) =>
        new($@"C:\P\Portal {version}", version, $@"C:\P\Portal {version}\PublicAPI\{version}", null, TiaPortalInstall.ModularApi);

    [Fact]
    public void The_list_keys_follow_the_rename_between_V20_and_V21()
    {
        // Observed: V20 under its own key with the old name, V21 at the root with the new one.
        // Both spellings under the release's key for every release, the root for V21 on.
        Assert.Equal([@"20.0\Whitelist", @"20.0\AllowList"], OpennessAccessList.ListKeysFor(20));
        Assert.Equal(["AllowList", @"21.0\Whitelist", @"21.0\AllowList"], OpennessAccessList.ListKeysFor(21));
        Assert.Equal(["AllowList", @"22.0\Whitelist", @"22.0\AllowList"], OpennessAccessList.ListKeysFor(22));
    }

    [Fact]
    public void The_hash_and_the_stamp_are_what_Portal_writes()
    {
        // FileHash is base64 of the file's SHA-256 (matched byte for byte against the installed
        // host's entry); DateModified is the last write in UTC to the millisecond, slashes
        // between the date parts. Both verified against the owner's registry on 2026-10-29.
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "Host.exe");
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(file, bytes);

        Assert.Equal(Convert.ToBase64String(SHA256.HashData(bytes)), OpennessAccessList.HashOf(file));
        Assert.Matches(new Regex(@"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\.\d{3}$"), OpennessAccessList.StampOf(file));
        Assert.StartsWith(File.GetLastWriteTimeUtc(file).ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), OpennessAccessList.StampOf(file));
    }

    [Fact]
    public void A_match_is_the_path_case_insensitively_and_the_hash_exactly()
    {
        const string host = @"C:\Users\Me\AppData\Local\Versio\current\tia\Versio.Plc.TiaPortal.Host.exe";
        Assert.True(OpennessAccessList.Matches(host, "abc=", host, "abc="));
        Assert.True(OpennessAccessList.Matches(host.ToUpperInvariant(), "abc=", host, "abc="));
        Assert.False(OpennessAccessList.Matches(host, "ABC=", host, "abc="), "a hash is bytes, not text");
        Assert.False(OpennessAccessList.Matches(@"C:\elsewhere\Versio.Plc.TiaPortal.Host.exe", "abc=", host, "abc="));
        Assert.False(OpennessAccessList.Matches(null, null, host, "abc="));
    }

    [SkippableFact]
    public void A_host_nobody_has_granted_is_not_listed_and_the_detail_names_the_exe()
    {
        // Against the real registry, whatever this machine has: a freshly written file has
        // never been granted by any Portal, so the answer is NotListed and not Unknown — the
        // list could be read, there was just nothing for this file in it.
        Skip.IfNot(OperatingSystem.IsWindows(), "the list is the Windows registry");
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "Versio.Plc.TiaPortal.Host.exe");
        File.WriteAllBytes(file, Guid.NewGuid().ToByteArray());

        var check = new OpennessAccessList().Check(Install("V21"), file);

        Assert.Equal(OpennessAccessStanding.NotListed, check.Standing);
        Assert.Contains("Versio.Plc.TiaPortal.Host.exe", check.Detail);
    }

    [Fact]
    public void A_host_that_cannot_be_hashed_is_unknown_not_a_dialog()
    {
        var check = new OpennessAccessList().Check(Install("V21"), Path.Combine(_root, "missing.exe"));

        Assert.Equal(OpennessAccessStanding.Unknown, check.Standing);
        Assert.Contains("could not hash", check.Detail);
    }

    [Fact]
    public void The_timeout_hint_is_added_only_for_a_security_timeout_on_an_unlisted_host()
    {
        const string timeout = "Security error. The operation has timed out.";
        var hinted = LocalTiaPortalRunner.WithAccessHint(timeout, dialogExpected: true);
        Assert.StartsWith(timeout, hinted);
        Assert.Contains("Openness access", hinted);
        Assert.Contains("Yes to all", hinted);

        // Our own job timeout, when it is shorter than Portal's, ends the same wait.
        Assert.Contains("Yes to all",
            LocalTiaPortalRunner.WithAccessHint("the TIA Portal host did not finish within the timeout", dialogExpected: true));

        // A listed host's security timeout is some other security timeout.
        Assert.Equal(timeout, LocalTiaPortalRunner.WithAccessHint(timeout, dialogExpected: false));
        // And an unlisted host's other failure is not about the dialog.
        Assert.Equal("host exited 1", LocalTiaPortalRunner.WithAccessHint("host exited 1", dialogExpected: true));
    }

    // ----- the notice, against a fake list -----

    private sealed class FakeList(Func<string, OpennessAccessStanding> standingOf) : IOpennessAccessList
    {
        public List<(string Release, string Host)> Asked { get; } = [];

        public OpennessAccessCheck Check(TiaPortalInstall install, string hostPath)
        {
            Asked.Add((install.Version, hostPath));
            return new(standingOf(install.Version), $"fake for {install.Version}");
        }
    }

    private sealed class AdmittedGroups : ILocalGroups
    {
        public LocalGroupCheck Check(string groupName) =>
            new(LocalGroupStanding.InToken, "tester", "S-1-5-21-0", "fake");

        public Task<PlcRemedyOutcome> JoinElevatedAsync(string groupName, LocalGroupCheck account, CancellationToken ct) =>
            Task.FromResult(PlcRemedyOutcome.Refused("not offered"));
    }

    private void FakeInstall(string version)
    {
        var api = Path.Combine(_root, $"Portal {version}", "PublicAPI", version, "net48");
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "Siemens.Engineering.Base.dll"), string.Empty);
    }

    private TiaPortalToolchain Toolchain(FakeList list, TestLogger? log = null) =>
        new((IVersioLogger?)log ?? NullLogger.Instance, [_root], new AdmittedGroups(), list);

    [SkippableFact]
    public async Task The_release_that_will_ask_is_named_on_an_available_status_and_in_the_log()
    {
        // The owner's afternoon: V21 had the host listed, V20 had not been asked yet.
        Skip.If(LocalTiaPortalRunner.FindHost() is null, "TIA Portal host was not copied to the output directory");
        FakeInstall("V21");
        FakeInstall("V20");
        var list = new FakeList(v => v == "V20" ? OpennessAccessStanding.NotListed : OpennessAccessStanding.Listed);
        var log = new TestLogger(output);

        var status = await Toolchain(list, log).ProbeAsync(CancellationToken.None);

        Assert.True(status.Available, status.Explanation);
        var notice = Assert.IsType<string>(status.Notice);
        Assert.StartsWith("Portal V20 will ask for Openness access", notice);
        Assert.DoesNotContain("V21", notice);
        Assert.Contains("'Openness access'", notice);
        Assert.Contains("Yes to all", notice);
        Assert.Contains("Security error. The operation has timed out.", notice);
        Assert.Equal(2, list.Asked.Count);
        Assert.All(list.Asked, a => Assert.Equal(LocalTiaPortalRunner.FindHost(), a.Host));
        Assert.Contains(log.Entries, e =>
            e.Message.Contains("toolchain ready", StringComparison.Ordinal)
            && e.Message.Contains("V21 listed", StringComparison.Ordinal)
            && e.Message.Contains("V20 not listed", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Two_releases_asking_are_both_named_and_a_listed_host_says_nothing()
    {
        Skip.If(LocalTiaPortalRunner.FindHost() is null, "TIA Portal host was not copied to the output directory");
        FakeInstall("V21");
        FakeInstall("V20");

        var both = await Toolchain(new FakeList(_ => OpennessAccessStanding.NotListed)).ProbeAsync(CancellationToken.None);
        Assert.StartsWith("Portal V21 and V20 will ask", both.Notice);

        var none = await Toolchain(new FakeList(_ => OpennessAccessStanding.Listed)).ProbeAsync(CancellationToken.None);
        Assert.True(none.Available);
        Assert.Null(none.Notice);
    }

    [SkippableFact]
    public async Task A_list_that_could_not_be_read_puts_nothing_on_the_card()
    {
        // Portal will ask or it will not; the log carries the reason and the card carries
        // nothing it cannot stand behind.
        Skip.If(LocalTiaPortalRunner.FindHost() is null, "TIA Portal host was not copied to the output directory");
        FakeInstall("V21");
        var log = new TestLogger(output);

        var status = await Toolchain(new FakeList(_ => OpennessAccessStanding.Unknown), log).ProbeAsync(CancellationToken.None);

        Assert.True(status.Available);
        Assert.Null(status.Notice);
        Assert.Contains(log.Entries, e => e.Message.Contains("V21 unknown (fake for V21)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_runner_says_a_dialog_is_coming_before_it_launches_the_host()
    {
        // The probe may be minutes old and an update in between is what drops the host off
        // the list, so the runner asks again at launch. The host path does not exist: the
        // warning is in the log before the launch fails on it, which proves the order.
        FakeInstall("V21");
        var log = new TestLogger(output);
        var list = new FakeList(_ => OpennessAccessStanding.NotListed);
        var host = Path.Combine(_root, "no-such-host.exe");
        var runner = new LocalTiaPortalRunner(host, "V21", log, [_root], list);

        await Assert.ThrowsAnyAsync<Exception>(() => runner.RunAsync(
            new PlcJob
            {
                Operation = PlcOperation.Export,
                Profile = "V21",
                Projects =
                [
                    new PlcProjectJob
                    {
                        BindingId = "b",
                        ProjectPath = Path.Combine(_root, "plc", "Machine.ap21"),
                        Path = Path.Combine(_root, "out"),
                    },
                ],
            },
            progress: null,
            CancellationToken.None));

        Assert.Equal([("V21", host)], list.Asked);
        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("Portal V21 will ask for Openness access", StringComparison.Ordinal)
            && e.Message.Contains("Yes to all", StringComparison.Ordinal));
    }
}
