namespace Versio.Core.Git;

/// <summary>What a proposed merge would touch, and whether Versio can carry it safely.</summary>
public enum MergeVerdict
{
    /// <summary>Nothing tracked is involved. Ordinary git, ordinary merge.</summary>
    Proceed,

    /// <summary>The branch is already in this one; merging would do nothing.</summary>
    AlreadyMerged,

    /// <summary>
    /// The merge reaches a tracked project or its mirror. Allowed, and the project must then
    /// be regenerated from the merged text by an import.
    /// </summary>
    /// <remarks>
    /// <b>This used to be a refusal, and the refusal was a stopgap.</b> It existed for exactly
    /// as long as Versio had no way to order the import ahead of the export gate: both of the
    /// behaviours available then lost work — see the remarks on <see cref="MergePreflight"/>.
    /// With <c>SyncOrchestrator.NoteMergedTextAsync</c> the mirror can be made authoritative
    /// before the gate is ever asked, so the merge is allowed and the import is required
    /// afterwards rather than the whole operation being turned away.
    /// </remarks>
    TouchesTrackedProject,

    /// <summary>
    /// Git could not say what the merge would bring, so nothing can be decided about it.
    /// </summary>
    /// <remarks>
    /// Distinct from every other answer on purpose. The first version had no such case: an
    /// unanswerable diff came back as an empty list, which is what "brings nothing" looks
    /// like — so a branch git cannot even resolve was reported as <i>already in this
    /// branch</i>. Refusing while claiming to know why is worse than refusing.
    /// </remarks>
    CannotTell,
}

/// <summary>The verdict, and the sentence to put in front of the user.</summary>
public sealed record MergePlan
{
    public required MergeVerdict Verdict { get; init; }

    /// <summary>Why, in words somebody can act on. Empty when the answer is Proceed.</summary>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>
    /// The tracked projects the merge would reach — to name them, and to mark them afterwards.
    /// </summary>
    /// <remarks>
    /// The roots rather than their names, because the caller needs the binding id to put the
    /// mirror in charge once the merge has landed. Two parallel lists would be two chances to
    /// mark the wrong project.
    /// </remarks>
    public IReadOnlyList<TrackedRoot> Affected { get; init; } = [];

    /// <summary>
    /// True when the merge may run — including the case that needs an import afterwards.
    /// </summary>
    /// <remarks>
    /// <b>Not the same as "nothing to do afterwards".</b> A merge reaching a tracked project
    /// proceeds and leaves the mirror authoritative; the caller has to act on
    /// <see cref="NeedsImport"/> or the project silently stays a branch behind its text.
    /// </remarks>
    public bool CanProceed =>
        Verdict is MergeVerdict.Proceed or MergeVerdict.TouchesTrackedProject;

    /// <summary>The merge will leave the mirror holding work the project does not have.</summary>
    public bool NeedsImport => Verdict == MergeVerdict.TouchesTrackedProject;
}

/// <summary>
/// Whether merging a branch into the current one is something Versio can finish, and what it
/// owes afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <b>The case this exists for is measured.</b> Probe P-M1, run on 2026-08-18 against two real
/// exports of the fixture: a merge touching a tracked project conflicts on the <c>.project</c> —
/// correctly, it is <c>binary -diff -merge</c> — while the mirror beside it three-way merges
/// <b>clean</b>. Pick a side for the binary, as the panel's <i>Keep theirs</i> does, and the
/// committed result is one branch's project sitting beside a mirror carrying <i>both</i>
/// branches' work: 41 mirror files, two of them holding content the kept project does not
/// contain — a POU method body, and the manifest.
/// </para>
/// <para>
/// <b>Two of the three things Versio could do next lose work</b>, which is why the third is not
/// optional:
/// </para>
/// <list type="bullet">
///   <item><b>Adopt the result</b>, as the upstream merge does through
///   <c>AdoptExternalStateAsync</c>: the disagreement is recorded as the synced truth, the
///   commit gate then sees nothing stale, and the mirror's extra work survives on disk only
///   until the next genuine project edit exports over it.</item>
///   <item><b>Do nothing</b>: the commit gate compares the project's hash against
///   <c>LastSynced.ProjectHash</c>, finds it changed, and exports — regenerating the mirror from
///   one branch's project and deleting the other branch's work immediately.</item>
///   <item><b>Put the text in charge and import it.</b> The project is regenerated from text
///   holding both sides, after a person has reviewed it. This is what happens.</item>
/// </list>
/// <para>
/// <b>The ordering is what makes the third one work</b>, and it is not this class's doing:
/// <c>SyncOrchestrator.NoteMergedTextAsync</c> moves the binding to
/// <c>TextAuthoritative</c>, from which the state machine refuses <c>ProjectChanged</c> — and
/// <c>AttemptAsync</c> asks that <i>before</i> running the toolchain, so the gate's export is
/// turned away without the mirror being touched. What this class decides is only <b>which</b>
/// projects that applies to.
/// </para>
/// <para>
/// Merges touching nothing tracked — documentation, tests, configuration, a second application
/// in the same repository — are ordinary git and owe nothing afterwards.
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
    /// <c>git diff --name-only HEAD...branch</c> reports. <b>Null when git could not answer</b>,
    /// which is a different thing from an empty list and must not be flattened into one.
    /// </param>
    /// <param name="trackedRoots">
    /// One entry per tracked project: the project file's path and its mirror directory, both
    /// repository-relative, forward-slashed.
    /// </param>
    public static MergePlan For(
        string branch,
        IReadOnlyList<string>? incomingPaths,
        IReadOnlyList<TrackedRoot> trackedRoots)
    {
        if (incomingPaths is null)
        {
            return new MergePlan
            {
                Verdict = MergeVerdict.CannotTell,
                Explanation =
                    $"Git could not work out what merging {branch} would bring, so Versio " +
                    "will not guess. The usual cause is a branch that only exists on the " +
                    "remote: fetch it and check it out once, and it can be merged from then on.",
            };
        }

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
            .ToList();

        if (affected.Count == 0)
        {
            return new MergePlan { Verdict = MergeVerdict.Proceed };
        }

        // Named, not counted. "This merge touches a PLC project" sends somebody looking; the
        // name tells them straight away whether it is the one they care about.
        var titles = affected.Select(a => a.Name).ToList();
        var names = titles.Count == 1
            ? titles[0]
            : string.Join(", ", titles.Take(titles.Count - 1)) + " and " + titles[^1];

        return new MergePlan
        {
            Verdict = MergeVerdict.TouchesTrackedProject,
            Affected = affected,
            Explanation =
                $"This merge changes {names}. Git can merge the reviewable text, but the " +
                "project file itself is binary and only one side of it can be kept — so once " +
                "the merge lands, the text holds work the project does not. Versio will put " +
                "the text in charge and ask you to import it, which is what puts both sides " +
                "back into the project. Until you do, exports are held off so nothing " +
                "overwrites the merged text.",
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
    /// <summary>Which binding tracks it — the identity, so the caller can act on it.</summary>
    public required string BindingId { get; init; }

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
