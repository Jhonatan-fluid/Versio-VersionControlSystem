using Versio.Core.Codesys;
using Versio.Infrastructure.Codesys;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// Drives a real CODESYS installation through the C# runner.
/// </summary>
/// <remarks>
/// Skips cleanly when CODESYS or the fixture project is absent, so the suite still runs on
/// a machine without them. These are slow (~20 s each, dominated by CODESYS cold start) and
/// deliberately separated from the unit project.
/// </remarks>
[Trait("Category", "Integration")]
public class CodesysRunnerTests(ITestOutputHelper output)
{
    private const string FixtureProject = @"C:\VERSIO\fixtures\projects\RefrigeratorControl.project";

    private static CodesysLocator Locator => new();

    private static CodesysInstall? Install =>
        Locator.FindInstalls().FirstOrDefault(i => i.Profiles.Count > 0);

    [SkippableFact]
    public void The_locator_finds_a_real_install_with_a_usable_profile()
    {
        var installs = Locator.FindInstalls();
        Skip.If(installs.Count == 0, "no CODESYS installation on this machine");

        var install = installs[0];
        output.WriteLine($"install : {install.InstallFolder}  (file version {install.FileVersion})");
        foreach (var p in install.Profiles)
        {
            output.WriteLine($"profile : {p}");
        }

        Assert.True(File.Exists(install.ExecutablePath));
        Assert.NotEmpty(install.Profiles);
    }

    [SkippableFact]
    public void The_profile_name_is_read_from_disk_and_differs_from_the_install_folder()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");

        // The trap this guards: folder "CODESYS 3.5.22.20" vs profile
        // "CODESYS V3.5 SP22 Patch 2". Deriving one from the other produces a profile that
        // does not exist, and CODESYS then refuses to start at all.
        var profile = install!.Profiles[0];

        Assert.DoesNotContain(profile, install.InstallFolder);
        Assert.StartsWith("CODESYS", profile);
    }

    [SkippableFact]
    public void Scripts_are_embedded_and_extracted()
    {
        var extractor = new ScriptExtractor();
        var names = extractor.ScriptNames();
        output.WriteLine($"extracted to: {extractor.Root}");
        foreach (var n in names)
        {
            output.WriteLine($"  {n}");
        }

        Assert.Contains("versio_export.py", names);
        Assert.Contains("versio_import.py", names);
        Assert.Contains("versio_types.py", names);
        Assert.True(File.Exists(extractor.PathFor("versio_export.py")));
    }

    [SkippableFact]
    public async Task An_export_runs_end_to_end_through_the_runner()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.IfNot(File.Exists(FixtureProject), "fixture project not present");

        var work = Path.Combine(Path.GetTempPath(), "Versio", "it", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var project = Path.Combine(work, "RefrigeratorControl.project");
        File.Copy(FixtureProject, project);
        var outDir = Path.Combine(work, "export");

        var runner = new LocalCodesysRunner(install!.ExecutablePath, new ScriptExtractor(),
            log: output.WriteLine);

        var seen = new List<CodesysProgress>();
        var progress = new Progress<CodesysProgress>(p => seen.Add(p));

        var job = new CodesysJob
        {
            Operation = CodesysOperation.Export,
            Profile = install.Profiles[0],
            Projects = [new ProjectJob { BindingId = "it-1", ProjectPath = project, Path = outDir }],
            Timeout = TimeSpan.FromMinutes(5),
        };

        var result = await runner.RunAsync(job, progress, CancellationToken.None);

        output.WriteLine($"exit={result.ExitCode} timedOut={result.TimedOut} " +
                         $"duration={result.Duration.TotalSeconds:F1}s");
        foreach (var line in result.Output.Chatter.Take(10))
        {
            output.WriteLine($"chatter: {line}");
        }

        Assert.True(result.Succeeded, result.FailureReason ?? "no reason given");
        Assert.Null(result.FailureReason);
        Assert.False(result.MissingProfile);

        var summary = result.Output.Summary;
        Assert.NotNull(summary);
        output.WriteLine($"summary: {summary!.Raw}");
        Assert.True(summary.Int("written") > 0);
        Assert.Equal(0, summary.Int("warnings"));

        // The export actually landed on disk, with both GVL forms present.
        var files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
        Assert.Contains(files, f => f.EndsWith("_manifest.json", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("Simulation.st", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("Glob_Var.gvl.st", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("Glob_Var.gvl.xml", StringComparison.Ordinal));

        Directory.Delete(work, recursive: true);
    }

    [SkippableFact]
    public async Task A_missing_profile_is_reported_as_such_rather_than_a_bare_exit_code()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation");

        var runner = new LocalCodesysRunner(install!.ExecutablePath, new ScriptExtractor(),
            log: output.WriteLine);

        var job = new CodesysJob
        {
            Operation = CodesysOperation.Probe,
            Profile = "",                    // the failure under test
            Projects = [new ProjectJob { BindingId = "x", ProjectPath = "nope", Path = "nope" }],
            Timeout = TimeSpan.FromMinutes(2),
        };

        var result = await runner.RunAsync(job, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.MissingProfile,
            $"expected the missing-profile refusal; chatter was: " +
            string.Join(" | ", result.Output.Chatter.Take(5)));
        Assert.Contains("--profile", result.FailureReason);
    }

    [SkippableFact]
    public async Task Cancellation_terminates_the_process_tree()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.IfNot(File.Exists(FixtureProject), "fixture project not present");

        var runner = new LocalCodesysRunner(install!.ExecutablePath, new ScriptExtractor(),
            log: output.WriteLine);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var job = new CodesysJob
        {
            Operation = CodesysOperation.Export,
            Profile = install.Profiles[0],
            Projects =
            [
                new ProjectJob
                {
                    BindingId = "cancel",
                    ProjectPath = FixtureProject,
                    Path = Path.Combine(Path.GetTempPath(), "Versio", "cancel-test"),
                },
            ],
        };

        // Cancelling mid-run must surface as cancellation, not as a hang or a silent success.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(job, null, cts.Token));
    }
}
