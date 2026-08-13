using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// Neither direction runs while the tree is mid-merge.
/// </summary>
/// <remarks>
/// <para>
/// <c>GitStatus.HasConflicts</c> was parsed, exposed and read by nothing. To the importer,
/// <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; HEAD</c> in a <c>.st</c> file is just text — it would be
/// written into the project, which is a structured file that stops compiling rather than a
/// document with an obvious mess in it.
/// </para>
/// <para>
/// Export is refused as well, and that half is less obvious: exporting over a half-merged
/// mirror overwrites the conflict somebody is in the middle of resolving, and records the
/// result as the truth.
/// </para>
/// </remarks>
public sealed class ConflictGateTests
{
    /// <summary>Whatever git is currently reporting as unmerged.</summary>
    private sealed class StubConflicts : IConflictProbe
    {
        public List<string> Unmerged { get; } = [];

        public int TimesAsked { get; private set; }

        public Task<IReadOnlyList<string>> UnmergedPathsAsync(CancellationToken ct)
        {
            TimesAsked++;
            return Task.FromResult<IReadOnlyList<string>>([.. Unmerged]);
        }
    }

    private static (SyncOrchestratorTests.Harness Harness, StubConflicts Conflicts) Build()
    {
        var harness = new SyncOrchestratorTests.Harness();
        var conflicts = new StubConflicts();

        harness.Conflicts = conflicts;
        harness.Build();

        return (harness, conflicts);
    }

