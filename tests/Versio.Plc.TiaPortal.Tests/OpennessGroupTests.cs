using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.Platform;
using Versio.Plc.TiaPortal.Infrastructure;
using Versio.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Plc.TiaPortal.Tests;

/// <summary>
/// The Openness group, read before Portal is launched, and the fix the probe offers for it.
/// Against a fake group reader and a fake install tree; runs on every machine.
/// </summary>
/// <remarks>
/// <b>2026-10-29.</b> Portal refused the owner's first export with <i>"Owner … is not member
/// of the windows group 'Siemens TIA Openness'"</i>, three seconds into a Portal launch, as a
/// failure card. The probe asks the token first now, and there are three answers that are
/// not "go ahead" — no group, not a member, member but signed in before the add — each with
/// its own sentence, and only the middle one with a button, because it is the only one a
/// program can act on. A check that fails does not block: Portal is asked as before.
/// </remarks>
public sealed class OpennessGroupTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "versio-tia-group-" + Guid.NewGuid().ToString("N")[..8]);

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

    private sealed class FakeGroups(LocalGroupStanding standing) : ILocalGroups
    {
        public LocalGroupStanding Standing { get; set; } = standing;

        public List<(string Group, string Sid)> Joins { get; } = [];

        public PlcRemedyOutcome JoinAnswer { get; set; } =
            PlcRemedyOutcome.Done("added S-1-5-21-1 to the local group 'Siemens TIA Openness'.");

        public LocalGroupCheck Check(string groupName) =>
            new(Standing, @"AzureAD\Engineer", "S-1-5-21-1", $"fake: {Standing}");

        public Task<PlcRemedyOutcome> JoinElevatedAsync(string groupName, LocalGroupCheck account, CancellationToken ct)
        {
            Joins.Add((groupName, account.Sid));
            return Task.FromResult(JoinAnswer);
        }
    }

    private TiaPortalToolchain Toolchain(FakeGroups groups, TestLogger? log = null)
    {
        var api = Path.Combine(_root, "Portal V21", "PublicAPI", "V21", "net48");
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "Siemens.Engineering.Base.dll"), string.Empty);
        return new TiaPortalToolchain((IVersioLogger?)log ?? NullLogger.Instance, [_root], groups);
    }

    [SkippableFact]
    public async Task An_account_outside_the_group_is_refused_before_Portal_with_the_fix_on_offer()
    {
        Skip.If(LocalTiaPortalRunner.FindHost() is null, "TIA Portal host was not copied to the output directory");
        var groups = new FakeGroups(LocalGroupStanding.NotMember);

        var status = await Toolchain(groups).ProbeAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Contains(@"AzureAD\Engineer is not in the Windows group 'Siemens TIA Openness'", status.Explanation);
        Assert.Contains("Portal refuses Openness to any account outside it", status.Explanation);
        var remedy = Assert.IsType<PlcToolchainRemedy>(status.Remedy);
        Assert.Equal("Add this account to the Siemens TIA Openness group…", remedy.Action);
        Assert.Contains(@"Adds AzureAD\Engineer to the local Windows group 'Siemens TIA Openness'", remedy.Explanation);
        Assert.Contains("administrator approval", remedy.Explanation);
        Assert.Equal(TiaPortalToolchain.SignOutAndIn, remedy.Afterwards);
    }

    [SkippableFact]
    public async Task A_member_whose_sign_in_predates_the_add_is_told_to_sign_out_and_offered_nothing()
    {
        Skip.If(LocalTiaPortalRunner.FindHost() is null, "TIA Portal host was not copied to the output directory");
        var groups = new FakeGroups(LocalGroupStanding.MemberNotInToken);

        var status = await Toolchain(groups).ProbeAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Contains("is in the Windows group 'Siemens TIA Openness', but this Windows session started before it was added", status.Explanation);
        Assert.Contains("Sign out of Windows and back in", status.Explanation);
        Assert.Null(status.Remedy);
    }

    [SkippableFact]
    public async Task No_group_means_no_Openness_feature_and_names_the_Portal_setup()
    {
        Skip.If(LocalTiaPortalRunner.FindHost() is null, "TIA Portal host was not copied to the output directory");
        var groups = new FakeGroups(LocalGroupStanding.GroupMissing);

        var status = await Toolchain(groups).ProbeAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Contains("the Windows group 'Siemens TIA Openness' does not exist", status.Explanation);
        Assert.Contains("Run the Portal setup and add Openness", status.Explanation);
        Assert.Null(status.Remedy);
    }

    [SkippableFact]
    public async Task In_the_token_is_ready_and_an_unreadable_check_does_not_block()
    {
        Skip.If(LocalTiaPortalRunner.FindHost() is null, "TIA Portal host was not copied to the output directory");
        var inToken = await Toolchain(new FakeGroups(LocalGroupStanding.InToken)).ProbeAsync(CancellationToken.None);
        Assert.True(inToken.Available, inToken.Explanation);

        // The check failed: Portal is asked as before, and the log says the check could not be made.
        var log = new TestLogger(output);
        var unknown = await Toolchain(new FakeGroups(LocalGroupStanding.Unknown), log).ProbeAsync(CancellationToken.None);
        Assert.True(unknown.Available, unknown.Explanation);
        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("could not tell whether", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task The_fix_joins_by_SID_and_ends_with_what_to_do_next()
    {
        Skip.If(LocalTiaPortalRunner.FindHost() is null, "TIA Portal host was not copied to the output directory");
        var groups = new FakeGroups(LocalGroupStanding.NotMember);
        var toolchain = Toolchain(groups);
        var status = await toolchain.ProbeAsync(CancellationToken.None);

        var outcome = await toolchain.ApplyRemedyAsync(status, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal([("Siemens TIA Openness", "S-1-5-21-1")], groups.Joins);
        Assert.StartsWith("Added S-1-5-21-1 to the local group 'Siemens TIA Openness'.", outcome.Message);
        Assert.EndsWith(TiaPortalToolchain.SignOutAndIn, outcome.Message);
    }

    [SkippableFact]
    public async Task A_declined_prompt_is_a_refusal_in_the_helpers_words()
    {
        Skip.If(LocalTiaPortalRunner.FindHost() is null, "TIA Portal host was not copied to the output directory");
        var groups = new FakeGroups(LocalGroupStanding.NotMember)
        {
            JoinAnswer = PlcRemedyOutcome.Refused("the administrator prompt was declined, so nothing was changed."),
        };
        var toolchain = Toolchain(groups);
        var status = await toolchain.ProbeAsync(CancellationToken.None);

        var outcome = await toolchain.ApplyRemedyAsync(status, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal("the administrator prompt was declined, so nothing was changed.", outcome.Message);
    }

    [SkippableFact]
    public async Task The_fix_re_checks_first_and_does_not_add_a_member_twice()
    {
        // The status it was handed is minutes old; somebody may have added the account
        // meanwhile. Pressing the button then says so and starts no helper.
        Skip.If(LocalTiaPortalRunner.FindHost() is null, "TIA Portal host was not copied to the output directory");
        var groups = new FakeGroups(LocalGroupStanding.NotMember);
        var toolchain = Toolchain(groups);
        var status = await toolchain.ProbeAsync(CancellationToken.None);
        groups.Standing = LocalGroupStanding.MemberNotInToken;

        var outcome = await toolchain.ApplyRemedyAsync(status, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Contains("already a member", outcome.Message);
        Assert.Contains("Sign out of Windows", outcome.Message);
        Assert.Empty(groups.Joins);

        // And a status with nothing on offer is refused without a check at all.
        var none = await toolchain.ApplyRemedyAsync(new PlcToolchainStatus { Available = true }, CancellationToken.None);
        Assert.False(none.Succeeded);
    }
}
