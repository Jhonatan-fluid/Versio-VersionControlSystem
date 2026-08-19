using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// Whether merging a branch into the current one is something Versio can finish, and what it
/// owes afterwards.
/// </summary>
/// <remarks>
/// What this encodes is measured, not cautious — probe P-M1 committed a project holding one
/// branch's work beside a mirror holding both. The merge is allowed; what the measurement
/// decides is which projects must then be regenerated from the merged text. See
/// <see cref="MergePreflight"/> for the whole argument.
/// </remarks>
public sealed class MergePreflightTests
{
    private static readonly TrackedRoot Refrigerator = new()
    {
        BindingId = "6d3f8855e205",
        Name = "RefrigeratorControl",
        ProjectPath = "fixtures/projects/RefrigeratorControl.project",
        MirrorPath = "fixtures/projects/RefrigeratorControl",
    };

    [Fact]
    public void A_merge_that_touches_nothing_tracked_goes_through()
    {
        var plan = MergePreflight.For(
            "feature/docs",
            ["README.md", "docs/STATUS.md"],
            [Refrigerator]);

        Assert.Equal(MergeVerdict.Proceed, plan.Verdict);
        Assert.True(plan.CanProceed);

        // Nothing tracked, so nothing owed afterwards. The distinction matters: CanProceed
        // alone no longer means "and there is nothing to do".
        Assert.False(plan.NeedsImport);
    }

    [Fact]
    public void A_merge_reaching_the_project_file_is_allowed_and_owes_an_import()
    {
        var plan = MergePreflight.For(
            "feature/alarms",
            ["fixtures/projects/RefrigeratorControl.project"],
            [Refrigerator]);

        Assert.Equal(MergeVerdict.TouchesTrackedProject, plan.Verdict);

        // ALLOWED, AND OWING AN IMPORT. This was a refusal while Versio had no way to order
        // the import ahead of the export gate; NoteMergedTextAsync is that way, so the merge
        // runs and the mirror is put in charge afterwards.
        Assert.True(plan.CanProceed);
        Assert.True(plan.NeedsImport);

        // Named rather than counted: the name is what tells somebody whether it is the project
        // they care about, without going to look. The binding id comes with it, because the
        // caller has to mark that exact project once the merge lands.
        Assert.Contains("RefrigeratorControl", plan.Explanation);
        Assert.Equal(["RefrigeratorControl"], plan.Affected.Select(a => a.Name));
        Assert.Equal(["6d3f8855e205"], plan.Affected.Select(a => a.BindingId));
    }

    [Fact]
    public void A_merge_reaching_only_the_mirror_owes_one_too()
    {
        // The mirror alone is enough. Merged text still has to reach the project through an
        // import - the project has not changed yet, and the next export would overwrite the
        // merged text with what the project still says.
        var plan = MergePreflight.For(
            "feature/alarms",
            ["fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/PLC_PRG.st"],
            [Refrigerator]);

        Assert.Equal(MergeVerdict.TouchesTrackedProject, plan.Verdict);
        Assert.True(plan.NeedsImport);
    }

    [Fact]
    public void A_sibling_path_sharing_the_mirrors_prefix_is_not_the_mirror()
    {
        // "fixtures/projects/RefrigeratorControl" must not claim
        // "fixtures/projects/RefrigeratorControlNotes.md". The separator is what stops it, and
        // a StartsWith without one is the classic way this goes wrong.
        var plan = MergePreflight.For(
            "feature/docs",
            ["fixtures/projects/RefrigeratorControlNotes.md"],
            [Refrigerator]);

        Assert.Equal(MergeVerdict.Proceed, plan.Verdict);
    }

    [Fact]
    public void Nothing_incoming_means_the_branch_is_already_in()
    {
        // Distinct from Proceed on purpose: running a merge that can do nothing produces a
        // confusing "Already up to date" and a refresh for no reason.
        var plan = MergePreflight.For("feature/done", [], [Refrigerator]);

        Assert.Equal(MergeVerdict.AlreadyMerged, plan.Verdict);
        Assert.False(plan.CanProceed);
        Assert.Contains("already in this branch", plan.Explanation);
    }

    [Fact]
    public void Every_affected_project_is_named_not_just_the_first()
    {
        var second = new TrackedRoot
        {
            BindingId = "aa11bb22cc33",
            Name = "MachineWithMembers",
            ProjectPath = "fixtures/projects/MachineWithMembers.project",
            MirrorPath = "fixtures/projects/MachineWithMembers",
        };

        var plan = MergePreflight.For(
            "feature/both",
            [
                "fixtures/projects/RefrigeratorControl.project",
                "fixtures/projects/MachineWithMembers/Device/x.st",
            ],
            [Refrigerator, second]);

        Assert.Equal(2, plan.Affected.Count);
        Assert.Contains("RefrigeratorControl", plan.Explanation);
        Assert.Contains("MachineWithMembers", plan.Explanation);
    }

    [Fact]
    public void An_unanswerable_diff_is_not_an_empty_one()
    {
        // THE DEFECT THIS CLOSES, FOUND ON A SECOND READ. IncomingPathsAsync returned [] when
        // git could not resolve the branch at all - a remote-only name, which has no local ref
        // - and [] is what "brings nothing" looks like. So the preflight answered "already in
        // this branch" about a branch it had never managed to look at: refusing while
        // confidently stating the wrong reason, which is worse than refusing.
        var plan = MergePreflight.For("origin-only", incomingPaths: null, [Refrigerator]);

        Assert.Equal(MergeVerdict.CannotTell, plan.Verdict);
        Assert.False(plan.CanProceed);

        Assert.DoesNotContain("already in", plan.Explanation);
        Assert.Contains("remote", plan.Explanation);
    }

    [Fact]
    public void Nothing_incoming_still_means_already_merged()
    {
        // The other side of the distinction: an EMPTY list is a real answer and keeps its own
        // verdict. If these two ever collapse back into one, one of these two tests fails.
        Assert.Equal(MergeVerdict.AlreadyMerged,
                     MergePreflight.For("done", [], [Refrigerator]).Verdict);
    }

    [Fact]
    public void A_repository_tracking_nothing_never_refuses()
    {
        // Versio on a plain repository is still a git client, and every merge in it is
        // ordinary git. Refusing there would be inventing a restriction out of nothing.
        var plan = MergePreflight.For(
            "feature/x",
            ["src/Program.cs", "anything/at/all.project"],
            []);

        Assert.Equal(MergeVerdict.Proceed, plan.Verdict);
    }
}
