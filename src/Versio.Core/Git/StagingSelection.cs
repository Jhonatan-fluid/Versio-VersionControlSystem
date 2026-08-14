namespace Versio.Core.Git;

/// <summary>A master checkbox's three positions.</summary>
public enum SelectionState
{
    /// <summary>Nothing in the list is ticked.</summary>
    None,

    /// <summary>Some but not all.</summary>
    Some,

    /// <summary>Everything.</summary>
    All,
}

/// <summary>
/// What a staging action applies to, and how the header describes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The checkbox is not the list selection.</b> Ticking a box says "this is what the next
/// action applies to"; clicking a row says "show me this one's diff". Conflating them is the
/// standard way this feature goes wrong — the user loses their diff every time they change
/// what they are about to stage, or ticks a box and finds the pane has jumped somewhere else.
/// </para>
/// <para>
/// <b>Nothing ticked means the whole list.</b> That is what makes the pair of buttons collapse
/// into one: "Stage all" and "Stage selected" are the same command with a different argument,
/// and a panel 380 px wide cannot afford two of everything. The label carries the difference so
/// nobody presses it expecting the other meaning.
/// </para>
/// <para>
/// Pure, because the interesting part is a set of rules about counts rather than anything to do
/// with WPF. The tri-state in particular is worth testing without a window: it has an empty-list
/// case that reads wrong if it is derived naively from "are all of them ticked".
/// </para>
/// </remarks>
public static class StagingSelection
{
    /// <summary>
    /// Where a master checkbox should sit for a list of this shape.
    /// </summary>
    /// <remarks>
    /// <b>An empty list is <see cref="SelectionState.None"/>, not
    /// <see cref="SelectionState.All"/>.</b> "Every one of no items is ticked" is true and
    /// useless: it would show a header checkbox ticked over an empty list, which invites a
    /// click that does nothing.
    /// </remarks>
    public static SelectionState StateFor(int total, int selected)
    {
        if (total <= 0 || selected <= 0)
        {
            return SelectionState.None;
        }

        return selected >= total ? SelectionState.All : SelectionState.Some;
    }

    // Affected() and CanAct() were here, computing "the ticked ones, or all of them" as a
    // count and "is there anything to do" from it. Neither was ever read: the shell needs the
    // ROWS rather than a count, and CanAct reduced to `total > 0` once the substitution is
    // done. Two members that looked like the rule and were not it.

    /// <summary>
    /// The label for an action button, saying what it will do rather than what it is called.
    /// </summary>
    /// <remarks>
    /// <b>The count is in the label, not only in the header.</b> A button reading "Stage" over
    /// a list of forty with three ticked is ambiguous in the one direction that costs
    /// something: staging forty files when three were meant is a commit that has to be
    /// unpicked.
    /// </remarks>
    public static string Label(string verb, int total, int selected)
    {
        if (total <= 0)
        {
            return verb;
        }

        return selected > 0 ? $"{verb} {selected}" : $"{verb} all";
    }

    /// <summary>
    /// The header caption: how much of this list is spoken for.
    /// </summary>
    /// <remarks>
    /// Empty when nothing is ticked, because "0 of 12 selected" is a sentence that says nothing
    /// and takes a line to say it. The list already shows twelve rows.
    /// </remarks>
    public static string Caption(int total, int selected) =>
        selected <= 0 || total <= 0 ? string.Empty : $"{selected} of {total} selected";
}
