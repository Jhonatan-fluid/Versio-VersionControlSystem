using System.Runtime.Versioning;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// The real Windows group reader, against groups every Windows machine has.
/// </summary>
/// <remarks>
/// The join itself is not driven here: it needs a UAC prompt and would change the machine.
/// What can be proven without either is the reading — the token, the missing group, and the
/// helper's refusal of a malformed command line — which is what the TIA probe decides on.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LocalGroupsTests
{
    [SkippableFact]
    public void Every_interactive_account_has_Users_in_its_token()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows local groups");

        var check = new LocalGroups().Check("Users");

        Assert.Equal(LocalGroupStanding.InToken, check.Standing);
        Assert.False(string.IsNullOrEmpty(check.Account));
        // Local and domain accounts are S-1-5-…; Azure AD accounts are S-1-12-….
        Assert.StartsWith("S-1-", check.Sid);
        Assert.Contains("logon token", check.Detail);
    }

    [SkippableFact]
    public void A_group_that_does_not_exist_is_GroupMissing_not_an_exception()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows local groups");

        var check = new LocalGroups().Check("Versio No Such Group 7f3a");

        Assert.Equal(LocalGroupStanding.GroupMissing, check.Standing);
        Assert.Contains("no local group named", check.Detail);
    }

    [SkippableFact]
    public void Guests_is_a_group_this_account_is_not_in()
    {
        // The standing the TIA probe offers its button for, read from a real group with
        // real members (or none): the member list came back and the account is not on it.
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows local groups");

        var check = new LocalGroups().Check("Guests");

        Assert.Equal(LocalGroupStanding.NotMember, check.Standing);
        Assert.Contains("is not a member of 'Guests'", check.Detail);
    }

    [SkippableFact]
    public void Administrators_is_read_from_the_member_list_when_the_token_filters_it()
    {
        // An administrator running without elevation has Administrators in the token as
        // deny-only, so IsInRole says no; the member list says yes, and the two together are
        // "member, not in the token" — the same two reads the Openness group needs after an
        // add and before a sign-in. A non-administrator is simply not a member. Either way the
        // list was read, which is what this proves; an elevated test host is the one case
        // where the token already answers, and that is InToken.
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows local groups");

        var check = new LocalGroups().Check("Administrators");

        Assert.True(
            check.Standing is LocalGroupStanding.MemberNotInToken
                or LocalGroupStanding.NotMember
                or LocalGroupStanding.InToken,
            check.Detail);
        Assert.NotEqual(LocalGroupStanding.Unknown, check.Standing);
    }

    [SkippableFact]
    public void The_elevated_helper_refuses_a_malformed_command_line_and_an_unshaped_SID()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows local groups");

        Assert.Equal(2, LocalGroups.RunJoin([]));
        Assert.Equal(2, LocalGroups.RunJoin(["--something-else", "g", "sid", "file"]));
        Assert.Equal(2, LocalGroups.RunJoin([LocalGroups.JoinArgument, "g"]));

        // What goes into the PowerShell command line has one legitimate shape each. Refused
        // before PowerShell is started, with the reason in the result file.
        var result = Path.Combine(Path.GetTempPath(), $"versio-join-test-{Guid.NewGuid():N}.txt");
        try
        {
            Assert.Equal(2, LocalGroups.RunJoin([LocalGroups.JoinArgument, "Users", "'; Remove-Item x; '", result]));
            Assert.Contains("is not a SID", File.ReadAllText(result));
            Assert.Equal(2, LocalGroups.RunJoin([LocalGroups.JoinArgument, "Users' -Member 'S-1-1-0", "S-1-5-21-1-2-3-1001", result]));
            Assert.Contains("not a plain group name", File.ReadAllText(result));
        }
        finally
        {
            File.Delete(result);
        }
    }
}
