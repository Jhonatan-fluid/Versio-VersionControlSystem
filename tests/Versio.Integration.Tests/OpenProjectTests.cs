using System.Diagnostics;
using Versio.Core.Bindings;
using Versio.Core.Locking;
using Versio.Core.Plc;
using Versio.Plc.Codesys;
using Versio.Core.Sync;
using Versio.Infrastructure.Bindings;
using Versio.Infrastructure.FileSystem;
using Versio.Infrastructure.Git;
using Versio.Infrastructure.Platform;
using Versio.Plc.Codesys.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// Working while CODESYS has the project open — against a real CODESYS holding it.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests prove the state machine does the right thing when it is <i>told</i> the
/// project is held. Only this one proves it is told: that the marker is where the format says,
/// that the pid in it is live, and — the part nothing else can establish — that exporting a
/// copy actually succeeds against a CODESYS that is refusing the original.
/// </para>
/// <para>
/// A second CODESYS is launched to hold the project, which is what an engineer's open IDE looks
/// like to everything downstream: the same lock file, the same refusal.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Toolchain", "Codesys")]
[Collection(CodesysCollection.Name)]
public sealed class OpenProjectTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _repo = Path.Combine(
        Path.GetTempPath(), "Versio", "openproj", Guid.NewGuid().ToString("N")[..8]);

    private Process? _holder;

    public void Dispose()
    {
        try
        {
            if (_holder is { HasExited: false })
            {
                _holder.Kill(entireProcessTree: true);
                _holder.WaitForExit(10_000);
            }
        }
        catch (Exception)
        {
            // Best effort; the test process is going away regardless.
        }

        try
        {
            if (Directory.Exists(_repo))
            {
                foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(_repo, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private static CodesysInstall? Install =>
        new CodesysLocator().FindInstalls().FirstOrDefault(i => i.Profiles.Count > 0);

    /// <summary>Launch a CODESYS that opens the project and sits on it.</summary>
    /// <remarks>
    /// A script that opens and sleeps. Indistinguishable, from the outside, from an engineer
    /// who saved and went to a meeting — the same <c>.~u</c> and the same refusal.
    /// </remarks>
    private async Task<bool> HoldProjectAsync(CodesysInstall install, string project, int seconds)
    {
        var script = Path.Combine(_repo, "hold.py");
        var job = Path.Combine(_repo, "hold.json");

        await File.WriteAllTextAsync(script, """
            import json, sys, time
            import scriptengine
            with open(sys.argv[1], "r") as f:
                job = json.load(f)
            proj = scriptengine.projects.open(job["project"])
            time.sleep(job["seconds"])
            """);

        await File.WriteAllTextAsync(job,
            $$"""{"project": {{System.Text.Json.JsonSerializer.Serialize(project)}}, "seconds": {{seconds}}}""",
            new System.Text.UTF8Encoding(false));

        // BuildArguments, not ArgumentList. ArgumentList quotes the whole "--profile=A B C"
        // pair, which CODESYS does not parse: it exits 1 in under a second with no output.
        // Reusing the runner's own builder is also what keeps this test honest - it launches
        // CODESYS the same way the product does.
        var psi = new ProcessStartInfo(install.ExecutablePath)
        {
            Arguments = LocalCodesysRunner.BuildArguments(install.Profiles[0], script, job),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        output.WriteLine($"holder: {install.ExecutablePath} {psi.Arguments}");
        _holder = Process.Start(psi);

        // Wait for the lock to appear rather than for a fixed time: CODESYS takes as long as
        // it takes, and a sleep long enough to be safe would be most of a minute.
        var marker = CodesysProjectFormat.Instance.OccupancyMarkerFor(project)!;
        for (var i = 0; i < 60 && !File.Exists(marker); i++)
        {
            if (_holder is { HasExited: true })
            {
                return false;
            }

            await Task.Delay(1000);
        }

        return File.Exists(marker);
    }

    [SkippableFact]
    public async Task An_export_succeeds_while_CODESYS_holds_the_project_open()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");

        var fixture = Core.Tests.TestPaths.Project("RefrigeratorControl.project");
        Skip.If(!File.Exists(fixture), "fixture project missing");

        Directory.CreateDirectory(Path.Combine(_repo, "plc"));
        var project = Path.Combine(_repo, "plc", "RefrigeratorControl.project");
        File.Copy(fixture, project);

        Skip.IfNot(await HoldProjectAsync(install!, project, seconds: 150),
                   "CODESYS did not take the project");

        // The premise, asserted rather than assumed: a real lock, naming a live process.
        var marker = CodesysProjectFormat.Instance.OccupancyMarkerFor(project)!;
        output.WriteLine($"marker: {marker}");
        output.WriteLine(await File.ReadAllTextAsync(marker));

        var binding = new ProjectBinding
        {
            Id = "b-open",
            ProjectPath = "plc/RefrigeratorControl.project",
            ExportPath = "plc/RefrigeratorControl",
            Profile = install.Profiles[0],
        };

        var probe = new ProjectOccupancyProbe(
            new WindowsFileSystem(), new MachineIdentity(), new ProcessProbe());

        var occupancy = await probe.CheckAsync(binding, project, CancellationToken.None);
        output.WriteLine($"occupancy: {occupancy.Verdict} — {occupancy.Owner}");

        Assert.Equal(OccupancyVerdict.Held, occupancy.Verdict);
        Assert.True(occupancy.IsHeld);

        var orchestrator = new SyncOrchestrator(
            _repo, [binding],
            new LockOwner
            {
                Machine = "TEST-PC", User = "tester", SessionId = "s1",
                ProcessId = Environment.ProcessId, BootId = DateTimeOffset.UtcNow,
            },
            new SystemClock(), new WindowsFileSystem(),
            new LocalCodesysRunner(install.ExecutablePath, new ScriptExtractor(),
                                   new TestLogger(output)),
            new JsonLockStore(_repo),
            new OrchestratorOptions { RequireImportConfirmation = false },
            logger: new TestLogger(output),
            occupancy: probe);

        // Export: reads a copy, and succeeds against a CODESYS that is refusing the original.
        var exported = await orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = binding.Id, Source = ChangeSource.Project, Synchronous = true },
            CancellationToken.None);

        output.WriteLine(SyncNarrative.Describe(exported));
        Assert.Equal(SyncVerdict.Completed, exported.Verdict);

        var manifest = Path.Combine(_repo, "plc", "RefrigeratorControl", "_manifest.json");
        Assert.True(File.Exists(manifest), "the export produced no manifest");

        // The scratch copy is scaffolding, not a backup: nothing in that folder outlives the
        // run. Opening a project makes CODESYS write its own sidecars beside it, so deleting
        // only the file we created would leave those behind for good.
        var scratch = Path.Combine(_repo, ".versio", "local", "open", binding.Id);
        var leftovers = Directory.Exists(scratch)
            ? Directory.EnumerateFiles(scratch).Select(Path.GetFileName).ToList()
            : [];

        Assert.True(leftovers.Count == 0,
                    "the scratch folder still holds: " + string.Join(", ", leftovers));
    }

    [SkippableFact]
    public async Task An_import_waits_and_says_who_has_the_project()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");

        var fixture = Core.Tests.TestPaths.Project("RefrigeratorControl.project");
        Skip.If(!File.Exists(fixture), "fixture project missing");

        await InitRepoAsync();

        Directory.CreateDirectory(Path.Combine(_repo, "plc"));
        var project = Path.Combine(_repo, "plc", "RefrigeratorControl.project");
        File.Copy(fixture, project);

        var git = new GitCliClient(_repo, logger: new TestLogger(output));
        var setup = new RepoSetup(_repo, CodesysProjectFormat.Instance);
        var adopted = await setup.AdoptAsync("plc/RefrigeratorControl.project", git,
                                             install!.Profiles[0], CancellationToken.None);
        var binding = adopted.Config.Bindings[0];

        var probe = new ProjectOccupancyProbe(
            new WindowsFileSystem(), new MachineIdentity(), new ProcessProbe());

        var orchestrator = new SyncOrchestrator(
            _repo, [binding],
            new LockOwner
            {
                Machine = "TEST-PC", User = "tester", SessionId = "s1",
                ProcessId = Environment.ProcessId, BootId = DateTimeOffset.UtcNow,
            },
            new SystemClock(), new WindowsFileSystem(),
            new LocalCodesysRunner(install.ExecutablePath, new ScriptExtractor(),
                                   new TestLogger(output)),
            new JsonLockStore(_repo),
            new OrchestratorOptions { RequireImportConfirmation = false },
            logger: new TestLogger(output),
            occupancy: probe);

        // Export first, so there is a mirror to edit.
        Assert.Equal(SyncVerdict.Completed, (await orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = binding.Id, Source = ChangeSource.Project, Synchronous = true },
            CancellationToken.None)).Verdict);

        await orchestrator.ReleaseAfterPushAsync("c1", CancellationToken.None);

        var st = Directory
            .EnumerateFiles(Path.Combine(_repo, "plc", "RefrigeratorControl"), "*.st",
                            SearchOption.AllDirectories)
            .First();

        await File.AppendAllTextAsync(st, Environment.NewLine + "(* edited while open *)" + Environment.NewLine);

        var before = await File.ReadAllBytesAsync(project);

        Skip.IfNot(await HoldProjectAsync(install, project, seconds: 90),
                   "CODESYS did not take the project");

        var outcome = await orchestrator.HandleChangeAsync(
            new SyncJob { BindingId = binding.Id, Source = ChangeSource.ExportedText,
                          Confirmed = true, Synchronous = true },
            CancellationToken.None);

        var sentence = SyncNarrative.Describe(outcome);
        output.WriteLine(sentence);

        // A wait, not a failure — the whole point.
        Assert.Equal(SyncVerdict.BlockedByOpenProject, outcome.Verdict);
        Assert.False(outcome.IsFailure);
        Assert.NotEqual(SyncDirection.Failed, orchestrator.StateOf(binding.Id));

        Assert.Contains("open in the IDE", sentence, StringComparison.Ordinal);
        Assert.Contains("Close it", sentence, StringComparison.Ordinal);

        // And the project is untouched, which is the thing that actually matters.
        Assert.Equal(before, await File.ReadAllBytesAsync(project));
    }

    private async Task InitRepoAsync()
    {
        Directory.CreateDirectory(_repo);
        foreach (var args in new[]
                 {
                     new[] { "init", "-b", "main" },
                     ["config", "user.email", "test@versio.local"],
                     ["config", "user.name", "Versio Test"],
                 })
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = _repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
        }
    }
}
