using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// Which branches exist where, which one is the trunk, and what a delete is about to do.
/// </summary>
/// <remarks>
/// The wording is asserted throughout, because here it is the last thing standing between
/// somebody and a branch that is gone. A confirmation saying "12 branches" without naming them
/// is not a confirmation.
/// </remarks>
public sealed class BranchDeletionTests
{
    private static BranchInfo Local(string name, bool current = false) => new()
    {
        Name = name,
        IsCurrent = current,
    };

    // ------------------------------------------------------------------ inventory

    [Fact]
    public void A_remote_only_branch_appears_at_all()
    {
        // It did not, before this. BranchesAsync read refs/heads, so a colleague's branch was
        // invisible and the dropdown looked like the whole repository while being half of it.
        var combined = BranchInventory.Combine([Local("main")], ["origin/main", "origin/theirs"]);

        Assert.Equal(2, combined.Count);
        Assert.Contains(combined, b => b.Name == "theirs" && b.IsRemoteOnly);
    }

    [Fact]
    public void A_branch_on_both_sides_is_one_row()
    {
        var combined = BranchInventory.Combine([Local("main")], ["origin/main"]);

        Assert.Single(combined);
        Assert.Equal(BranchExistence.Both, combined[0].Existence);
    }

    [Fact]
    public void A_branch_with_no_remote_copy_says_so()
    {
        var combined = BranchInventory.Combine([Local("scratch")], []);

        Assert.True(combined[0].IsLocalOnly);
    }

    [Fact]
    public void The_remotes_own_HEAD_is_not_a_branch()
    {
        // A symbolic ref naming the remote's default. Offering it would be a checkout of an
        // alias for something already in the list.
        var combined = BranchInventory.Combine([Local("main")], ["origin/HEAD", "origin/main"]);

        Assert.Single(combined);
        Assert.Equal("main", combined[0].Name);
    }

    [Fact]
    public void A_branch_name_containing_a_slash_survives()
    {
        var combined = BranchInventory.Combine([], ["origin/release/2026"]);

        Assert.Equal("release/2026", combined[0].Name);
    }

    /// <summary>
    /// The same name on two remotes is still remote-only here.
    /// </summary>
    /// <remarks>
    /// <c>origin/topic</c> inserted a remote-only row, and <c>upstream/topic</c> then found
    /// that row by name and marked it Both without asking whether it had come from a local
    /// branch. BranchDeletion.HasLocal then ran <c>git branch -d topic</c> on a branch this
    /// clone never had, which failed and skipped the remote half of the delete. The
    /// 2026-10-24 review's item 39.
    /// </remarks>
    [Fact]
    public void The_same_name_on_two_remotes_is_one_remote_only_row()
    {
        var combined = BranchInventory.Combine([], ["origin/topic", "upstream/topic"]);

        var topic = Assert.Single(combined);
        Assert.Equal("topic", topic.Name);
        Assert.Equal(BranchExistence.RemoteOnly, topic.Existence);
        Assert.Equal("origin/topic", topic.RemoteRef);
    }

    [Fact]
    public void A_local_branch_seen_on_two_remotes_is_still_both()
    {
        var combined = BranchInventory.Combine([Local("topic")], ["origin/topic", "upstream/topic"]);

        Assert.Equal(BranchExistence.Both, Assert.Single(combined).Existence);
    }

    [Fact]
    public void The_current_branch_leads_and_remote_only_branches_sort_last()
    {
        // A busy remote carries hundreds of branches nobody here has checked out, and they must
        // not bury the three somebody works on.
        var combined = BranchInventory.Combine(
            [Local("zeta"), Local("alpha", current: true)],
            ["origin/aaa-theirs"]);

        Assert.Equal("alpha", combined[0].Name);
        Assert.Equal("zeta", combined[1].Name);
        Assert.Equal("aaa-theirs", combined[2].Name);
    }

    // ------------------------------------------------------------------ default branch

