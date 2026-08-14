using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// Reading what git said and deciding what it meant.
/// </summary>
/// <remarks>
/// <para>
/// Every string here is a real message from git or a forge, not a paraphrase. The whole value
/// of this class is that it recognises what actually arrives, and a fixture written from memory
/// would agree with the implementation by construction while failing against the real thing.
/// </para>
/// <para>
/// The two cases worth reading first are the ones that would otherwise send somebody in a
/// circle: a 403 means the credential <i>worked</i>, and an SSH key refusal has no credential
/// helper to fix it. Both read superficially like "authentication failed".
/// </para>
/// </remarks>
public sealed class GitAuthClassifierTests
{
    [Fact]
    public void Nothing_written_is_not_a_failure()
    {
        Assert.Equal(GitAuthState.Fine,
                     GitAuthClassifier.Classify("", RemoteTransport.Http));
    }

    [Fact]
    public void Our_own_suppressed_prompt_is_recognised()
    {
        // Versio sets GIT_TERMINAL_PROMPT=0 on every invocation, so with no credential stored
        // git does not ask - it fails with this. The signature is unambiguous precisely because
        // we cause it.
        const string stderr =
            "fatal: could not read Username for 'https://github.com': terminal prompts disabled";

        Assert.Equal(GitAuthState.NoCredential,
                     GitAuthClassifier.Classify(stderr, RemoteTransport.Http));
    }

    [Theory]
    [InlineData("remote: Invalid username or password.\nfatal: Authentication failed for 'https://github.com/x/y.git/'")]
    [InlineData("fatal: Authentication failed for 'https://dev.azure.com/org/proj/_git/repo/'")]
    [InlineData("remote: HTTP Basic: Access denied. The provided password or token is incorrect")]
    public void A_refused_credential_is_a_rejection(string stderr)
    {
        Assert.Equal(GitAuthState.Rejected,
                     GitAuthClassifier.Classify(stderr, RemoteTransport.Http));
    }

    [Fact]
    public void A_password_where_a_token_is_required_says_so_specifically()
    {
        // Contains the word "authentication" and means something more specific than a wrong
        // password: the remedy is a token, not another attempt at the same thing.
        const string stderr =
            "remote: Support for password authentication was removed on August 13, 2021.";

        Assert.Equal(GitAuthState.PasswordNotSupported,
                     GitAuthClassifier.Classify(stderr, RemoteTransport.Http));
    }

    [Theory]
    [InlineData("remote: Write access to repository not granted.\nfatal: unable to access ...: The requested URL returned error: 403")]
    [InlineData("remote: You do not have permission to push to this repository")]
    [InlineData("remote: HTTP Basic: Access denied\nfatal: The requested URL returned error: 403")]
    public void A_forbidden_response_is_not_a_credential_problem(string stderr)
    {
        // THE LOOP THAT CANNOT TERMINATE. The credential worked; the account lacks write
        // access. Telling this user to sign in again sends them round for ever.
        Assert.Equal(GitAuthState.NotAuthorised,
                     GitAuthClassifier.Classify(stderr, RemoteTransport.Http));
    }

    [Fact]
    public void Signing_in_is_not_offered_for_a_forbidden_response()
    {
        Assert.False(GitAuthClassifier.SignInWouldHelp(GitAuthState.NotAuthorised));
    }

