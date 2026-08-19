namespace Versio.Core.Git;

/// <summary>What a proposed merge would touch, and whether Versio can carry it safely.</summary>
public enum MergeVerdict
{
    /// <summary>Nothing tracked is involved. Ordinary git, ordinary merge.</summary>
    Proceed,

    /// <summary>The branch is already in this one; merging would do nothing.</summary>
    AlreadyMerged,

    /// <summary>
    /// The merge reaches a tracked project or its mirror, which Versio cannot yet finish
    /// safely.
    /// </summary>
    TouchesTrackedProject,
}

/// <summary>The verdict, and the sentence to put in front of the user.</summary>
public sealed record MergePlan
{
    public required MergeVerdict Verdict { get; init; }

    /// <summary>Why, in words somebody can act on. Empty when the answer is Proceed.</summary>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>The tracked projects the merge would reach, for naming them.</summary>
    public IReadOnlyList<string> AffectedProjects { get; init; } = [];

    public bool CanProceed => Verdict == MergeVerdict.Proceed;
}

/// <summary>
/// Whether merging a branch into the current one is something Versio can finish.
/// </summary>
/// <remarks>
/// <para>
/// <b>This refuses a case that git itself would handle, and the reason is measured rather than
/// cautious.</b> Probe P-M1, run on 2026-08-18 against two real exports of the fixture: a merge
/// that touches a tracked project conflicts on the <c>.project</c> — correctly, it is
/// <c>binary -diff -merge</c> — while the mirror beside it three-way merges <b>clean</b>. Pick
/// a side for the binary, as the panel's <i>Keep theirs</i> does, and the committed result is
/// one branch's project sitting beside a mirror carrying <i>both</i> branches' work. Measured:
/// 41 mirror files, two of them holding content the kept project does not contain — a POU
/// method body, and the manifest.
/// </para>
/// <para>
/// <b>Neither of the two things Versio could do next is right</b>, which is why this refuses
/// rather than choosing one:
/// </para>
/// <list type="bullet">
///   <item><b>Adopt the result</b>, as the existing upstream merge does through
///   <c>AdoptExternalStateAsync</c>: the disagreement is recorded as the synced truth, the
///   commit gate then sees nothing stale, and the mirror's extra work survives on disk until
///   the next genuine project edit exports over it.</item>
///   <item><b>Do not adopt</b>: the commit gate compares the project's hash against
///   <c>LastSynced.ProjectHash</c>, finds it changed, and exports — regenerating the mirror
///   from one branch's project and deleting the other branch's work immediately.</item>
/// </list>
/// <para>
/// <b>The right answer is the third one: import the merged mirror</b>, so the project is
/// regenerated from text that holds both sides and a person has reviewed. That needs the
/// direction lock pointed at import so the export gate cannot win the race first, and that is
/// a change to the lock model rather than to this panel. Until it exists, a merge that reaches
/// a tracked project is refused <i>with the reason</i>, which is a worse feature and not a
/// worse outcome: the alternative on offer is losing somebody's POU.
/// </para>
/// <para>
/// Merges that touch nothing tracked — documentation, tests, configuration, a second
/// application in the same repository — are unaffected and go through.
/// </para>
/// </remarks>
public static class MergePreflight
{
    /// <summary>
    /// Decide, from the paths the merge would bring and the roots Versio is tracking.
    /// </summary>
    /// <param name="branch">The branch being merged in, for the sentence.</param>
    /// <param name="incomingPaths">
    /// Repository-relative paths that differ between this branch and that one — what
    /// <c>git diff --name-only HEAD...branch</c> reports.
    /// </param>
    /// <param name="trackedRoots">
    /// One entry per tracked project: the project file's path and its mirror directory, both
    /// repository-relative, forward-slashed.
    /// </param>
    public static MergePlan For(
        string branch,
        IReadOnlyList<string> incomingPaths,
        IReadOnlyList<TrackedRoot> trackedRoots)
    {
        if (incomingPaths.Count == 0)
        {
            return new MergePlan
            {
                Verdict = MergeVerdict.AlreadyMerged,
                Explanation =
                    $"{branch} is already in this branch. There is nothing to merge.",
            };
        }

        var affected = trackedRoots
            .Where(root => incomingPaths.Any(root.Covers))
            .Select(root => root.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (affected.Count == 0)
        {
            return new MergePlan { Verdict = MergeVerdict.Proceed };
        }

        // Named, not counted. "This merge touches a PLC project" sends somebody looking; the
        // name tells them straight away whether it is the one they care about.
        var names = affected.Count == 1
            ? affected[0]
            : string.Join(", ", affected.Take(affected.Count - 1)) + " and " + affected[^1];

        return new MergePlan
        {
            Verdict = MergeVerdict.TouchesTrackedProject,
            AffectedProjects = affected,
            Explanation =
                $"Merging {branch} would change {names}, and Versio cannot finish that " +
                "safely yet. Git can merge the reviewable text, but the project file itself " +
                "is binary and only one side of it can be kept — which would leave the " +
                "project missing work the text still has. Merge this one in your git client " +
                "and re-open the project, or bring the change in as commits on top instead.",
        };
    }
}

/// <summary>One tracked project, as a pair of repository-relative roots.</summary>
/// <remarks>
/// The project file and its mirror are listed separately because a merge reaching <i>either</i>
/// is enough: a change to the mirror alone still ends up needing an import, and a change to the
/// project alone still ends up needing an export the merge cannot decide the order of.
/// </remarks>
public sealed record TrackedRoot
{
    /// <summary>What to call it in a sentence.</summary>
    public required string Name { get; init; }

    /// <summary>The project file, repository-relative, forward-slashed.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>The mirror directory, repository-relative, forward-slashed, no trailing slash.</summary>
    public required string MirrorPath { get; init; }

    /// <summary>True when this path is the project or lives inside its mirror.</summary>
    public bool Covers(string path)
    {
        if (string.Equals(path, ProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The separator is required, or a mirror at "src/App" would claim "src/AppNotes.md".
        return MirrorPath.Length > 0
            && path.StartsWith(MirrorPath + "/", StringComparison.OrdinalIgnoreCase);
    }
}
