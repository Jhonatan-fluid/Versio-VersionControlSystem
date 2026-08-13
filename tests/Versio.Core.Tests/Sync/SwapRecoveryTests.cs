using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// Reading what a killed export left beside a mirror.
/// </summary>
/// <remarks>
/// <para>
/// The export swaps in three renames — mirror to <c>.versio_trash</c>, staging to mirror,
/// delete trash — so that no instant exists in which the mirror is absent. Being killed
/// between any two of them leaves a different combination on disk, and exactly one of those
/// combinations is dangerous.
/// </para>
/// <para>
/// <b>The obvious implementation is the bug.</b> "Clear anything left over" destroys the last
/// copy of the mirror in precisely the case the swap was built to survive, and it would do it
/// quietly, at startup, to a repository the user had not touched.
/// </para>
/// </remarks>
public sealed class SwapRecoveryTests
{
    [Fact]
    public void A_finished_export_leaves_nothing_to_do()
    {
        var verdict = SwapRecovery.Decide(mirrorExists: true, stagingExists: false, trashExists: false);

        Assert.Equal(SwapAction.Nothing, verdict.Action);
        Assert.Equal(string.Empty, verdict.Explanation);
    }

    [Fact]
    public void Killed_before_the_swap_leaves_a_partial_tree_worth_nothing()
    {
        // The staging tree is a partial export by definition - the swap is what declares it
        // finished - so there is nothing in it worth the confusion of leaving it where
        // "stage all" can find it.
        var verdict = SwapRecovery.Decide(mirrorExists: true, stagingExists: true, trashExists: false);

        Assert.Equal(SwapAction.DeleteStaging, verdict.Action);
        Assert.Contains("partial export", verdict.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Killed_between_the_renames_means_the_trash_is_the_only_copy()
    {
        // THE case. The mirror is gone, and what is in the trash directory is the last
        // complete export in existence. A sweep that deleted it would do the damage the
        // three-rename swap exists to prevent.
        var verdict = SwapRecovery.Decide(mirrorExists: false, stagingExists: false, trashExists: true);

        Assert.Equal(SwapAction.RestoreTrash, verdict.Action);
        Assert.Contains("last complete export", verdict.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Killed_after_the_swap_leaves_only_the_tidy_up()
    {
        // The new mirror is in place and correct; the old one simply outlived it.
        var verdict = SwapRecovery.Decide(mirrorExists: true, stagingExists: false, trashExists: true);

        Assert.Equal(SwapAction.DeleteTrash, verdict.Action);
        Assert.Contains("intact", verdict.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_mirror_outranks_everything_else_on_disk()
    {
        // A run killed between the renames of one export, with a staging tree from an earlier
        // killed run still beside it. Restoring the mirror comes first; the staging tree is
        // cleared by the next export, which is exactly what it is for.
        var verdict = SwapRecovery.Decide(mirrorExists: false, stagingExists: true, trashExists: true);

        Assert.Equal(SwapAction.RestoreTrash, verdict.Action);
    }

    [Fact]
    public void A_project_that_has_never_been_exported_is_not_a_casualty()
    {
        // No mirror and no leftovers is the ordinary state of a tracked project nobody has
        // exported yet. Reading it as a disaster would put a warning in front of somebody on
        // the day they adopt their first project.
        var verdict = SwapRecovery.Decide(mirrorExists: false, stagingExists: false, trashExists: false);

        Assert.Equal(SwapAction.Nothing, verdict.Action);
    }

    [Fact]
    public void A_missing_mirror_with_only_a_staging_tree_is_not_restorable()
    {
        // Killed before the first rename, having deleted nothing: there is no previous export
        // to put back, and the staging tree is not one. Clearing it and re-exporting is the
        // only honest answer.
        var verdict = SwapRecovery.Decide(mirrorExists: false, stagingExists: true, trashExists: false);

        Assert.Equal(SwapAction.DeleteStaging, verdict.Action);
    }

    [Fact]
    public void Every_verdict_that_does_something_explains_itself()
    {
        // The explanation is the log line, and every one of these means a previous run of the
        // app was killed while writing - which somebody will want to read about later.
        foreach (var mirror in new[] { true, false })
        {
            foreach (var staging in new[] { true, false })
            {
                foreach (var trash in new[] { true, false })
                {
                    var verdict = SwapRecovery.Decide(mirror, staging, trash);
                    if (verdict.Action != SwapAction.Nothing)
                    {
                        Assert.NotEqual(string.Empty, verdict.Explanation);
                    }
                }
            }
        }
    }
}
