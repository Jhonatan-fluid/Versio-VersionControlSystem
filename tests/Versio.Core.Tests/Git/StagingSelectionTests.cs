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

    [Fact]
    public void The_header_carries_the_count_so_the_list_does_not_have_to_be_counted()
    {
        // The gap this closes: a forty-file stage read "STAGED", and the only way to learn it
        // was forty was to count the rows.
        Assert.Equal("STAGED · 12", StagingSelection.Header("STAGED", 12, 0));
    }

    [Fact]
    public void The_selection_takes_the_slot_while_anything_is_ticked()
    {
        // Not "STAGED · 12 · 3 of 12 selected". The selection sentence already carries the
        // total, and printing it twice in one header is two controls' worth of noise in one.
        Assert.Equal("NOT STAGED · 3 of 12 selected",
                     StagingSelection.Header("NOT STAGED", 12, 3));
    }

    [Fact]
    public void An_empty_list_gets_the_bare_label()
    {
        // "STAGED · 0" is the coverage line that congratulated itself on every refresh. With
        // both lists empty the panel shows its clean-tree sentence instead of either header,
        // so the only reader of this case is a list that is empty while the other is not.
        Assert.Equal("STAGED", StagingSelection.Header("STAGED", 0, 0));
    }
}
