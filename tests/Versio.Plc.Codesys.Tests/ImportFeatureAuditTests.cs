using Versio.Core.Bindings;
using Versio.Core.Locking;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Versio.Core.Sync;
using Versio.Infrastructure.Bindings;
using Versio.Infrastructure.FileSystem;
using Versio.Infrastructure.Git;
using Versio.Infrastructure.Platform;
using Versio.Plc.Codesys.Infrastructure;
using Versio.Plc.Codesys;
using Versio.TestSupport;
using Xunit.Abstractions;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// The import's feature checklist, each row driven against a real project through a real
/// CODESYS.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written as an audit rather than as regression tests</b>, before Phase 5 was started and
/// on the argument that a feature list nobody has run end to end is a list of intentions. Every
/// row here is one line of the checklist in <c>docs/vendors/codesys/IMPORT-PLAN.md</c>; the rows already
/// covered by <see cref="AdoptAndExportTests"/> and <see cref="ImportLoggingTests"/> are not
/// repeated, because running those files one test at a time was the other half of the same
/// audit.
/// </para>
/// <para>
/// <b>What these cover that the existing files do not</b> is the refusal side and the second
/// half of the delete rules: an object kept because only one of its two files went, a native
/// deletion that cannot be checked, a mirror whose manifest is gone, a form this version does
/// not know, a move CODESYS declines to perform, and the race between planning and executing.
/// Each of those has a pure planner test already; none had ever been watched reaching or not
/// reaching a project.
/// </para>
/// <para>
/// <b>Half of these are refusals, and a refusal test proves nothing on its own.</b> A run in
/// which the import simply stopped deleting, stopped renaming or stopped applying anything at
/// all would satisfy every row here that asserts an object survived. What makes them mean
/// something is the rows that go the other way, in the two files above: <c>An_object_whose_
/// file_is_deleted_…</c> deletes, <c>A_POU_renamed_in_the_mirror_…</c> renames, and
/// <c>An_unconfirmed_import_…</c> applies. <b>Read the two files as one claim</b> —
/// separately, each is satisfied by a broken implementation.
/// </para>
/// <para>
/// <b>One repository per test, one adoption per test, and no sharing.</b> Several of these
/// deliberately poison the mirror or the manifest, and a state left behind by one is not a
/// state the next should be measured against — the same reasoning
/// <c>tools\Probe-ImportApi.ps1</c> gives for one project copy per probe.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Toolchain", "Codesys")]
[Collection(CodesysCollection.Name)]
public sealed class ImportFeatureAuditTests(ITestOutputHelper output) : IDisposable
{
    private const string ProjectRelativePath = "plc/RefrigeratorControl.project";
    private const string RelApp = "Device/Plc Logic/Application";

    private readonly string _repo = Path.Combine(
        Path.GetTempPath(), "Versio", "audit", Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Everything the orchestrator and the planner said, for the rows that are
    /// about a sentence rather than about a project.</summary>
    private readonly InMemoryLogSink _sink = new();

    private VersioLogger? _logger;

    /// <summary>
    /// One logger, buffered and echoed into the test output.
    /// </summary>
    /// <remarks>
    /// Echoed through the sink's own <c>Emitted</c> event rather than through a second sink,
    /// so what a failing run prints and what an assertion reads are the same list by
    /// construction rather than by two code paths agreeing.
    /// </remarks>
    private VersioLogger Log
    {
        get
        {
            if (_logger is null)
            {
                _sink.Emitted += e =>
                {
                    try
                    {
                        output.WriteLine(e.Format());
                    }
                    catch (InvalidOperationException)
                    {
                        // The owning test has finished; see TestLogger's remarks.
                    }
                };

                _logger = new VersioLogger(new SystemClock());
                _logger.AddSink(_sink);
            }

            return _logger;
        }
    }

    /// <summary>Every line logged so far, formatted.</summary>
    private List<string> Lines() => [.. _sink.Snapshot().Select(e => e.Message)];

    private static CodesysInstall? Install =>
        new CodesysLocator().FindInstalls().FirstOrDefault(i => i.Profiles.Count > 0);

    private static string FixtureProject =>
        TestPaths.Project("RefrigeratorControl.project");

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

    // ── A2: a declaration-only form reaches the project ──────────────────────────────

    /// <summary>
    /// A GVL's declaration edited in the mirror reaches the project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ST_DECL</c> is a third apply path, not a variation of <c>ST_FULL</c>: it writes
    /// <c>textual_declaration</c> alone and never touches the implementation, and the fixture's
    /// only instance of it is a GVL. Every import test in the repository until now edited an
    /// <c>ST_FULL</c> POU or a <c>TASK_BOTH</c> file, so this branch had run on every import and
    /// been asserted by none.
    /// </para>
    /// <para>
    /// <b>It failed when it was written, and that failure is G12.</b> The script reported
    /// <c>applied … (declaration)</c> and the edit was not in the project: a GVL exports as
    /// <c>GVL_BOTH</c> — a <c>.gvl.st</c> recorded <c>ST_DECL</c> and a <c>.gvl.xml</c> recorded
    /// plain <c>NATIVE</c> — and the import's phases run textual before native, so the XML's
    /// copy of the old declaration was written over the edit seconds later.
    /// <c>versio_types.py</c> stated the intention outright — <i>"a GVL imports from its
    /// .st"</i> — and nothing enforced it.
    /// </para>
    /// <para>
    /// Fixed the same day by recording that XML as <c>GVL_NATIVE</c>, which
    /// <c>ExportCoverage.IsExportOnly</c> names, exactly as <c>TASK_NATIVE</c> already was. So
    /// this is now G12's regression test, and the thing that stops the shape recurring is
    /// <c>An_object_never_writes_two_files_the_import_would_both_apply</c> rather than this.
    /// See the finding in <c>STATUS.md</c>.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_declaration_only_object_edited_in_the_mirror_reaches_the_project()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        var (binding, orchestrator) = await AdoptAndExportAsync(install!);

        var gvl = Path.Combine(_repo, "plc", "RefrigeratorControl",
                               RelApp.Replace('/', Path.DirectorySeparatorChar), "Glob_Var.gvl.st");
        var before = await File.ReadAllTextAsync(gvl);
        Assert.Contains("VAR_GLOBAL", before, StringComparison.Ordinal);

        // A declaration that is unmistakably ours on the way back, and legal ST.
        await WriteAsync(gvl, before.Replace(
            "VAR_GLOBAL", "VAR_GLOBAL\n\taudit_marker : BOOL := TRUE;", StringComparison.Ordinal));

        var outcome = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import — {outcome.Verdict}: {SyncNarrative.Describe(outcome)}");
        foreach (var w in outcome.Warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);

        var reExported = await File.ReadAllTextAsync(Path.Combine(
            await ReExport(install!, binding.Id, "verify-decl"), RelApp, "Glob_Var.gvl.st"));
        output.WriteLine(reExported);

        Assert.True(
            reExported.Contains("audit_marker", StringComparison.Ordinal),
            "G12 is back: the GVL declaration edit did not reach the project, which means "
            + "something is applying the .gvl.xml after the .gvl.st again. Check that its "
            + "manifest form is still GVL_NATIVE and that ExportCoverage.IsExportOnly still "
            + "names it - see the finding in docs/STATUS.md.");
    }

