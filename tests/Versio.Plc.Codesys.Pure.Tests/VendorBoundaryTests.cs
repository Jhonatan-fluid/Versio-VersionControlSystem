using Versio.Core.Plc;
using Versio.Plc.Codesys.Infrastructure;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// The translation from a neutral job into CODESYS's own terms, asserted without starting
/// CODESYS.
/// </summary>
/// <remarks>
/// <para>
/// <c>LocalCodesysRunner.RunAsync</c> is the vendor boundary — above it the sync engine speaks
/// <see cref="PlcJob"/>; below it everything is CODESYS. The translation used to be inline in
/// that method, which meant the only way to check that a field survives the crossing was a
/// 17–31 s run against a real install, and in practice nothing checked at all.
/// </para>
/// <para>
/// <b>That became a real hazard the moment the job grew a field whose loss is silent.</b>
/// <c>RunAsync</c> computes an import plan when a job arrives without one, so a dropped
/// <c>Plan</c> does not fail, warn, or change a count — the import runs, succeeds, and
/// executes a plan that is not the one the person was shown. Nothing downstream can tell the
/// difference, which is why the check has to be here.
/// </para>
/// <para>
/// In this project rather than <c>Versio.Core.Tests</c> only because
/// <c>Versio.Infrastructure</c> is where the runner lives. It needs no CODESYS, no python and
/// no git, and runs in milliseconds.
/// </para>
/// </remarks>
public sealed class VendorBoundaryTests
{
    private static PlcJob ImportJob(ImportPlan? plan) => new()
    {
        Operation = PlcOperation.Import,
        Profile = "CODESYS V3.5 SP22 Patch 2",
        OperationId = "op12345",
        Projects =
        [
            new PlcProjectJob
            {
                BindingId = "b-1",
                ProjectPath = @"C:\repo\plc\A.project",
                Path = @"C:\repo\plc\A",
                Plan = plan,
            },
        ],
    };

    [Fact]
    public void The_import_plan_survives_the_crossing_into_CODESYS_terms()
    {
        var plan = new ImportPlan
        {
            Files =
            [
                new PlannedFile
                {
                    Path = "App/FB_Motor.st",
                    Action = PlannedAction.ApplyText,
                    Name = "FB_Motor",
                    Guid = "6f9dac99-8de1-4efc-8465-68ac443b7d08",
                    Resolve = ["Device", "Plc Logic", "Application", "FB_Motor"],
                },
            ],
            MissingFromMirror = ["App/Gone.st"],
            Warnings = ["something a person should read"],
        };

        var native = LocalCodesysRunner.ToNative(ImportJob(plan));

        var carried = Assert.Single(native.Projects).Plan;
        Assert.NotNull(carried);

        // Field by field rather than by reference, so a future translation that rebuilds the
        // record instead of passing it still has to carry everything the script reads.
        var file = Assert.Single(carried.Files);
        Assert.Equal("App/FB_Motor.st", file.Path);
        Assert.Equal(PlannedAction.ApplyText, file.Action);
        Assert.Equal("FB_Motor", file.Name);
        Assert.Equal("6f9dac99-8de1-4efc-8465-68ac443b7d08", file.Guid);
        Assert.Equal(["Device", "Plc Logic", "Application", "FB_Motor"], file.Resolve);

        // Phase 2's delete candidates. Nothing acts on them yet, and they still have to
        // cross: the confirmation dialog is what names them, and dropping them here would
        // make the dialog and the run disagree about the one thing that destroys work.
        Assert.Equal(["App/Gone.st"], carried.MissingFromMirror);
    }

    [Fact]
    public void A_job_with_no_plan_crosses_as_a_job_with_no_plan()
    {
        // Null must stay null rather than becoming an empty plan. An empty plan is a valid
        // instruction meaning "do nothing to any file"; null means "nobody has decided yet",
        // and the runner computes one. Collapsing the two would turn every import that came
        // through a door without a planner into a silent no-op that reports success.
        var native = LocalCodesysRunner.ToNative(ImportJob(plan: null));

        Assert.Null(Assert.Single(native.Projects).Plan);
    }
}
