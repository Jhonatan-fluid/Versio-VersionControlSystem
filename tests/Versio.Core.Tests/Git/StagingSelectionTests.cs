using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// What a staging action applies to, and how the panel describes it.
/// </summary>
/// <remarks>
/// Small rules with one sharp edge each. The empty-list tri-state reads wrong if it is derived
/// naively, and the button label is the only thing standing between "stage the three I ticked"
/// and "stage all forty" — which is a commit that has to be unpicked rather than a cosmetic
/// difference.
/// </remarks>
public sealed class StagingSelectionTests
{
    [Theory]
    [InlineData(0, 0, SelectionState.None)]
    [InlineData(5, 0, SelectionState.None)]
    [InlineData(5, 2, SelectionState.Some)]
    [InlineData(5, 5, SelectionState.All)]
    public void The_master_checkbox_follows_the_children(int total, int selected, SelectionState expected)
    {
        Assert.Equal(expected, StagingSelection.StateFor(total, selected));
    }

    [Fact]
    public void An_empty_list_is_unticked_rather_than_fully_ticked()
    {
        // "Every one of no items is ticked" is true and useless: a ticked header over an empty
        // list invites a click that does nothing.
        Assert.Equal(SelectionState.None, StagingSelection.StateFor(0, 0));
    }

    [Fact]
    public void More_selected_than_present_is_still_all()
    {
        // Defensive rather than expected: a refresh that removes a row while a selection is
        // held must not produce a fourth state nothing renders.
        Assert.Equal(SelectionState.All, StagingSelection.StateFor(3, 5));
    }

    [Fact]
    public void Nothing_ticked_means_the_whole_list()
    {
        // What makes "Stage all" and "Stage selected" collapse into one button. A 380 px panel
        // cannot afford two of everything.
        Assert.Equal(12, StagingSelection.Affected(total: 12, selected: 0));
    }

    [Fact]
    public void Something_ticked_means_only_that()
    {
        Assert.Equal(3, StagingSelection.Affected(total: 12, selected: 3));
    }

    [Fact]
    public void An_empty_list_has_nothing_to_act_on()
    {
        Assert.False(StagingSelection.CanAct(0, 0));
        Assert.True(StagingSelection.CanAct(1, 0));
        Assert.True(StagingSelection.CanAct(12, 3));
    }

    [Theory]
    [InlineData(0, 0, "Stage")]
    [InlineData(12, 0, "Stage all")]
    [InlineData(12, 3, "Stage 3")]
    [InlineData(12, 12, "Stage 12")]
    public void The_label_says_what_will_happen(int total, int selected, string expected)
    {
        // The one thing standing between "stage the three I ticked" and "stage all forty".
        Assert.Equal(expected, StagingSelection.Label("Stage", total, selected));
    }

    [Fact]
    public void The_label_reads_for_any_verb()
    {
        Assert.Equal("Unstage 2", StagingSelection.Label("Unstage", 5, 2));
        Assert.Equal("Discard all", StagingSelection.Label("Discard", 5, 0));
    }

    [Fact]
    public void The_caption_counts_what_is_spoken_for()
    {
        Assert.Equal("3 of 12 selected", StagingSelection.Caption(12, 3));
    }

    [Theory]
    [InlineData(12, 0)]
    [InlineData(0, 0)]
    public void The_caption_says_nothing_when_there_is_nothing_to_say(int total, int selected)
    {
        // "0 of 12 selected" is a sentence that says nothing and takes a line to say it. The
        // list already shows twelve rows.
        Assert.Equal(string.Empty, StagingSelection.Caption(total, selected));
    }
}
