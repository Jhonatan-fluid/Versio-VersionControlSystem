using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// The gate that makes a dropped watcher event a UX problem rather than a correctness one —
/// and, since <c>On git action</c> became the default sync mode, the primary path by which
/// the export tree is written at all.
/// </summary>
public class CommitGateTests
{
    private static BindingFreshness Fresh(string id = "b-1") => new()
    {
        BindingId = id,
        CurrentProjectHash = "sha256:same",
        ExportedProjectHash = "sha256:same",
    };

    private static BindingFreshness Stale(string id = "b-1") => new()
    {
        BindingId = id,
        CurrentProjectHash = "sha256:new",
        ExportedProjectHash = "sha256:old",
    };

    [Fact]
    public void A_fresh_export_lets_the_commit_through_untouched()
    {
        var result = CommitGate.Evaluate([Fresh()]);

        Assert.Equal(GateDecision.Proceed, result.Decision);
        Assert.True(result.CanCommit);
        Assert.Empty(result.StaleBindings);
    }

    [Fact]
    public void A_stale_export_requires_an_export_before_committing()
    {
        var result = CommitGate.Evaluate([Stale()]);

        Assert.Equal(GateDecision.ExportRequired, result.Decision);
        Assert.False(result.CanCommit);
        Assert.Equal(["b-1"], result.StaleBindings);
    }

    [Fact]
    public void A_stale_SECOND_project_blocks_the_commit_even_when_the_first_is_fresh()
    {
        // The mistake this exists to prevent: committing while looking at project A, with
        // project B quietly stale.
        var result = CommitGate.Evaluate([Fresh("b-1"), Stale("b-2")]);

        Assert.Equal(GateDecision.ExportRequired, result.Decision);
        Assert.Equal(["b-2"], result.StaleBindings);
    }

    [Fact]
    public void Every_stale_binding_is_listed_so_one_export_run_can_cover_them_all()
    {
        // Batched into a single CODESYS session; cold start is paid once, not per project.
        var result = CommitGate.Evaluate([Stale("b-1"), Stale("b-2"), Fresh("b-3")]);

        Assert.Equal(2, result.StaleBindings.Count);
        Assert.Contains("b-1", result.StaleBindings);
        Assert.Contains("b-2", result.StaleBindings);
        Assert.Contains("2 projects", result.Explanation);
    }

    [Fact]
    public void A_disabled_binding_is_ignored()
    {
        var result = CommitGate.Evaluate([Stale("b-1") with { Enabled = false }]);

        Assert.Equal(GateDecision.Proceed, result.Decision);
    }

    [Fact]
    public void A_failed_binding_BLOCKS_rather_than_offering_another_export()
    {
        // Re-running an export cannot clear a failure that needs acknowledging, so offering
        // to do so would just loop.
        var result = CommitGate.Evaluate([Fresh() with { InFailedState = true }]);

        Assert.Equal(GateDecision.Blocked, result.Decision);
        Assert.False(result.CanCommit);
        Assert.Contains("acknowledge", result.Explanation);
    }

    [Fact]
    public void A_failure_takes_precedence_over_staleness()
    {
        var result = CommitGate.Evaluate([Stale("b-1") with { InFailedState = true }, Stale("b-2")]);

        Assert.Equal(GateDecision.Blocked, result.Decision);
        Assert.Equal(["b-1"], result.StaleBindings);
    }

    [Fact]
    public void A_repo_with_no_tracked_projects_commits_freely()
    {
        // Versio must not get in the way of a repo it has nothing to do with.
        var result = CommitGate.Evaluate([]);

        Assert.Equal(GateDecision.Proceed, result.Decision);
        Assert.Contains("no CODESYS projects", result.Explanation);
    }

    [Fact]
    public void Hash_comparison_is_ordinal_and_case_sensitive()
    {
        var result = CommitGate.Evaluate([
            new BindingFreshness
            {
                BindingId = "b-1",
                CurrentProjectHash = "sha256:ABC",
                ExportedProjectHash = "sha256:abc",
            },
        ]);

        Assert.Equal(GateDecision.ExportRequired, result.Decision);
    }

    [Fact]
    public void The_explanation_is_always_something_a_user_can_read()
    {
        foreach (var bindings in new[]
        {
            new[] { Fresh() },
            [Stale()],
            [Fresh() with { InFailedState = true }],
            [],
        })
        {
            var result = CommitGate.Evaluate(bindings);
            Assert.False(string.IsNullOrWhiteSpace(result.Explanation));
            Assert.DoesNotContain("sha256", result.Explanation);
        }
    }
}
