using Versio.Core.Bindings;
using Versio.Core.Locking;
using Versio.Core.Plc.Codesys;
using Versio.Core.Sync;
using Versio.Infrastructure.Bindings;
using Versio.Infrastructure.FileSystem;
using Versio.Infrastructure.Git;
using Versio.Infrastructure.Platform;
using Versio.Infrastructure.Plc;
using Versio.Infrastructure.Plc.Codesys;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// The milestone path: adopt a real project in a real repository, then export it.
/// </summary>
/// <remarks>
/// <para>
/// Every layer below this is already covered — the orchestrator against fakes, the runner
/// against real CODESYS, the git client against real git. What was never covered is the two
/// of them assembled, which is exactly where the wiring bugs live: a binding written with the
/// wrong path shape, an export landing outside the repository, a lock that is taken but never
/// persisted.
/// </para>
/// <para>
/// The second export is not a redundant assertion. Without it the first one could have
/// "succeeded" while recording nothing, and a green result that cannot fail is worse than a
/// red one — the same vacuous pass this project has already been caught by four times.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AdoptAndExportTests(ITestOutputHelper output) : IDisposable
{
    private const string ProjectRelativePath = "plc/RefrigeratorControl.project";

    private readonly string _repo = Path.Combine(
        Path.GetTempPath(), "Versio", "adopt", Guid.NewGuid().ToString("N")[..8]);

    private static CodesysInstall? Install =>
        new CodesysLocator().FindInstalls().FirstOrDefault(i => i.Profiles.Count > 0);

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_repo))
            {
                return;
            }

            // git marks objects read-only, so a plain delete fails.
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [SkippableFact]
    public async Task Adopting_writes_the_binding_and_the_attribute_rules()
    {
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");
        await InitRepoAsync();

        var git = new GitCliClient(_repo, logger: new TestLogger(output));
        var setup = new RepoSetup(_repo, CodesysProjectFormat.Instance);

        var result = await setup.AdoptAsync(
            ProjectRelativePath, git, "CODESYS V3.5 SP22 Patch 2", CancellationToken.None);

        Assert.True(result.Adopted, result.Decision.Explanation);

        var binding = Assert.Single(result.Config.Bindings);
        Assert.Equal(ProjectRelativePath, binding.ProjectPath);
        Assert.Equal("plc/RefrigeratorControl", binding.ExportPath);

        // Stored repo-relative with forward slashes, so a clone or a move does not break it.
        Assert.False(Path.IsPathRooted(binding.ProjectPath));
        Assert.DoesNotContain('\\', binding.ProjectPath);

        // Reloaded from disk rather than trusted from memory: the point of the file is that
        // a teammate's clone reads the same mapping back.
        var reloaded = await new RepoConfigStore(_repo).LoadAsync(CancellationToken.None);
        Assert.Equal(binding.Id, Assert.Single(reloaded.Bindings).Id);

        var attributes = await File.ReadAllTextAsync(setup.GitAttributesPath);
        output.WriteLine(attributes);
        Assert.Contains("*.project", attributes);
        Assert.Contains("binary -diff -merge", attributes);

        // LFS is written only where git can run it, so the assertion follows the probe
        // rather than assuming either answer.
        Assert.Equal(await git.IsLfsAvailableAsync(CancellationToken.None), result.LfsConfigured);
    }

    [SkippableFact]
    public async Task Adopting_the_same_project_twice_is_refused_rather_than_duplicated()
    {
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");
        await InitRepoAsync();

        var git = new GitCliClient(_repo, logger: new TestLogger(output));
        var setup = new RepoSetup(_repo, CodesysProjectFormat.Instance);

        await setup.AdoptAsync(ProjectRelativePath, git, null, CancellationToken.None);
        var second = await setup.AdoptAsync(ProjectRelativePath, git, null, CancellationToken.None);

        Assert.False(second.Adopted);
        Assert.Equal(AdoptionVerdict.AlreadyBound, second.Decision.Verdict);
        Assert.Single(second.Config.Bindings);

        // The attributes file must not grow a duplicate block on every attempt either.
        var attributes = await File.ReadAllTextAsync(setup.GitAttributesPath);
        Assert.Equal(1, CountOccurrences(attributes, "*.project"));
    }

    [SkippableFact]
    public async Task A_manual_export_writes_the_mirror_and_takes_the_lock()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        await InitRepoAsync();
        var git = new GitCliClient(_repo, logger: new TestLogger(output));
        var setup = new RepoSetup(_repo, CodesysProjectFormat.Instance);

        var adopted = await setup.AdoptAsync(
            ProjectRelativePath, git, install!.Profiles[0], CancellationToken.None);
        Assert.True(adopted.Adopted, adopted.Decision.Explanation);

        var binding = adopted.Config.Bindings[0];
        var orchestrator = BuildOrchestrator(adopted.Config, install);

        var outcome = await orchestrator.HandleChangeAsync(new SyncJob
        {
            BindingId = binding.Id,
            Source = ChangeSource.Project,
            Synchronous = true,
            Reason = "manual export",
        }, CancellationToken.None);

        output.WriteLine($"{outcome.Verdict}: {SyncNarrative.Describe(outcome)}");
        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);

        // The export must have actually produced the mirror, inside the repository.
        var exportRoot = Path.Combine(_repo, "plc", "RefrigeratorControl");
        Assert.True(Directory.Exists(exportRoot), $"{exportRoot} was not created");
        Assert.True(File.Exists(Path.Combine(exportRoot, BindingConventions.ManifestFileName)));

        var written = Directory.GetFiles(exportRoot, "*", SearchOption.AllDirectories);
        output.WriteLine($"{written.Length} file(s) written in {outcome.Duration.TotalSeconds:F1}s");
        Assert.True(written.Length > 1, "the export produced a manifest and nothing else");

        // The lock is taken and persisted before the work, so a crash mid-export leaves the
        // unsynced work guarded rather than looking clean to the next session.
        Assert.Equal(SyncDirection.ProjectAuthoritative, orchestrator.StateOf(binding.Id));
        Assert.True(File.Exists(new JsonLockStore(_repo).Path), "the lock was never persisted");

        // git now sees the mirror as new work — which is the entire point of the tool.
        var status = await git.StatusAsync(CancellationToken.None);
        Assert.Contains(status.Changes, c => c.Path.Contains("RefrigeratorControl/"));

        // And the proof the first result was not vacuous: nothing changed, so the hash gate
        // must refuse the second run.
        var again = await orchestrator.HandleChangeAsync(new SyncJob
        {
            BindingId = binding.Id,
            Source = ChangeSource.Project,
            Synchronous = true,
            Reason = "manual export",
        }, CancellationToken.None);

        Assert.Equal(SyncVerdict.NoChange, again.Verdict);
    }

    [SkippableFact]
    public async Task Editing_an_exported_source_file_is_detected_as_a_real_change()
    {
        // The whole point of the mirror, end to end on real files: export, open a .st, change
        // a line, save. This was silently impossible — the gate hashed _manifest.json, which
        // an export writes and a human never does, so the edit read as "nothing changed" and
        // the import was refused before the direction lock was consulted.
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        await InitRepoAsync();
        var git = new GitCliClient(_repo, logger: new TestLogger(output));
        var setup = new RepoSetup(_repo, CodesysProjectFormat.Instance);

        var adopted = await setup.AdoptAsync(
            ProjectRelativePath, git, install!.Profiles[0], CancellationToken.None);
        var binding = adopted.Config.Bindings[0];
        var orchestrator = BuildOrchestrator(adopted.Config, install);

        Assert.Equal(SyncVerdict.Completed, (await Export(orchestrator, binding.Id)).Verdict);
        await orchestrator.ReleaseAfterPushAsync("c1", CancellationToken.None);
        Assert.Equal(SyncDirection.Clean, orchestrator.StateOf(binding.Id));

        // Nothing has moved yet, so the gate must still say so. Without this the assertion
        // below proves only that the gate says "changed" to everything.
        Assert.Equal(SyncVerdict.NoChange, (await TextChanged(orchestrator, binding.Id)).Verdict);

        var exportRoot = Path.Combine(_repo, "plc", "RefrigeratorControl");
        var source = Directory.GetFiles(exportRoot, "*.st", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
        Skip.If(source is null, "the fixture exported no Structured Text to edit");

        output.WriteLine($"editing {Path.GetRelativePath(_repo, source!)}");
        await File.AppendAllTextAsync(source!, Environment.NewLine + "// edited by the engineer" + Environment.NewLine);

        var outcome = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"{outcome.Verdict}: {SyncNarrative.Describe(outcome)}");

        Assert.NotEqual(SyncVerdict.NoChange, outcome.Verdict);
        Assert.Equal(SyncDirection.TextAuthoritative, orchestrator.StateOf(binding.Id));
    }

    [SkippableFact]
    public async Task An_unconfirmed_import_writes_nothing_and_a_confirmed_one_reaches_the_project()
    {
        // The two-stage flow the panel drives, against a real project. The unconfirmed pass
        // must leave the .project byte-identical: if it did not, "propose" would already be
        // "apply" and the confirmation would be theatre.
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        await InitRepoAsync();
        var git = new GitCliClient(_repo, logger: new TestLogger(output));
        var setup = new RepoSetup(_repo, CodesysProjectFormat.Instance);

        var adopted = await setup.AdoptAsync(
            ProjectRelativePath, git, install!.Profiles[0], CancellationToken.None);
        var binding = adopted.Config.Bindings[0];

        var orchestrator = new SyncOrchestrator(
            _repo, adopted.Config.Bindings, Owner(), new SystemClock(), new WindowsFileSystem(),
            new LocalCodesysRunner(install.ExecutablePath, new ScriptExtractor(), new TestLogger(output)),
            new JsonLockStore(_repo),
            new OrchestratorOptions { RequireImportConfirmation = true });

        Assert.Equal(SyncVerdict.Completed, (await Export(orchestrator, binding.Id)).Verdict);
        await orchestrator.ReleaseAfterPushAsync("c1", CancellationToken.None);

        var source = Directory.GetFiles(
                Path.Combine(_repo, "plc", "RefrigeratorControl"), "*.st",
                SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
        Skip.If(source is null, "the fixture exported no Structured Text to edit");

        await File.AppendAllTextAsync(source!, Environment.NewLine + "// edited" + Environment.NewLine);

        var projectPath = Path.Combine(_repo, "plc", "RefrigeratorControl.project");
        var before = await File.ReadAllBytesAsync(projectPath);

        var proposed = await TextChanged(orchestrator, binding.Id, confirmed: false);
        Assert.Equal(SyncVerdict.AwaitingConfirmation, proposed.Verdict);
        Assert.Equal(before, await File.ReadAllBytesAsync(projectPath));
        Assert.Equal(SyncDirection.Clean, orchestrator.StateOf(binding.Id));

        var applied = await TextChanged(orchestrator, binding.Id, confirmed: true);
        output.WriteLine($"{applied.Verdict}: {SyncNarrative.Describe(applied)}");

        Assert.Equal(SyncVerdict.Completed, applied.Verdict);
        Assert.Equal(SyncDirection.TextAuthoritative, orchestrator.StateOf(binding.Id));
        Assert.NotEqual(before, await File.ReadAllBytesAsync(projectPath));
    }

    private LockOwner Owner()
    {
        var machine = new MachineIdentity();
        return new LockOwner
        {
            Machine = machine.MachineName,
            User = machine.UserName,
            SessionId = Guid.NewGuid().ToString("N"),
            ProcessId = machine.CurrentProcessId,
            BootId = machine.BootId,
        };
    }

    private static Task<SyncOutcome> Export(SyncOrchestrator orchestrator, string bindingId) =>
        orchestrator.HandleChangeAsync(new SyncJob
        {
            BindingId = bindingId,
            Source = ChangeSource.Project,
            Synchronous = true,
            Reason = "manual export",
        }, CancellationToken.None);

    private static Task<SyncOutcome> TextChanged(
        SyncOrchestrator orchestrator, string bindingId, bool confirmed = true) =>
        orchestrator.HandleChangeAsync(new SyncJob
        {
            BindingId = bindingId,
            Source = ChangeSource.ExportedText,
            Confirmed = confirmed,
            Synchronous = true,
            Reason = "manual import",
        }, CancellationToken.None);

    [SkippableFact]
    public async Task An_export_without_a_toolchain_fails_with_a_reason_rather_than_a_null()
    {
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");
        await InitRepoAsync();

        var git = new GitCliClient(_repo, logger: new TestLogger(output));
        var setup = new RepoSetup(_repo, CodesysProjectFormat.Instance);
        var adopted = await setup.AdoptAsync(ProjectRelativePath, git, null, CancellationToken.None);
        var binding = adopted.Config.Bindings[0];

        var orchestrator = BuildOrchestrator(
            adopted.Config, install: null,
            runner: new UnavailableToolchainRunner("CODESYS is not installed on this machine"));

        var outcome = await orchestrator.HandleChangeAsync(new SyncJob
        {
            BindingId = binding.Id,
            Source = ChangeSource.Project,
            Synchronous = true,
        }, CancellationToken.None);

        Assert.Equal(SyncVerdict.Failed, outcome.Verdict);
        Assert.Contains("not installed", SyncNarrative.Describe(outcome));

        // A failed binding stops until acknowledged: silently resuming after a failure is how
        // half-written state becomes permanent.
        Assert.Equal(SyncDirection.Failed, orchestrator.StateOf(binding.Id));
    }

    private static string FixtureProject =>
        Core.Tests.TestPaths.Project("RefrigeratorControl.project");

    private SyncOrchestrator BuildOrchestrator(
        RepoConfig config, CodesysInstall? install, Versio.Core.Plc.IPlcRunner? runner = null)
    {
        var machine = new MachineIdentity();
        return new SyncOrchestrator(
            _repo,
            config.Bindings,
            new LockOwner
            {
                Machine = machine.MachineName,
                User = machine.UserName,
                SessionId = Guid.NewGuid().ToString("N"),
                ProcessId = machine.CurrentProcessId,
                BootId = machine.BootId,
            },
            new SystemClock(),
            new WindowsFileSystem(),
            runner ?? new LocalCodesysRunner(
                install!.ExecutablePath, new ScriptExtractor(), new TestLogger(output)),
            new JsonLockStore(_repo));
    }

    private async Task InitRepoAsync()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "plc"));
        File.Copy(FixtureProject, Path.Combine(_repo, "plc", "RefrigeratorControl.project"));

        await RunGitAsync("init", "-b", "main");
        await RunGitAsync("config", "user.email", "test@versio.local");
        await RunGitAsync("config", "user.name", "Versio Test");
    }

    private async Task RunGitAsync(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
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

        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
