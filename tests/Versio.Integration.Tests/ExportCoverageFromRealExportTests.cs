using Versio.Core.Projects;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

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
        var install = new Versio.Infrastructure.Plc.Codesys.CodesysLocator()
            .FindInstalls().FirstOrDefault(i => i.Profiles.Count > 0);
        Skip.If(install is null, "no CODESYS installation with a profile");

        var exportRoot = Path.Combine(_root, outLabel);

        var runner = new Versio.Infrastructure.Plc.Codesys.LocalCodesysRunner(
            install!.ExecutablePath,
            new Versio.Infrastructure.Plc.Codesys.ScriptExtractor(),
            new TestLogger(output));

        var result = await runner.RunAsync(
            new Versio.Core.Plc.Codesys.CodesysJob
            {
                Operation = Versio.Core.Plc.Codesys.CodesysOperation.Export,
                Profile = install.Profiles[0],
                Projects =
                [
                    new Versio.Core.Plc.Codesys.ProjectJob
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
        var install = new Versio.Infrastructure.Plc.Codesys.CodesysLocator()
            .FindInstalls().FirstOrDefault(i => i.Profiles.Count > 0);
        Skip.If(install is null, "no CODESYS installation with a profile");

        var fixture = Core.Tests.TestPaths.Project("RefrigeratorControl.project");
        Skip.If(!File.Exists(fixture), "fixture project missing");

        var work = Path.Combine(_root, label);
        Directory.CreateDirectory(work);

        var project = Path.Combine(work, "RefrigeratorControl.project");
        File.Copy(fixture, project);

        var runner = new Versio.Infrastructure.Plc.Codesys.LocalCodesysRunner(
            install!.ExecutablePath,
            new Versio.Infrastructure.Plc.Codesys.ScriptExtractor(),
            new TestLogger(output));

        var exportRoot = Path.Combine(work, "RefrigeratorControl");

        var result = await runner.RunAsync(
            new Versio.Core.Plc.Codesys.CodesysJob
            {
                Operation = Versio.Core.Plc.Codesys.CodesysOperation.Export,
                Profile = install.Profiles[0],
                Projects =
                [
                    new Versio.Core.Plc.Codesys.ProjectJob
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

        Assert.Equal(2, written.Count);
        Assert.All(written, o => Assert.Equal(ExportCoverage.LibrariesForm, o.Form));
        Assert.Contains(written, o => o.Path == "Library Manager.libraries.yaml");
        Assert.Contains(written,
            o => o.Path == "Device/Plc Logic/Application/Library Manager.libraries.yaml");

        // Exported is not the same as editable, and the report says so rather than leaving
        // it to be found out by editing one.
        //
        // THREE, not two: the two library files plus the task configuration's native .xml,
        // which stopped being applied back when Phase 4 made its .tasks.yaml the importable
        // half. This number was 2 for as long as that was already true, which is the whole
        // finding — the count is the cheapest thing that could have noticed and it was
        // written before there was a second kind of one-way file to notice.
        Assert.Equal(3, report.ExportOnlyFiles);
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

        // One object, two files, one GUID — the GVL_BOTH shape, with the halves reversed.
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
        var fixture = Core.Tests.TestPaths.Project("RefrigeratorControl.project");
        Skip.If(!File.Exists(fixture), "fixture project missing");

        var work = Path.Combine(_root, label);
        Directory.CreateDirectory(work);

        var project = Path.Combine(work, "RefrigeratorControl.project");
        File.Copy(fixture, project, overwrite: true);

        await Task.CompletedTask;
        return project;
    }

}
