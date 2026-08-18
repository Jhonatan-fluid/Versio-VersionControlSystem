using Versio.Core.Projects;
using Versio.TestSupport;
using Xunit.Abstractions;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// The coverage report, produced by a real CODESYS export.
/// </summary>
/// <remarks>
/// <para>
/// The C# side is unit-tested against manifests written by hand, which proves the rules and
/// nothing about whether the exporter produces them. This runs the real thing and asserts the
/// two halves agree — and in particular that the <c>LIBRARY_MANAGER</c> gap is reported on a
/// project that actually has one, which is the only way to know the skip reason is being
/// reached at all.
/// </para>
/// <para>
/// The export is exercised twice, because <c>_coverage.md</c> is now part of an export whose
/// determinism is a load-bearing property: a report containing a clock or an unsorted list
/// would put a spurious diff in every commit, which is precisely the noise the
/// <c>Timestamp</c> scrub exists to remove.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Toolchain", "Codesys")]
[Collection(CodesysCollection.Name)]
public sealed class ExportCoverageFromRealExportTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Versio", "coverage", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    /// <summary>Export a project that already exists, to a directory of its own.</summary>
    private async Task<string> ExportAgainAsync(string project, string outLabel)
    {
        var install = new Versio.Plc.Codesys.Infrastructure.CodesysLocator()
            .FindInstalls().FirstOrDefault(i => i.Profiles.Count > 0);
        Skip.If(install is null, "no CODESYS installation with a profile");

        var exportRoot = Path.Combine(_root, outLabel);

        var runner = new Versio.Plc.Codesys.Infrastructure.LocalCodesysRunner(
            install!.ExecutablePath,
            new Versio.Plc.Codesys.Infrastructure.ScriptExtractor(),
            new TestLogger(output));

        var result = await runner.RunAsync(
            new Versio.Plc.Codesys.CodesysJob
            {
                Operation = Versio.Plc.Codesys.CodesysOperation.Export,
                Profile = install.Profiles[0],
                Projects =
                [
                    new Versio.Plc.Codesys.ProjectJob
                    {
                        BindingId = "b-coverage",
                        ProjectPath = project,
                        Path = exportRoot,
                    },
                ],
            }, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        return exportRoot;
    }

    private async Task<string> ExportAsync(string label)
    {
        var install = new Versio.Plc.Codesys.Infrastructure.CodesysLocator()
            .FindInstalls().FirstOrDefault(i => i.Profiles.Count > 0);
        Skip.If(install is null, "no CODESYS installation with a profile");

        var fixture = TestPaths.Project("RefrigeratorControl.project");
        Skip.If(!File.Exists(fixture), "fixture project missing");

        var work = Path.Combine(_root, label);
        Directory.CreateDirectory(work);

        var project = Path.Combine(work, "RefrigeratorControl.project");
        File.Copy(fixture, project);

        var runner = new Versio.Plc.Codesys.Infrastructure.LocalCodesysRunner(
            install!.ExecutablePath,
            new Versio.Plc.Codesys.Infrastructure.ScriptExtractor(),
            new TestLogger(output));

        var exportRoot = Path.Combine(work, "RefrigeratorControl");

        var result = await runner.RunAsync(
            new Versio.Plc.Codesys.CodesysJob
            {
                Operation = Versio.Plc.Codesys.CodesysOperation.Export,
                Profile = install.Profiles[0],
                Projects =
                [
                    new Versio.Plc.Codesys.ProjectJob
                    {
                        BindingId = "b-coverage",
                        ProjectPath = project,
                        Path = exportRoot,
                    },
                ],
            }, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        return exportRoot;
    }

    [SkippableFact]
    public async Task A_real_export_records_what_it_did_not_write_and_why()
    {
        var exportRoot = await ExportAsync("first");

        var manifest = ProjectManifest.Parse(
            await File.ReadAllTextAsync(Path.Combine(exportRoot, "_manifest.json")));

        foreach (var skipped in manifest.Skipped)
        {
            output.WriteLine($"{skipped.Reason,-18} {skipped.Type,-22} {skipped.Path}");
        }

        // The schema is what says "this export counted". Without the bump, an old manifest
        // would read as an export that skipped nothing.
        //
        // Asserted against CurrentSchema rather than ReportingSchema: the latter is the
        // oldest manifest whose empty skip list can be believed, and it deliberately does
        // not move when the writer's version does.
        Assert.Equal(ExportCoverage.CurrentSchema, manifest.Schema);
        Assert.True(manifest.Schema >= ExportCoverage.ReportingSchema);
        Assert.NotEmpty(manifest.Skipped);

        // WHICH TOOLCHAIN WROTE IT, from schema 10 — and only a real export can prove this
        // one, because the string comes from inside CODESYS. `sys.version` there is not the
        // CPython version it is anywhere else: IronPython hosted by CODESYS answers with the
        // host and the ScriptEngine plug-in, which is exactly the fact the project card had
        // no way to state.
        //
        // Asserted by SHAPE, not by the version on this machine — pinning "SP22 Patch 2"
        // would fail on the next service pack and teach nobody anything. What must hold is
        // that something was recorded and that it names the toolchain.
        output.WriteLine($"toolchainVersion: {manifest.ToolchainVersion}");

        Assert.False(string.IsNullOrWhiteSpace(manifest.ToolchainVersion),
            "schema 10 records the toolchain that produced the mirror, and this export "
            + "recorded nothing — the card cannot say which CODESYS wrote it");

        Assert.Contains("CODESYS", manifest.ToolchainVersion, StringComparison.OrdinalIgnoreCase);

        Assert.All(manifest.Skipped, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Type), "a skip record has no type");
            Assert.False(string.IsNullOrWhiteSpace(s.Reason), $"{s.Type} has no reason");
            Assert.False(string.IsNullOrWhiteSpace(s.Path), $"{s.Type} has no path");
        });

        // Nothing the manifest lists as written may also be listed as skipped: the two lists
        // partition the project, and an object in both would make the report meaningless.
        // Keyed on path, because a skip record carries no GUID - see SkippedObject for why.
        var written = manifest.Objects
            .Select(o => o.Path)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(manifest.Skipped, x => written.Contains(x.Path));
    }

    [SkippableFact]
    public async Task The_library_manager_is_exported_and_no_longer_reported_as_a_gap()
    {
        // The finding this whole feature came from, now proven closed against the same real
        // project that exposed it: the fixture has TWO library managers, neither of which
        // reached the mirror while a comment promised a config/libraries.yaml that nothing
        // wrote. This assertion used to say the opposite.
        var exportRoot = await ExportAsync("first");

        var manifest = ProjectManifest.Parse(
            await File.ReadAllTextAsync(Path.Combine(exportRoot, "_manifest.json")));

        var report = ExportCoverage.Of(manifest);
        output.WriteLine(report.Describe());

        Assert.False(report.HasGaps);
        Assert.Empty(report.Gaps);
        Assert.DoesNotContain(manifest.Skipped, s => s.Type == "LIBRARY_MANAGER");

        // Both managers are written, each beside the node that owns it — the point of a
        // file per manager rather than one config/libraries.yaml: a library change shows up
        // under the device it belongs to, and two managers cannot share one file.
        var written = manifest.Objects.Where(o => o.Type == "LIBRARY_MANAGER").ToList();

        // One file per library manager, however many the project has. It read 2 until the
        // corpus gained a device that brings its own; what is being checked is that every
        // manager reaches the mirror, not how many there happen to be.
        Assert.Equal(
            manifest.Objects.Count(o => o.Type == "LIBRARY_MANAGER"),
            written.Count);

        Assert.True(written.Count >= 2,
            "the fixture must keep both library managers — the project's and the "
            + "application's — or the per-manager export proves nothing");
        Assert.All(written, o => Assert.Equal(ExportCoverage.LibrariesForm, o.Form));
        Assert.Contains(written, o => o.Path == "Library Manager.libraries.yaml");
        Assert.Contains(written,
            o => o.Path == "Device/Plc Logic/Application/Library Manager.libraries.yaml");

        // Exported is not the same as editable, and the report says so rather than leaving
        // it to be found out by editing one.
        //
        // FOUR: the two library files, the task configuration's native .xml, and the GVL's.
        //
        // It was 2, then 3, and each step was a defect being closed rather than a feature
        // arriving. 2 → 3 when Phase 4 made the task's .tasks.yaml the importable half and
        // nobody re-recorded its .xml; 3 → 4 on 2026-08-16 when G12 found the GVL's .gvl.xml
        // doing the identical thing — applied after the .gvl.st and overwriting it, with the
        // log reporting both as successes.
        //
        // BOTH TIMES THIS COUNT WAS THE CHEAPEST THING THAT COULD HAVE NOTICED, and both
        // times it did not, because it was written to pin the number rather than to ask
        // whether the number was right. What asks that now is
        // An_object_never_writes_two_files_the_import_would_both_apply, which is about the
        // shape and cannot be satisfied by an arithmetic that happens to agree.
        // DERIVED, with a floor. The literal 4 was right until 2026-08-18, when properties
        // began writing readable text beside their payload and the corpus gained a second GVL
        // — and a number that has to be re-typed whenever the project grows is a number that
        // stops being checked. What must hold is that the report agrees with the manifest it
        // describes; the floor keeps the claim non-vacuous.
        Assert.Equal(
            manifest.Objects.Count(o => ExportCoverage.IsExportOnly(o.Form)),
            report.ExportOnlyFiles);

        Assert.True(report.ExportOnlyFiles >= 4,
            $"only {report.ExportOnlyFiles} one-way file(s) — the fixture must still carry "
            + "the two library managers, the task XML and the GVL XML");
        Assert.Contains("not applied back", report.Describe(), StringComparison.Ordinal);

        // The deliberate skips are still not counted as gaps: the fixture's visualisation
        // manager is skipped for a reason that loses nothing.
        Assert.Contains(manifest.Skipped, s => s.Reason == "SERVICE_OBJECT");
        Assert.DoesNotContain(report.Gaps, g => g.Reason == "SERVICE_OBJECT");
    }

    [SkippableFact]
    public async Task The_task_configurations_native_half_is_recorded_and_named_as_one_way()
    {
        // What nothing was watching. The .tasks.yaml is the importable half and the .xml
        // beside it is discarded by the importer as TYPED_SIBLING — but the manifest recorded
        // that .xml as NATIVE_R, a form that a property's recursive export uses and that the
        // importer applies. So the panel drew it as an ordinary editable file and _coverage.md
        // never mentioned it: an edit to a task's XML was thrown away in silence.
        //
        // Against a real export rather than a hand-built manifest, because the defect was in
        // what the exporter WROTE. The unit tests would all have passed with the old form: the
        // C#/python seam only proves the two sides agree about which forms are one-way, not
        // that this file carries one of them.
        var exportRoot = await ExportAsync("first");

        var manifest = ProjectManifest.Parse(
            await File.ReadAllTextAsync(Path.Combine(exportRoot, "_manifest.json")));

        var task = manifest.Objects
            .Where(o => o.Type == "TASK_CONFIGURATION")
            .OrderBy(o => o.Path, StringComparer.Ordinal)
            .ToList();

        output.WriteLine(string.Join("\n", task.Select(o => $"{o.Form,-12} {o.Path}")));

        // One object, two files, one GUID — the GVL_BOTH shape, with the halves
        // reversed. GVL_BOTH was the model this was written against and it had the
        // identical defect at the time, unnoticed for another day: see G12 and
        // An_object_never_writes_two_files_the_import_would_both_apply.
        Assert.Equal(2, task.Count);
        Assert.Single(task.Select(o => o.Guid).Distinct(StringComparer.Ordinal));

        var xml = Assert.Single(task, o => o.Path.EndsWith(".xml", StringComparison.Ordinal));
        var yaml = Assert.Single(task,
            o => o.Path.EndsWith(".tasks.yaml", StringComparison.Ordinal));

        Assert.Equal(ExportCoverage.TaskNativeForm, xml.Form);
        Assert.True(ExportCoverage.IsExportOnly(xml.Form));
        Assert.False(ExportCoverage.IsExportOnly(yaml.Form));

        // And said out loud where somebody will meet it, with the remedy that is actually
        // right for this file. "Change it in the IDE" — correct for a library manager — would
        // walk the reader straight past the .tasks.yaml they were meant to edit.
        var coverage = await File.ReadAllTextAsync(Path.Combine(exportRoot, "_coverage.md"));
        output.WriteLine(coverage);

        Assert.Contains("Exported for review, not applied back", coverage, StringComparison.Ordinal);
        Assert.Contains(xml.Path, coverage, StringComparison.Ordinal);
        Assert.Contains($"edit `{yaml.Path}` instead", coverage, StringComparison.Ordinal);
    }

    /// <summary>
    /// No object writes two files the import would both apply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule the two-file shape never had, written after it failed twice.</b> A
    /// <c>_BOTH</c> form gives one object two files under one GUID, and
    /// <c>versio_import.py</c> runs its phases in a fixed order — rename, textual, native,
    /// delete. If both files are importable, the later phase silently overwrites whatever the
    /// earlier one applied, and the log reports both as successes because both were.
    /// </para>
    /// <para>
    /// That is G7 and it is G12. <c>TASK_BOTH</c> was fixed in Phase 4 by giving its XML half
    /// a form of its own; <c>GVL_BOTH</c> had the identical defect and nobody looked, because
    /// the fix was applied to the instance and not to the shape. This is the shape.
    /// </para>
    /// <para>
    /// <b>Against a real export, deliberately.</b> Both defects were in what the exporter
    /// <i>wrote</i>: every unit test passed throughout, and the C#/python seam only proves the
    /// two sides agree about which forms are one-way — never that the file in front of you
    /// carries one of them. A hand-built manifest would assert this test's own assumptions.
    /// </para>
    /// <para>
    /// It costs no CODESYS launch of its own: <c>ExportAsync</c> is cached across this class.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task An_object_never_writes_two_files_the_import_would_both_apply()
    {
        var exportRoot = await ExportAsync("first");

        var manifest = ProjectManifest.Parse(
            await File.ReadAllTextAsync(Path.Combine(exportRoot, "_manifest.json")));

        var doubled = manifest.Objects
            .Where(o => !ExportCoverage.IsExportOnly(o.Form))
            .GroupBy(o => o.Guid, StringComparer.OrdinalIgnoreCase)

            // An empty GUID is not an identity. record() falls back to "" when obj.guid
            // raises, so grouping on it would file every unreadable object together and
            // report a collision that is really two different objects.
            .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        foreach (var group in doubled)
        {
            output.WriteLine($"{group.Key}: "
                             + string.Join(", ", group.Select(o => $"{o.Form} {o.Path}")));
        }

        Assert.True(doubled.Count == 0,
            "these objects write more than one file the import applies, so the last phase to "
            + "run wins and the other file's edits are discarded in silence — give the "
            + "non-authoritative half its own form and add it to EXPORT_ONLY_FORMS and "
            + "ExportCoverage.IsExportOnly, as TASK_NATIVE and GVL_NATIVE both do: "
            + string.Join(" | ", doubled.Select(g => string.Join(", ", g.Select(o => o.Path)))));

        // NON-VACUOUS, and it has to be said because the assertion above passes on an empty
        // manifest, on a manifest of one object, and on a project with no two-file form in it
        // at all. The fixture has two of them — the GVL and the task configuration — so a
        // regression that made either half importable again lands here.
        var pairs = manifest.Objects
            .GroupBy(o => o.Guid, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1)
            .ToList();

        // AT LEAST the GVL and the task configuration; more since properties began writing
        // their payload and their readable text under one GUID. The count was never the
        // claim — the loop below is — and pinning it meant re-typing a literal every time a
        // two-file form was added, which is how a non-vacuity guard turns into a chore.
        Assert.True(pairs.Count >= 2,
            $"only {pairs.Count} two-file object(s); the assertion above passes vacuously "
            + "without them");

        foreach (var pair in pairs)
        {
            Assert.Single(pair, o => !ExportCoverage.IsExportOnly(o.Form));
        }
    }
    /// <summary>
    /// A GVL's native half is recorded as one-way, and its declaration is the half that
    /// imports.
    /// </summary>
    /// <remarks>
    /// G12's regression test, and the sibling of
    /// <see cref="The_task_configurations_native_half_is_recorded_and_named_as_one_way"/> —
    /// the same claim about the pair that had the defect rather than the pair that was fixed.
    /// Until 2026-08-16 the <c>.gvl.xml</c> was recorded plain <c>NATIVE</c>, so the import
    /// applied it after the <c>.gvl.st</c> and put the old declaration back.
    /// </remarks>
    [SkippableFact]
    public async Task The_GVLs_native_half_is_recorded_and_named_as_one_way()
    {
        var exportRoot = await ExportAsync("first");

        var manifest = ProjectManifest.Parse(
            await File.ReadAllTextAsync(Path.Combine(exportRoot, "_manifest.json")));

        var gvl = manifest.Objects
            .Where(o => o.Type == "GVL")
            .OrderBy(o => o.Path, StringComparer.Ordinal)
            .ToList();

        output.WriteLine(string.Join("\n", gvl.Select(o => $"{o.Form,-12} {o.Path}")));

        // PER GVL, however many the project has. This assumed exactly one — two records, one
        // GUID — and broke when the corpus gained PersistentVars. The claim was never about
        // the count: it is that each GVL writes a pair whose native half is one-way.
        var byGuid = gvl
            .GroupBy(o => o.Guid, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(byGuid);
        Assert.All(byGuid, g => Assert.Equal(2, g.Count()));

        foreach (var one in byGuid)
        {
            var half = Assert.Single(one, o => o.Path.EndsWith(".gvl.xml", StringComparison.Ordinal));
            Assert.Equal(ExportCoverage.GvlNativeForm, half.Form);
            Assert.True(ExportCoverage.IsExportOnly(half.Form));

            var readable = Assert.Single(one, o => o.Path.EndsWith(".gvl.st", StringComparison.Ordinal));
            Assert.False(ExportCoverage.IsExportOnly(readable.Form));
        }

        // ONE GVL's pair, for the coverage sentence below. The forms were asserted for every
        // GVL in the loop above; these two lines pick a subject rather than repeat the claim,
        // and they are scoped to that GUID because scanning all of them matched two .gvl.st
        // files the moment the corpus gained a second GVL.
        var pair = byGuid[0];
        var xml = Assert.Single(pair, o => o.Path.EndsWith(".gvl.xml", StringComparison.Ordinal));
        var st = Assert.Single(pair, o => o.Path.EndsWith(".gvl.st", StringComparison.Ordinal));

        // And the remedy names the file somebody is meant to edit, rather than sending them
        // to the IDE. The exporter finds it by GUID, so this sentence costs nothing per form.
        var coverage = await File.ReadAllTextAsync(Path.Combine(exportRoot, "_coverage.md"));
        output.WriteLine(coverage);

        Assert.Contains(xml.Path, coverage, StringComparison.Ordinal);
        Assert.Contains($"edit `{st.Path}` instead", coverage, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task The_exported_library_file_carries_what_the_project_asks_for()
    {
        // The two hand-written halves of this file format meet here and nowhere else:
        // versio_yaml.py writes it inside CODESYS's IronPython 2.7, LibraryCatalog reads it
        // in .NET. The unit tests parse a committed sample; this is what proves the sample
        // is still what a real export produces.
        var exportRoot = await ExportAsync("first");

        var path = Path.Combine(exportRoot, "Device", "Plc Logic", "Application",
                                "Library Manager.libraries.yaml");
        Assert.True(File.Exists(path), "the export wrote no library file for the application");

        var text = await File.ReadAllTextAsync(path);
        var catalog = LibraryCatalog.Parse(text);
        output.WriteLine($"{catalog.References.Count} reference(s), " +
                         $"{catalog.References.Count(r => r.SystemLibrary)} from plug-ins");

        Assert.True(catalog.IsValid);
        Assert.Equal("codesys", catalog.Vendor);
        Assert.Equal("Device/Plc Logic/Application/Library Manager", catalog.Path);
        Assert.NotEmpty(catalog.References);
        Assert.All(catalog.References, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));

        // Measured on this fixture: a redirection pins Standard to a fixed version, and Util
        // carries a parameter. Both are project state, and both are exactly the kind of
        // change that reached no diff at all before this existed.
        Assert.Equal("Standard, 3.5.22.0 (System)",
            catalog.References.Single(r => r.Name == "#Standard").Redirection);
        Assert.Equal("22800", Assert.Contains("IBLOCKSIZE",
            catalog.References.Single(r => r.Name == "#Util").Parameters));

        // The reference id is NOT recorded. CODESYS re-mints it on every open — measured, 34
        // of 34 changed across two runs with nothing else moving — so a file carrying it
        // would put a spurious diff in every commit, exactly as the __VisualizationStyle
        // guid did. Asserted here rather than left to the determinism test, which would only
        // catch it after somebody exported twice.
        Assert.DoesNotContain("id:", text, StringComparison.Ordinal);

        // Nor how it resolved on this machine: that describes which libraries are installed
        // here, not what the project asks for, and it would differ between two engineers
        // sharing one repository with neither of them wrong.
        Assert.DoesNotContain("effectiveResolution", text, StringComparison.Ordinal);
        Assert.DoesNotContain("resolutionInfo", text, StringComparison.Ordinal);

        // It goes into the repository beside the objects, so it has to read as a diff.
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task The_coverage_report_is_written_and_names_the_gap_in_prose()
    {
        var exportRoot = await ExportAsync("first");
        var path = Path.Combine(exportRoot, "_coverage.md");

        Assert.True(File.Exists(path), "the export wrote no coverage report");

        var text = await File.ReadAllTextAsync(path);
        output.WriteLine(text);

        Assert.Contains("# Export coverage", text, StringComparison.Ordinal);
        Assert.Contains("LIBRARY_MANAGER", text, StringComparison.Ordinal);

        // The gap section is GONE from this fixture, because the gap is closed. What the
        // library managers get instead is the one-way section: exported, reviewable, and not
        // applied back. Telling those two states apart is the entire reason this report
        // exists, so the report is asserted to have moved one to the other rather than
        // merely to have stopped mentioning it.
        Assert.DoesNotContain("Not exported, and that is a gap", text, StringComparison.Ordinal);
        Assert.Contains("Exported for review, not applied back", text, StringComparison.Ordinal);
        Assert.Contains("**Of those, real gaps** | **0**", text, StringComparison.Ordinal);

        // The deliberate skips are still named, with their reason.
        Assert.Contains("Not exported, deliberately", text, StringComparison.Ordinal);

        // It goes into the repository beside the objects, so it has to read as a diff: no
        // CRLF, and a trailing newline like every other file the export writes.
        Assert.DoesNotContain('\r', text);
    }

    /// <summary>
    /// An import and a re-export leave the mirror byte-identical — G10.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The churn this measures was measured first and fixed second.</b> P7 ran three
    /// import→export cycles with nothing edited between them and watched
    /// <c>UniqueIdGenerator</c> climb 44 → 47 → 50 → 53, +3 every cycle, never
    /// settling. Four of the eight native files differed forever, from two causes: three
    /// visualisations riding that counter, and <c>GlobalTextList.xml</c>, which churns with no
    /// counter in it at all.
    /// </para>
    /// <para>
    /// <b>That second file is why Phase 5 keys on equivalence and never on the counter.</b> A
    /// fix aimed at <c>UniqueIdGenerator</c> would have settled three files, left the fourth
    /// diffing forever, and looked like it worked.
    /// </para>
    /// <para>
    /// <b>Why the export has to land in the SAME directory both times.</b> The preservation
    /// only happens when there is a previous mirror to preserve from, so exporting into two
    /// fresh directories cannot see this at all — that arrangement is the determinism gate
    /// next door, and it deliberately still means what it always meant. This one exports, round
    /// trips through a real import, and exports again over the top.
    /// </para>
    /// <para>
    /// <b>The manifest is included in the comparison, not excluded from it.</b> It was the one
    /// file still differing between two steady-state snapshots, and it settles only if the
    /// recorded hash follows the bytes actually written — which is why the substitution
    /// happens before <c>record()</c> hashes anything rather than at write time. Excluding it
    /// here would hide exactly the mistake that trap describes.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task An_import_and_a_re_export_leave_the_mirror_byte_identical()
    {
        var install = new Versio.Plc.Codesys.Infrastructure.CodesysLocator()
            .FindInstalls().FirstOrDefault(i => i.Profiles.Count > 0);
        Skip.If(install is null, "no CODESYS installation with a profile");

        var fixture = TestPaths.Project("RefrigeratorControl.project");
        Skip.If(!File.Exists(fixture), "fixture project missing");

        var work = Path.Combine(_root, "g10");
        Directory.CreateDirectory(work);

        var project = Path.Combine(work, "RefrigeratorControl.project");
        File.Copy(fixture, project);

        var mirror = Path.Combine(work, "RefrigeratorControl");
        var runner = new Versio.Plc.Codesys.Infrastructure.LocalCodesysRunner(
            install!.ExecutablePath,
            new Versio.Plc.Codesys.Infrastructure.ScriptExtractor(),
            new TestLogger(output));

        async Task RunAsync(Versio.Plc.Codesys.CodesysOperation operation)
        {
            var job = new Versio.Plc.Codesys.CodesysJob
            {
                Operation = operation,
                Profile = install.Profiles[0],
                Projects =
                [
                    new Versio.Plc.Codesys.ProjectJob
                    {
                        BindingId = "g10", ProjectPath = project, Path = mirror,
                    },
                ],
                Timeout = TimeSpan.FromMinutes(5),
            };

            var result = await runner.RunAsync(job, null, CancellationToken.None);
            Assert.True(result.Succeeded, result.FailureReason ?? $"{operation} failed");
        }

        await RunAsync(Versio.Plc.Codesys.CodesysOperation.Export);
        var before = Fingerprint(mirror);

        // The TEXT of every source file, not just its hash — so a failure says what changed
        // rather than only that something did. Added 2026-08-18, when seven property getters
        // started differing across the round trip and the file list alone could not say
        // whether they had been reformatted or emptied. They had been emptied.
        var beforeText = Directory
            .EnumerateFiles(mirror, "*.st", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(mirror, f).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllText,
                StringComparer.Ordinal);

        // The round trip. Nothing is edited: this is P7's measurement, which found the mirror
        // moving anyway.
        await RunAsync(Versio.Plc.Codesys.CodesysOperation.Import);
        await RunAsync(Versio.Plc.Codesys.CodesysOperation.Export);

        var after = Fingerprint(mirror);

        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     after.Keys.OrderBy(k => k, StringComparer.Ordinal));

        var differing = before.Where(kv => after[kv.Key] != kv.Value)
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        foreach (var path in differing)
        {
            output.WriteLine($"differs: {path}");
            if (beforeText.TryGetValue(path, out var was))
            {
                var rel = path.Replace('/', Path.DirectorySeparatorChar);
                output.WriteLine("  BEFORE >>" + was + "<<");
                output.WriteLine("  AFTER  >>" + File.ReadAllText(Path.Combine(mirror, rel)) + "<<");
            }
        }

        Assert.True(differing.Count == 0,
            "these files changed across an import and a re-export with nothing edited, which "
            + "is G10 - every one of them is a diff in somebody's commit that means nothing: "
            + string.Join(", ", differing));

        // NON-VACUOUS. All of the above passes on a mirror of no files at all, and the four
        // that used to churn are native - so if the fixture ever stopped having native objects
        // this test would go quietly green while measuring nothing.
        Assert.True(before.Count >= 15, $"only {before.Count} files in the mirror");
        Assert.Contains(before.Keys, k => k.EndsWith(".vis.xml", StringComparison.Ordinal));
        Assert.Contains(before.Keys, k => k.EndsWith("GlobalTextList.xml", StringComparison.Ordinal));
        Assert.Contains(before.Keys, k => k.EndsWith("_manifest.json", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task The_whole_export_is_byte_identical_when_the_same_project_is_exported_twice()
    {
        // Determinism is load-bearing: a spurious byte anywhere in this tree is a diff in
        // every commit, forever, and the reviewer stops reading them.
        //
        // This compares the WHOLE tree rather than one file, because the version that
        // compared only _coverage.md passed while the manifest was churning on every single
        // export. CODESYS re-mints the project-level __VisualizationStyle's guid on every
        // open; recording skipped objects' guids therefore made _manifest.json differ every
        // time, and the narrower test could not see it because the report carries no guids.
        //
        // Phase 0's gate 0.2 measured this by hand across three runs. Nothing in the suite
        // enforced it until now, which is why a regression could be committed.
        Skip.IfNot(OperatingSystem.IsWindows(), "CODESYS is Windows-only");

        var project = await ProjectCopyAsync("det");
        var first = await ExportAgainAsync(project, "det-out-1");
        var second = await ExportAgainAsync(project, "det-out-2");

        var a = Fingerprint(first);
        var b = Fingerprint(second);

        foreach (var (path, hash) in a)
        {
            output.WriteLine($"{hash[..12]}  {path}");
        }

        Assert.Equal(a.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     b.Keys.OrderBy(k => k, StringComparer.Ordinal));

        var differing = a.Where(kv => b[kv.Key] != kv.Value).Select(kv => kv.Key).ToList();
        Assert.True(differing.Count == 0,
                    "these files differ between two exports of the same project: " +
                    string.Join(", ", differing));
    }

    /// <summary>SHA-256 of every file in an export, keyed by its path within the tree.</summary>
    private static Dictionary<string, string> Fingerprint(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(root, f).Replace('\\', '/'),
                f => Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>Put a copy of the fixture project somewhere it can be exported from.</summary>
    private async Task<string> ProjectCopyAsync(string label)
    {
        var fixture = TestPaths.Project("RefrigeratorControl.project");
        Skip.If(!File.Exists(fixture), "fixture project missing");

        var work = Path.Combine(_root, label);
        Directory.CreateDirectory(work);

        var project = Path.Combine(work, "RefrigeratorControl.project");
        File.Copy(fixture, project, overwrite: true);

        await Task.CompletedTask;
        return project;
    }

}
