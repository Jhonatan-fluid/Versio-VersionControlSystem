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
[Collection(CodesysCollection.Name)]
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
    public async Task Repo_config_is_written_with_LF_so_a_clone_does_not_show_it_modified()
    {
        // repo.json is committed by design - it is how a teammate gets the same project
        // mapping - and .gitattributes stores it LF. System.Text.Json indents with
        // Environment.NewLine, so on Windows it was written CRLF: the file showed as modified
        // the moment anyone cloned, and again every time Versio touched it.
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");
        await InitRepoAsync();

        var store = new RepoConfigStore(_repo);
        await store.SaveAsync(
            new RepoConfig
            {
                Bindings =
                [
                    new ProjectBinding
                    {
                        Id = "b-eol",
                        ProjectPath = ProjectRelativePath,
                        ExportPath = "plc/RefrigeratorControl",
                    },
                ],
            }, CancellationToken.None);

        var text = await File.ReadAllTextAsync(store.Path);
        output.WriteLine(text);

        // By name, not by ordinal. "vendor": 0 tells a reviewer nothing, and inserting a
        // vendor into PlcVendor would silently repoint every existing binding at a different
        // toolchain - in a file that is committed and shared, that is a data bug that
        // announces itself only as an export driven by the wrong tool.
        Assert.Contains("\"vendor\": \"Codesys\"", text, StringComparison.Ordinal);

        var bytes = await File.ReadAllBytesAsync(store.Path);
        var carriageReturns = bytes.Count(b => b == (byte)'\r');

        output.WriteLine($"{bytes.Length} bytes, {carriageReturns} CR");
        Assert.Equal(0, carriageReturns);

        // And it must still parse back to what went in, since the point is a shared mapping.
        var reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("b-eol", Assert.Single(reloaded.Bindings).Id);
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

    [SkippableFact]
    public async Task A_task_priority_edited_in_the_mirror_reaches_the_project_and_survives_a_second_import()
    {
        // THE TEST PHASE 4 SHIPPED WITHOUT, and the two axes G11 hid behind, in one run.
        //
        // Phase 4 stopped handing Task Configuration to import_native and started setting the
        // fields through typed properties instead. That closed G7 - CODESYS rewriting a task's
        // CPU core affinity on every import, unasked - and G11 - every import after the first
        // throwing on MainTask and VISU_TASK while the summary read `failed: 0`.
        //
        // Everything around the write was covered: the emitter, the reader, the committed
        // bytes, the TYPED_SIBLING skip. `setattr(obj, prop, wanted)` was covered by one
        // hand-run mutation and nothing else - the single step the whole G7/G11 argument rests
        // on. That is the same shape of gap that let G11 live for months, so it is closed here
        // rather than left in Phase 6.
        //
        // Four CODESYS launches, measured at 72-78 s. That is the price of the only assertion
        // that can distinguish "the importer applied the file" from "the importer read it",
        // and it is the most expensive single test in the suite. It earns it: deleting either
        // half of Phase 4 turns it red, verified both ways rather than assumed - see the note
        // above the re-export below.
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

        var tasksFile = Path.Combine(
            _repo, "plc", "RefrigeratorControl", "Device", "Plc Logic", "Application",
            "Task Configuration.tasks.yaml");
        Assert.True(File.Exists(tasksFile), $"the export wrote no {Path.GetFileName(tasksFile)}");

        // The starting values, asserted rather than assumed. If the fixture ever changes so
        // that MainTask already has the priority written below, the test would set a field to
        // what it already held and pass while proving nothing.
        var exported = await File.ReadAllTextAsync(tasksFile);
        Assert.Equal("1", FieldOf(exported, "MainTask", "priority"));
        Assert.Equal("-1", FieldOf(exported, "VISU_TASK", "coreBinding"));

        // LF and no BOM: adoption writes `<mirror>/** text eol=lf`, and Versio's hash gate
        // reads the bytes on disk rather than asking git.
        await File.WriteAllTextAsync(
            tasksFile, WithField(exported, "MainTask", "priority", "\"9\""),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // WHAT IS BESIDE THE PROJECT AT EACH IMPORT, both recorded, because "the first import
        // creates the cache" had never been checked from a test, and an EXPORT has already run
        // by this point - which opens the project too, so it was a fair suspect.
        //
        // Measured, and the two lines together say which tool writes which sidecar:
        //
        //   before import 1   the .opt pair, and NO cache   <- the export wrote the .opt files
        //   before import 2   the .opt pair AND the cache   <- the import wrote the cache
        //
        // So the finding's claim holds, and it is now measured here rather than inherited from
        // Measure-ImportStability.ps1. It also explains why copying the committed .opt files in
        // was watching nothing: the export had already produced them.
        output.WriteLine($"beside the project before import 1: {string.Join(", ", Sidecars())}");

        // Import once. This is the cycle that always worked; the second one below is the one
        // that used to fail.
        var first = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import 1 — {first.Verdict}: {SyncNarrative.Describe(first)}");
        Assert.Equal(SyncVerdict.Completed, first.Verdict);

        // A SECOND, DIFFERENT edit, and it is not padding. Re-running the same import is
        // refused as NoChange - correctly, by the hash gate, because the mirror has not moved
        // since the last one - so a second import needs something new to carry. That is not a
        // workaround: G11's trigger was the compile cache and the measurement recorded that the
        // mirror's bytes are irrelevant, so any second import with the cache present reproduces
        // the condition.
        //
        // It also makes the final assertion sharper than a repeat would have been. The value
        // ends at 7 only if the SECOND import applied - the cycle that used to throw on
        // MainTask while reporting failed: 0. A test that imported 9 twice would read 9 at the
        // end whether the second cycle worked or did nothing at all.
        await File.WriteAllTextAsync(
            tasksFile,
            WithField(await File.ReadAllTextAsync(tasksFile), "MainTask", "priority", "\"7\""),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // WHICH STATE THE SECOND IMPORT IS ABOUT TO RUN IN, asserted rather than assumed.
        // G11's trigger was a `<project>_project.precompilecache` beside the project, and this
        // is the point at which one exists - see the two measurements above.
        //
        // NEITHER SIDECAR IS COPIED IN, and that was worth finding out. This test originally
        // copied the committed `.opt` files beside the project; deleting that copy left every
        // assertion here green, because the export writes its own pair. So the copy was
        // watching nothing and is gone - see InitRepoAsync.
        //
        // ASSERTED rather than merely printed, because the whole G11 axis rests on it. If a
        // future CODESYS stops writing a cache on a headless import, this test would quietly
        // decay into "imports twice" - still useful, but no longer covering the condition it
        // was written for, and nothing would say so. This is what says so.
        var sidecars = Sidecars();
        output.WriteLine($"beside the project before import 2: {string.Join(", ", sidecars)}");
        Assert.Contains(sidecars,
                        f => f.EndsWith(".precompilecache", StringComparison.Ordinal));
        Assert.Contains(sidecars, f => f.EndsWith(".opt", StringComparison.Ordinal));

        // Import again, with whatever the first one left behind. THIS is the cycle that used
        // to throw twice and report failed: 0. Asserting on the warnings rather than on the
        // verdict is the whole point: the verdict was Completed while it was broken.
        var second = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import 2 — {second.Verdict}: {SyncNarrative.Describe(second)}");
        Assert.Equal(SyncVerdict.Completed, second.Verdict);

        foreach (var outcome in new[] { first, second })
        {
            Assert.DoesNotContain(outcome.Warnings,
                                  w => w.StartsWith("NATIVE_IMPORT_ERROR", StringComparison.Ordinal));
            Assert.DoesNotContain(outcome.Warnings,
                                  w => w.StartsWith("TASK_", StringComparison.Ordinal));
        }

        // WHAT THESE TWO ASSERTIONS ARE AND ARE NOT WATCHING, because it was measured rather
        // than assumed. Reverting Phase 4 - making typed_sibling_for return None, so the task
        // XML goes back to import_native - turns this test red, but at the priority assertion
        // below and NOT here: the native import re-applies the whole configuration from the
        // XML and silently overwrites the typed write, without throwing. G11's exception did
        // not reappear.
        //
        // The likely reason is worth the next reader's time: G11 was measured on cycles with
        // `created: 0, updated: 0`, where versio_import's phase-split save - the guard written
        // for that exact exception - is skipped by its own `if`. This test edits a task field,
        // so `updated > 0` and the save runs. That is a lead, not a conclusion; the mutation
        // changed two things at once and nobody has varied them separately. The open loose end
        // in STATUS.md asks whether running that guard would have helped, and this is the first
        // evidence either way.
        //
        // So: the priority assertion is what guards Phase 4. These two guard the summary being
        // honest, and they would have caught the original G11 - a run that warned twice while
        // reporting failed: 0.

        // Re-export and read the value back out of the PROJECT. Nothing short of this can tell
        // the difference between a field that was applied and a file that was merely parsed:
        // every cheaper assertion is satisfied by an importer that reads the YAML and drops it.
        //
        // Straight through the runner into a scratch directory, NOT back through the
        // orchestrator. Asking the orchestrator to export here returns NoChange, and it is
        // right to: it recorded the project's hash when the import wrote it, so from its point
        // of view nothing has happened since. That gate is correct and is not what this test is
        // about - going around it asks the only question that is, which is what the project on
        // disk now contains. It also means the assertion below cannot be satisfied by anything
        // Versio cached; the bytes come out of CODESYS.
        var verify = Path.Combine(_repo, ".versio", "local", "verify");
        var runner = new LocalCodesysRunner(
            install.ExecutablePath, new ScriptExtractor(), new TestLogger(output));

        var reread = await runner.RunAsync(new CodesysJob
        {
            Operation = CodesysOperation.Export,
            Profile = install.Profiles[0],
            Projects =
            [
                new ProjectJob
                {
                    BindingId = binding.Id,
                    ProjectPath = Path.Combine(_repo, "plc", "RefrigeratorControl.project"),
                    Path = verify,
                },
            ],
            Timeout = TimeSpan.FromMinutes(5),
        }, null, CancellationToken.None);

        Assert.True(reread.Succeeded, reread.FailureReason ?? "the verification export failed");

        var reExported = await File.ReadAllTextAsync(Path.Combine(
            verify, "Device", "Plc Logic", "Application", "Task Configuration.tasks.yaml"));
        output.WriteLine(reExported);

        Assert.Equal("7", FieldOf(reExported, "MainTask", "priority"));

        // And G7, free on the same file. `Core2` is a task's CPU core affinity, and
        // import_native moved it from -1 to -2 on every import without being asked.
        //
        // THE TWO ASSERTIONS ARE NOT INTERCHANGEABLE, and reading the equivalence fixtures is
        // what shows which is which. In fixtures/equivalence/baseline/Task Configuration.xml -
        // captured BEFORE an import - MainTask already holds Core2 = -2 and VISU_TASK holds
        // -1; in the roundtrip capture, taken after, both read -2.
        //
        //   VISU_TASK = -1  is the canary. It is the value G7 destroyed, so this is the line
        //                   that goes red if the typed path ever stops being the one that
        //                   writes task configuration.
        //   MainTask  = -2  is the control. It was -2 to begin with, which is exactly why it
        //                   is worth asserting: a change that set every core binding to -2
        //                   would satisfy the canary's neighbour and this pair still catches
        //                   nothing - but a change that moved MainTask off its own value would
        //                   be caught here and nowhere else.
        //
        // Asserting the invariant, not a mutation result: the Phase-4-reverted run above never
        // reached these lines, because it failed on the priority two lines up. So it is
        // unobserved whether a regression trips these or only that one, and this says so
        // rather than claiming a coverage it has not been shown to have.
        Assert.Equal("-1", FieldOf(reExported, "VISU_TASK", "coreBinding"));
        Assert.Equal("-2", FieldOf(reExported, "MainTask", "coreBinding"));
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

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task An_object_whose_file_is_deleted_leaves_the_project_and_an_edited_one_does_not()
    {
        // THE FIRST TEST OF VERSIO DESTROYING SOMETHING, and it is written as two halves in
        // one run because the halves are only meaningful against each other: a delete path
        // that never refuses is indistinguishable from one that always deletes, and a refusal
        // that never lets anything through is indistinguishable from a delete path that does
        // not work.
        //
        // P3 measured what remove() does to an object others reference: it removes it, saves
        // cleanly, and warns about nothing. There is no toolchain backstop. So the planner's
        // preconditions and the script's content check are the entire safety story, and the
        // content check is the half that no unit test can reach - versio_import.py imports
        // scriptengine at module level, so it cannot be loaded under CPython at all.
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

        var app = Path.Combine(
            _repo, "plc", "RefrigeratorControl", "Device", "Plc Logic", "Application");

        var doomed = Path.Combine(app, "Simulation.st");
        var gvl = Path.Combine(app, "Glob_Var.gvl.st");

        Assert.True(File.Exists(doomed), "the export wrote no Simulation.st");
        Assert.True(File.Exists(gvl), "the export wrote no Glob_Var.gvl.st");

        var manifestPath = Path.Combine(_repo, "plc", "RefrigeratorControl", "_manifest.json");
        var honest = await File.ReadAllTextAsync(manifestPath);

        // ── one: an object edited since the export is KEPT ───────────────────────────
        //
        // Simulation.st is deleted from the mirror and the manifest's recorded hash for it is
        // falsified. That is exactly the state an engineer creates by editing the object in
        // the IDE after the last export: the project no longer matches what was exported, so
        // the missing file is not permission to destroy that work.
        //
        // Falsifying the hash rather than driving the IDE is what makes this affordable, and
        // it is not a weaker test — comparing the live object against the recorded hash is
        // the WHOLE of the script's check, so it cannot tell the two apart.
        //
        // THE SAME OBJECT IS DELETED SUCCESSFULLY BELOW, with only the hash different. That
        // is the point of ordering it this way: a refusal test using a different object could
        // pass because that object was undeletable for some unrelated reason.
        File.Delete(doomed);

        await File.WriteAllTextAsync(manifestPath, PoisonHashFor(honest, "Simulation.st"),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var refused = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import 1 — {refused.Verdict}: {SyncNarrative.Describe(refused)}");

        // The import SUCCEEDS. A refused deletion is a refusal, not a failure — marking the
        // binding failed would stop all syncing over Versio declining to destroy something.
        Assert.Equal(SyncVerdict.Completed, refused.Verdict);
        Assert.Contains(refused.Warnings,
            w => w.Contains("DELETE_CONTENT_CHANGED", StringComparison.Ordinal));

        // Read back out of the PROJECT, and STRAIGHT THROUGH THE RUNNER rather than back
        // through the orchestrator. Asking the orchestrator to export here returns NoChange
        // and is right to: it recorded the project's hash when the import ran, so from its
        // point of view nothing has happened since. Going around that gate asks the only
        // question this test is about — what the project on disk now contains.
        var afterRefusal = await ReExport(install, binding.Id, "verify-kept");

        Assert.True(File.Exists(Path.Combine(afterRefusal, RelApp, "Simulation.st")),
            "Simulation was deleted despite no longer matching what was exported");

        // ── two: with an honest hash, the same object goes ───────────────────────────
        //
        // Nothing changes but the manifest. If this fails while the refusal above passed, the
        // delete path does not work; if it passed while the refusal failed, the check does
        // not. Only both together say the script is deciding rather than defaulting.
        await File.WriteAllTextAsync(manifestPath, honest,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var deleted = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import 2 — {deleted.Verdict}: {SyncNarrative.Describe(deleted)}");
        Assert.Equal(SyncVerdict.Completed, deleted.Verdict);

        var afterDelete = await ReExport(install, binding.Id, "verify-delete");

        Assert.False(File.Exists(Path.Combine(afterDelete, RelApp, "Simulation.st")),
            "Simulation came back, so the object is still in the project");

        // And nothing else went with it. remove() dangles referenced objects silently (P3),
        // so "the right object left" and "only the right object left" are two claims.
        Assert.True(File.Exists(Path.Combine(afterDelete, RelApp, "PLC_PRG.xml")),
            "PLC_PRG went missing — the deletion took more than it was asked to");
        Assert.True(File.Exists(Path.Combine(afterDelete, RelApp, "Glob_Var.gvl.st")),
            "Glob_Var went missing — the deletion took more than it was asked to");

        // ── three: an object whose export cannot be rebuilt is never deleted ─────────
        //
        // A GVL is two files under one GUID — a declaration and its native XML — and the
        // manifest's hash for the XML is over exported native bytes. P7 measured a re-export
        // of an untouched native object changing on every cycle, so hashing a fresh one
        // proves nothing, and the old bytes are in the file that was just deleted. Whether
        // the object changed since is therefore unknowable, and unknowable means kept.
        //
        // Found by this test rather than by design review: half two originally used the GVL
        // and reported DELETE_UNVERIFIABLE where DELETE_CONTENT_CHANGED was expected.
        File.Delete(gvl);
        File.Delete(Path.Combine(app, "Glob_Var.gvl.xml"));

        var unverifiable = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import 3 — {unverifiable.Verdict}: {SyncNarrative.Describe(unverifiable)}");

        Assert.Equal(SyncVerdict.Completed, unverifiable.Verdict);
        Assert.Contains(unverifiable.Warnings,
            w => w.Contains("DELETE_UNVERIFIABLE", StringComparison.Ordinal));

        var afterUnverifiable = await ReExport(install, binding.Id, "verify-native");

        Assert.True(File.Exists(Path.Combine(afterUnverifiable, RelApp, "Glob_Var.gvl.st")),
            "the GVL was deleted on the strength of a check that could not be made");
    }

    /// <summary>Where the fixture's objects live, relative to a mirror root.</summary>
    private static string RelApp => Path.Combine("Device", "Plc Logic", "Application");

    /// <summary>
    /// Export the project into a scratch directory, through the runner.
    /// </summary>
    /// <remarks>
    /// Deliberately not through the orchestrator: it recorded the project's hash when the
    /// import wrote it, so it answers <c>NoChange</c> — correctly. This asks CODESYS what the
    /// project contains now, which is the only thing that can tell an applied change from a
    /// parsed one.
    /// </remarks>
    private async Task<string> ReExport(CodesysInstall install, string bindingId, string into)
    {
        var target = Path.Combine(_repo, ".versio", "local", into);

        var result = await new LocalCodesysRunner(
                install.ExecutablePath, new ScriptExtractor(), new TestLogger(output))
            .RunAsync(new CodesysJob
            {
                Operation = CodesysOperation.Export,
                Profile = install.Profiles[0],
                Projects =
                [
                    new ProjectJob
                    {
                        BindingId = bindingId,
                        ProjectPath = Path.Combine(_repo, "plc", "RefrigeratorControl.project"),
                        Path = target,
                    },
                ],
                Timeout = TimeSpan.FromMinutes(5),
            }, null, CancellationToken.None);

        Assert.True(result.Succeeded, $"the verification export failed: {result.FailureReason}");
        return target;
    }

    /// <summary>
    /// Rewrite one object's recorded hash so the live object cannot match it.
    /// </summary>
    /// <remarks>
    /// Stands in for "somebody edited this in the IDE since the last export", which is the
    /// only state the script's content check is looking for — and which it cannot tell apart
    /// from this, because comparing the live object against the recorded hash is the whole of
    /// the check.
    /// </remarks>
    private static string PoisonHashFor(string manifest, string fileName)
    {
        const string key = "\"hash\": \"";

        var at = manifest.IndexOf(fileName, StringComparison.Ordinal);
        Assert.True(at > 0, $"{fileName} is not in the manifest");

        // BACKWARDS from the path, not forwards. The manifest is written with sort_keys, so
        // an object's members are alphabetical and `hash` comes BEFORE `path` — searching
        // forward finds the NEXT object's hash and poisons a record this test never touches.
        // It did exactly that first time round: the deletion went ahead, the assertion failed
        // on an empty warning list, and nothing about the code under test was wrong.
        var hashAt = manifest.LastIndexOf(key, at, StringComparison.Ordinal);
        Assert.True(hashAt > 0, $"no hash recorded before {fileName}");

        // And prove it belongs to the same record: no object boundary may sit between them.
        Assert.DoesNotContain("}", manifest[hashAt..at], StringComparison.Ordinal);

        var valueAt = hashAt + key.Length;
        var end = manifest.IndexOf('"', valueAt);
        Assert.True(end > valueAt, "the recorded hash is not a string");

        return manifest[..valueAt]
               + "sha256:0000000000000000000000000000000000000000000000000000000000000000"
               + manifest[end..];
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

    [SkippableFact]
    public async Task An_import_ignores_the_files_that_are_not_objects()
    {
        // A real defect, caught only because the coverage report was added and somebody
        // looked. The importer walks the export tree and treats every file it finds as an
        // object, knowing about "_manifest.json" by name in two places - so _coverage.md was
        // handed to CODESYS as native XML and came back
        // "Data at the root level is invalid. Line 1, position 1."
        //
        // Nothing was corrupted. The cost is worse than that: EVERY import would carry a
        // failure warning that is always there and always meaningless, which is how a real
        // native-import failure gets read past. The round-trip test passed throughout.
        //
        // The export tree now holds THREE kinds of file, and the third is the one this test
        // exists to catch on the way in: *.libraries.yaml is real project state, written for
        // review, with no import path that can be trusted. Handing YAML to import_native
        // would be the same defect with a different extension - which is precisely what
        // "the next file added is added for both halves at once" was written to prevent.
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

        var exportRoot = Path.Combine(_repo, binding.ExportPath.Replace('/', Path.DirectorySeparatorChar));

        // The export must actually have written the files this is about, or the assertion
        // below would hold for the uninteresting reason.
        Assert.True(File.Exists(Path.Combine(exportRoot, "_coverage.md")),
                    "the export wrote no coverage report, so this proves nothing");
        Assert.True(File.Exists(Path.Combine(exportRoot, "_manifest.json")));

        var libraryFiles = Directory
            .EnumerateFiles(exportRoot, "*.libraries.yaml", SearchOption.AllDirectories)
            .ToList();
        Assert.True(libraryFiles.Count > 0,
                    "the export wrote no library file, so the third case proves nothing");

        await orchestrator.ReleaseAfterPushAsync("c1", CancellationToken.None);

        // Touch a real object so the import has something to do and the hash gate lets it run.
        var st = Directory.EnumerateFiles(exportRoot, "*.st", SearchOption.AllDirectories).First();
        await File.AppendAllTextAsync(st, Environment.NewLine + "(* bookkeeping probe *)" + Environment.NewLine);

        var outcome = await TextChanged(orchestrator, binding.Id);

        foreach (var warning in outcome.Warnings)
        {
            output.WriteLine(warning);
        }

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);

        // Named explicitly rather than asserting no warnings at all: a warning about a real
        // object is information, and a test that forbade every warning would be reworded to
        // death the first time the exporter legitimately had something to say.
        Assert.DoesNotContain(outcome.Warnings,
            w => w.Contains("_coverage.md", StringComparison.OrdinalIgnoreCase) ||
                 w.Contains("_manifest.json", StringComparison.OrdinalIgnoreCase) ||
                 w.Contains(".libraries.yaml", StringComparison.OrdinalIgnoreCase));

        // And the project still has its library file afterwards, unchanged. The import is
        // meant to leave it alone entirely - not to consume it, not to rewrite it, and not
        // to delete it on the way past.
        foreach (var file in libraryFiles)
        {
            Assert.True(File.Exists(file), $"the import removed {Path.GetFileName(file)}");
        }
    }

    [SkippableFact]
    public async Task Adoption_creates_a_gitignore_and_git_then_ignores_the_toolchains_leftovers()
    {
        // The end of the chain, asserted with git itself rather than by reading our own file
        // back: a rule that looks right and does not match is the same as no rule.
        //
        // Measured on CODESYS 3.5.22.20: opening a project leaves <name>-AllUsers.opt and
        // <name>-<user>-<machine>.opt beside it, and a .~u lock while it is held. The per-user
        // one carries a username and a machine name in its filename.
        Skip.IfNot(GitAvailable, "git not on PATH");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        await InitRepoAsync();

        // No .gitignore at all — the normal starting point for somebody who has kept PLC
        // projects in a folder until now.
        Assert.False(File.Exists(Path.Combine(_repo, ".gitignore")));

        var git = new GitCliClient(_repo, logger: new TestLogger(output));
        var setup = new RepoSetup(_repo, CodesysProjectFormat.Instance, new TestLogger(output));

        var adopted = await setup.AdoptAsync(ProjectRelativePath, git, null, CancellationToken.None);
        Assert.True(adopted.Adopted, adopted.Decision.Explanation);

        var ignorePath = Path.Combine(_repo, ".gitignore");
        Assert.True(File.Exists(ignorePath), "adoption created no .gitignore");

        var contents = await File.ReadAllTextAsync(ignorePath);
        output.WriteLine(contents);
        Assert.NotEmpty(adopted.GitIgnorePatterns);

        // Now put the real files there and ask git, not ourselves.
        var folder = Path.GetDirectoryName(Path.Combine(_repo, ProjectRelativePath))!;
        var stem = Path.GetFileNameWithoutExtension(ProjectRelativePath);

        string[] leftovers =
        [
            Path.Combine(folder, stem + "-AllUsers.opt"),
            Path.Combine(folder, stem + "-elli_-ROG-JVM.opt"),
            Path.Combine(folder, stem + ".~u"),

            // Written through a transient sibling by the IDE, and covered by the same rule.
            Path.Combine(folder, stem + ".~u~"),

            // The two directories an export writes beside the mirror while it swaps. Both
            // exist for milliseconds in the happy case and both survive a killed run, and
            // until this test they were untracked directories that "stage all" would commit.
            Path.Combine(folder, stem + BindingConventions.StagingSuffix, "Device", "Main.st"),
            Path.Combine(folder, stem + BindingConventions.TrashSuffix, "Device", "Main.st"),
        ];

        foreach (var file in leftovers)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "scratch");
        }

        var status = await git.StatusAsync(CancellationToken.None);
        foreach (var change in status.Changes)
        {
            output.WriteLine($"{change.Effective} {change.Path}");
        }

        Assert.All(leftovers, file =>
        {
            var relative = Path.GetRelativePath(_repo, file).Replace(Path.DirectorySeparatorChar, '/');
            Assert.DoesNotContain(status.Changes, c =>
                string.Equals(c.Path.Replace(Path.DirectorySeparatorChar, '/'), relative, StringComparison.OrdinalIgnoreCase));
        });

        // And the project itself is still very much visible - an ignore rule that swallowed
        // the thing being tracked would be a far worse bug than the noise it removes.
        Assert.Contains(status.Changes, c =>
            c.Path.Replace(Path.DirectorySeparatorChar, '/').EndsWith(ProjectRelativePath, StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task Adopting_twice_does_not_grow_the_gitignore()
    {
        // Adoption re-runs on every scan. A second copy of the rules would show up as a diff
        // on every startup, which is its own kind of broken.
        Skip.IfNot(GitAvailable, "git not on PATH");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        await InitRepoAsync();

        var git = new GitCliClient(_repo, logger: new TestLogger(output));
        var setup = new RepoSetup(_repo, CodesysProjectFormat.Instance, new TestLogger(output));

        await setup.AdoptAsync(ProjectRelativePath, git, null, CancellationToken.None);
        var first = await File.ReadAllTextAsync(Path.Combine(_repo, ".gitignore"));

        var again = await setup.EnsureGitIgnoreAsync(ProjectRelativePath, CancellationToken.None);
        var second = await File.ReadAllTextAsync(Path.Combine(_repo, ".gitignore"));

        Assert.False(again.ChangeNeeded);
        Assert.Equal(first, second);
    }

    [SkippableFact]
    public async Task Versios_own_local_directory_is_ignored_in_a_repository_it_adopts_into()
    {
        // Three comments in the engine describe .versio/local/ as gitignored. Nothing wrote
        // the rule: this repository has one because a person typed it, and a repository
        // somebody else adopts a project in did not.
        //
        // What lives there: the direction locks, session state, project backups - megabytes
        // of binary snapshots - and the scratch copy taken while the IDE holds a project. The
        // plan calls a committed lock pulled onto a colleague's machine a bug factory, and it
        // is right.
        Skip.IfNot(GitAvailable, "git not on PATH");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        await InitRepoAsync();

        var git = new GitCliClient(_repo, logger: new TestLogger(output));
        var setup = new RepoSetup(_repo, CodesysProjectFormat.Instance, new TestLogger(output));

        await setup.AdoptAsync(ProjectRelativePath, git, null, CancellationToken.None);

        // Put the real things there, then ask git rather than reading our own file back.
        var locks = Path.Combine(_repo, ".versio", "local");
        Directory.CreateDirectory(Path.Combine(locks, "backups", "b-1"));
        Directory.CreateDirectory(Path.Combine(locks, "open", "b-1"));

        await File.WriteAllTextAsync(Path.Combine(locks, "locks.json"), "{}");
        await File.WriteAllTextAsync(
            Path.Combine(locks, "backups", "b-1", "20260812-120000-A.project"), "binary");
        await File.WriteAllTextAsync(
            Path.Combine(locks, "open", "b-1", "A.project"), "scratch");

        var status = await git.StatusAsync(CancellationToken.None);
        foreach (var change in status.Changes)
        {
            output.WriteLine($"{change.Effective} {change.Path}");
        }

        Assert.DoesNotContain(status.Changes, c =>
            c.Path.Replace(Path.DirectorySeparatorChar, '/')
             .StartsWith(".versio/local/", StringComparison.OrdinalIgnoreCase));

        // repo.json is committed on purpose - it is the shared project mapping, and a clone
        // needs it to get the same baseline. Ignoring .versio wholesale would break that.
        Assert.Contains(status.Changes, c =>
            c.Path.Replace(Path.DirectorySeparatorChar, '/')
             .Equals(".versio/repo.json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether git is on PATH, so a machine without it skips rather than fails.</summary>
    private static bool GitAvailable
    {
        get
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("git", "--version")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                p!.WaitForExit(5000);
                return p.ExitCode == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
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

        // COPYING THE `.opt` SIDECARS WAS TRIED HERE AND REMOVED, because it was watching
        // nothing. `A_task_priority_edited_…` asserts the sidecars are beside the project
        // before its second import; deleting the copy left that assertion green, because
        // CODESYS writes its own `<project>-<user>-<machine>.opt` pair the first time it opens
        // the project - which every test here does, on its first export, long before it
        // imports.
        //
        // That corrects the loose end this came from, which read "none imports with the
        // project's sidecars present". The `.opt` half of it was never true: they are there by
        // import time in every test that exports first. What was genuinely missing is the
        // compile cache and the second cycle, and those are what the test now covers.
        await RunGitAsync("init", "-b", "main");
        await RunGitAsync("config", "user.email", "test@versio.local");
        await RunGitAsync("config", "user.name", "Versio Test");
    }

    /// <summary>Everything CODESYS has left beside the project, newest state, sorted.</summary>
    private List<string> Sidecars() =>
        [.. Directory.EnumerateFiles(Path.Combine(_repo, "plc"))
            .Select(f => Path.GetFileName(f)!)
            .Where(f => !f.EndsWith(".project", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)];

    /// <summary>
    /// Read one field of one named task out of a <c>.tasks.yaml</c>, by indent, without a
    /// parser.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>LibraryCatalog</c>-shaped and deliberately not shared with the
    /// production reader. The point of this test is to check what CODESYS holds after an
    /// import, and reading the file back with the same code that wrote it would let a bug
    /// common to both sides cancel out. Twenty lines of dumb line-scanning cannot agree with
    /// the emitter by accident.
    ///
    /// Returns a sentence rather than throwing or returning null on a miss, so a failed
    /// assertion says which field was not found instead of a bare "expected 9, got ".
    /// </remarks>
    private static string FieldOf(string yaml, string task, string field)
    {
        const string NameKey = "  - name: ";
        var wanted = $"    {field}: ";
        var inTask = false;

        foreach (var line in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith(NameKey, StringComparison.Ordinal))
            {
                inTask = line[NameKey.Length..].Trim().Trim('"') == task;
            }
            else if (inTask && line.StartsWith(wanted, StringComparison.Ordinal))
            {
                return line[wanted.Length..].Trim().Trim('"');
            }
        }

        return $"<no {field} on a task called {task}>";
    }

    /// <summary>Rewrite one field of one named task, asserting it hit exactly one line.</summary>
    /// <remarks>
    /// The count assertion is the guard that matters. A blind string replace over this file
    /// would also match <c>watchdogSensitivity: "1"</c> or the other task's value, and a test
    /// that edits the wrong line still goes green when the importer does nothing - it would be
    /// asserting that an unchanged value stayed unchanged.
    /// </remarks>
    private static string WithField(string yaml, string task, string field, string value)
    {
        const string NameKey = "  - name: ";
        var wanted = $"    {field}: ";
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var inTask = false;
        var rewritten = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith(NameKey, StringComparison.Ordinal))
            {
                inTask = lines[i][NameKey.Length..].Trim().Trim('"') == task;
            }
            else if (inTask && lines[i].StartsWith(wanted, StringComparison.Ordinal))
            {
                lines[i] = wanted + value;
                inTask = false;
                rewritten++;
            }
        }

        Assert.True(rewritten == 1,
                    $"expected to rewrite exactly one {field} line for {task}, rewrote {rewritten}");
        return string.Join("\n", lines);
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
