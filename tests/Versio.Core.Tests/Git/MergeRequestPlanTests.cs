using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// Whether the merge-request button exists, what it says, and why it is dead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each gate is failed one at a time.</b> A test that fails three conditions at once passes
/// whichever one the code happens to check first, and would keep passing if two of the three
/// were deleted. Every case below is otherwise-perfect state with exactly one thing wrong.
/// </para>
/// <para>
/// <b>Hidden and disabled are different assertions.</b> An unidentifiable forge is not a
/// problem the user can fix from this panel, so it produces nothing; everything else produces a
/// button that says what to do.
/// </para>
/// </remarks>
public sealed class MergeRequestPlanTests
{
    private static readonly RemoteUrl GitHub =
        RemoteUrl.Parse("https://github.com/acme/machine.git");

    private static readonly DefaultBranch Main = new()
    {
        Source = DefaultBranchSource.Remote,
        Name = "main",
    };

    private static BranchInfo Branch(string name = "feature/pumps", int ahead = 0, int behind = 0) =>
        new()
        {
            Name = name,
            Upstream = $"origin/{name}",
            Ahead = ahead,
            Behind = behind,
            IsCurrent = true,
        };

    /// <summary>Everything correct. Each test below breaks exactly one thing.</summary>
    private static MergeRequestPlan Plan(
        ForgeKind kind = ForgeKind.GitHub,
        RemoteUrl? remote = null,
        BranchInfo? branch = null,
        TrackingState tracking = TrackingState.InSync,
        DefaultBranch? defaultBranch = null,
        bool isDirty = false,
        int? existingNumber = null) =>
        MergeRequestPlan.For(
            kind, remote ?? GitHub, branch ?? Branch(), tracking,
            defaultBranch ?? Main, isDirty, existingNumber);

    [Fact]
    public void Everything_in_order_offers_to_create_one()
    {
        var plan = Plan();

        Assert.Equal(MergeRequestIntent.Create, plan.Intent);
        Assert.True(plan.CanOpen);
        Assert.Equal("Create pull request", plan.Label);
        Assert.Equal(
            "https://github.com/acme/machine/compare/main...feature/pumps?expand=1", plan.Url);
        Assert.Equal(string.Empty, plan.BlockedReason);
    }

    // ---- the five gates, one at a time ---------------------------------------------------

    [Fact]
    public void An_unidentified_forge_produces_no_button()
    {
        var plan = Plan(kind: ForgeKind.Unknown,
            remote: RemoteUrl.Parse("https://git.acme.internal/g/machine.git"));

        Assert.False(plan.IsVisible);
        Assert.Equal(MergeRequestIntent.None, plan.Intent);

        // Not a disabled button with an explanation: there is nothing to fix from this panel,
        // and a permanently dead control on every local-only repository is clutter.
        Assert.Equal(string.Empty, plan.BlockedReason);
    }

    [Fact]
    public void The_default_branch_cannot_raise_one_into_itself()
    {
        var plan = Plan(branch: Branch("main"));

        Assert.True(plan.IsVisible);
        Assert.False(plan.CanOpen);
        Assert.Contains("default branch", plan.BlockedReason);
    }

    [Fact]
    public void A_dirty_tree_is_asked_to_commit_first()
    {
        var plan = Plan(isDirty: true);

        Assert.False(plan.CanOpen);
        Assert.Equal("Commit your changes first.", plan.BlockedReason);
    }

    [Fact]
    public void A_branch_with_no_upstream_is_asked_to_publish()
    {
        var plan = Plan(tracking: TrackingState.NoUpstream);

        Assert.False(plan.CanOpen);
        Assert.Equal("Publish this branch first.", plan.BlockedReason);
    }

    [Fact]
    public void Unpushed_commits_are_counted_in_the_refusal()
    {
        var plan = Plan(branch: Branch(ahead: 3), tracking: TrackingState.Ahead);

        Assert.False(plan.CanOpen);
        Assert.Contains("Push your 3 commits first", plan.BlockedReason);

        // A request opened now would be named after changes it does not contain.
        Assert.Contains("missing them", plan.BlockedReason);
    }

    [Fact]
    public void One_unpushed_commit_is_not_pluralised()
    {
        Assert.Contains(
            "Push your 1 commit first",
            Plan(branch: Branch(ahead: 1), tracking: TrackingState.Ahead).BlockedReason);
    }

    [Fact]
    public void Being_behind_does_not_block_it()
    {
        // Behind is not a reason to refuse: the forge's own page shows the comparison, and a
        // branch that is level with its upstream but behind the trunk is the normal case for a
        // request that has been open for a day.
        var plan = Plan(branch: Branch(behind: 4), tracking: TrackingState.Behind);

        Assert.True(plan.CanOpen);
    }

