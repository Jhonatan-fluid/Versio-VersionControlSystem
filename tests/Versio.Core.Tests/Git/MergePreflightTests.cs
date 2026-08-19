using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// Whether merging a branch into the current one is something Versio can finish.
/// </summary>
/// <remarks>
/// The refusal this encodes is measured, not cautious — probe P-M1 committed a project holding
/// one branch's work beside a mirror holding both, and neither of the two things Versio could
/// do next preserves the difference. See <see cref="MergePreflight"/> for the whole argument.
/// </remarks>
public sealed class MergePreflightTests
{
    private static readonly TrackedRoot Refrigerator = new()
    {
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
    }

    [Fact]
    public void A_merge_reaching_the_project_file_is_refused_by_name()
    {
        var plan = MergePreflight.For(
            "feature/alarms",
            ["fixtures/projects/RefrigeratorControl.project"],
            [Refrigerator]);

        Assert.Equal(MergeVerdict.TouchesTrackedProject, plan.Verdict);
        Assert.False(plan.CanProceed);

        // Named rather than counted: the name is what tells somebody whether it is the project
        // they care about, without going to look.
        Assert.Contains("RefrigeratorControl", plan.Explanation);
        Assert.Equal(["RefrigeratorControl"], plan.AffectedProjects);
    }

    [Fact]
    public void A_merge_reaching_only_the_mirror_is_refused_too()
    {
        // The mirror alone is enough. Merged text still has to reach the project through an
        // import, and that is the half Versio cannot yet order safely against the export gate.
        var plan = MergePreflight.For(
            "feature/alarms",
            ["fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/PLC_PRG.st"],
            [Refrigerator]);

        Assert.Equal(MergeVerdict.TouchesTrackedProject, plan.Verdict);
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

        Assert.Equal(2, plan.AffectedProjects.Count);
        Assert.Contains("RefrigeratorControl", plan.Explanation);
        Assert.Contains("MachineWithMembers", plan.Explanation);
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
