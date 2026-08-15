using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// What the panel says about credentials, and which buttons it offers.
/// </summary>
/// <remarks>
/// <para>
/// The classification rules are proved in <c>GitAuthClassifierTests</c> against real git and
/// forge messages. What these cover is the shell reaching them with the right transport, and
/// the two cases where offering the obvious button would be wrong: a 403, where the credential
/// worked and signing in again is a loop that cannot terminate, and an SSH remote, which has no
/// credential helper at all.
/// </para>
/// <para>
/// No credential is ever entered, stored or read. The remotes here are local paths and
/// deliberately-shaped URLs; nothing in this file can prompt, and nothing touches the
/// developer's own credential store.
/// </para>
/// </remarks>
public sealed class CredentialFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    [Fact]
    public async Task A_local_remote_asks_for_no_credentials_at_all()
    {
        // A folder is a perfectly good remote and authenticates nothing. A sign-in banner over
        // one would be pure noise.
        using var repo = new TempRepo(originUrl: @"C:\some\bare\repo.git");
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.Equal(GitAuthState.Fine, shell.Auth.AuthState);
        Assert.False(shell.Auth.NeedsAttentionForAuth);
        Assert.False(shell.Auth.CanSignIn);
        Assert.False(shell.Auth.SignOutCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_repository_with_no_remote_says_nothing_about_credentials()
    {
        using var repo = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.Equal(GitAuthState.Fine, shell.Auth.AuthState);
        Assert.False(shell.Auth.NeedsAttentionForAuth);
    }

    [Fact]
    public async Task An_ssh_remote_is_never_offered_a_sign_in_button()
    {
        // There is no credential helper on that path — it is keys and ssh-agent. The button
        // would be one that cannot work, the same sin as advising a pull that --ff-only will
        // refuse.
        using var repo = new TempRepo(originUrl: "git@github.com:someone/something.git");
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.False(shell.Auth.CanSignIn);
        Assert.False(shell.Auth.SignInCommand.CanExecute(null));
        Assert.False(shell.Auth.SignOutCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_host_is_taken_from_the_remote_so_the_sentence_is_about_somewhere()
    {
        using var repo = new TempRepo(originUrl: "https://git.acme.internal/team/plc.git");
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.Equal("git.acme.internal", shell.Auth.RemoteHost);
    }

    [Fact]
    public async Task Forgetting_a_credential_is_available_for_an_https_remote()
    {
        // Available whenever a credential could be stored, not only after a failure: a button
        // that only appears once something has broken cannot be found before it does.
        using var repo = new TempRepo(originUrl: "https://git.acme.internal/team/plc.git");
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.True(shell.Auth.SignOutCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_credential_check_runs_when_an_https_repository_opens()
    {
        // The proactive half: the panel should be able to say "sign in needed" while the user
        // is still looking at the window, rather than after their first push fails. This host
        // does not exist, so nothing is stored for it.
        using var repo = new TempRepo(originUrl: "https://versio-test.invalid/team/plc.git");
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.Equal(GitAuthState.NoCredential, shell.Auth.AuthState);
        Assert.True(shell.Auth.NeedsAttentionForAuth);
        Assert.True(shell.Auth.CanSignIn);
        Assert.Contains("Sign in", shell.Auth.AuthExplanation);
        Assert.Contains("versio-test.invalid", shell.Auth.AuthExplanation);
    }

    [Fact]
    public async Task Opening_a_different_repository_clears_the_credential_state()
    {
        using var https = new TempRepo(originUrl: "https://versio-test.invalid/team/plc.git");
        using var local = new TempRepo(originUrl: @"C:\some\bare\repo.git");
        using var shell = Shell();

        await shell.OpenAsync(https.Path);
        Assert.True(shell.Auth.NeedsAttentionForAuth);

        await shell.OpenAsync(local.Path);

        // The banner was about a host this repository has nothing to do with.
        Assert.False(shell.Auth.NeedsAttentionForAuth);
        Assert.Equal(string.Empty, shell.Auth.RemoteHost);
    }

    [Fact]
    public void The_explanation_for_a_forbidden_response_does_not_offer_a_sign_in()
    {
        // Asserted through the classifier rather than by provoking a real 403, which would
        // need a server. The shell binds CanSignIn to exactly this predicate.
        Assert.False(GitAuthClassifier.SignInWouldHelp(GitAuthState.NotAuthorised));
        Assert.Contains("signing in again will not change it",
                        GitAuthClassifier.Explain(GitAuthState.NotAuthorised, "github.com"),
                        StringComparison.OrdinalIgnoreCase);
    }
}