    [Fact]
    public void A_forbidden_response_says_not_to_bother_signing_in_again()
    {
        var explanation = GitAuthClassifier.Explain(GitAuthState.NotAuthorised, "github.com");

        Assert.Contains("cannot write", explanation);
        Assert.Contains("signing in again will not change it", explanation,
                        StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.")]
    [InlineData("Host key verification failed.\nfatal: Could not read from remote repository.")]
    [InlineData("error: Could not open a connection to your authentication agent.")]
    public void An_ssh_key_refusal_is_not_a_credential_problem_either(string stderr)
    {
        Assert.Equal(GitAuthState.SshKeyRefused,
                     GitAuthClassifier.Classify(stderr, RemoteTransport.Ssh));
    }

    [Fact]
    public void An_ssh_refusal_is_recognised_even_when_the_transport_is_unknown()
    {
        // The publickey signature is unmistakable on its own. Depending on the transport
        // having been parsed correctly would make this fail on a URL shape nothing recognised.
        Assert.Equal(GitAuthState.SshKeyRefused,
                     GitAuthClassifier.Classify("Permission denied (publickey).",
                                                RemoteTransport.Unknown));
    }

    [Fact]
    public void Ssh_advice_mentions_keys_and_not_signing_in()
    {
        var explanation = GitAuthClassifier.Explain(GitAuthState.SshKeyRefused, "github.com");

        // There is no credential helper on this path. A "Sign in" button here would be a
        // button that cannot work.
        Assert.Contains("keys", explanation);
        Assert.Contains("ssh -T", explanation);
        Assert.False(GitAuthClassifier.SignInWouldHelp(GitAuthState.SshKeyRefused));
    }

    [Theory]
    [InlineData("fatal: unable to access 'https://github.com/x/y.git/': Could not resolve host: github.com")]
    [InlineData("ssh: connect to host github.com port 22: Connection timed out")]
    [InlineData("fatal: unable to access '...': SSL certificate problem: unable to get local issuer certificate")]
    public void Being_offline_is_not_a_credential_problem(string stderr)
    {
        Assert.Equal(GitAuthState.Unreachable,
                     GitAuthClassifier.Classify(stderr, RemoteTransport.Http));
    }

    [Fact]
    public void Repository_not_found_is_treated_as_unreachable_rather_than_guessed_at()
    {
        // Genuinely ambiguous: a private repository returns this for an unauthenticated
        // request too. "Check the address and your connection" is safe either way, where
        // "sign in again" is wrong half the time.
        const string stderr = "remote: Repository not found.\nfatal: repository ... not found";

        Assert.Equal(GitAuthState.Unreachable,
                     GitAuthClassifier.Classify(stderr, RemoteTransport.Http));
    }

    [Fact]
    public void An_unrecognised_message_offers_no_advice_at_all()
    {
        var state = GitAuthClassifier.Classify(
            "fatal: something nobody has ever seen before", RemoteTransport.Http);

        // Git's messages are not a machine format. Guessing a remedy from one nothing
        // recognised is how somebody ends up re-entering credentials at a firewall.
        Assert.Equal(GitAuthState.OtherFailure, state);
        Assert.Equal(string.Empty, GitAuthClassifier.Explain(state, "github.com"));
        Assert.False(GitAuthClassifier.SignInWouldHelp(state));
    }

    [Fact]
    public void A_rejected_credential_is_erased_before_asking_again()
    {
        // The maddening case: an expired token the helper keeps cheerfully returning, so every
        // push fails identically until something erases it.
        Assert.True(GitAuthClassifier.ShouldEraseFirst(GitAuthState.Rejected));
        Assert.True(GitAuthClassifier.ShouldEraseFirst(GitAuthState.PasswordNotSupported));

        // Nothing is stored yet, so there is nothing to erase.
        Assert.False(GitAuthClassifier.ShouldEraseFirst(GitAuthState.NoCredential));
    }

    [Theory]
    [InlineData(GitAuthState.NoCredential)]
    [InlineData(GitAuthState.Rejected)]
    [InlineData(GitAuthState.PasswordNotSupported)]
    [InlineData(GitAuthState.NotAuthorised)]
    [InlineData(GitAuthState.SshKeyRefused)]
    public void Auth_failures_are_terminal_for_the_poller(GitAuthState state)
    {
        // Retrying any of these every few seconds cannot succeed, and may lock an account.
        Assert.True(GitAuthClassifier.IsTerminal(state));
    }

    [Fact]
    public void Being_offline_is_worth_retrying()
    {
        Assert.False(GitAuthClassifier.IsTerminal(GitAuthState.Unreachable));
        Assert.False(GitAuthClassifier.IsTerminal(GitAuthState.Fine));
    }

    [Fact]
    public void Every_actionable_state_names_the_host()
    {
        GitAuthState[] states =
        [
            GitAuthState.NoCredential, GitAuthState.Rejected,
            GitAuthState.PasswordNotSupported, GitAuthState.NotAuthorised,
            GitAuthState.SshKeyRefused, GitAuthState.Unreachable,
        ];

        foreach (var state in states)
        {
            var explanation = GitAuthClassifier.Explain(state, "git.acme.internal");

            Assert.False(string.IsNullOrWhiteSpace(explanation), $"{state} says nothing");
            Assert.Contains("git.acme.internal", explanation);
        }
    }

    [Fact]
    public void An_unknown_host_still_produces_a_sentence()
    {
        // A remote whose URL nothing parsed still fails, and the message has to read.
        foreach (var state in new[] { GitAuthState.NoCredential, GitAuthState.Rejected })
        {
            var explanation = GitAuthClassifier.Explain(state, string.Empty);

            Assert.False(string.IsNullOrWhiteSpace(explanation));
            Assert.DoesNotContain("  ", explanation);
        }
    }
}
