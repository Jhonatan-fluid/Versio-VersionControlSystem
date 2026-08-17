using Versio.Plc.Codesys;
using Xunit;

namespace Versio.Core.Tests.Plc.Codesys;

/// <summary>
/// Profile resolution is a hard precondition, not a convenience: CODESYS exits 1 in
/// --noUI mode when --profile is absent.
/// </summary>
public class ProfileResolverTests
{
    private static CodesysInstall Install(params string[] profiles) => new()
    {
        InstallFolder = "CODESYS 3.5.22.20",
        ExecutablePath = @"C:\Program Files\CODESYS 3.5.22.20\CODESYS\Common\CODESYS.exe",
        Profiles = profiles,
    };

    [Fact]
    public void The_profile_name_cannot_be_derived_from_the_install_folder()
    {
        // Documents the trap. Folder "3.5.22.20" means SP22 *Patch 2*, not Patch 20.
        // Any attempt to synthesise the name produces a profile that does not exist.
        const string installFolder = "CODESYS 3.5.22.20";
        const string actualProfile = "CODESYS V3.5 SP22 Patch 2";

        var naiveGuess = "CODESYS V3.5 SP22 Patch 20";

        Assert.NotEqual(actualProfile, naiveGuess);
        Assert.DoesNotContain(actualProfile, installFolder);
    }

    [Theory]
    [InlineData("CODESYS V3.5 SP22 Patch 2.profile.xml", "CODESYS V3.5 SP22 Patch 2")]
    [InlineData("CODESYS V3.5 SP19.profile", "CODESYS V3.5 SP19")]
    [InlineData(@"C:\x\y\CODESYS V3.5 SP20 Patch 4.profile.xml", "CODESYS V3.5 SP20 Patch 4")]
    public void Profile_name_is_read_from_the_file_name(string file, string expected)
    {
        Assert.Equal(expected, ProfileResolver.ProfileNameFromFile(file));
    }

    [Fact]
    public void A_non_profile_file_yields_null()
    {
        Assert.Null(ProfileResolver.ProfileNameFromFile("GLOBAL.dependencies"));
    }

    [Fact]
    public void Profiles_rank_newest_first()
    {
        var ranked = ProfileResolver.RankNewestFirst(
        [
            "CODESYS V3.5 SP19",
            "CODESYS V3.5 SP22 Patch 2",
            "CODESYS V3.5 SP20 Patch 4",
            "CODESYS V3.5 SP22",
        ]);

        Assert.Equal("CODESYS V3.5 SP22 Patch 2", ranked[0]);
        Assert.Equal("CODESYS V3.5 SP22", ranked[1]);
        Assert.Equal("CODESYS V3.5 SP20 Patch 4", ranked[2]);
        Assert.Equal("CODESYS V3.5 SP19", ranked[3]);
    }

    [Fact]
    public void Patch_2_sorts_below_Patch_20_when_both_exist()
    {
        // The string-sort trap in the other direction.
        var ranked = ProfileResolver.RankNewestFirst(
            ["CODESYS V3.5 SP22 Patch 2", "CODESYS V3.5 SP22 Patch 20"]);

        Assert.Equal("CODESYS V3.5 SP22 Patch 20", ranked[0]);
    }

    [Fact]
    public void A_single_profile_resolves_automatically()
    {
        var r = ProfileResolver.Resolve([Install("CODESYS V3.5 SP22 Patch 2")]);

        Assert.Equal(ProfileResolution.Resolved, r.Outcome);
        Assert.Equal("CODESYS V3.5 SP22 Patch 2", r.Profile);
    }

    [Fact]
    public void Several_profiles_ask_the_user_rather_than_guessing()
    {
        // Guessing wrong can trigger a silent project upgrade to a newer service pack.
        var r = ProfileResolver.Resolve([Install("CODESYS V3.5 SP22 Patch 2", "CODESYS V3.5 SP19")]);

        Assert.Equal(ProfileResolution.AmbiguousNeedsUserChoice, r.Outcome);
        Assert.Null(r.Profile);
        Assert.Equal(2, r.Candidates.Count);
        Assert.Equal("CODESYS V3.5 SP22 Patch 2", r.Candidates[0]);
    }

    [Fact]
    public void A_pinned_profile_wins_over_the_newest()
    {
        var r = ProfileResolver.Resolve(
            [Install("CODESYS V3.5 SP22 Patch 2", "CODESYS V3.5 SP19")],
            pinnedProfile: "CODESYS V3.5 SP19");

        Assert.Equal(ProfileResolution.Resolved, r.Outcome);
        Assert.Equal("CODESYS V3.5 SP19", r.Profile);
    }

    [Fact]
    public void A_pinned_profile_that_is_not_installed_fails_loudly()
    {
        // Silently falling back to another service pack is how a project gets upgraded
        // without anyone asking.
        var r = ProfileResolver.Resolve([Install("CODESYS V3.5 SP22 Patch 2")],
            pinnedProfile: "CODESYS V3.5 SP19");

        Assert.Equal(ProfileResolution.NoProfileFound, r.Outcome);
        Assert.Contains("SP19", r.Explanation);
        Assert.Contains("not installed", r.Explanation);
        Assert.Contains("CODESYS V3.5 SP22 Patch 2", r.Explanation);
    }

    [Fact]
    public void No_install_is_reported_distinctly_from_no_profile()
    {
        Assert.Equal(ProfileResolution.NoInstallFound,
            ProfileResolver.Resolve([]).Outcome);

        Assert.Equal(ProfileResolution.NoProfileFound,
            ProfileResolver.Resolve([Install()]).Outcome);
    }

    [Fact]
    public void Duplicate_profiles_across_installs_are_collapsed()
    {
        var r = ProfileResolver.Resolve(
        [
            Install("CODESYS V3.5 SP22 Patch 2"),
            Install("CODESYS V3.5 SP22 Patch 2"),
        ]);

        Assert.Equal(ProfileResolution.Resolved, r.Outcome);
        Assert.Single(r.Candidates);
    }

    [Fact]
    public void The_missing_profile_refusal_is_recognised()
    {
        // Turns a bare exit code 1 into an actionable message.
        const string real =
            "When running in --noUI mode, you must specify a profile using --profile=\"profile name\".";

        Assert.True(ProfileResolver.IsMissingProfileError(real));
        Assert.False(ProfileResolver.IsMissingProfileError("some other failure"));
        Assert.False(ProfileResolver.IsMissingProfileError(null));
    }
}
