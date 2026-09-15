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

        // "PLC", not the brand. This assertion is why the wording could not drift quietly, and
        // it is worth keeping specific: the gate is explaining itself to somebody who is being
        // allowed to commit, and naming a toolchain they may not own reads as a tool that has
        // misunderstood their repository.
        Assert.Contains("no PLC projects", result.Explanation);
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

    private static BindingFreshness Tracked(
        string id, string project, string export, bool failed = false, bool stale = false) =>
        new()
        {
            BindingId = id,
            Name = id,
            ProjectPath = project,
            ExportPath = export,
            CurrentProjectHash = stale ? "sha256:new" : "sha256:same",
            ExportedProjectHash = "sha256:same",
            InFailedState = failed,
        };

    [Fact]
    public void A_failed_binding_does_not_block_a_commit_it_does_not_own()
    {
        // Committing a README next to a broken PLC project used to refuse the whole
        // repository. The failure still needs acknowledging — but not by holding every
        // other file hostage.
        var result = CommitGate.Evaluate(
            [Tracked("plc", "plc/Machine.project", "plc/Machine", failed: true)],
            includePaths: ["docs/README.md"]);

        Assert.Equal(GateDecision.Proceed, result.Decision);
        Assert.Empty(result.FailedBindings);
    }

    [Fact]
    public void A_failed_binding_plus_unrelated_files_asks_before_dropping_its_own()
    {
        var result = CommitGate.Evaluate(
            [Tracked("plc", "plc/Machine.project", "plc/Machine", failed: true)],
            includePaths: ["plc/Machine/App/FB_Motor.st", "docs/README.md"]);

        Assert.Equal(GateDecision.ExcludeAndAsk, result.Decision);
        Assert.Equal(["plc"], result.FailedBindings);
        Assert.Equal(["plc/Machine/App/FB_Motor.st"], result.BlockedPaths);
        Assert.Contains("left out", result.Explanation);
    }

    [Fact]
    public void A_failed_binding_whose_files_are_the_whole_include_set_blocks()
    {
        var result = CommitGate.Evaluate(
            [Tracked("plc", "plc/Machine.project", "plc/Machine", failed: true)],
            includePaths: ["plc/Machine.project", "plc/Machine/App/FB_Motor.st"]);

        Assert.Equal(GateDecision.Blocked, result.Decision);
        Assert.Contains("acknowledge", result.Explanation);
    }

    [Fact]
    public void A_stale_binding_not_in_the_include_set_is_not_exported()
    {
        // Committing docs/README.md must not spend 17-31 s exporting a stale second project.
        var result = CommitGate.Evaluate(
            [Fresh("b-1"), Stale("b-2") with { ProjectPath = "plc/B.project", ExportPath = "plc/B" }],
            includePaths: ["docs/README.md"]);

        Assert.Equal(GateDecision.Proceed, result.Decision);
        Assert.Empty(result.StaleBindings);
    }

    [Fact]
    public void A_stale_binding_in_the_include_set_still_exports_first()
    {
        var result = CommitGate.Evaluate(
            [Stale("b-2") with { ProjectPath = "plc/B.project", ExportPath = "plc/B" }],
            includePaths: ["plc/B/App/FB_Motor.st"]);

        Assert.Equal(GateDecision.ExportRequired, result.Decision);
        Assert.Equal(["b-2"], result.StaleBindings);
    }

    [Fact]
    public void The_project_file_itself_counts_as_owned()
    {
        // OwnerOf on the binding only matches the export tree. The gate has to match the
        // pointer too, or a failed import of the .project would not be in the include set
        // while its mirror was.
        Assert.True(CommitGate.Owns(
            Tracked("plc", "plc/Machine.project", "plc/Machine"),
            "plc/Machine.project"));
        Assert.False(CommitGate.Owns(
            Tracked("plc", "plc/Machine.project", "plc/Machine"),
            "plc/Machine.project.bak"));
    }

    [Fact]
    public void A_file_inside_a_directory_projects_folder_counts_as_owned()
    {
        // A TIA Portal .apXX is a pointer; the program is System/PEData.plf beside it. Owns
        // matched only the pointer and the mirror, so a commit of the program store alone -
        // the file a Portal save actually rewrites - skipped the gate and recorded the program
        // beside a mirror that did not describe it.
        var binding = Tracked("tia", "plc/Station/Station.ap21", "plc/Station/Station")
            with { PayloadPath = "plc/Station" };

        Assert.True(CommitGate.Owns(binding, "plc/Station/System/PEData.plf"));

        // A sibling folder that merely starts with the same name is somebody else's.
        Assert.False(CommitGate.Owns(binding, "plc/StationB/System/PEData.plf"));
        Assert.False(CommitGate.Owns(binding, "plc/Station.txt"));
    }

    [Fact]
    public void A_file_project_has_no_folder_to_claim_files_by()
    {
        // Empty payload is the file-project case. It must not start owning everything under
        // the repository root.
        var binding = Tracked("plc", "plc/Machine.project", "plc/Machine");

        Assert.False(CommitGate.Owns(binding, "plc/Other.project"));
        Assert.False(CommitGate.Owns(binding, "docs/README.md"));
    }

    [Fact]
    public void A_commit_of_only_the_program_store_still_exports_first()
    {
        var result = CommitGate.Evaluate(
            [Tracked("tia", "plc/Station/Station.ap21", "plc/Station/Station", stale: true)
                with { PayloadPath = "plc/Station" }],
            includePaths: ["plc/Station/System/PEData.plf"]);

        Assert.Equal(GateDecision.ExportRequired, result.Decision);
        Assert.Equal(["tia"], result.StaleBindings);
    }

    // --- after the export the gate asked for ------------------------------------------

    [Fact]
    public void A_wait_is_not_an_export()
    {
        // The IDE held a directory project, so the export before the commit refused - as a
        // wait, deliberately not a failure. The commit flow stopped only on failures, so it
        // went on to record the old mirror beside the new project until this existed.
        var gate = CommitGate.Evaluate([Stale("b-1")]);
        var waited = SyncOutcome.Refused(SyncVerdict.BlockedByOpenProject, "b-1",
            "Station is open in the IDE, so it cannot be exported until it is closed.");

        Assert.False(waited.IsFailure);
        Assert.Equal(waited.Explanation, CommitGate.StillStaleAfterExport(gate, [waited]));
    }

    [Theory]
    [InlineData(SyncVerdict.Completed)]
    [InlineData(SyncVerdict.NoChange)]
    public void An_export_that_ran_or_found_nothing_to_do_lets_the_commit_through(SyncVerdict verdict)
    {
        var gate = CommitGate.Evaluate([Stale("b-1")]);

        Assert.Null(CommitGate.StillStaleAfterExport(
            gate, [SyncOutcome.Refused(verdict, "b-1", "done")]));
    }

    [Theory]
    [InlineData(SyncVerdict.BlockedByLock)]
    [InlineData(SyncVerdict.BlockedByCircuitBreaker)]
    [InlineData(SyncVerdict.Coalesced)]
    public void Every_other_non_failure_leaves_the_mirror_stale_too(SyncVerdict verdict)
    {
        // Not only the IDE: any outcome that did not write the mirror or prove it current
        // leaves the old export on disk.
        var gate = CommitGate.Evaluate([Stale("b-1")]);

        Assert.NotNull(CommitGate.StillStaleAfterExport(
            gate, [SyncOutcome.Refused(verdict, "b-1", "did not run")]));
    }

    [Fact]
    public void A_stale_binding_with_no_outcome_is_refused_rather_than_assumed_exported()
    {
        var gate = CommitGate.Evaluate([Stale("b-1"), Stale("b-2")]);

        var why = CommitGate.StillStaleAfterExport(
            gate, [SyncOutcome.Refused(SyncVerdict.Completed, "b-1", "done")]);

        Assert.False(string.IsNullOrWhiteSpace(why));
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
