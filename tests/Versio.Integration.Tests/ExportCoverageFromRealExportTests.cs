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
        var written = manifest.Objects.Select(o => o.Guid).Where(g => g.Length > 0).ToHashSet();
        Assert.DoesNotContain(manifest.Skipped, s => written.Contains(s.Guid));
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
    public async Task The_coverage_report_is_byte_identical_across_two_exports()
    {
        // _coverage.md is part of an export whose determinism is proven and load-bearing. A
        // clock or an unsorted list in here would put a diff in every commit - the same noise
        // the Timestamp scrub was written to remove, reintroduced by the report about it.
        var first = await ExportAsync("first");
        var second = await ExportAsync("second");

        var a = await File.ReadAllBytesAsync(Path.Combine(first, "_coverage.md"));
        var b = await File.ReadAllBytesAsync(Path.Combine(second, "_coverage.md"));

        output.WriteLine($"{a.Length} bytes vs {b.Length}");
        Assert.Equal(a, b);
    }
}
