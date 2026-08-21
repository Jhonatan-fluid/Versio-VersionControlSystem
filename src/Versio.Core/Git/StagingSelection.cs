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
/// <b>Nothing ticked means the whole list.</b> That is what makes Commit and Discard one
/// command each: an explicit tick is the include set, and an empty tick set is every file.
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

    /// <summary>
    /// The whole header for one staging list: its name, and how much is in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The count is the first thing anybody wants from either list, and the one thing a
    /// scrollable list cannot show.</b> Until this existed a forty-file stage read
    /// <c>STAGED</c>, and the way to learn it was forty was to count it — while the commit
    /// button beside it already said <c>Stage &amp; commit (3)</c>, so the panel knew the
    /// number and stated it only where it was about the <i>selection</i>.
    /// </para>
    /// <para>
    /// <b>One string rather than a label and a caption in two columns, and that is a layout
    /// fix rather than a cosmetic one.</b> Both list headers are
    /// <c>Auto | Auto | * | Auto</c>: checkbox, label, caption, buttons. The label sat in an
    /// <c>Auto</c> column in a letter-spaced face with no trimming and no ceiling, so it was
    /// rigid — and a Grid hands every Auto column its full desired width and floors the star
    /// at zero, so once the Autos wanted more than the row had, the star collapsed, the Autos
    /// kept their width, and the buttons arranged past the panel's right edge. Measured at the
    /// panel's own <c>MinWidth</c> of 260 px: the <c>NOT STAGED</c> row, which carries two
    /// buttons where <c>STAGED</c> carries one, overflowed by 13 px with the short labels and
    /// by 26 px with three-digit counts. Merging the two into one trimming star column removes
    /// the rigid column outright, so the header yields to the buttons instead of shoving them
    /// off the panel.
    /// </para>
    /// <para>
    /// <b>Nothing is said about an empty list.</b> <c>STAGED · 0</c> is the coverage line that
    /// congratulated itself on every refresh — a caption that reads the same every time is read
    /// once and never again — and with both lists empty the panel shows its clean-tree sentence
    /// instead of either header.
    /// </para>
    /// </remarks>
    /// <param name="label">The list's name, already in the case the panel wants it.</param>
    public static string Header(string label, int total, int selected)
    {
        if (total <= 0)
        {
            return label;
        }

        // The selection wins the slot when there is one: while somebody is ticking, "3 of 12
        // selected" answers both questions - how much is here, and how much is spoken for -
        // and repeating the bare total beside it would say the same number twice.
        var detail = Caption(total, selected);

        return detail.Length > 0 ? $"{label} · {detail}" : $"{label} · {total}";
    }
}
