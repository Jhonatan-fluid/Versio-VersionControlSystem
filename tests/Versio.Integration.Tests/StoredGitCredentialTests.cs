using Versio.Infrastructure.Git;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// Reading the secret out of a credential helper's reply, which is the one place the
/// application takes it back from git at all.
/// </summary>
public sealed class StoredGitCredentialTests
{
    [Fact]
    public void The_password_line_is_the_token()
    {
        const string reply = "protocol=https\nhost=github.com\nusername=someone\npassword=gho_abc123\n";

        Assert.Equal("gho_abc123", StoredGitCredential.PasswordOf(reply));
    }

    [Fact]
    public void A_carriage_return_does_not_ride_along_into_the_header()
    {
        // A token ending in \r is sent to GitHub as part of the Authorization header and
        // answered 401 - which would read as "no access to the releases repository".
        Assert.Equal("tok", StoredGitCredential.PasswordOf("password=tok\r\n"));
    }

    [Fact]
    public void A_reply_that_only_echoes_the_request_has_no_token()
    {
        // A helper with nothing stored can exit zero after echoing the protocol and host back.
        Assert.Null(StoredGitCredential.PasswordOf("protocol=https\nhost=github.com\n"));
        Assert.Null(StoredGitCredential.PasswordOf("password=\n"));
    }
}
