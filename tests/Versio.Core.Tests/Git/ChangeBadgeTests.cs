using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// The rail's count, which is the only thing on screen that says the working tree has anything
/// in it while any panel but the git one is showing.
/// </summary>
public sealed class ChangeBadgeTests
{
    [Fact]
    public void A_clean_tree_shows_nothing_at_all()
    {
        // Not "0". The pill this replaces had no zero-suppression, so a clean tree drew a
        // badge containing zero beside a panel already saying so in words.
        var badge = ChangeBadge.For(changed: 0, conflicted: false);

        Assert.False(badge.IsVisible);
        Assert.Equal(string.Empty, badge.Text);
    }

    [Fact]
    public void One_change_is_enough_to_appear()
    {
        var badge = ChangeBadge.For(changed: 1, conflicted: false);

        Assert.True(badge.IsVisible);
        Assert.Equal("1", badge.Text);
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(438, "99+")]
    public void The_count_stops_being_exact_past_the_cap(int changed, string expected)
    {
        // A rail tile is 40 px and a real import produces hundreds of mirror rows. At that
        // size the useful distinction is "some" against "a lot".
        Assert.Equal(expected, ChangeBadge.For(changed, conflicted: false).Text);
    }

    [Fact]
    public void A_conflicted_tree_is_a_different_colour_from_a_busy_one()
    {
        // Both produce a count and they are not the same news: one unmerged path means git
        // refuses the commit and the sync engine refuses both directions.
        var busy = ChangeBadge.For(changed: 12, conflicted: false);
        var stuck = ChangeBadge.For(changed: 12, conflicted: true);

        Assert.Equal("12", busy.Text);
        Assert.Equal("12", stuck.Text);
        Assert.NotEqual(busy.BrushKey, stuck.BrushKey);
    }

    [Fact]
    public void Conflicts_on_an_empty_tree_still_show_nothing()
    {
        // Not reachable through the shell - an unmerged path is a change - but the rule is
        // "nothing to say" rather than "no conflicts", and a badge that appeared with no count
        // in it would be a coloured dot with no meaning.
        Assert.False(ChangeBadge.For(changed: 0, conflicted: true).IsVisible);
    }
}
