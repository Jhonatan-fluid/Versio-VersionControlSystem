using Versio.Core.Plc;
using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// The plan a person agreed to is the plan the toolchain is given.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this suite exists at all.</b> Every link in this chain can be cut without anything
/// failing. The runner computes a plan when a job arrives without one — correctly, so that an
/// import is plan-driven whichever door it came through — which means a plan dropped anywhere
/// upstream is silently replaced by a fresh one. The import still runs, still succeeds, and
/// still executes <i>a</i> plan. Just not the one that was on screen.
/// </para>
/// <para>
/// That matters from the moment the confirmation dialog names deletions. A dialog listing
/// three objects it will remove, followed by a plan recomputed against a mirror that moved
/// while the dialog was open, is a dialog that told the truth about something that then did
/// not happen — and <c>remove()</c> was measured to delete a referenced object silently, with
/// no exception and no cascade (P3). There is no toolchain backstop to catch the difference.
/// </para>
/// <para>
/// So each test here corresponds to one link, and each was checked by deleting that link and
/// watching this suite go red — <c>SyncJob.Plan</c>, the orchestrator's use of it,
/// <c>PlcProjectJob.Plan</c>, and the vendor boundary's <c>ToNative</c>, which is covered
/// beside the runner because it lives in another assembly.
/// </para>
/// </remarks>
public class ImportPlanCarriageTests
{
    /// <summary>
    /// A plan nothing could compute by accident.
    /// </summary>
    /// <remarks>
    /// The path is deliberately not a file the harness's mirror contains. If any link
    /// recomputes instead of carrying, it produces a plan describing the real mirror, and the
    /// assertion fails on content rather than on a reference — which is the failure that
    /// still catches a future caller that clones the plan on the way through.
    /// </remarks>
    private static ImportPlan Distinctive() => new()
    {
        Files =
        [
            new PlannedFile
            {
                Path = "Agreed/OnlyInThePlan.st",
                Action = PlannedAction.ApplyText,
                Name = "OnlyInThePlan",
                Guid = "11111111-2222-3333-4444-555555555555",
            },
        ],
        MissingFromMirror = ["Agreed/AboutToBeDeleted.st"],
    };

    [Fact]
    public async Task A_plan_the_caller_agreed_to_is_the_plan_the_toolchain_receives()
    {
        var h = new SyncOrchestratorTests.Harness();
        var agreed = Distinctive();

        await h.Orchestrator.HandleChangeAsync(new SyncJob
        {
            BindingId = SyncOrchestratorTests.BindingId,
            Source = ChangeSource.ExportedText,
            Confirmed = true,
            Synchronous = true,
            Plan = agreed,
        }, CancellationToken.None);

        // Filtered to imports rather than taking the only job: an export slipping into the
        // sequence later would break this test for a reason that has nothing to do with it.
        var job = Assert.Single(h.Codesys.Jobs, j => j.Operation == PlcOperation.Import);
        var carried = Assert.Single(job.Projects).Plan;

        Assert.NotNull(carried);

        // By content, not only by reference: a link that rebuilt an equivalent record would
        // still be carrying the agreement, and a link that recomputed could not produce this.
        Assert.Equal(["Agreed/OnlyInThePlan.st"], carried.Files.Select(f => f.Path));
        Assert.Equal(["Agreed/AboutToBeDeleted.st"], carried.MissingFromMirror);
    }

    [Fact]
    public async Task An_import_nobody_was_asked_about_still_gets_a_plan()
    {
        // The watcher's path. There is no dialog and so nothing to agree to, and this must
        // NOT become a way to reach the toolchain without a plan — the walk that inferred
        // everything from file names is deleted, not bypassed.
        var h = new SyncOrchestratorTests.Harness();

        await h.Orchestrator.HandleChangeAsync(new SyncJob
        {
            BindingId = SyncOrchestratorTests.BindingId,
            Source = ChangeSource.ExportedText,
            Confirmed = true,
            Synchronous = true,
        }, CancellationToken.None);

        var carried = Assert.Single(Assert.Single(
            h.Codesys.Jobs, j => j.Operation == PlcOperation.Import).Projects).Plan;

        Assert.NotNull(carried);

        // The real mirror, not the Distinctive one: this plan was computed from what the
        // harness wrote to the fake filesystem.
        Assert.Contains(carried.Files, f => f.Path == "Simulation.st");
    }