    [Fact]
    public void Configuration_wins_over_everything()
    {
        // A team whose trunk is `develop` records it once and every clone agrees.
        var resolved = DefaultBranch.Resolve("develop", "refs/remotes/origin/main", ["main"]);

        Assert.Equal(DefaultBranchSource.Configured, resolved.Source);
        Assert.Equal("develop", resolved.Name);
    }

    [Theory]
    [InlineData("refs/remotes/origin/main", "main")]
    [InlineData("origin/main", "main")]
    [InlineData("refs/remotes/origin/release/2026", "release/2026")]
    public void The_remotes_head_is_read_in_whatever_shape_git_gives_it(string head, string expected)
    {
        // symbolic-ref answers one way and other commands another, and a branch name may itself
        // contain slashes — so taking the last segment would answer "2026".
        var resolved = DefaultBranch.Resolve(null, head, []);

        Assert.Equal(DefaultBranchSource.Remote, resolved.Source);
        Assert.Equal(expected, resolved.Name);
    }

    [Fact]
    public void Main_is_preferred_to_master_when_both_exist()
    {
        // A repository carrying both almost always renamed one and kept the other.
        var resolved = DefaultBranch.Resolve(null, null, ["master", "main", "other"]);

        Assert.Equal(DefaultBranchSource.Conventional, resolved.Source);
        Assert.Equal("main", resolved.Name);
    }

    [Fact]
    public void Master_still_counts_when_it_is_the_only_one()
    {
        Assert.Equal("master", DefaultBranch.Resolve(null, null, ["master"]).Name);
    }

    [Fact]
    public void Nothing_resolvable_refuses_rather_than_guessing()
    {
        // THE IMPORTANT RUNG. Guessing the trunk and then checking it out moves somebody's
        // working tree to a branch nobody chose.
        var resolved = DefaultBranch.Resolve(null, null, ["feature/a", "feature/b"]);

        Assert.False(resolved.IsKnown);
        Assert.Equal(DefaultBranchSource.Unknown, resolved.Source);
        Assert.Contains(".versio/repo.json", resolved.BlockedReason);
    }

    [Fact]
    public void A_known_default_blocks_nothing()
    {
        Assert.Equal(string.Empty, DefaultBranch.Resolve("main", null, []).BlockedReason);
    }

    [Fact]
    public void Whitespace_is_not_a_configured_branch()
    {
        Assert.False(DefaultBranch.Resolve("   ", null, []).IsKnown);
    }

    // ------------------------------------------------------------------ the delete plan

    private static BranchCandidate Candidate(
        string name, BranchExistence existence = BranchExistence.Both,
        bool merged = true, bool current = false, bool isDefault = false) => new()
    {
        Name = name,
        Existence = existence,
        IsMerged = merged,
        IsCurrent = current,
        IsDefault = isDefault,
    };

    private static DeletePlan Plan(
        IReadOnlyList<BranchCandidate> selected,
        DeleteScope scope = DeleteScope.Local,
        bool acknowledged = false) => new()
    {
        Selected = selected,
        Scope = scope,
        ForceAcknowledged = acknowledged,
    };

    [Fact]
    public void The_default_branch_cannot_be_chosen()
    {
        // It is the fallback the current-branch case checks out. Deleting it has no recovery
        // path inside the application.
        var main = Candidate("main", isDefault: true);

        Assert.False(main.CanSelect);
        Assert.Contains("main branch", main.Blocked);
    }

    [Fact]
    public void Nothing_selected_cannot_proceed()
    {
        var plan = Plan([]);

        Assert.False(plan.CanProceed);
        Assert.Contains("at least one", plan.BlockedReason);
    }

    [Fact]
    public void A_merged_branch_needs_no_acknowledgement()
    {
        Assert.True(Plan([Candidate("tidy")]).CanProceed);
    }

    [Fact]
    public void An_unmerged_branch_needs_one()
    {
        // git refuses an unmerged branch on its own account, and that refusal is information.
        // Escalating -d to -D quietly is how somebody loses an afternoon.
        var plan = Plan([Candidate("feature/x", merged: false)]);

        Assert.False(plan.CanProceed);
        Assert.True(plan.HasUnmerged);
        Assert.Contains("feature/x is not fully merged", plan.BlockedReason);
    }