    /// <summary>
    /// The same edit survives when the GVL's native half is not imported — which is what
    /// names the overwriter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The control for the test above, and the whole of the evidence.</b> "The edit is not
    /// in the project" has two candidate explanations — the declaration write never landed, or
    /// something wrote over it — and the log cannot tell them apart, because it says
    /// <c>applied</c> in both. So this run changes exactly one thing: the plan's entry for
    /// <c>Glob_Var.gvl.xml</c> is turned into a skip before the job is handed over. Everything
    /// else, including the edit, is identical.
    /// </para>
    /// <para>
    /// The plan is mutated here rather than the mirror, deliberately: deleting the
    /// <c>.gvl.xml</c> from the mirror would trip the two-file rule and change what is being
    /// measured into something else entirely.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_GVLs_declaration_survives_when_its_native_half_is_not_imported()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        Directory.CreateDirectory(Path.Combine(_repo, "plc"));
        var project = Path.Combine(_repo, "plc", "RefrigeratorControl.project");
        File.Copy(FixtureProject, project);

        var mirror = Path.Combine(_repo, "plc", "RefrigeratorControl");
        var logger = new TestLogger(output);
        var runner = new LocalCodesysRunner(install!.ExecutablePath, new ScriptExtractor(), logger);

        Assert.True((await runner.RunAsync(
            Job(install, CodesysOperation.Export, project, mirror, null),
            null, CancellationToken.None)).Succeeded);

        var gvl = Path.Combine(mirror, RelApp.Replace('/', Path.DirectorySeparatorChar),
                               "Glob_Var.gvl.st");
        var before = await File.ReadAllTextAsync(gvl);
        await WriteAsync(gvl, before.Replace(
            "VAR_GLOBAL", "VAR_GLOBAL\n\taudit_marker : BOOL := TRUE;", StringComparison.Ordinal));

        var plan = await ImportPlanReader.ReadAsync(
            new WindowsFileSystem(), CodesysProjectFormat.Instance, mirror, logger,
            "audit", "gvl", CancellationToken.None);

        // The one difference from the run above.
        var withoutNative = plan with
        {
            Files = [.. plan.Files.Select(f =>
                f.Path.EndsWith("Glob_Var.gvl.xml", StringComparison.Ordinal)
                    ? f with { Action = PlannedAction.Skip, Skip = PlannedSkip.ExportOnly }
                    : f)],
        };

        Assert.True((await runner.RunAsync(
            Job(install, CodesysOperation.Import, project, mirror, withoutNative),
            null, CancellationToken.None)).Succeeded);

        var verify = Path.Combine(_repo, "verify-gvl");
        Assert.True((await runner.RunAsync(
            Job(install, CodesysOperation.Export, project, verify, null),
            null, CancellationToken.None)).Succeeded);

        var reExported = await File.ReadAllTextAsync(
            Path.Combine(verify, RelApp, "Glob_Var.gvl.st"));
        output.WriteLine(reExported);

