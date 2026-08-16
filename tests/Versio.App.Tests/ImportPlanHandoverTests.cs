using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Plc;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The plan the question was asked about is the plan the toolchain is given.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one link in the carriage that cannot be checked from
/// <c>Versio.Core.Tests</c></b>, because it is the view model's: everything downstream of
/// <c>SyncJob.Plan</c> is covered by <c>ImportPlanCarriageTests</c>, and cutting any of those
/// turns that suite red. Cutting <i>this</i> one turned nothing red at all — the whole app
/// suite passed with the plan discarded — which is exactly the shape of defect the orchestrator
/// is designed to absorb: a missing plan is silently replaced by one it computes itself, so the
/// import runs, succeeds, and reports the same numbers.
/// </para>
/// <para>
/// <b>Asserting the plan is non-null would not catch it</b>, and neither would comparing its
/// contents, because a plan recomputed against an unchanged mirror is identical to the one that
/// was agreed to. The two are only distinguishable if the mirror moves in between — so this
/// test moves it, from inside the confirmation prompt, which is the one moment that is
/// guaranteed to sit between the plan being computed and the import being run.
/// </para>
/// <para>
/// That is not a contrivance to make a test work. It is the actual race: a dialog is open for
/// as long as somebody takes to read it, and Versio watches the mirror the whole time. Once the
/// dialog names deletions, "the mirror changed while the dialog was open" is the difference
/// between removing three objects and removing four — and <c>remove()</c> was measured to take
/// a referenced object out silently, with nothing downstream to object (P3).
/// </para>
/// </remarks>
public sealed class ImportPlanHandoverTests
{
    [Fact]
    public async Task A_file_that_appears_while_the_dialog_is_open_is_not_in_the_import()
    {
        using var repo = new TempRepo();
        ImportConfirmationTests.GivenATrackedProject(repo);

        var toolchain = new CapturingToolchain();
        using var shell = TestShell.Build(out var sink, toolchain: toolchain);
        await shell.OpenAsync(repo.Path);

        // A toolchain being PRESENT changes what opening does, and every other test in this
        // suite runs without one. Versio catches up on open — the project has changed since it
        // was last exported, so it exports — and that takes the direction lock to
        // ProjectAuthoritative, which correctly refuses an import afterwards.
        //
        // Released the way the application itself releases it: ShellViewModel calls exactly
        // this after discarding. It is not a test back door — it is the ordinary "I threw that
        // change away" path, and without it this scenario cannot be reached at all, which is
        // itself worth knowing.
        await shell.Sync.ReleaseAfterDiscardAsync("b1", CancellationToken.None);

        await ImportConfirmationTests.EditTheMirrorAndSettle(repo, shell);

        // Guarded rather than assumed. With a toolchain present the settle path can take a
        // different branch than it does in the rest of this suite, where there is none — and a
        // "the button never appeared" timeout thirty seconds later says nothing about why.
        Assert.True(shell.Sync.HasPendingImport,
            "no import is waiting. log:\n" + string.Join("\n",
                sink.Snapshot().Select(e => $"{e.Level} {e.Category} {e.Message}")));

        var appeared = Path.Combine(repo.Path, "plc", "Machine", "App", "Appeared.st");

        shell.Asked().Import = _ =>
        {
            // Runs after the plan has been decided and before the import runs — the window a
            // person spends reading the question. A colleague's pull, a watcher-driven
            // re-export, or their own editor saving lands here.
            File.WriteAllText(appeared, "FUNCTION_BLOCK Appeared\n");
            return true;
        };

        await Eventually.Press(shell.Sync.ConfirmImportCommand, "the import button");

        var job = Assert.Single(toolchain.Jobs, j => j.Operation == PlcOperation.Import);
        var plan = Assert.Single(job.Projects).Plan;

        Assert.NotNull(plan);

        // The file really did appear, or this test proves nothing at all.
        Assert.True(File.Exists(appeared), "the fixture did not write the late file");

        // And it is NOT in the plan. A recomputed plan would carry it as a Create — which is
        // how this assertion tells "the agreed plan travelled" apart from "an identical plan
        // was rebuilt", the two the mirror has to move to distinguish.
        Assert.DoesNotContain(plan.Files, f => f.Path.EndsWith("Appeared.st", StringComparison.Ordinal));

        // The edit that prompted the import IS in it, so the plan is the right one rather than
        // merely an old one.
        Assert.Contains(plan.Files, f => f.Path == "App/FB_Motor.st");
    }
}
