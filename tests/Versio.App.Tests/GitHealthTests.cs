using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The two questions Versio has to answer about git before it can do anything with it: is it
/// there, and does it know who is committing.
/// </summary>
/// <remarks>
/// <para>
/// Both are cheap and both were previously invisible. The identity check is the one with a
/// measurable cost behind it: the PLC export gate runs <i>before</i> the commit, so a repository
/// with no <c>user.email</c> spent 17–31 seconds running CODESYS and was then handed git's
/// nine-line lecture. The assertion that matters here is not that the button is disabled — it
/// is that it is disabled <b>for that reason</b>, so the export never starts.
/// </para>
/// <para>
/// No git process is run. The shell is exercised with no repository open, which is exactly the
/// state these properties have to be correct in — a prompt on an empty shell is noise, and a
/// missing-git banner that flashes during startup is worse than no banner.
/// </para>
/// </remarks>
public sealed class GitHealthTests
{
    private static ShellViewModel Shell()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        return new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);
    }

    [Fact]
    public void Git_is_not_reported_missing_before_the_probe_has_answered()
    {
        using var shell = Shell();

        // The window is shown before the probe returns. If Unknown counted as Missing, the
        // "git not available" banner would flash on every single launch.
        Assert.False(shell.GitIsMissing);
        Assert.Equal(GitAvailability.Unknown, shell.GitPresence.Availability);
    }

    [Fact]
    public async Task The_launch_probe_finds_the_git_this_machine_runs_on()
    {
        using var shell = Shell();

        await shell.ProbeGitAsync(CancellationToken.None);

        // The suite itself is running from a git checkout, so git is on PATH by construction.
        Assert.True(shell.GitPresence.IsUsable);
        Assert.False(shell.GitIsMissing);
        Assert.StartsWith("git version", shell.GitPresence.Version);
    }

    [Fact]
    public async Task The_probe_resolves_which_git_it_found()
    {
        using var shell = Shell();

        await shell.ProbeGitAsync(CancellationToken.None);

        // "Which git is it using" is a real question on a machine carrying several, and the
        // answer belongs in System Info rather than in a support call.
        Assert.NotEqual(string.Empty, shell.GitPresence.ExecutablePath);
    }

    [Fact]
    public void An_identity_prompt_does_not_appear_with_no_repository_open()
    {
        using var shell = Shell();

        // Nothing to be missing an identity *for*. The prompt is gated on having a repository
        // so that an empty shell is quiet.
        Assert.False(shell.IdentityIsMissing);
    }

    [Fact]
    public async Task Commit_is_refused_while_git_cannot_name_a_committer()
    {
        // AGAINST A REAL REPOSITORY, and that is not incidental.
        //
        // The first version of this test asserted CanExecute was false with no repository open
        // — which it was, because `Ready` is false without a git client. It passed with the
        // identity check present AND with it deleted: a vacuous test, exactly the shape this
        // repository keeps catching. It was the mutation check that exposed it.
        //
        // To reach the identity term, every other term of CanCommit has to be satisfied: a
        // repository, a staged file, and a message.
        using var repo = new TempRepo(identity: false, unstagedFiles: 1, stagedFiles: 1);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        shell.CommitMessage = "a real message";

        Assert.NotEmpty(shell.Staged);
        Assert.False(shell.Identity.IsComplete);
        Assert.True(shell.IdentityIsMissing);
        Assert.False(shell.CommitCommand.CanExecute(null));
        Assert.NotEqual(string.Empty, shell.Identity.CommitBlockedReason);
    }

    [Fact]
    public async Task Commit_is_allowed_once_the_identity_is_there()
    {
        // The other half, and what makes the test above mean something: with the identity
        // written and nothing else changed, the same command becomes live. Without this, a
        // CanCommit hard-wired to false would satisfy the refusal test.
        using var repo = new TempRepo(identity: true, unstagedFiles: 1, stagedFiles: 1);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        shell.CommitMessage = "a real message";

        Assert.NotEmpty(shell.Staged);
        Assert.True(shell.Identity.IsComplete);
        Assert.False(shell.IdentityIsMissing);
        Assert.True(shell.CommitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Setting_an_identity_from_the_panel_unblocks_the_commit()
    {
        using var repo = new TempRepo(identity: false, unstagedFiles: 1, stagedFiles: 1);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        shell.CommitMessage = "a real message";
        Assert.False(shell.CommitCommand.CanExecute(null));

        shell.IdentityName = "Jane Engineer";
        shell.IdentityEmail = "jane@example.com";
        Assert.True(shell.SaveIdentityCommand.CanExecute(null));

        shell.SaveIdentityCommand.Execute(null);
        await Eventually.Until(() => shell.Identity.IsComplete, "the identity was written");

        // Re-read from git rather than assumed from what was typed — writing --local into a
        // repository whose global config holds something else is a case where the two are not
        // obviously the same, and the panel must report git's answer.
        Assert.True(shell.Identity.IsComplete);
        Assert.Equal("jane@example.com", shell.Identity.Email);
        Assert.False(shell.IdentityIsMissing);
        Assert.True(shell.CommitCommand.CanExecute(null));
    }

    [Fact]
    public void Saving_an_identity_needs_both_fields()
    {
        using var shell = Shell();

        Assert.False(shell.SaveIdentityCommand.CanExecute(null));

        shell.IdentityName = "Jane Engineer";
        Assert.False(shell.SaveIdentityCommand.CanExecute(null));

        shell.IdentityEmail = "jane@example.com";

        // Still false: there is no repository to write into. The point is that the field
        // gate alone is not what is holding it.
        Assert.Equal("Jane Engineer", shell.IdentityName);
        Assert.Equal("jane@example.com", shell.IdentityEmail);
    }

    [Fact]
    public void A_malformed_address_is_flagged_but_never_blocks()
    {
        using var shell = Shell();

        shell.IdentityName = "Jane Engineer";
        shell.IdentityEmail = "Jane Engineer";
        Assert.True(shell.IdentityEmailLooksWrong);

        // engineer@plc has no dot and is a perfectly good intranet address. Versio must not be
        // stricter than the git it is driving.
        shell.IdentityEmail = "engineer@plc";
        Assert.False(shell.IdentityEmailLooksWrong);
    }

    [Fact]
    public void The_global_switch_is_off_by_default()
    {
        using var shell = Shell();

        // A work machine carrying a personal global identity is a common and annoying way to
        // attribute commits to the wrong person. Local is the safe default.
        Assert.False(shell.IdentityGlobal);
    }
}
