namespace Versio.Core.Sync;

/// <summary>What to do about the directories an export leaves behind when it is killed.</summary>
public enum SwapAction
{
    /// <summary>Nothing to do: no leftovers.</summary>
    Nothing,

    /// <summary>A run died before the swap. The half-built tree is worthless; delete it.</summary>
    DeleteStaging,

    /// <summary>The swap completed and only the tidy-up was interrupted. Delete the old tree.</summary>
    DeleteTrash,

    /// <summary>
    /// The swap died between the two renames and the mirror is <b>missing</b>. The trash is
    /// the previous export and the only copy there is: put it back.
    /// </summary>
    RestoreTrash,
}

/// <summary>One decision, with the sentence that goes in the log.</summary>
public sealed record SwapVerdict(SwapAction Action, string Explanation)
{
    public static readonly SwapVerdict Nothing = new(SwapAction.Nothing, string.Empty);
}

/// <summary>
/// Reads what a killed export left beside a mirror and decides what it means.
/// </summary>
/// <remarks>
/// <para>
/// The export swaps in three renames — mirror to <c>.versio_trash</c>, staging to mirror,
/// delete trash — so that no instant exists in which the mirror is absent. Each gap between
/// them leaves a different combination on disk, and <b>only one of the four is dangerous</b>:
/// a trash directory with no mirror beside it means the process died between the first and
/// second rename, and the trash is then the only copy of the export in existence.
/// </para>
/// <para>
/// <b>That case is why this is a decision and not a delete loop.</b> The obvious sweep — "clear
/// anything left over" — would in exactly that case destroy the last copy of the mirror, which
/// is the outcome the three-rename swap was built to prevent.
/// </para>
/// <para>
/// Pure, so every combination is a test rather than a filesystem to arrange.
/// </para>
/// </remarks>
public static class SwapRecovery
{
    /// <param name="mirrorExists">The export directory itself.</param>
    /// <param name="stagingExists"><c>&lt;mirror&gt;.versio_staging</c>.</param>
    /// <param name="trashExists"><c>&lt;mirror&gt;.versio_trash</c>.</param>
    public static SwapVerdict Decide(bool mirrorExists, bool stagingExists, bool trashExists)
    {
        // Ordered by how much is at stake, not by how the directories are laid out. A mirror
        // that is missing outranks everything else that could be true at the same time.
        if (trashExists && !mirrorExists)
        {
            return new SwapVerdict(
                SwapAction.RestoreTrash,
                "the export directory is missing and a trash directory is beside it: a " +
                "previous export was killed midway through replacing it, so the trash is " +
                "the last complete export and is being put back");
        }

        if (trashExists)
        {
            return new SwapVerdict(
                SwapAction.DeleteTrash,
                "a trash directory was left behind by an export that was killed after it had " +
                "already swapped the new tree into place; the export itself is intact");
        }

        if (stagingExists)
        {
            // Deleted rather than kept. A staging tree is a partial export by definition -
            // the swap is what declares it finished - so there is nothing in it worth the
            // confusion of leaving it where "stage all" can find it.
            return new SwapVerdict(
                SwapAction.DeleteStaging,
                "a staging directory was left behind by an export that was killed before it " +
                "finished; it holds a partial export and is being cleared");
        }

        return SwapVerdict.Nothing;
    }
}