    [Fact]
    public void Acknowledging_lets_it_through()
    {
        Assert.True(Plan([Candidate("feature/x", merged: false)], acknowledged: true).CanProceed);
    }

    [Fact]
    public void Several_unmerged_branches_are_counted_rather_than_listed_in_the_reason()
    {
        var plan = Plan([
            Candidate("a", merged: false),
            Candidate("b", merged: false),
            Candidate("c"),
        ]);

        Assert.Contains("2 of them are not fully merged", plan.BlockedReason);
    }

    [Fact]
    public void The_summary_breaks_the_selection_down_by_where_it_lives()
    {
        var plan = Plan([
            Candidate("a"),
            Candidate("b"),
            Candidate("c", BranchExistence.LocalOnly),
        ]);

        Assert.Contains("3 branches selected", plan.Summary);
        Assert.Contains("2 local + remote", plan.Summary);
        Assert.Contains("1 local only", plan.Summary);
    }

    [Fact]
    public void One_branch_reads_as_one()
    {
        Assert.StartsWith("1 branch selected", Plan([Candidate("a")]).Summary);
    }

    [Fact]
    public void The_summary_warns_about_unmerged_work()
    {
        Assert.Contains("not merged", Plan([Candidate("a", merged: false)]).Summary);
    }

    [Fact]
    public void An_empty_selection_says_nothing()
    {
        Assert.Equal(string.Empty, Plan([]).Summary);
    }

    [Fact]
    public void A_local_only_delete_does_not_touch_the_remote()
    {
        Assert.False(Plan([Candidate("a")], DeleteScope.Local).TouchesRemote);
    }

    [Fact]
    public void A_remote_delete_is_recognised_only_when_there_is_a_remote_copy()
    {
        Assert.True(Plan([Candidate("a")], DeleteScope.LocalAndRemote).TouchesRemote);
        Assert.False(Plan([Candidate("a", BranchExistence.LocalOnly)],
                          DeleteScope.LocalAndRemote).TouchesRemote);
    }

    [Fact]
    public void The_remote_confirmation_names_the_branches_and_the_remote()
    {
        var plan = Plan([Candidate("feature/a"), Candidate("feature/b")],
                        DeleteScope.LocalAndRemote);

        var text = plan.RemoteConfirmation("origin");

        Assert.Contains("feature/a", text);
        Assert.Contains("feature/b", text);
        Assert.Contains("origin", text);

        // The one operation here whose consequences land on somebody not in the room.
        Assert.Contains("stale copy", text);
    }

    [Fact]
    public void A_long_selection_names_five_and_counts_the_rest()
    {
        var many = Enumerable.Range(1, 9).Select(i => Candidate($"branch-{i}")).ToList();
        var text = Plan(many, DeleteScope.LocalAndRemote).RemoteConfirmation("origin");

        Assert.Contains("branch-1", text);
        Assert.Contains("branch-5", text);
        Assert.DoesNotContain("branch-6", text);
        Assert.Contains("and 4 more", text);
    }

    [Fact]
    public void Deleting_the_current_branch_is_recognised()
    {
        // git refuses it outright, so the application handles it explicitly rather than
        // surfacing the refusal.
        Assert.True(Plan([Candidate("feature/x", current: true)]).IncludesCurrent);
        Assert.False(Plan([Candidate("feature/x")]).IncludesCurrent);
    }

    [Fact]
    public void An_outcome_reports_per_branch_rather_than_as_one_verdict()
    {
        // Deleting five where the third fails must say which three succeeded and why the one
        // did not. "Some branches could not be deleted" sends somebody to the terminal.
        var ok = new DeleteOutcome { Branch = "a", LocalDeleted = true };
        var bad = new DeleteOutcome { Branch = "b", Failure = "not fully merged" };

        Assert.True(ok.Succeeded);
        Assert.False(bad.Succeeded);
        Assert.Equal("not fully merged", bad.Failure);
    }
}
