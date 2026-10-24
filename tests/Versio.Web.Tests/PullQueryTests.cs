using Versio.Web.Review;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// GitHub's search grammar, the subset the list takes (docs/web/FINISH.md, B10): every token
/// checked, only known ones re-emitted, the repository and the type pinned by this side.
/// </summary>
public sealed class PullQueryTests
{
    [Fact]
    public void A_query_is_pinned_to_the_repository_and_to_pull_requests_with_open_and_the_sort_as_defaults()
    {
        var (parsed, refused) = PullQuery.Parse("author:jo label:\"needs soak\" interlock", "acme", "plant");

        Assert.Null(refused);
        Assert.Equal("repo:acme/plant is:pr is:open author:jo label:\"needs soak\" interlock sort:updated-desc", parsed!.Search);
        Assert.Equal(("open", "updated", "desc"), (parsed.State, parsed.Sort, parsed.Direction));
    }

    [Fact]
    public void The_state_and_the_sort_are_read_for_the_REST_fallback()
    {
        var closed = PullQuery.Parse("is:closed sort:created-asc", "acme", "plant").Query!;
        var merged = PullQuery.Parse("is:merged review:approved", "acme", "plant").Query!;

        Assert.Equal(("closed", "created", "asc"), (closed.State, closed.Sort, closed.Direction));
        Assert.Contains("sort:created-asc", closed.Search, StringComparison.Ordinal);
        Assert.Equal("closed", merged.State);
        Assert.Contains("is:merged review:approved", merged.Search, StringComparison.Ordinal);
    }

    /// <summary>Nothing this page does not show reaches GitHub: another repository, another type, an unknown operator, a login that is not one.</summary>
    [Theory]
    [InlineData("repo:other/thing", "repo:")]
    [InlineData("is:issue", "is:issue")]
    [InlineData("type:issue", "type:")]
    [InlineData("author:bad!login", "author:bad!login")]
    [InlineData("fine word!", "word!")]
    [InlineData("review:maybe", "review:maybe")]
    [InlineData("sort:stars", "sort:stars")]
    [InlineData("head:a:b", "head:a:b")]
    public void A_token_outside_the_grammar_is_refused_by_name(string q, string refused)
    {
        var (parsed, word) = PullQuery.Parse(q, "acme", "plant");

        Assert.Null(parsed);
        Assert.Equal(refused, word);
    }

    /// <summary>The counts' queries swap the state by token: a quoted value holding the words is left alone (review, 2026-10-24).</summary>
    [Fact]
    public void The_state_is_swapped_by_token_and_never_inside_a_quoted_value()
    {
        Assert.Equal("repo:acme/plant is:pr is:closed label:\"is:closed\" sort:updated-desc", PullQuery.WithState("repo:acme/plant is:pr is:open label:\"is:closed\" sort:updated-desc", "closed"));
        Assert.Equal("repo:acme/plant is:pr is:open x", PullQuery.WithState("repo:acme/plant is:pr is:merged x", "open"));
        Assert.Equal("repo:acme/plant is:pr is:open", PullQuery.WithState("repo:acme/plant is:pr", "open"));
    }

    [Fact]
    public void An_empty_query_is_the_open_list()
    {
        var parsed = PullQuery.Parse(null, "acme", "plant").Query!;

        Assert.Equal("repo:acme/plant is:pr is:open sort:updated-desc", parsed.Search);
        Assert.Equal("is:open sort:updated-desc", parsed.Normalized);
    }
}