    [Fact]
    public async Task The_plan_is_computed_from_mirror_relative_paths()
    {
        // The convention the walk turns on, and the one that is silent when got wrong.
        // IFileSystem.EnumerateFiles returns ABSOLUTE paths and its own comment claimed
        // repo-relative ones until 2026-08-15; a walk that believed the comment would hand
        // the planner "C:\repo\plc\A\Simulation.st", match nothing against the manifest, and
        // plan a Create for every file in the mirror while looking entirely healthy.
        var h = new SyncOrchestratorTests.Harness();

        var plan = await h.Orchestrator.PlanImportAsync(
            SyncOrchestratorTests.BindingId, CancellationToken.None);

        Assert.NotNull(plan);
        Assert.NotEmpty(plan.Files);
        Assert.All(plan.Files, f =>
        {
            Assert.DoesNotContain(":", f.Path, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", f.Path, StringComparison.Ordinal);
            Assert.False(f.Path.StartsWith('/'), $"{f.Path} is not mirror-relative");
        });
    }

    [Fact]
    public async Task An_import_of_a_mirror_that_is_not_there_is_refused_rather_than_reported_done()
    {
        // A MISSING mirror is not an empty one. An empty plan is a valid instruction meaning
        // "touch no file", so without this the import runs, changes nothing, and comes back
        // Completed — which a user reads as "my edits were applied".
        //
        // This was caught by accident until the plan walk moved onto IFileSystem:
        // Directory.EnumerateFiles throws on a missing directory and the old walk called it
        // straight, while IFileSystem.EnumerateFiles answers [] — so a throw became a silent
        // success, and nothing in the suite noticed.
        var h = new SyncOrchestratorTests.Harness();

        // The scenario has to be reached honestly, and the first attempt at it was not: with a
        // fresh binding the hash gate answers NoChange, because "never synced" and "the mirror
        // is gone" both hash to nothing and the gate cannot tell them apart. That is the gate
        // working — but it means the refusal below is reachable only once a real baseline
        // exists, which is exactly the state a user is in.
        //
        // So: export (records a baseline, takes the lock), push (releases it), then delete.
        await h.ProjectChanged();
        await h.Orchestrator.ReleaseAfterPushAsync("abc1234", CancellationToken.None);

        // The FOLDER, not just its contents — and that distinction cost a wrong test before it
        // was noticed. Deleting every file leaves the directory behind on a real filesystem
        // just as it does in the fake, so an emptied mirror still exists and takes the other
        // path: a plan with no files, everything in MissingFromMirror, and a logged sentence
        // saying deletes are not applied by this version. That is Phase 2's question.
        //
        // This is the case that regressed: the folder itself gone.
        h.Fs.RemoveDirectory(Path.Combine(@"C:\repo", "plc", "A"));

        var outcome = await h.Orchestrator.HandleChangeAsync(new SyncJob
        {
            BindingId = SyncOrchestratorTests.BindingId,
            Source = ChangeSource.ExportedText,
            Confirmed = true,
            Synchronous = true,
        }, CancellationToken.None);

        Assert.Equal(SyncVerdict.Failed, outcome.Verdict);
        Assert.Contains("nothing to import", outcome.Explanation, StringComparison.Ordinal);

        // And no toolchain was asked to do it — refused before the launch, and before the
        // project was copied aside for an import that cannot happen.
        Assert.DoesNotContain(h.Codesys.Jobs, j => j.Operation == PlcOperation.Import);
    }

    [Fact]
    public async Task Planning_a_binding_nobody_tracks_answers_null_rather_than_throwing()
    {
        // Called speculatively, to fill in a dialog. A binding that went away with a
        // repository switch between the click and the read is an ordinary race, not an error
        // worth taking the window down over — and the import path treats null as "compute one
        // later", so nothing is lost either.
        var h = new SyncOrchestratorTests.Harness();

        Assert.Null(await h.Orchestrator.PlanImportAsync("no-such-binding", CancellationToken.None));
    }
}
