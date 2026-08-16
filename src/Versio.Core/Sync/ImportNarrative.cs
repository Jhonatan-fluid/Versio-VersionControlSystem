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
/// <b>Present tense, and only about what this version actually does.</b> Deletions are planned
/// and executed by nothing today, so this says objects <i>will stay</i> — not that they will be
/// removed. Naming three objects as doomed and then leaving them there is the same class of
/// lie as the dialog this whole chain exists to make truthful, pointed the other way. When the
/// script grows <c>remove()</c>, this wording changes with it, and the test below is what says
/// so.
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

        if (plan.Deletions.Count > 0)
        {
            // Named, and in the plan's own order — which is sorted, so two readings of the
            // same repository list them the same way round.
            var names = string.Join(", ", plan.Deletions.Select(d => d.Name));

            lines.Add(
                $"{Objects(plan.Deletions.Count)} no longer {(plan.Deletions.Count == 1 ? "has" : "have")} "
                + $"any exported file: {names}. This version does not remove objects from the "
                + "project, so they will stay — delete them in the IDE if that is what you want.");
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
}