        Assert.Contains("audit_marker", reExported, StringComparison.Ordinal);
    }

    // ── A8 and A9: the two create refusals ─────────────────────────────────────────────

    /// <summary>
    /// A garbled source is reported, not approximated; a create under a folder the project
    /// does not have is reported, and nothing is created for either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These were the checklist's A8 and A9, and they sat NOT REACHED for a reason worth
    /// recording: nobody tried.</b> Both rows said what would be needed — "a mirror file that
    /// is textual by suffix and unparseable by content", "a create under a folder the project
    /// does not have" — and both are one <c>WriteAsync</c> each, unlike E2 and E4, whose
    /// fixture genuinely cannot be built. A row that names its own fixture and stays NOT
    /// REACHED is a decision by default, and this list is supposed to contain decisions made
    /// deliberately.
    /// </para>
    /// <para>
    /// One run covers both because they refuse at different points of the same path:
    /// <c>create_textual</c> resolves the owner <i>before</i> it reads the file, so the orphan
    /// refuses on <c>PARENT_NOT_FOUND</c> without its (perfectly valid) declaration ever being
    /// parsed, and the garbled file resolves its owner fine and then refuses on
    /// <c>CANNOT_IDENTIFY_DECLARATION</c>. The planner plans a Create for both — it warns about
    /// a name disagreement only when the declaration parses, and a missing folder is the
    /// script's to discover, because only the project knows its own tree.
    /// </para>
    /// <para>
    /// The re-export is the half that matters: a refusal message with a created object behind
    /// it would be the import guessing while saying it did not — "creating the wrong kind of
    /// object is worse than creating none" is the script's own comment, asserted here from
    /// outside.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_garbled_source_and_a_create_under_a_missing_folder_are_reported_not_guessed()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        var (binding, orchestrator) = await AdoptAndExportAsync(install!);

        var app = Path.Combine(_repo, "plc", "RefrigeratorControl",
                               RelApp.Replace('/', Path.DirectorySeparatorChar));

        // A8: textual by suffix, unparseable by content. No PROGRAM / FUNCTION_BLOCK /
        // FUNCTION / INTERFACE / TYPE header anywhere in it.
        await WriteAsync(Path.Combine(app, "Garbage.st"),
                         "// somebody saved scratch notes with the wrong extension\n"
                         + "these lines are not a declaration of anything\n");

        // A9: a legal declaration under a folder the project does not have. The owner is
        // resolved before the file is read (create_textual's own order), so this refusal
        // never parses the declaration — a code fact, not one this test can distinguish:
        // with valid content, PARENT_NOT_FOUND is the only refusal either order produces.
        Directory.CreateDirectory(Path.Combine(app, "NoSuchFolder"));
        await WriteAsync(Path.Combine(app, "NoSuchFolder", "Orphan.st"),
                         "PROGRAM Orphan\nVAR\nEND_VAR\n\nEND_PROGRAM\n");

        var outcome = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import — {outcome.Verdict}: {SyncNarrative.Describe(outcome)}");
        foreach (var w in outcome.Warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        Assert.Contains(outcome.Warnings, w =>
            w.Contains("CANNOT_IDENTIFY_DECLARATION", StringComparison.Ordinal)
            && w.Contains("Garbage.st", StringComparison.Ordinal));
        Assert.Contains(outcome.Warnings, w =>
            w.Contains("PARENT_NOT_FOUND", StringComparison.Ordinal)
            && w.Contains("Orphan.st", StringComparison.Ordinal)
            && w.Contains("NoSuchFolder", StringComparison.Ordinal));

        // Neither refusal may have an object behind it. The re-export is a fresh read of the
        // project off disk, so an object either of these quietly created would surface here.
        var verify = await ReExport(install!, binding.Id, "verify-refused-creates");
        Assert.Empty(Directory.GetFiles(verify, "Garbage*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(verify, "Orphan*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(verify, "NoSuchFolder", SearchOption.AllDirectories));
    }

    // ── C6: what replaced the two-file rule ────────────────────────────────────────────────────────

    /// <summary>
    /// Deleting a GVL's declaration removes the GVL, and its one-way half does not veto it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test asserted the opposite until G12 was fixed, and both versions were right at
    /// the time.</b> A GVL used to write two <i>importable</i> files, so deleting one of them
    /// was somebody tidying rather than a decision, and the two-file rule kept the object.
    /// Making the <c>.gvl.xml</c> export-only leaves the GVL with exactly one importable file,
    /// which makes it an ordinary single-file object: deleting <c>Simulation.st</c> has always
    /// removed the POU, and this is the same rule reaching the same conclusion.
    /// </para>
    /// <para>
    /// <b>The safety did not go anywhere; it moved.</b> What stands between this and destroying
    /// work is the content check, not the file count — <c>ST_DECL</c> is verifiable, so the
    /// live declaration is rebuilt and compared against the manifest before anything is
    /// removed, and a GVL edited in the IDE since the export is kept.
    /// </para>
    /// <para>
    /// <b>The <c>.gvl.xml</c> is left behind in the mirror</b> until the next export tidies it,
    /// because an export-only file is not something the import is entitled to delete. Asserted
    /// rather than tolerated silently, so the next person meets it here rather than in a
    /// working tree.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Deleting_a_GVLs_declaration_removes_it_and_its_one_way_half_does_not_veto()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        var (binding, orchestrator) = await AdoptAndExportAsync(install!);
        var app = Path.Combine(_repo, "plc", "RefrigeratorControl",
                               RelApp.Replace('/', Path.DirectorySeparatorChar));

        // The declaration goes; the one-way half is left exactly where it is.
        File.Delete(Path.Combine(app, "Glob_Var.gvl.st"));
        Assert.True(File.Exists(Path.Combine(app, "Glob_Var.gvl.xml")));

        var outcome = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import — {outcome.Verdict}: {SyncNarrative.Describe(outcome)}");
        foreach (var w in outcome.Warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);

        // No refusal of any kind. The export-only record neither votes nor vetoes, so there is
        // nothing here about a file of several going. Read from the LOG, because a planner
        // sentence never crosses the wire.
        Assert.DoesNotContain(Lines(),
            l => l.Contains("not a decision to delete it", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.Warnings,
            w => w.Contains("DELETE_", StringComparison.Ordinal));

        var after = await ReExport(install!, binding.Id, "verify-gvl-delete");
        Assert.False(File.Exists(Path.Combine(after, RelApp, "Glob_Var.gvl.st")),
                     "the GVL is still in the project, so its .gvl.xml vetoed the deletion");

        // Gone means gone: the object left, so its native half is not re-exported either.
        Assert.False(File.Exists(Path.Combine(after, RelApp, "Glob_Var.gvl.xml")));

        // And nothing else went with it. remove() dangles referenced objects silently (P3),
        // and the POUs in this fixture read from this GVL.
        Assert.True(File.Exists(Path.Combine(after, RelApp, "Simulation.st")));
        Assert.True(File.Exists(Path.Combine(after, RelApp, "PLC_PRG.xml")));

        // The mirror still holds the orphaned one-way file, which the next export removes.
        Assert.True(File.Exists(Path.Combine(app, "Glob_Var.gvl.xml")),
                    "the import deleted an export-only file, which is not its to delete");
    }

    // ── C4: a native deletion that cannot be checked ─────────────────────────────────

    /// <summary>
    /// A native object whose manifest record carries no normalised hash is kept, not removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the pre-schema-5 mirror, reproduced by stripping <c>nhash</c> from the record
    /// rather than by keeping an old mirror around: the script's whole condition is whether
    /// that value is there, so the two cases are indistinguishable to it.
    /// </para>
    /// <para>
    /// A raw <c>hash</c> cannot stand in. It is over bytes that churn between exports — P7
    /// measured four native files changing every cycle with nothing edited — so comparing a
    /// re-export against it would refuse the deletion for a reason with nothing to do with the
    /// object.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_native_deletion_with_nothing_to_check_it_against_is_refused()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        var (binding, orchestrator) = await AdoptAndExportAsync(install!);
        var mirror = Path.Combine(_repo, "plc", "RefrigeratorControl");
        var app = Path.Combine(mirror, RelApp.Replace('/', Path.DirectorySeparatorChar));

        // Diagnosis is a single-file NATIVE object, so its fate turns on nothing else.
        var manifestPath = Path.Combine(mirror, "_manifest.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Diagnosis.vis.xml", manifest, StringComparison.Ordinal);

        await WriteAsync(manifestPath, StripNormalizedHashFor(manifest, "Diagnosis.vis.xml"));
        File.Delete(Path.Combine(app, "Diagnosis.vis.xml"));

        var outcome = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import — {outcome.Verdict}: {SyncNarrative.Describe(outcome)}");
        foreach (var w in outcome.Warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
        Assert.Contains(outcome.Warnings,
            w => w.Contains("DELETE_UNVERIFIABLE", StringComparison.Ordinal));

        var after = await ReExport(install!, binding.Id, "verify-unverifiable");
        Assert.True(File.Exists(Path.Combine(after, RelApp, "Diagnosis.vis.xml")),
                    "a deletion nothing could check went ahead anyway");
    }

    // ── C9: no manifest, no structural change ────────────────────────────────────────

    /// <summary>
    /// A mirror with no manifest imports its files and removes nothing.
    /// </summary>
    /// <remarks>
    /// What a manifest does not list cannot be told apart from what it never recorded, so with
    /// no manifest at all every absence would look like a deletion. The plan says structural
    /// changes are unavailable and carries no <c>deletions</c> key — this is the end-to-end
    /// half of that: the object whose file was deleted is still in the project.
    /// </remarks>
    [SkippableFact]
    public async Task A_mirror_with_no_manifest_deletes_nothing()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        var (binding, orchestrator) = await AdoptAndExportAsync(install!);
        var mirror = Path.Combine(_repo, "plc", "RefrigeratorControl");
        var app = Path.Combine(mirror, RelApp.Replace('/', Path.DirectorySeparatorChar));

        File.Delete(Path.Combine(app, "Simulation.st"));
        File.Delete(Path.Combine(mirror, "_manifest.json"));

        var outcome = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import — {outcome.Verdict}: {SyncNarrative.Describe(outcome)}");
        foreach (var w in outcome.Warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        var after = await ReExport(install!, binding.Id, "verify-no-manifest");
        Assert.True(File.Exists(Path.Combine(after, RelApp, "Simulation.st")),
                    "an object was deleted on the strength of a mirror with no record at all");
    }

    // ── B4: a form this version does not know ────────────────────────────────────────

    /// <summary>
    /// An object recorded with an unknown form is skipped rather than guessed at.
    /// </summary>
    /// <remarks>
    /// A newer exporter writing a form this importer has never heard of is the case: refusing
    /// is safer than picking an importer for it. Reproduced by rewriting one record's form,
    /// which is exactly what such a mirror would look like.
    /// </remarks>
    [SkippableFact]
    public async Task An_object_recorded_with_an_unknown_form_is_skipped_and_named()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        var (binding, orchestrator) = await AdoptAndExportAsync(install!);
        var mirror = Path.Combine(_repo, "plc", "RefrigeratorControl");
        var app = Path.Combine(mirror, RelApp.Replace('/', Path.DirectorySeparatorChar));

        var manifestPath = Path.Combine(mirror, "_manifest.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);

        // Simulation's record becomes a form from a future Versio. Its file is edited too, so
        // a planner that ignored the form would have something to apply and the difference
        // between "skipped" and "applied" is visible in the project.
        await WriteAsync(manifestPath, RetypeFormFor(manifest, "Simulation.st", "ST_FUTURE"));

        var st = Path.Combine(app, "Simulation.st");
        await WriteAsync(st, await File.ReadAllTextAsync(st) + "\n(* audit marker *)\n");

        var outcome = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import — {outcome.Verdict}: {SyncNarrative.Describe(outcome)}");
        foreach (var w in outcome.Warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        Assert.Contains(Lines(), l => l.Contains("ST_FUTURE", StringComparison.Ordinal));

        var after = await ReExport(install!, binding.Id, "verify-unknown-form");

        // EXISTENCE FIRST. Reading the file before asserting it is there means a missing object
        // fails as an IOException from the harness rather than as the sentence that explains it.
        Assert.True(File.Exists(Path.Combine(after, RelApp, "Simulation.st")),
                    "the object went, on a record this version said it did not understand");

        var reExported = await File.ReadAllTextAsync(Path.Combine(after, RelApp, "Simulation.st"));
        Assert.DoesNotContain("audit marker", reExported, StringComparison.Ordinal);
    }

    // ── D6: a move, and whether CODESYS performed it ─────────────────────────────────

    /// <summary>
    /// A POU moved into another container is either moved or reported — never claimed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The measurement, not the assertion, is the point of this one.</b> Phase 3 recorded
    /// that <c>move()</c> accepted an instruction to move a POU out of <c>Application</c> and
    /// into <c>Plc Logic</c>, raised nothing, and left the object where it was — which is why
    /// the script re-resolves the object at its intended location and reports
    /// <c>RENAME_NOT_APPLIED</c>. This drives that path through the whole pipeline for the
    /// first time.
    /// </para>
    /// <para>
    /// So it asserts the disjunction: the object is at the new location, or the import said it
    /// is not. What must never happen is the third outcome — a clean import and an object still
    /// in its old place.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_POU_moved_to_another_container_is_moved_or_reported_not_claimed()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        // The history probe is not what pairs this one - pass 1 matches on content and never
        // asks git anything. It is here so the run looks like a real repository, where history
        // is always available, rather than like the narrower arrangement every other rename
        // test uses.
        var git = new GitCliClient(_repo, logger: new TestLogger(output));
        var (binding, orchestrator) = await AdoptAndExportAsync(install!, git);

        var mirror = Path.Combine(_repo, "plc", "RefrigeratorControl");
        var app = Path.Combine(mirror, RelApp.Replace('/', Path.DirectorySeparatorChar));
        var plcLogic = Directory.GetParent(app)!.FullName;

        var source = Path.Combine(app, "Simulation.st");
        var body = await File.ReadAllTextAsync(source);
        File.Delete(source);

        // Same name, same content, one level up: a pure move, so the exact-content pass is
        // what pairs it and nothing about the declaration changes.
        await WriteAsync(Path.Combine(plcLogic, "Simulation.st"), body);

        var outcome = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import — {outcome.Verdict}: {SyncNarrative.Describe(outcome)}");
        foreach (var w in outcome.Warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        var after = await ReExport(install!, binding.Id, "verify-move");
        var landed = File.Exists(Path.Combine(after, "Device", "Plc Logic", "Simulation.st"));
        var stayed = File.Exists(Path.Combine(after, RelApp, "Simulation.st"));
        output.WriteLine($"landed={landed} stayed={stayed}");

        // Whatever CODESYS did, the object still exists somewhere: a move must never lose it.
        Assert.True(landed || stayed, "the object is in neither place — the move destroyed it");

        if (!landed)
        {
            Assert.Contains(outcome.Warnings,
                w => w.Contains("RENAME_NOT_APPLIED", StringComparison.Ordinal)
                     || w.Contains("MOVE_FAILED", StringComparison.Ordinal)
                     || w.Contains("RENAME_NO_PARENT", StringComparison.Ordinal));
        }

        // AND IF IT WAS DECLINED, THE SENTENCE HAS TO SAY SO IN A WAY SOMEBODY CAN ACT ON.
        // Until P11 (2026-08-17) the refusal named the object and the path and left the reader
        // nothing to do but try again. The probe ran nine ordered moves: a POU into a folder,
        // into a nested folder and back out into Application, a GVL to the project root, and
        // an EMPTY FOLDER into Plc Logic are all performed; a POU into Device, a visualisation
        // into Plc Logic, and a folder holding a POU into Plc Logic are accepted and silently
        // ignored. Nothing ever raised.
        //
        // Asserted on the warning that is actually there rather than on the script's source,
        // because the sentence has to survive the trip through the script, the protocol, the
        // runner and SyncOutcome — and F4 had to be written to prove a detail can cross that
        // seam at all.
        var declined = outcome.Warnings.FirstOrDefault(
            w => w.Contains("RENAME_NOT_APPLIED", StringComparison.Ordinal));
        if (declined is not null)
        {
            Assert.Contains("Plc Logic", declined, StringComparison.Ordinal);

            // The actionable half. A refusal that explains the toolchain but not the next
            // step is the state this replaced.
            Assert.Contains("Move it in the IDE", declined, StringComparison.Ordinal);
        }
    }

    // ── C10: an object already gone from the project ─────────────────────────────────

    /// <summary>
    /// A deletion whose object is already gone is neither a failure nor a refusal.
    /// </summary>
    /// <remarks>
    /// Reached by importing the same deletion twice: the first import removes the object, and
    /// the second is handed the same plan against a project that no longer has it. The mirror
    /// and the project agree, so there is nothing to do — and the run must not be marked failed
    /// for it, because a failed binding stops syncing altogether.
    /// </remarks>
    [SkippableFact]
    public async Task A_deletion_of_an_object_already_gone_is_reported_as_nothing_to_do()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        var (binding, orchestrator) = await AdoptAndExportAsync(install!);
        var mirror = Path.Combine(_repo, "plc", "RefrigeratorControl");
        var app = Path.Combine(mirror, RelApp.Replace('/', Path.DirectorySeparatorChar));

        var manifestPath = Path.Combine(mirror, "_manifest.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);

        File.Delete(Path.Combine(app, "Simulation.st"));

        var first = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import 1 — {first.Verdict}: {SyncNarrative.Describe(first)}");
        Assert.Equal(SyncVerdict.Completed, first.Verdict);

        var afterFirst = await ReExport(install!, binding.Id, "verify-gone-1");
        Assert.False(File.Exists(Path.Combine(afterFirst, RelApp, "Simulation.st")),
                     "the first import did not remove the object, so the second proves nothing");

        // The manifest still records the object — a successful import does not re-export — so
        // planning again produces the same deletion against a project that no longer has it.
        // Rewritten anyway, because the mirror must look untouched for the plan to repeat.
        await WriteAsync(manifestPath, manifest);

        // Something to apply, so the import is not refused for having no work.
        var st = Path.Combine(app, "Glob_Var.gvl.st");
        await WriteAsync(st, await File.ReadAllTextAsync(st) + "\n");

        var second = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import 2 — {second.Verdict}: {SyncNarrative.Describe(second)}");
        foreach (var w in second.Warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        Assert.Equal(SyncVerdict.Completed, second.Verdict);
        Assert.DoesNotContain(second.Warnings,
            w => w.Contains("DELETE_FAILED", StringComparison.Ordinal));
    }

    // ── B5 and B6: the race between planning and executing ───────────────────────────

    /// <summary>
    /// A file that appears after the plan is not imported; one that vanishes is reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The only two rows in the checklist that cannot be reached through the orchestrator</b>,
    /// because they are about the window between the plan being computed and CODESYS opening the
    /// project — which the orchestrator closes. So the plan is computed here, the mirror is
    /// changed underneath it, and the job is handed to the runner with that plan attached: the
    /// same door <c>SyncViewModel</c> uses, and the reason <c>ProjectJob.Plan</c> is a caller's
    /// to supply.
    /// </para>
    /// <para>
    /// Both in one test because the two halves share three CODESYS launches and neither
    /// disturbs the other: one file arrives, a different one goes.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task The_race_between_planning_and_importing_is_reported_both_ways()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        Directory.CreateDirectory(Path.Combine(_repo, "plc"));
        var project = Path.Combine(_repo, "plc", "RefrigeratorControl.project");
        File.Copy(FixtureProject, project);

        var mirror = Path.Combine(_repo, "plc", "RefrigeratorControl");
        var logger = new TestLogger(output);
        var runner = new LocalCodesysRunner(install!.ExecutablePath, new ScriptExtractor(), logger);

        Assert.True((await runner.RunAsync(
            Job(install, CodesysOperation.Export, project, mirror, null),
            null, CancellationToken.None)).Succeeded);

        var app = Path.Combine(mirror, RelApp.Replace('/', Path.DirectorySeparatorChar));

        // Edited BEFORE planning, so the plan has real work in it and the two warnings below
        // are not the only thing the run produces.
        var st = Path.Combine(app, "Simulation.st");
        await WriteAsync(st, await File.ReadAllTextAsync(st) + "\n(* race probe *)\n");

        var plan = await ImportPlanReader.ReadAsync(
            new WindowsFileSystem(), CodesysProjectFormat.Instance, mirror, logger,
            "audit", "race", CancellationToken.None);

        output.WriteLine($"planned {plan.Files.Count} files");

        // NOW change the mirror underneath the plan. One file the plan has never heard of, and
        // one the plan expects that is no longer there.
        await WriteAsync(Path.Combine(app, "Latecomer.st"), "PROGRAM Latecomer\nVAR\nEND_VAR\n\n;\n");
        File.Delete(Path.Combine(app, "Glob_Var.gvl.st"));

        var result = await runner.RunAsync(
            Job(install, CodesysOperation.Import, project, mirror, plan),
            null, CancellationToken.None);

        // The script's own events rather than the orchestrator's projected sentences: this run
        // never went through the orchestrator, and the code and the path are what the two rows
        // are about.
        var warnings = result.Output.Warnings
            .Select(w => $"{w.String("code")} {w.String("path")} {w.String("detail")}")
            .ToList();

        foreach (var w in warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        Assert.Contains(warnings,
            w => w.Contains("NOT_IN_PLAN", StringComparison.Ordinal)
                 && w.Contains("Latecomer", StringComparison.Ordinal));

        Assert.Contains(warnings,
            w => w.Contains("PLANNED_FILE_MISSING", StringComparison.Ordinal)
                 && w.Contains("Glob_Var", StringComparison.Ordinal));

        // And the latecomer never became an object, which is G6's rule not getting a
        // race-window exception.
        var verify = Path.Combine(_repo, "verify-race");
        Assert.True((await runner.RunAsync(
            Job(install, CodesysOperation.Export, project, verify, null),
            null, CancellationToken.None)).Succeeded);

        Assert.False(File.Exists(Path.Combine(verify, RelApp, "Latecomer.st")),
                     "a file nobody planned was imported anyway");
    }

    // ── D4: the line sketch, on a mirror that was never committed ────────────────────

    /// <summary>
    /// A POU renamed <i>and</i> edited on an uncommitted mirror is still recognised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sibling of <c>A_POU_renamed_and_edited_together_…</c>, and the half that does not
    /// have git to fall back on. That test commits the mirror on purpose so HEAD holds the
    /// departed file's bytes; this one deliberately does not commit and builds the orchestrator
    /// with no history probe at all, which is the state of any mirror between an export and the
    /// first commit — the exact window in which somebody renames something.
    /// </para>
    /// <para>
    /// What pairs the two files here is manifest schema 7's <c>shash</c>: a bounded fingerprint
    /// of the departed file's lines, recorded at export time. It is an estimate and second
    /// choice to git's actual bytes, which is why it is the fallback and not the rule.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_renamed_and_edited_POU_on_an_uncommitted_mirror_is_matched_by_its_sketch()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        // NO history probe and NO commit: git has nothing to say about the departed file.
        var (binding, orchestrator) = await AdoptAndExportAsync(install!);

        var mirror = Path.Combine(_repo, "plc", "RefrigeratorControl");
        var app = Path.Combine(mirror, RelApp.Replace('/', Path.DirectorySeparatorChar));

        var before = ProjectManifest.Parse(
            await File.ReadAllTextAsync(Path.Combine(mirror, "_manifest.json")));
        var original = Assert.Single(
            before.Objects, o => o.Path.EndsWith("Simulation.st", StringComparison.Ordinal));

        // Without this the test would prove nothing: no sketch, no third pass.
        Assert.NotEqual(string.Empty, original.LineSketch);
        output.WriteLine($"before: {original.Path} guid={original.Guid}");

        var source = Path.Combine(app, "Simulation.st");
        var body = await File.ReadAllTextAsync(source);
        File.Delete(source);
        await WriteAsync(
            Path.Combine(app, "Renamed.st"),
            body.Replace("PROGRAM Simulation", "PROGRAM Renamed", StringComparison.Ordinal)
            + "\n(* one more line *)\n");

        var outcome = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import — {outcome.Verdict}: {SyncNarrative.Describe(outcome)}");
        foreach (var w in outcome.Warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        var after = ProjectManifest.Parse(await File.ReadAllTextAsync(Path.Combine(
            await ReExport(install!, binding.Id, "verify-sketch"), "_manifest.json")));

        var renamed = Assert.Single(
            after.Objects, o => o.Path.EndsWith("Renamed.st", StringComparison.Ordinal));
        output.WriteLine($"after:  {renamed.Path} guid={renamed.Guid}");

        Assert.Equal(original.Guid, renamed.Guid);
        Assert.DoesNotContain(after.Objects,
            o => o.Path.EndsWith("Simulation.st", StringComparison.Ordinal));
    }

    // ── C7: the orphan rule ──────────────────────────────────────────────────────────

    /// <summary>
    /// An object something else still lives inside is kept, whatever the mirror says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// P3 is why this rule exists: <c>remove()</c> deletes an object other objects reference
    /// with no exception, no cascade and no warning, so nothing downstream will object to a
    /// mistake made here.
    /// </para>
    /// <para>
    /// <b>The nesting is written into the manifest rather than into the project</b>, and that is
    /// the honest limit of this row. The fixture has no member objects at all — no method, no
    /// action, no property — so the only way to give the planner a parent-child pair to reason
    /// about is to record one. What that measures is the rule and the refusal reaching the
    /// project; what it does not measure is the export recording a real parent, which needs a
    /// project this repository does not have.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task An_object_something_still_lives_inside_is_kept()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        var (binding, orchestrator) = await AdoptAndExportAsync(install!);
        var mirror = Path.Combine(_repo, "plc", "RefrigeratorControl");
        var app = Path.Combine(mirror, RelApp.Replace('/', Path.DirectorySeparatorChar));

        var manifestPath = Path.Combine(mirror, "_manifest.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        var parsed = ProjectManifest.Parse(manifest);

        var owner = Assert.Single(
            parsed.Objects, o => o.Path.EndsWith("Simulation.st", StringComparison.Ordinal));

        // Signals is recorded as living inside Simulation, and its file stays put.
        await WriteAsync(manifestPath, GiveParent(manifest, "Signals.xml", owner.Guid));
        File.Delete(Path.Combine(app, "Simulation.st"));

        var outcome = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import — {outcome.Verdict}: {SyncNarrative.Describe(outcome)}");
        foreach (var w in outcome.Warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        Assert.Contains(Lines(),
            l => l.Contains("still lives inside it", StringComparison.Ordinal)
                 || l.Contains("still live inside it", StringComparison.Ordinal));

        var after = await ReExport(install!, binding.Id, "verify-orphan");
        Assert.True(File.Exists(Path.Combine(after, RelApp, "Simulation.st")),
                    "the object was removed with something still recorded inside it");
    }

    // ── C8: the guessed-chain rule ───────────────────────────────────────────────────

    /// <summary>
    /// An object whose owner is not in the manifest is kept rather than resolved by file name.
    /// </summary>
    /// <remarks>
    /// The chain falls back to splitting the file stem when an owner is missing, and for an
    /// update that is a fair guess — the worst case is text applied to the wrong object, which
    /// git can undo. For a deletion the same guess resolves to some other object and destroys
    /// it, and P3 says nothing downstream will complain.
    /// </remarks>
    [SkippableFact]
    public async Task An_object_whose_owner_is_not_recorded_is_kept_rather_than_guessed_at()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        var (binding, orchestrator) = await AdoptAndExportAsync(install!);
        var mirror = Path.Combine(_repo, "plc", "RefrigeratorControl");
        var app = Path.Combine(mirror, RelApp.Replace('/', Path.DirectorySeparatorChar));

        var manifestPath = Path.Combine(mirror, "_manifest.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);

        // An owner GUID no record in this manifest carries.
        await WriteAsync(manifestPath, GiveParent(
            manifest, "Diagnosis.vis.xml", "00000000-dead-beef-0000-000000000000"));

        File.Delete(Path.Combine(app, "Diagnosis.vis.xml"));

        var outcome = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import — {outcome.Verdict}: {SyncNarrative.Describe(outcome)}");
        foreach (var w in outcome.Warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        Assert.Contains(Lines(),
            l => l.Contains("can only be guessed at", StringComparison.Ordinal));

        var after = await ReExport(install!, binding.Id, "verify-guessed-chain");
        Assert.True(File.Exists(Path.Combine(after, RelApp, "Diagnosis.vis.xml")),
                    "an object was deleted at a location that could only be guessed at");
    }

    // ── D8: an ambiguous pairing ─────────────────────────────────────────────────────

    /// <summary>
    /// One departed file and two arrivals holding its body is refused, not guessed.
    /// </summary>
    /// <remarks>
    /// Which of the two is the rename cannot be told, and picking one silently would rename the
    /// object onto an unrelated file. Refusing costs the GUID — the pair becomes a deletion and
    /// two creates — which is the outcome the ambiguity rule chooses deliberately.
    /// </remarks>
    [SkippableFact]
    public async Task Two_arrivals_holding_one_departures_body_are_refused_as_a_rename()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");
        Skip.If(!File.Exists(FixtureProject), "fixture project missing");

        var (binding, orchestrator) = await AdoptAndExportAsync(install!);
        var app = Path.Combine(_repo, "plc", "RefrigeratorControl",
                               RelApp.Replace('/', Path.DirectorySeparatorChar));

        var source = Path.Combine(app, "Simulation.st");
        var body = await File.ReadAllTextAsync(source);
        File.Delete(source);

        // Two files, two different names, one body. The implementation hash matches both.
        await WriteAsync(Path.Combine(app, "First.st"),
            body.Replace("PROGRAM Simulation", "PROGRAM First", StringComparison.Ordinal));
        await WriteAsync(Path.Combine(app, "Second.st"),
            body.Replace("PROGRAM Simulation", "PROGRAM Second", StringComparison.Ordinal));

        var outcome = await TextChanged(orchestrator, binding.Id);
        output.WriteLine($"import — {outcome.Verdict}: {SyncNarrative.Describe(outcome)}");
        foreach (var w in outcome.Warnings)
        {
            output.WriteLine($"warning: {w}");
        }

        Assert.Contains(Lines(),
            l => l.Contains("which is the rename cannot be told", StringComparison.Ordinal));
    }

    // ── A3: the forms only a member object has ───────────────────────

    /// <summary>
    /// An action, a method and a property, edited in the mirror and read back out of the
    /// project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row the first fixture could not reach.</b> <c>ST_IMPL</c> is the form an ACTION
    /// and a TRANSITION export as — implementation only, no declaration at all — and
    /// <c>RefrigeratorControl</c> has neither, so nothing in this repository had ever imported
    /// one. <c>MachineWithMembers</c> was built against real CODESYS for exactly this;
    /// <c>tools\versio_make_member_fixture.py</c> is what built it and records what the four
    /// creation calls actually accept.
    /// </para>
    /// <para>
    /// <b>Three forms in one run, because the CODESYS launches are the cost.</b> The action is
    /// <c>ST_IMPL</c>, the method is <c>ST_FULL</c> on a member rather than on a top-level POU,
    /// and the property is <c>NATIVE_R</c> — a recursive native export that carries its own
    /// Get and Set. Between them they are every route a member takes into a project.
    /// </para>
    /// <para>
    /// <b>Driven straight through the runner</b>, like
    /// <see cref="The_race_between_planning_and_importing_is_reported_both_ways"/>: no
    /// adoption, no git, no orchestrator. The subject is the planner and the script against a
    /// shape neither has met, and the gates above them would only add ways not to reach it.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task An_action_a_method_and_a_property_edited_in_the_mirror_reach_the_project()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");

        var fixture = TestPaths.Project("MachineWithMembers.project");
        Skip.If(!File.Exists(fixture), "the member fixture is missing");

        Directory.CreateDirectory(Path.Combine(_repo, "plc"));
        var project = Path.Combine(_repo, "plc", "MachineWithMembers.project");
        File.Copy(fixture, project);

        var mirror = Path.Combine(_repo, "plc", "MachineWithMembers");
        var runner = new LocalCodesysRunner(
            install!.ExecutablePath, new ScriptExtractor(), new TestLogger(output));

        CodesysJob Job(CodesysOperation operation, string path) => new()
        {
            Operation = operation,
            Profile = install.Profiles[0],
            Projects = [new ProjectJob
            {
                BindingId = "members", ProjectPath = project, Path = path,
            }],
            Timeout = TimeSpan.FromMinutes(5),
        };

        Assert.True((await runner.RunAsync(
            Job(CodesysOperation.Export, mirror), null, CancellationToken.None)).Succeeded);

        var pumps = Path.Combine(mirror, "Device", "Plc Logic", "Application", "Pumps");

        // THE ACTION — ST_IMPL. Its file is the body and nothing else, so an edit here has
        // nowhere to hide: there is no declaration for the importer to fall back on.
        var action = Path.Combine(pumps, "FB_Pump.Stop.action.st");
        var actionText = await File.ReadAllTextAsync(action);
        Assert.DoesNotContain("audit_action", actionText, StringComparison.Ordinal);
        await WriteAsync(action, actionText.TrimEnd() + "\nspeed := 0; (* audit_action *)\n");

        // THE METHOD — ST_FULL, on a member. Both halves edited, so a run that applied the
        // declaration and dropped the implementation is caught.
        var method = Path.Combine(pumps, "FB_Pump.Start.st");
        var methodText = await File.ReadAllTextAsync(method);
        await WriteAsync(method, methodText
            .Replace("target : INT;", "target : INT; (* audit_decl *)", StringComparison.Ordinal)
            .Replace("Start := TRUE;", "Start := TRUE; (* audit_impl *)", StringComparison.Ordinal));

        var import = await runner.RunAsync(
            Job(CodesysOperation.Import, mirror), null, CancellationToken.None);

        foreach (var w in import.Output.Warnings)
        {
            output.WriteLine($"warning: {w.String("code")} {w.String("path")} {w.String("detail")}");
        }

        Assert.True(import.Succeeded, import.FailureReason ?? "the import failed");

        // No warning at all about the member files. A resolve chain built from the stem
        // instead of the recorded parent would land here as PARENT_NOT_FOUND.
        Assert.DoesNotContain(import.Output.Warnings,
            w => (w.String("path") ?? string.Empty).Contains("FB_Pump", StringComparison.Ordinal));

        // Read back out of the PROJECT. Nothing cheaper distinguishes an applied edit from a
        // parsed one.
        var verify = Path.Combine(_repo, "verify-members");
        Assert.True((await runner.RunAsync(
            Job(CodesysOperation.Export, verify), null, CancellationToken.None)).Succeeded);

        var verifyPumps = Path.Combine(verify, "Device", "Plc Logic", "Application", "Pumps");

        var reAction = await File.ReadAllTextAsync(
            Path.Combine(verifyPumps, "FB_Pump.Stop.action.st"));
        output.WriteLine(reAction);
        Assert.Contains("audit_action", reAction, StringComparison.Ordinal);

        var reMethod = await File.ReadAllTextAsync(
            Path.Combine(verifyPumps, "FB_Pump.Start.st"));
        output.WriteLine(reMethod);
        Assert.Contains("audit_decl", reMethod, StringComparison.Ordinal);
        Assert.Contains("audit_impl", reMethod, StringComparison.Ordinal);

        // AND THE PROPERTY SURVIVED THE ROUND TRIP INTACT, which is a different claim from
        // the two above and the reason NATIVE_R is in this test at all: it is imported
        // recursively, so a mistake takes its Get and Set with it rather than reporting
        // anything. The accessors are inside the payload and never appear as records.
        var property = Path.Combine(verifyPumps, "FB_Pump.Speed.xml");
        Assert.True(File.Exists(property), "the property left the project");

        var manifest = ProjectManifest.Parse(
            await File.ReadAllTextAsync(Path.Combine(verify, "_manifest.json")));

        Assert.Equal("NATIVE_R", Assert.Single(
            manifest.Objects, o => o.Path.EndsWith("FB_Pump.Speed.xml", StringComparison.Ordinal))
            .Form);

        // The owner chain is still recorded after an import, not just after the export that
        // built the fixture — three members, each pointing at FB_Pump.
        Assert.Equal(3, manifest.Objects.Count(o => !string.IsNullOrEmpty(o.Parent)));
    }

    // ── E4: the digest-tail stem, through a real round trip ─────────────────────────

    /// <summary>
    /// An external file whose name overflows the stem survives export, import and re-export,
    /// resolved through the manifest the whole way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The live half of E4</b>, over the two objects the member fixture gained on
    /// 2026-08-17. One is named past <c>MaxStem</c>, so its file is
    /// <c>Commissioning_Checklist_…~ce8286.xml</c> — a stem no resolution can decode, which
    /// makes this round trip the proof that resolution never needs to: the recorded name
    /// carries it. The pure half — the same file straying when its record is stripped — is
    /// <c>CodesysImportPlannerTests</c>, where it costs milliseconds instead of launches.
    /// </para>
    /// <para>
    /// <b>The first run of this test handed <c>import_native</c> an <c>EXTERNAL_FILE</c>
    /// payload, and CODESYS duplicated the object</b> — same name, same parent, a re-minted
    /// GUID, two manifest records on one mirror path. That is G13, found exactly the way G11
    /// taught this repository to look: a type that exports losslessly and mutates on the way
    /// back. The form is <c>EXTERNAL_NATIVE</c> now, export-only, so the import announces the
    /// skip instead of feeding the duplicator — and the load-bearing assertion below is the
    /// single-record check, which is this defect's regression test.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task An_external_file_with_a_digest_stem_survives_a_real_round_trip()
    {
        var install = Install;
        Skip.If(install is null, "no CODESYS installation with a profile");

        var fixture = TestPaths.Project("MachineWithMembers.project");
        Skip.If(!File.Exists(fixture), "the member fixture is missing");

        Directory.CreateDirectory(Path.Combine(_repo, "plc"));
        var project = Path.Combine(_repo, "plc", "MachineWithMembers.project");
        File.Copy(fixture, project);

        var mirror = Path.Combine(_repo, "plc", "MachineWithMembers");
        var runner = new LocalCodesysRunner(
            install!.ExecutablePath, new ScriptExtractor(), new TestLogger(output));

        CodesysJob Job(CodesysOperation operation, string path) => new()
        {
            Operation = operation,
            Profile = install.Profiles[0],
            Projects = [new ProjectJob
            {
                BindingId = "external", ProjectPath = project, Path = path,
            }],
            Timeout = TimeSpan.FromMinutes(5),
        };

        Assert.True((await runner.RunAsync(
            Job(CodesysOperation.Export, mirror), null, CancellationToken.None)).Succeeded);

        var app = Path.Combine(mirror, "Device", "Plc Logic", "Application");
        var digestFile = Directory.GetFiles(app, "*~*.xml");
        var digestName = Path.GetFileName(Assert.Single(digestFile));
        output.WriteLine($"digest stem on disk: {digestName}");
        var digestXml = await File.ReadAllTextAsync(digestFile[0]);

        // An edit somewhere ordinary, so the import has work and the hash gate opens. The
        // external files themselves are untouched — they travel as unchanged natives, which
        // is exactly how every real import will meet them.
        var pou = Path.Combine(app, "Pumps", "FB_Pump.st");
        await WriteAsync(pou, (await File.ReadAllTextAsync(pou))
            .Replace("END_VAR", "\t(* external_audit *)\nEND_VAR", StringComparison.Ordinal));

        var import = await runner.RunAsync(
            Job(CodesysOperation.Import, mirror), null, CancellationToken.None);

        foreach (var w in import.Output.Warnings)
        {
            output.WriteLine($"warning: {w.String("code")} {w.String("path")} {w.String("detail")}");
        }

        Assert.True(import.Succeeded, import.FailureReason ?? "the import failed");

        // No warning may mention either external object — and neither may reach CODESYS at
        // all: the plan skips them as export-only, announced, which the summary's
        // "not applied (export-only)" count carries.
        Assert.DoesNotContain(import.Output.Warnings,
            w => (w.String("path") ?? string.Empty).Contains("~", StringComparison.Ordinal)
                 || (w.String("path") ?? string.Empty).Contains("Wiring", StringComparison.Ordinal));

        // Read back out of the PROJECT. THE SINGLE-RECORD ASSERTIONS ARE G13'S REGRESSION
        // TEST: the first run of this test imported these two files as plain NATIVE and
        // CODESYS duplicated each object — two records, one path, a re-minted GUID. The
        // export-only form is what stands between an import and that duplication, and this
        // is the run that fails if anything puts the files back on the import path.
        var verify = Path.Combine(_repo, "verify-external");
        Assert.True((await runner.RunAsync(
            Job(CodesysOperation.Export, verify), null, CancellationToken.None)).Succeeded);

        var verifyApp = Path.Combine(verify, "Device", "Plc Logic", "Application");
        var reDigest = Path.Combine(verifyApp, digestName);
        Assert.True(File.Exists(reDigest), "the long-named external file left the project");

        // Byte-identical, not merely equivalent: the import never touched the objects, so
        // unlike a round-tripped native there is no re-minted GUID to mask.
        Assert.Equal(digestXml, await File.ReadAllTextAsync(reDigest));
        Assert.True(File.Exists(Path.Combine(verifyApp, "Wiring Notes.txt.xml")),
            "the plain external file left the project");

        var manifest = ProjectManifest.Parse(
            await File.ReadAllTextAsync(Path.Combine(verify, "_manifest.json")));

        var digestRecord = Assert.Single(
            manifest.Objects, o => o.Path.Contains("~", StringComparison.Ordinal));
        Assert.Equal("EXTERNAL_FILE", digestRecord.Type);
        Assert.Equal("EXTERNAL_NATIVE", digestRecord.Form);
        Assert.EndsWith("Final.txt", digestRecord.Name, StringComparison.Ordinal);

        Assert.Single(manifest.Objects,
            o => o.Name == "Wiring Notes.txt" && o.Type == "EXTERNAL_FILE");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private static CodesysJob Job(
        CodesysInstall install, CodesysOperation operation, string project, string path,
        ImportPlan? plan) => new()
        {
            Operation = operation,
            Profile = install.Profiles[0],
            Projects = [new ProjectJob
            {
                BindingId = "audit", ProjectPath = project, Path = path, Plan = plan,
            }],
            Timeout = TimeSpan.FromMinutes(5),
        };

    /// <summary>Adopt the fixture, export it once, and release the lock the export took.</summary>
    private async Task<(ProjectBinding Binding, SyncOrchestrator Orchestrator)> AdoptAndExportAsync(
        CodesysInstall install, GitCliClient? history = null)
    {
        Directory.CreateDirectory(Path.Combine(_repo, "plc"));
        File.Copy(FixtureProject, Path.Combine(_repo, "plc", "RefrigeratorControl.project"));

        var git = new GitCliClient(_repo, logger: new TestLogger(output));
        await RunGitAsync("init", "-b", "main");
        await RunGitAsync("config", "user.email", "test@versio.local");
        await RunGitAsync("config", "user.name", "Versio Test");

        var adopted = await new RepoSetup(_repo, CodesysProjectFormat.Instance)
            .AdoptAsync(ProjectRelativePath, git, install.Profiles[0], CancellationToken.None);

        var binding = adopted.Config.Bindings[0];
        var machine = new MachineIdentity();

        var orchestrator = new SyncOrchestrator(
            _repo,
            adopted.Config.Bindings,
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
            new LocalCodesysRunner(install.ExecutablePath, new ScriptExtractor(), new TestLogger(output)),
            new JsonLockStore(_repo),

            // THE ORCHESTRATOR NEEDS A LOGGER HERE, and finding out why cost a run. The
            // planner's sentences — the two-file rule, the orphan rule, the guessed chain —
            // travel by LOG and deliberately not on the wire: `PlanDto` says so outright,
            // because they are for a person rather than for the script. So a harness that
            // asserts on `SyncOutcome.Warnings` alone can only ever see what the SCRIPT
            // said, and every planner refusal looks like silence.
            logger: Log,
            history: history is null ? null : new GitHistoricContent(history));

        var export = await orchestrator.HandleChangeAsync(new SyncJob
        {
            BindingId = binding.Id,
            Source = ChangeSource.Project,
            Synchronous = true,
            Reason = "audit export",
        }, CancellationToken.None);

        Assert.Equal(SyncVerdict.Completed, export.Verdict);
        await orchestrator.ReleaseAfterPushAsync("c1", CancellationToken.None);

        return (binding, orchestrator);
    }

    private static Task<SyncOutcome> TextChanged(SyncOrchestrator orchestrator, string bindingId) =>
        orchestrator.HandleChangeAsync(new SyncJob
        {
            BindingId = bindingId,
            Source = ChangeSource.ExportedText,
            Confirmed = true,
            Synchronous = true,
            Reason = "audit import",
        }, CancellationToken.None);

    /// <summary>
    /// Export the project again into a scratch directory, straight through the runner.
    /// </summary>
    /// <remarks>
    /// Not back through the orchestrator: it recorded the project's hash when the import ran,
    /// so from its point of view nothing has happened since and it would answer NoChange. Going
    /// around that gate asks the only question these tests are about — what the project on disk
    /// now contains.
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

    private static Task WriteAsync(string path, string text) =>
        File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false));

    /// <summary>
    /// Record an owner for one object, as manifest schema 4 and later do for a member.
    /// </summary>
    /// <remarks>
    /// Inserted textually rather than round-tripped through a serializer, because the file the
    /// planner reads must stay the file the exporter wrote in every other respect — and JSON
    /// member order carries no meaning to a reader, so an inserted key is a real record rather
    /// than an approximation of one.
    /// </remarks>
    private static string GiveParent(string manifest, string fileName, string parentGuid)
    {
        const string key = "\"path\": \"";
        var at = manifest.IndexOf(fileName, StringComparison.Ordinal);
        Assert.True(at > 0, $"{fileName} is not in the manifest");

        var pathAt = manifest.LastIndexOf(key, at, StringComparison.Ordinal);
        Assert.True(pathAt > 0, $"no path key before {fileName}");

        var indent = 0;
        while (pathAt - indent - 1 >= 0 && manifest[pathAt - indent - 1] == ' ')
        {
            indent++;
        }

        return manifest[..pathAt]
               + $"\"parent\": \"{parentGuid}\",\n{new string(' ', indent)}"
               + manifest[pathAt..];
    }

    /// <summary>
    /// Remove one object's <c>nhash</c>, leaving a record shaped like a pre-schema-5 export.
    /// </summary>
    /// <remarks>
    /// BACKWARDS from the path, for the reason <c>PoisonHashFor</c> records: the manifest is
    /// written with <c>sort_keys</c>, so a record's members are alphabetical and <c>nhash</c>
    /// comes before <c>path</c>. Searching forward finds the NEXT object's and edits a record
    /// the test never meant to touch.
    /// </remarks>
    private static string StripNormalizedHashFor(string manifest, string fileName)
    {
        const string key = "\"nhash\": \"";

        var at = manifest.IndexOf(fileName, StringComparison.Ordinal);
        Assert.True(at > 0, $"{fileName} is not in the manifest");

        var hashAt = manifest.LastIndexOf(key, at, StringComparison.Ordinal);
        Assert.True(hashAt > 0, $"no nhash recorded before {fileName}");
        Assert.DoesNotContain("}", manifest[hashAt..at], StringComparison.Ordinal);

        var end = manifest.IndexOf('\n', hashAt);
        Assert.True(end > hashAt, "the nhash line does not end");

        return manifest[..hashAt] + manifest[(end + 1)..];
    }

    /// <summary>Rewrite one object's recorded form, as a newer exporter's mirror would read.</summary>
    private static string RetypeFormFor(string manifest, string fileName, string form)
    {
        const string key = "\"form\": \"";

        var at = manifest.IndexOf(fileName, StringComparison.Ordinal);
        Assert.True(at > 0, $"{fileName} is not in the manifest");

        var formAt = manifest.LastIndexOf(key, at, StringComparison.Ordinal);
        Assert.True(formAt > 0, $"no form recorded before {fileName}");
        Assert.DoesNotContain("}", manifest[formAt..at], StringComparison.Ordinal);

        var valueAt = formAt + key.Length;
        var end = manifest.IndexOf('"', valueAt);
        Assert.True(end > valueAt, "the recorded form is not a string");

        return manifest[..valueAt] + form + manifest[end..];
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
        Assert.Equal(0, p.ExitCode);
    }
}
