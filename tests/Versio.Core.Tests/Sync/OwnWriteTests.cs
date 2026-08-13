using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// What happens when Versio's own export or import is what moved the files.
/// </summary>
/// <remarks>
/// <para>
/// Observed in the running app, on a project with 56 exported files. The export swaps a
/// directory into place, the watcher raises one event <b>per file</b>, and each of those
/// arrived as a change to review: a conflict probe — a <c>git status</c> process — and a hash
/// of the entire mirror, before the direction lock refused it. Fifty-six status calls and
/// fifty-six full-tree hashes for one export, which is quadratic in the size of a project.
/// </para>
/// <para>
/// <b>Nothing was ever lost</b>, and that is why it survived: an export only runs while the
/// project side is authoritative, and a mirror edit is refused in that state anyway. The lock
/// was doing the suppressor's job, expensively.
/// </para>
/// <para>
/// <see cref="SelfWriteSuppressor"/> could not do it. It registers one path with one expected
/// hash, consumed by the first matching event, and the registration is made <i>after</i> the
/// run — tens of seconds after the swap the events describe. One late registration against
/// dozens of echoes.
/// </para>
/// </remarks>
public sealed class OwnWriteTests
{
    /// <summary>Counts what an echo would have cost: one git status per event.</summary>
    private sealed class CountingConflicts : IConflictProbe
    {
        public int TimesAsked { get; private set; }

