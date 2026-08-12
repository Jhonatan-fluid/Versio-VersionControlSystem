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

        // Schema 2 is what says "this export counted". Without the bump, an old manifest
        // would read as an export that skipped nothing.
        Assert.Equal(ExportCoverage.ReportingSchema, manifest.Schema);
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
    public async Task The_library_manager_is_reported_as_a_gap_on_a_project_that_has_one()
    {
        // The finding this feature exists for, proven on a real project rather than asserted
        // from the type table: the fixture has a library manager, nothing exports it, and
        // nothing writes the config/libraries.yaml the exporter's comment used to promise.
        var exportRoot = await ExportAsync("first");

        var manifest = ProjectManifest.Parse(
            await File.ReadAllTextAsync(Path.Combine(exportRoot, "_manifest.json")));

        var report = ExportCoverage.Of(manifest);
        output.WriteLine(report.Describe());

        Assert.Equal(CoverageVerdict.HasGaps, report.Verdict);
        Assert.Contains(report.Gaps, g => g.Type == "LIBRARY_MANAGER");
        Assert.Contains("LIBRARY_MANAGER", report.Describe(), StringComparison.Ordinal);

        // And the deliberate skips are not counted as gaps: the fixture's visualisation
        // manager is skipped for a reason that loses nothing.
        Assert.Contains(manifest.Skipped, s => s.Reason == "SERVICE_OBJECT");
        Assert.DoesNotContain(report.Gaps, g => g.Reason == "SERVICE_OBJECT");
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
        Assert.Contains("Not exported, and that is a gap", text, StringComparison.Ordinal);

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