    [Fact]
    public void An_unknown_trunk_says_what_to_set_rather_than_guessing()
    {
        var plan = Plan(defaultBranch: DefaultBranch.None);

        Assert.False(plan.CanOpen);

        // DefaultBranch's own wording, passed through: it names the setting, and somebody told
        // only "cannot determine" has nowhere to go.
        Assert.Equal(DefaultBranch.None.BlockedReason, plan.BlockedReason);
    }

    [Fact]
    public void A_detached_head_produces_no_button()
    {
        var plan = Plan(branch: new BranchInfo { Name = "(detached)", IsDetached = true });

        Assert.False(plan.IsVisible);
    }

    [Fact]
    public void No_repository_and_no_remote_produce_no_button()
    {
        Assert.False(Plan(tracking: TrackingState.NoRemote).IsVisible);
        Assert.False(Plan(tracking: TrackingState.Unborn).IsVisible);

        // Straight through rather than via Plan(), whose `branch ?? Branch()` would quietly
        // substitute a good branch and assert nothing. The no-branch case is real: the panel
        // asks for this before the first status has come back.
        Assert.False(MergeRequestPlan.For(
            ForgeKind.GitHub, GitHub, branch: null, TrackingState.NoRepository, Main,
            isDirty: false).IsVisible);
    }

    // ---- what the label follows ----------------------------------------------------------

    [Fact]
    public void A_ref_probe_that_found_one_changes_the_button_to_go_to()
    {
        var plan = Plan(existingNumber: 42);

        Assert.Equal(MergeRequestIntent.GoTo, plan.Intent);
        Assert.Equal("Go to pull request", plan.Label);
        Assert.Equal("https://github.com/acme/machine/pull/42", plan.Url);

        // Never "1 open pull request": the ref says a request exists whose head is this commit,
        // not whether it is open, merged or a draft.
        Assert.DoesNotContain("open", plan.Label);
    }

    [Fact]
    public void GitLab_is_asked_for_a_merge_request_in_its_own_words()
    {
        var plan = Plan(
            kind: ForgeKind.GitLab,
            remote: RemoteUrl.Parse("https://gitlab.com/acme/machine.git"));

        Assert.Equal("Create merge request", plan.Label);
        Assert.Contains("/-/merge_requests/new", plan.Url);
    }

    [Theory]
    [InlineData(ForgeKind.AzureDevOps, "https://dev.azure.com/acme/Plant/_git/machine",
        "https://dev.azure.com/acme/Plant/_git/machine/pullrequests")]
    [InlineData(ForgeKind.Bitbucket, "https://bitbucket.org/acme/machine.git",
        "https://bitbucket.org/acme/machine/pull-requests")]
    public void A_forge_with_no_request_refs_offers_the_list_rather_than_a_number(
        ForgeKind kind, string remote, string expected)
    {
        // These two publish no hidden refs, so "does one already exist?" is unanswerable
        // without an API token. A link to the list is honest; a fabricated number is not.
        var plan = Plan(kind: kind, remote: RemoteUrl.Parse(remote));

        Assert.Equal(MergeRequestIntent.GoToList, plan.Intent);
        Assert.Equal(expected, plan.Url);
        Assert.True(plan.CanOpen);
    }

    [Fact]
    public void A_blocked_button_says_what_this_forge_would_say_when_live()
    {
        // On Bitbucket the live button never reads "Create" — there are no request refs, so it
        // offers the list. A caption that changed verb as somebody fixed the thing it asked
        // them to fix would be the control moving under them.
        var bitbucket = Plan(
            kind: ForgeKind.Bitbucket,
            remote: RemoteUrl.Parse("https://bitbucket.org/acme/machine.git"),
            isDirty: true);

        Assert.False(bitbucket.CanOpen);
        Assert.Equal("Open pull requests", bitbucket.Label);
        Assert.Equal("Open pull requests", Plan(
            kind: ForgeKind.Bitbucket,
            remote: RemoteUrl.Parse("https://bitbucket.org/acme/machine.git")).Label);

        // And GitHub, which does have refs, keeps saying Create in both states.
        Assert.Equal("Create pull request", Plan(isDirty: true).Label);
        Assert.Equal("Create pull request", Plan().Label);
    }

    [Fact]
    public void A_blocked_button_carries_no_url()
    {
        // The click handler must have nothing to open even if it is somehow reached: a dead
        // button that would still navigate is one keyboard path away from a wrong page.
        foreach (var plan in new[]
                 {
                     Plan(isDirty: true),
                     Plan(branch: Branch("main")),
                     Plan(tracking: TrackingState.NoUpstream),
                     Plan(branch: Branch(ahead: 2), tracking: TrackingState.Ahead),
                 })
        {
            Assert.Equal(string.Empty, plan.Url);
            Assert.False(plan.CanOpen);
            Assert.NotEqual(string.Empty, plan.BlockedReason);
        }
    }
}