    [Fact]
    public async Task A_conflicted_mirror_stops_the_import()
    {
        var (harness, conflicts) = Build();

        conflicts.Unmerged.Add("plc/A/Simulation.st");
        harness.SetExportedText("edited");

        var outcome = await harness.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = SyncOrchestratorTests.BindingId, Source = ChangeSource.ExportedText },
            CancellationToken.None);

        Assert.Equal(SyncVerdict.BlockedByConflict, outcome.Verdict);
    }

    [Fact]
    public async Task A_conflicted_project_stops_the_export()
    {
        // The less obvious half. A binary git could not merge, exported anyway, would produce
        // a mirror of whichever side won and record it as what the project contains.
        var (harness, conflicts) = Build();

        conflicts.Unmerged.Add("plc/A.project");
        harness.SetProject("v2");

        var outcome = await harness.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = SyncOrchestratorTests.BindingId, Source = ChangeSource.Project },
            CancellationToken.None);

        Assert.Equal(SyncVerdict.BlockedByConflict, outcome.Verdict);
    }

    [Fact]
    public async Task A_conflict_somewhere_else_is_none_of_this_binding_s_business()
    {
        // A README both sides edited has nothing to do with this project. Halting its sync
        // would be the same over-reach as releasing every lock because one file was discarded.
        var (harness, conflicts) = Build();

        conflicts.Unmerged.Add("README.md");
        conflicts.Unmerged.Add("docs/PLAN.md");
        harness.SetProject("v2");

        var outcome = await harness.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = SyncOrchestratorTests.BindingId, Source = ChangeSource.Project },
            CancellationToken.None);

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
    }

    [Fact]
    public async Task The_project_beside_its_own_mirror_is_not_confused_for_it()
    {
        // plc/A.project sits beside plc/A. A careless prefix test has the mirror claim the
        // project, which is the trap Locate documents on the other side of the seam.
        var (harness, conflicts) = Build();

        conflicts.Unmerged.Add("plc/A.projectile");
        harness.SetProject("v2");

        var outcome = await harness.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = SyncOrchestratorTests.BindingId, Source = ChangeSource.Project },
            CancellationToken.None);

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
    }

    [Fact]
    public async Task The_refusal_names_the_files_and_the_remedy()
    {
        var (harness, conflicts) = Build();

        conflicts.Unmerged.Add("plc/A/Simulation.st");
        harness.SetExportedText("edited");

        var outcome = await harness.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = SyncOrchestratorTests.BindingId, Source = ChangeSource.ExportedText },
            CancellationToken.None);

        Assert.Contains("Simulation.st", outcome.Explanation, StringComparison.Ordinal);
        Assert.Contains("Finish the merge", outcome.Explanation, StringComparison.Ordinal);

        var sentence = SyncNarrative.Describe(outcome);
        Assert.Contains("Waiting for the merge", sentence, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ a wait, not a failure

    [Fact]
    public async Task It_leaves_the_direction_lock_where_it_was()
    {
        var (harness, conflicts) = Build();

        var before = harness.Orchestrator.StateOf(SyncOrchestratorTests.BindingId);

        conflicts.Unmerged.Add("plc/A/Simulation.st");
        harness.SetExportedText("edited");

        await harness.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = SyncOrchestratorTests.BindingId, Source = ChangeSource.ExportedText },
            CancellationToken.None);

        Assert.Equal(before, harness.Orchestrator.StateOf(SyncOrchestratorTests.BindingId));
    }

    [Fact]
    public async Task Repeated_saves_during_a_merge_do_not_trip_the_breaker()
    {
        // A merge takes as long as it takes, and somebody saving through it is normal. A
        // breaker fed by these would halt the project and demand a button press to resume.
        var (harness, conflicts) = Build();

        conflicts.Unmerged.Add("plc/A/Simulation.st");

        for (var i = 0; i < 10; i++)
        {
            harness.SetExportedText($"edit {i}");

            var outcome = await harness.Orchestrator.HandleChangeAsync(
                new SyncJob { BindingId = SyncOrchestratorTests.BindingId, Source = ChangeSource.ExportedText },
                CancellationToken.None);

            Assert.Equal(SyncVerdict.BlockedByConflict, outcome.Verdict);
        }
    }

    [Fact]
    public async Task Once_the_merge_is_finished_the_next_change_runs()
    {
        var (harness, conflicts) = Build();

        conflicts.Unmerged.Add("plc/A/Simulation.st");
        harness.SetProject("v2");

        Assert.Equal(SyncVerdict.BlockedByConflict, (await harness.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = SyncOrchestratorTests.BindingId, Source = ChangeSource.Project },
            CancellationToken.None)).Verdict);

        conflicts.Unmerged.Clear();

        Assert.Equal(SyncVerdict.Completed, (await harness.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = SyncOrchestratorTests.BindingId, Source = ChangeSource.Project },
            CancellationToken.None)).Verdict);
    }

    // ------------------------------------------------------------------------- what it costs

    [Fact]
    public async Task A_save_that_changed_nothing_is_not_worth_a_git_status()
    {
        // The check sits after the hash gate so the process spawn is paid only for changes
        // that are actually going somewhere. Most settled changes are not.
        //
        // A baseline has to exist before the hash gate can say NoChange, so this syncs once
        // and then fires an identical change — which is what a save that altered nothing is.
        var (harness, conflicts) = Build();

        harness.SetProject("v2");

        await harness.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = SyncOrchestratorTests.BindingId, Source = ChangeSource.Project },
            CancellationToken.None);

        var askedDuringTheRealChange = conflicts.TimesAsked;

        var outcome = await harness.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = SyncOrchestratorTests.BindingId, Source = ChangeSource.Project },
            CancellationToken.None);

        Assert.Equal(SyncVerdict.NoChange, outcome.Verdict);
        Assert.Equal(askedDuringTheRealChange, conflicts.TimesAsked);
    }

    [Fact]
    public async Task An_engine_with_no_probe_behaves_exactly_as_it_did_before()
    {
        // Optional, like the occupancy probe: the several hundred tests that predate this
        // construct the orchestrator without mentioning it.
        var harness = new SyncOrchestratorTests.Harness();

        harness.SetProject("v2");

        var outcome = await harness.Orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = SyncOrchestratorTests.BindingId, Source = ChangeSource.Project },
            CancellationToken.None);

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
    }
}
