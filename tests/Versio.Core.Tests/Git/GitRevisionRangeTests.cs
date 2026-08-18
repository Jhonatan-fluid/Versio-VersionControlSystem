using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// Which way round each preview range goes — the whole reason the builder exists.
/// </summary>
/// <remarks>
/// <c>upstream..HEAD</c> and <c>HEAD..upstream</c> both produce a plausible commit list, and
/// a preview showing the wrong direction would be read and believed. These tests are the
/// statement of the convention, one case per shape.
/// </remarks>
public sealed class GitRevisionRangeTests
{
    [Fact]
    public void Outgoing_is_what_HEAD_has_that_the_upstream_lacks()
    {
        Assert.Equal("origin/main..HEAD", GitRevisionRange.Outgoing("origin/main"));
    }

    [Fact]
    public void Incoming_is_what_the_upstream_has_that_HEAD_lacks()
    {
        Assert.Equal("HEAD..origin/main", GitRevisionRange.Incoming("origin/main"));
    }

    [Fact]
    public void The_incoming_diff_uses_three_dots()
    {
        // Two dots would count this machine's own unpushed edits as "arriving" on a
        // diverged branch — objects about to be imported that nobody else touched. Three
        // dots diff against the merge base, which is only what the other side changed.
        Assert.Equal("HEAD...origin/main", GitRevisionRange.IncomingDiff("origin/main"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void No_upstream_means_no_range_for_any_shape(string? upstream)
    {
        // Null, never a guess: `..HEAD` with nothing before it answers with every commit
        // in the repository, which a preview would happily display as "about to push".
        Assert.Null(GitRevisionRange.Outgoing(upstream));
        Assert.Null(GitRevisionRange.Incoming(upstream));
        Assert.Null(GitRevisionRange.IncomingDiff(upstream));
    }
}
