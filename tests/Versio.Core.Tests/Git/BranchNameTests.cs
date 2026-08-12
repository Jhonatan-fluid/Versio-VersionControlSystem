using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// The branch-name rules, checked before git is asked.
/// </summary>
/// <remarks>
/// <para>
/// The point of these is the <i>message</i> as much as the verdict. Git refuses a bad name
/// after a process spawn, with a sentence written for a terminal; this refuses it while the
/// box is still being typed in, and says which rule was broken.
/// </para>
/// <para>
/// Every rejection is asserted to carry a reason, because a check that returns false with an
/// empty string is a dialog that greys out its own button and explains nothing — which is
/// worse than letting git refuse.
/// </para>
/// </remarks>
public sealed class BranchNameTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("feature/library-export")]
    [InlineData("fix-1234")]
    [InlineData("release/3.5.22")]
    [InlineData("user/elli_/spike")]
    [InlineData("a")]
    [InlineData("Ärger-mit-Umlauten")]
    public void A_name_git_accepts_is_accepted(string name)
    {
        var check = BranchName.Validate(name);

        Assert.True(check.IsValid, $"{name} was rejected: {check.Reason}");
        Assert.Equal(string.Empty, check.Reason);
    }

    [Theory]
    [InlineData("", "needs a name")]
    [InlineData("   ", "needs a name")]
    [InlineData("feature branch", "spaces")]
    [InlineData(" leading", "space")]
    [InlineData("trailing ", "space")]
    [InlineData("-dashed", "'-'")]
    [InlineData("@", "@")]
    [InlineData("head@{1}", "@{")]
    [InlineData("a..b", "..")]
    [InlineData("/leading", "'/'")]
    [InlineData("trailing/", "'/'")]
    [InlineData("double//slash", "empty path segment")]
    [InlineData("ends.", "'.'")]
    [InlineData(".hidden", "'.'")]
    [InlineData("feature/.hidden", "'.'")]
    [InlineData("feature.lock", ".lock")]
    [InlineData("feature/x.lock", ".lock")]
    [InlineData("wild*card", "'*'")]
    [InlineData("question?", "'?'")]
    [InlineData("colon:here", "':'")]
    [InlineData("tilde~1", "'~'")]
    [InlineData("caret^", "'^'")]
    [InlineData("brack[et", "'['")]
    [InlineData("back\\slash", @"'\'")]
    public void A_name_git_refuses_is_refused_with_a_reason(string name, string expectedFragment)
    {
        var check = BranchName.Validate(name);

        Assert.False(check.IsValid, $"{name} was accepted");
        Assert.Contains(expectedFragment, check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_control_character_is_refused()
    {
        // Not typeable, but perfectly pasteable — and a tab in a ref name produces a git
        // error that names neither the character nor the field it came from.
        var check = BranchName.Validate("feature\tname");

        Assert.False(check.IsValid);
        Assert.Contains("control characters", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absurdly_long_name_is_refused_before_the_filesystem_refuses_it()
    {
        // Git's own limit is the filesystem's, because a loose ref is a file — so the failure
        // without this is an IO error from somewhere deep in git, on Windows, mentioning a
        // path length. This is the same refusal with a sentence somebody can act on.
        var check = BranchName.Validate(new string('x', BranchName.MaxLength + 1));

        Assert.False(check.IsValid);
        Assert.Contains("Too long", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_is_refused_rather_than_throwing()
    {
        // The caller is a text box, and a text box's Text can be null before it is loaded.
        Assert.False(BranchName.Validate(null).IsValid);
    }

    [Fact]
    public void Every_rejection_says_something()
    {
        // The whole reason this type exists rather than a bool. A rejection with no reason
        // disables a button and leaves the user with nothing to change.
        string[] bad =
        [
            "", "   ", "a b", "-x", "@", "a..b", "/x", "x/", "x//y", "x.", ".x",
            "x.lock", "a*b", "a?b", "a:b", "a~b", "a^b", "a[b", "a\\b", "a\tb",
        ];

        Assert.All(bad, name =>
        {
            var check = BranchName.Validate(name);
            Assert.False(check.IsValid, $"{name} was accepted");
            Assert.False(string.IsNullOrWhiteSpace(check.Reason), $"{name} has no reason");
            Assert.EndsWith(".", check.Reason.Trim(), StringComparison.Ordinal);
        });
    }
}
