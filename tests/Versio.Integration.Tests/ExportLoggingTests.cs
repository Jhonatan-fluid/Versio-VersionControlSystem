using Versio.Core.Logging;
using Versio.Core.Projects;
using Versio.Infrastructure.Platform;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// What the log says while a real export replaces a real export directory.
/// </summary>
/// <remarks>
/// <para>
/// Asserted against the log a real run produces, not against the code that writes it. The gap
/// this closes was invisible from the code: every line was present and correct, and the two
/// operations that can destroy work — swapping a new export over the old directory, and
/// clearing a staging folder a killed run left behind — simply had no line at all.
/// </para>
/// <para>
/// The export is run <b>twice</b>, because the interesting case is the second one: the first
/// writes into nothing, and only the second has an existing directory to delete.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Toolchain", "Codesys")]
[Collection(CodesysCollection.Name)]
public sealed class ExportLoggingTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Versio", "exportlog", Guid.NewGuid().ToString("N")[..8]);

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

    [SkippableFact]
    public async Task Replacing_the_export_directory_is_written_down()
    {
        var install = new Versio.Infrastructure.Plc.Codesys.CodesysLocator()
            .FindInstalls().FirstOrDefault(i => i.Profiles.Count > 0);
        Skip.If(install is null, "no CODESYS installation with a profile");

        var fixture = Core.Tests.TestPaths.Project("RefrigeratorControl.project");
        Skip.If(!File.Exists(fixture), "fixture project missing");

        Directory.CreateDirectory(_root);
        var project = Path.Combine(_root, "RefrigeratorControl.project");
        File.Copy(fixture, project);

        // The ring-buffer sink is what the log panel reads, so asserting against it is
        // asserting against what a person would actually see.
        var buffer = new InMemoryLogSink();
        var recorder = new VersioLogger(new SystemClock());
        recorder.AddSink(buffer);
        var exportRoot = Path.Combine(_root, "RefrigeratorControl");

        var runner = new Versio.Infrastructure.Plc.Codesys.LocalCodesysRunner(
            install!.ExecutablePath,
            new Versio.Infrastructure.Plc.Codesys.ScriptExtractor(),
            recorder);

        async Task ExportAsync() => Assert.True(
            (await runner.RunAsync(
                new Versio.Core.Plc.Codesys.CodesysJob
                {
                    Operation = Versio.Core.Plc.Codesys.CodesysOperation.Export,
                    Profile = install.Profiles[0],
                    Projects =
                    [
                        new Versio.Core.Plc.Codesys.ProjectJob
                        {
                            BindingId = "b-log",
                            ProjectPath = project,
                            Path = exportRoot,
                        },
                    ],
                }, null, CancellationToken.None)).Succeeded);

        await ExportAsync();
        await ExportAsync();

        var lines = buffer.Snapshot().Select(e => e.Message).ToList();
        foreach (var line in lines.Where(l => l.Contains("export directory", StringComparison.Ordinal)
                                              || l.Contains("not exported", StringComparison.Ordinal)
                                              || l.Contains("librar", StringComparison.Ordinal)
                                              || l.Contains("walked", StringComparison.Ordinal)))
        {
            output.WriteLine(line);
        }

        // The destructive step, with both counts: what was removed and what replaced it.
        // "Replaced a directory" without the numbers cannot be checked against the tree.
        //
        // Both exports are asserted, and the difference between them is the point: the first
        // removes nothing because there was nothing there, the second removes what the first
        // wrote. A count that did not track reality would show the same number twice.
        var swaps = lines
            .Where(l => l.StartsWith("replacing the export directory", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, swaps.Count);

        // Counted from the tree on disk rather than written in here as a literal. The first
        // version of this test hard-coded 12, and it broke the day the exporter learned to
        // write library references — a real behaviour change showing up as a failing string
        // comparison, which teaches nobody anything. What the number has to do is track
        // reality, so reality is what it is compared against.
        var written = Directory
            .EnumerateFiles(exportRoot, "*", SearchOption.AllDirectories)
            .Count();

        Assert.Contains($"0 existing file(s) removed, {written} written in their place",
                        swaps[0], StringComparison.Ordinal);
        Assert.Contains($"{written} existing file(s) removed, {written} written in their place",
                        swaps[1], StringComparison.Ordinal);

        Assert.Equal(2, lines.Count(l => l.Contains($"export directory now holds {written} file(s)",
                                                    StringComparison.Ordinal)));

        // The counts still have to DIFFER between the two runs, which is the property the
        // literals were there to protect: the first export removes nothing because there was
        // nothing there, the second removes what the first wrote. A count that ignored the
        // directory would print the same pair twice and pass a weaker test than this.
        Assert.NotEqual(swaps[0], swaps[1]);

        // The coverage story, in the log rather than only in a file somebody has to open.
        // Library references are exported now, so the line to look for is the one that says
        // they were recorded — the NOT_IMPLEMENTED line this used to assert was the gap, and
        // the fixture no longer has one.
        Assert.Contains(lines, l => l.Contains("library references recorded", StringComparison.Ordinal) &&
                                    l.Contains("Device/Plc Logic/Application", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("not exported (NOT_IMPLEMENTED)",
                                                     StringComparison.Ordinal));

        // One spelling of a path. A log line and the manifest disagreeing about separators
        // makes a reader wonder whether they are looking at the same object.
        Assert.DoesNotContain(lines, l => l.Contains("not exported", StringComparison.Ordinal) &&
                                          l.Contains('\\'));

        // The summary, checked against the manifest THIS export wrote rather than against
        // numbers typed in here. Both describe the same run, so they have to agree — and
        // that is a stronger statement than any literal, as well as one that survives the
        // exporter legitimately learning to write another file. (It has now done that twice:
        // _coverage.md, then the library references.)
        var manifest = ProjectManifest.Parse(
            File.ReadAllText(Path.Combine(exportRoot, "_manifest.json")));
        var gaps = manifest.Skipped.Count(s => s.Reason == ExportCoverage.GapReason);

        Assert.Contains(lines, l =>
            l.StartsWith("walked ", StringComparison.Ordinal) &&
            l.Contains($"wrote {manifest.Objects.Count}, ", StringComparison.Ordinal) &&
            l.Contains($"skipped {manifest.Skipped.Count} ", StringComparison.Ordinal) &&
            l.Contains($"{gaps} not implemented", StringComparison.Ordinal));

        // And the fixture's own shape, so a silent drop in what gets walked still fails here.
        // 18 objects is what this project contains; if that changes, it changed for a reason
        // somebody should have to state.
        Assert.Contains(lines, l => l.StartsWith("walked 18,", StringComparison.Ordinal));
    }
}