        public Task<IReadOnlyList<string>> UnmergedPathsAsync(CancellationToken ct)
        {
            TimesAsked++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private static SyncJob Change(ChangeSource source, string? binding = null) => new()
    {
        BindingId = binding ?? SyncOrchestratorTests.BindingId,
        Source = source,
    };

    [Fact]
    public async Task The_echo_of_our_own_export_is_not_an_edit()
    {
        var harness = new SyncOrchestratorTests.Harness();

        using (harness.Orchestrator.BeginOwnWrite(
                   SyncOrchestratorTests.BindingId, ChangeSource.ExportedText))
        {
            // What an export leaves behind: a mirror that genuinely differs from the last
            // synced state, because the export is what changed it.
            harness.SetExportedText("everything the export just wrote");

            var outcome = await harness.Orchestrator.HandleChangeAsync(
                Change(ChangeSource.ExportedText), CancellationToken.None);

            Assert.Equal(SyncVerdict.SelfWriteSuppressed, outcome.Verdict);
        }
    }

    [Fact]
    public async Task Fifty_six_echoes_cost_nothing_instead_of_fifty_six_git_calls()
    {
        // The whole point. The count is the measurement from the report: one project, one
        // export, one watcher event per exported file.
        var harness = new SyncOrchestratorTests.Harness();
        var conflicts = new CountingConflicts();

        harness.Conflicts = conflicts;
        harness.Build();

        using (harness.Orchestrator.BeginOwnWrite(
                   SyncOrchestratorTests.BindingId, ChangeSource.ExportedText))
        {
            harness.SetExportedText("everything the export just wrote");

            for (var i = 0; i < 56; i++)
            {
                await harness.Orchestrator.HandleChangeAsync(
                    Change(ChangeSource.ExportedText), CancellationToken.None);
            }
        }

        Assert.Equal(0, conflicts.TimesAsked);
    }

    [Fact]
    public async Task Fifty_six_echoes_are_one_line_in_the_log_rather_than_fifty_six()
    {
        // The other half of the cost. Before the window each echo wrote a refusal, a hash and
        // a git result - 493 lines in one 50-second export, measured in a real log. A line
        // apiece here would have swapped a burst of work for a burst of log, so the window
        // counts them and says how many when it closes.
        var harness = new SyncOrchestratorTests.Harness();

        using (harness.Orchestrator.BeginOwnWrite(
                   SyncOrchestratorTests.BindingId, ChangeSource.ExportedText))
        {
            harness.SetExportedText("everything the export just wrote");

            for (var i = 0; i < 56; i++)
            {
                await harness.Orchestrator.HandleChangeAsync(
                    Change(ChangeSource.ExportedText), CancellationToken.None);
            }
        }

        Assert.DoesNotContain(harness.Log, e => e.Message.Contains(
            "this was not an edit", StringComparison.Ordinal));

        Assert.Contains(harness.Log, e => e.Message.Contains(
            "ignored 56 echo(es)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_window_that_swallowed_nothing_says_nothing_about_echoes()
    {
        // A count of zero on every export would be the same noise one level quieter.
        var harness = new SyncOrchestratorTests.Harness();

        harness.Orchestrator.BeginOwnWrite(
            SyncOrchestratorTests.BindingId, ChangeSource.ExportedText).Dispose();

        await Task.CompletedTask;

        Assert.DoesNotContain(harness.Log, e => e.Message.Contains(
            "echo(es)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task It_is_dropped_before_the_hash_gate_is_asked()
    {
        // The distinguishing property, the same one the git-write window has: our own export
        // really did change the content, so a check that asked "did anything change" first
        // would answer yes. The verdict has to be the reason, not NoChange.
        var harness = new SyncOrchestratorTests.Harness();

        using (harness.Orchestrator.BeginOwnWrite(
                   SyncOrchestratorTests.BindingId, ChangeSource.ExportedText))
        {
            harness.SetExportedText("something genuinely different");

            var outcome = await harness.Orchestrator.HandleChangeAsync(
                Change(ChangeSource.ExportedText), CancellationToken.None);

            Assert.Equal(SyncVerdict.SelfWriteSuppressed, outcome.Verdict);
            Assert.NotEqual(SyncVerdict.NoChange, outcome.Verdict);
        }
    }

    [Fact]
    public async Task The_other_side_of_the_same_project_is_still_watched()
    {
        // Narrower than the git-write window on purpose. An export rewrites the mirror and
        // says nothing about the project file, so an engineer saving in the IDE during those
        // twenty seconds must still be seen.
        var harness = new SyncOrchestratorTests.Harness();

        using (harness.Orchestrator.BeginOwnWrite(
                   SyncOrchestratorTests.BindingId, ChangeSource.ExportedText))
        {
            harness.SetProject("saved while the export was running");

            var outcome = await harness.Orchestrator.HandleChangeAsync(
                Change(ChangeSource.Project), CancellationToken.None);

            Assert.NotEqual(SyncVerdict.SelfWriteSuppressed, outcome.Verdict);
        }
    }

    [Fact]
    public async Task Another_project_in_the_same_repository_is_still_watched()
    {
        // The reason this is per binding rather than repository-wide, which is what makes it
        // different from the git-write window: a checkout rewrites everything, an export
        // rewrites one mirror. Holding off every project for the 20-31 s a toolchain run takes
        // would drop real edits elsewhere.
        var other = new Versio.Core.Bindings.ProjectBinding
        {
            Id = "b-other",
            ProjectPath = "plc/B.project",
            ExportPath = "plc/B",
            Profile = "CODESYS V3.5 SP22 Patch 2",
        };

        var harness = new SyncOrchestratorTests.Harness(extra: other);
        harness.Fs.Write("root/plc/B/Main.st", "text");

        using (harness.Orchestrator.BeginOwnWrite(
                   SyncOrchestratorTests.BindingId, ChangeSource.ExportedText))
        {
            var outcome = await harness.Orchestrator.HandleChangeAsync(
                Change(ChangeSource.ExportedText, "b-other"), CancellationToken.None);

            Assert.NotEqual(SyncVerdict.SelfWriteSuppressed, outcome.Verdict);
        }
    }

    [Fact]
    public async Task A_real_edit_after_the_export_has_finished_is_seen()
    {
        var harness = new SyncOrchestratorTests.Harness();

        using (harness.Orchestrator.BeginOwnWrite(
                   SyncOrchestratorTests.BindingId, ChangeSource.ExportedText))
        {
            harness.SetExportedText("what the export wrote");

            Assert.Equal(
                SyncVerdict.SelfWriteSuppressed,
                (await harness.Orchestrator.HandleChangeAsync(
                    Change(ChangeSource.ExportedText), CancellationToken.None)).Verdict);
        }

        harness.SetExportedText("and then somebody edited it by hand");

        var after = await harness.Orchestrator.HandleChangeAsync(
            Change(ChangeSource.ExportedText), CancellationToken.None);

        Assert.NotEqual(SyncVerdict.SelfWriteSuppressed, after.Verdict);
    }

    [Fact]
    public async Task Nested_windows_close_in_any_order()
    {
        // Counted rather than a flag, for the same reason the git-write window is: two runs
        // on one binding would otherwise have the first to finish clear it for both.
        var harness = new SyncOrchestratorTests.Harness();

        var outer = harness.Orchestrator.BeginOwnWrite(
            SyncOrchestratorTests.BindingId, ChangeSource.ExportedText);
        var inner = harness.Orchestrator.BeginOwnWrite(
            SyncOrchestratorTests.BindingId, ChangeSource.ExportedText);

        outer.Dispose();
        harness.SetExportedText("still ours");

        Assert.Equal(
            SyncVerdict.SelfWriteSuppressed,
            (await harness.Orchestrator.HandleChangeAsync(
                Change(ChangeSource.ExportedText), CancellationToken.None)).Verdict);

        inner.Dispose();
        Assert.False(harness.Orchestrator.IsWriting(
            SyncOrchestratorTests.BindingId, ChangeSource.ExportedText));
    }

    [Fact]
    public async Task A_real_export_opens_the_window_itself()
    {
        // The test that matters, and the one the others cannot stand in for: every case above
        // opens the window by hand, which proves the gate and says nothing about whether the
        // export path reaches it. This repository has shipped that mistake three times -
        // GuardIntent values nothing called, ResetForRepositoryChange with nine passing tests
        // and no call site, TreeRow.Reconcile likewise - so the export is driven for real and
        // asked, from inside the run, whether the window is open.
        var harness = new SyncOrchestratorTests.Harness();
        var openDuringRun = (bool?)null;

        harness.Codesys.BeforeRun = job =>
        {
            if (job.Operation == Versio.Core.Plc.PlcOperation.Export)
            {
                openDuringRun = harness.Orchestrator.IsWriting(
                    SyncOrchestratorTests.BindingId, ChangeSource.ExportedText);

                harness.SetExportedText("written by the export");
            }

            return Task.CompletedTask;
        };

        harness.Codesys.EnqueueSuccess("plc/A/Main.st");
        harness.SetProject("v2");

        await harness.Orchestrator.HandleChangeAsync(
            Change(ChangeSource.Project), CancellationToken.None);

        Assert.True(openDuringRun, "the export ran without declaring that it was writing");

        // And closed again afterwards, or the mirror would never be watched at all.
        Assert.False(harness.Orchestrator.IsWriting(
            SyncOrchestratorTests.BindingId, ChangeSource.ExportedText));
    }

    [Fact]
    public void Disposing_twice_does_not_reopen_the_window()
    {
        var harness = new SyncOrchestratorTests.Harness();

        var window = harness.Orchestrator.BeginOwnWrite(
            SyncOrchestratorTests.BindingId, ChangeSource.ExportedText);

        window.Dispose();
        window.Dispose();

        Assert.False(harness.Orchestrator.IsWriting(
            SyncOrchestratorTests.BindingId, ChangeSource.ExportedText));
    }
}
