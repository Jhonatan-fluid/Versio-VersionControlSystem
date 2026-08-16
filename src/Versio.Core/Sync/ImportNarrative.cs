using Versio.Core.Plc;

namespace Versio.Core.Sync;

/// <summary>
/// Turns an import plan into the sentences somebody reads before agreeing to it.
/// </summary>
/// <remarks>
/// <para>
/// In Core beside <see cref="SyncNarrative"/> and for the same reason: this is the wording on
/// the one dialog that precedes writing into a binary file nobody can diff afterwards, so it
/// is part of the feature rather than a presentation detail, and it is asserted by tests
/// rather than eyeballed.
/// </para>
/// <para>
/// <b>Only about what this version actually does — and it has been wrong about that once.</b>
/// This said objects <i>will stay</i> while the toolchain removed them, for the one commit
/// between the executing side learning to delete and anybody noticing. That is the exact lie
/// the whole confirmation chain exists to prevent, arrived at from the other direction: every
/// gate around it worked and the sentence it delivered was false.
/// </para>
/// <para>
/// <b>Vendor-neutral, and it has to be.</b> Which deletions can be checked is a question only
/// a vendor's planner can answer — the forms, the hashes and the toolchain's own limits are
/// all its business. So this reads <see cref="PlannedDeletion.CannotVerify"/> as opaque prose
/// and never inspects a form. A second vendor changes nothing in this file, which is the whole
/// point of <c>Sync/</c> being on the neutral side of the seam.
/// </para>
/// <para>
/// <b>Two sentences, because there are two outcomes.</b> A deletion the executing side can
/// check will really happen; one it cannot is really refused, and the split is
/// <see cref="PlannedDeletion.CannotVerify"/>. Merging them would be wrong about one group
/// whichever tense it picked — and re-wording alone would have swapped one lie for another,
/// since the script refuses any deletion it cannot verify.
/// </para>
/// <para>
/// <b>The tests assert both sides on purpose.</b> The pair that failed to catch the original
/// defect asserted only that this said <i>will stay</i> and did <i>not</i> say <i>will be
/// removed</i> — assertions on the old sentence, which cannot fail when the behaviour moves
/// and can only fail when somebody fixes the wording. A tripwire that points one way is a
/// lock.
/// </para>
/// <para>
/// <b>Deletions are named in full, with no cap.</b> That is a decision taken deliberately in
/// the import plan: empty the mirror and the dialog lists every object. A threshold would let
/// the most destructive case be the one that says least.
/// </para>
/// </remarks>
public static class ImportNarrative
{
    /// <summary>
    /// What the plan means, as lines to sit under the dialog's opening sentence. Empty when
    /// there is nothing to add beyond what the caller already said.
    /// </summary>
    /// <remarks>
    /// A null plan produces nothing rather than an apology: it means the plan could not be
    /// computed — an unknown binding, or no format claiming the project — and the import
    /// refuses or replans on its own. Explaining that here would put a mechanism in front of
    /// somebody who asked a question about their project.
    /// </remarks>
    public static string Describe(ImportPlan? plan)
    {
        if (plan is null)
        {
            return string.Empty;
        }

        List<string> lines = [];

        var applied = plan.Files.Count(f => f.Action != PlannedAction.Skip);
        var created = plan.Files.Count(f => f.Action == PlannedAction.Create);

        if (applied > 0)
        {
            lines.Add(created > 0
                ? $"{Files(applied)} will be applied, including {created} new "
                  + $"object{(created == 1 ? string.Empty : "s")}."
                : $"{Files(applied)} will be applied.");
        }

        // Two sentences, because there are two outcomes and a dialog that merged them would be
        // wrong about one of them. Named in the plan's own order — which is sorted, so two
        // readings of the same repository list them the same way round.
        var removing = plan.Deletions.Where(d => d.CannotVerify is null).ToList();
        if (removing.Count > 0)
        {
            lines.Add(
                $"{Objects(removing.Count)} no longer {Has(removing.Count)} any exported file "
                + "and will be removed from the project: "
                + $"{string.Join(", ", removing.Select(d => d.Name))}. "
                + "This cannot be undone from inside Versio. An object that has been edited in "
                + "the IDE since the last export is kept instead, and the import says so.");
        }

        // Grouped by reason rather than lumped under one, because the reasons are not
        // interchangeable: "the export recorded no comparable hash" is fixed by exporting
        // again, and a form Versio cannot rebuild is not fixed by anything the reader can do.
        // Telling somebody to re-export when that will not help is its own small lie.
        foreach (var group in plan.Deletions
                     .Where(d => d.CannotVerify is not null)
                     .GroupBy(d => d.CannotVerify!, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var kept = group.ToList();
            lines.Add(
                $"{Objects(kept.Count)} no longer {Has(kept.Count)} any exported file but "
                + $"will stay in the project: {string.Join(", ", kept.Select(d => d.Name))}. "
                + $"{group.Key}. Delete {(kept.Count == 1 ? "it" : "them")} in the IDE if that "
                + "is what you want.");
        }

        if (plan.StructuralChangesUnavailable is not null)
        {
            // Deliberately not the internal sentence, which names a schema number. What
            // somebody needs here is that one class of change is off the table and why.
            lines.Add(
                "The exported text has no manifest this version can trust, so files that have "
                + "gone from it cannot be told apart from files it never recorded. Content is "
                + "still applied; nothing structural is.");
        }

        // The planner's own sentences, last. Each is something it decided or refused to
        // decide — a created object taking its declaration's name over its file's, a stray
        // file skipped, an object it will not delete and why. They exist to be read by a
        // person, and the log is the wrong place to read them for the first time when the
        // question being answered is "should I press Import".
        lines.AddRange(plan.Warnings);

        return string.Join("\n\n", lines);
    }

    private static string Files(int n) => n == 1 ? "1 file" : $"{n} files";

    private static string Objects(int n) => n == 1 ? "1 object" : $"{n} objects";

    private static string Has(int n) => n == 1 ? "has" : "have";
}
