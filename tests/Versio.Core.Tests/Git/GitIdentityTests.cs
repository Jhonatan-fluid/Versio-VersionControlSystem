using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// What git needs before it will record a commit, and how that is said.
/// </summary>
/// <remarks>
/// The interesting assertions here are the two boundaries: whitespace counts as absent, because
/// <c>git config user.name " "</c> is a configured setting and an unusable identity; and both
/// missing fields are named at once, because reporting one and letting the user discover the
/// other on the next attempt turns a one-step fix into two.
/// </remarks>
public sealed class GitIdentityTests
{
    [Fact]
    public void Nothing_configured_is_incomplete()
    {
        Assert.False(GitIdentity.None.IsComplete);
        Assert.False(GitIdentity.None.HasName);
        Assert.False(GitIdentity.None.HasEmail);
    }

    [Fact]
    public void Both_present_is_complete_and_says_nothing()
    {
        var identity = GitIdentity.From("Jane Engineer", "jane@example.com");

        Assert.True(identity.IsComplete);
        Assert.Equal(string.Empty, identity.Explanation);
        Assert.Equal(string.Empty, identity.CommitBlockedReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void Whitespace_counts_as_absent(string? blank)
    {
        // git config user.name " " is a configured setting and an unusable identity. Treating
        // it as present would gate the button open and let the commit fail anyway, which is the
        // exact failure this check exists to prevent.
        Assert.False(GitIdentity.From(blank, "jane@example.com").IsComplete);
        Assert.False(GitIdentity.From("Jane Engineer", blank).IsComplete);
    }

    [Fact]
    public void Values_are_trimmed()
    {
        var identity = GitIdentity.From("  Jane Engineer  ", "  jane@example.com\n");

        Assert.Equal("Jane Engineer", identity.Name);
        Assert.Equal("jane@example.com", identity.Email);
    }

    [Fact]
    public void Both_missing_are_named_together()
    {
        var explanation = GitIdentity.None.Explanation;

        Assert.Contains("name", explanation);
        Assert.Contains("email", explanation);
    }

    [Fact]
    public void A_missing_name_alone_does_not_mention_the_email()
    {
        var explanation = GitIdentity.From(null, "jane@example.com").Explanation;

        Assert.Contains("name", explanation);
        Assert.DoesNotContain("email", explanation);
    }

    [Fact]
    public void A_missing_email_alone_does_not_mention_the_name()
    {
        var explanation = GitIdentity.From("Jane Engineer", null).Explanation;

        Assert.Contains("email", explanation);
        Assert.DoesNotContain(" name", explanation);
    }

    [Fact]
    public void An_incomplete_identity_gives_the_button_a_reason()
    {
        Assert.NotEqual(string.Empty, GitIdentity.None.CommitBlockedReason);
    }

    [Theory]
    [InlineData("jane@example.com")]
    [InlineData("engineer@plc")]
    [InlineData("a@b")]
    public void Ordinary_addresses_look_like_emails(string email)
    {
        // engineer@plc has no dot and is a perfectly good intranet address. Refusing it would
        // make Versio stricter than the git it is driving, which is how people go back to the
        // terminal.
        Assert.True(GitIdentity.From("Jane", email).LooksLikeEmail);
    }

    [Theory]
    [InlineData("Jane Engineer")]
    [InlineData("@example.com")]
    [InlineData("jane@")]
    [InlineData("jane@@example.com")]
    [InlineData("")]
    public void A_name_typed_into_the_email_box_does_not(string email)
    {
        Assert.False(GitIdentity.From("Jane", email).LooksLikeEmail);
    }

    [Fact]
    public void Looking_wrong_does_not_make_it_incomplete()
    {
        // Advisory, not a gate. Git accepts anything non-empty and so does this.
        var identity = GitIdentity.From("Jane", "not-an-address");

        Assert.False(identity.LooksLikeEmail);
        Assert.True(identity.IsComplete);
    }
}
