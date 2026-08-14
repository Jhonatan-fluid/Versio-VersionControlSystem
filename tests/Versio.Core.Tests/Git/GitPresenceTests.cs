using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// The three states of "is git there", and the sentence each produces.
/// </summary>
/// <remarks>
/// The wording is asserted, not just the enum. The missing-git sentence is the only thing a
/// user on a git-less machine will ever see from this application, so it is behaviour rather
/// than decoration — and the specific mistake it exists to avoid, telling somebody to install
/// what they already have, is invisible unless something pins the word PATH.
/// </remarks>
public sealed class GitPresenceTests
{
    [Fact]
    public void Unknown_is_not_usable_and_says_it_is_still_looking()
    {
        Assert.False(GitPresence.Unknown.IsUsable);
        Assert.Equal(GitAvailability.Unknown, GitPresence.Unknown.Availability);
        Assert.Contains("Checking", GitPresence.Unknown.Explanation);
    }

    [Fact]
    public void Unknown_does_not_claim_git_is_missing()
    {
        // The distinction the type exists for: between the window opening and the probe
        // returning, the application must not announce a missing dependency it has not
        // established. Collapsing Unknown into Missing makes that lie flash on every launch.
        Assert.DoesNotContain("could not be run", GitPresence.Unknown.Explanation);
    }

    [Fact]
    public void Found_is_usable_and_reports_the_version_verbatim()
    {
        var presence = GitPresence.Found("git version 2.45.1.windows.1", @"C:\Program Files\Git\cmd\git.exe");

        Assert.True(presence.IsUsable);
        Assert.Equal("git version 2.45.1.windows.1", presence.Version);
        Assert.Equal("git version 2.45.1.windows.1", presence.Explanation);
        Assert.Equal(@"C:\Program Files\Git\cmd\git.exe", presence.ExecutablePath);
    }

    [Fact]
    public void Found_trims_the_trailing_newline_git_writes()
    {
        var presence = GitPresence.Found("git version 2.45.1\n", "  /usr/bin/git  ");

        Assert.Equal("git version 2.45.1", presence.Version);
        Assert.Equal("/usr/bin/git", presence.ExecutablePath);
    }

    [Fact]
    public void Missing_is_not_usable()
    {
        Assert.False(GitPresence.NotFound("The system cannot find the file specified.").IsUsable);
    }

    [Fact]
    public void Missing_names_PATH_rather_than_telling_the_user_to_install_git()
    {
        var presence = GitPresence.NotFound("The system cannot find the file specified.");

        // The usual cause is a git that exists but is not on the PATH of the process Versio
        // was launched from. "Git is not installed" is wrong about half the time and sends
        // somebody the wrong way for an afternoon.
        Assert.Contains("PATH", presence.Explanation);
    }

    [Fact]
    public void Missing_carries_the_underlying_reason_so_it_can_be_diagnosed()
    {
        var presence = GitPresence.NotFound("The system cannot find the file specified.");

        Assert.Contains("The system cannot find the file specified.", presence.Explanation);
    }

    [Fact]
    public void Missing_with_no_reason_still_reads_as_a_sentence()
    {
        var explanation = GitPresence.NotFound(string.Empty).Explanation;

        Assert.DoesNotContain("()", explanation);
        Assert.EndsWith("PATH.", explanation);
    }

    [Fact]
    public void A_found_git_with_no_resolved_path_is_still_usable()
    {
        // `where git` failing is not a reason to refuse to work: the version call already
        // proved the executable runs, and the path is a diagnostic nicety.
        Assert.True(GitPresence.Found("git version 2.45.1", string.Empty).IsUsable);
    }
}
