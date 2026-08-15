using Versio.Core.Logging;
using Versio.Core.Plc.Codesys;
using Versio.Infrastructure.Logging;
using Versio.Infrastructure.Platform;
using Versio.Infrastructure.Plc.Codesys;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// What an import writes to the log, asserted against a real one.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="ExportLoggingTests"/>, and it exists because the export had one
/// and the import did not. That asymmetry was the whole defect: <c>LocalCodesysRunner</c>
/// handled <c>case "summary"</c> with the <b>exporter's</b> field names — <c>walked</c>,
/// <c>written</c>, <c>skipped</c>, <c>gaps</c> — and an import reports none of them. Since
/// <c>ScriptEvent.Int</c> returns null for a key that is not there, every import in the
/// application's history logged <c>walked , wrote , skipped  (of which  not implemented), 0
/// warning(s)</c>: four blanks and one number, and the number was true only because
/// <c>warnings</c> happens to be common to both scripts.
/// </para>
/// <para>
/// It was found by reading the code and confirmed by watching it happen, but nothing in the
/// suite could have failed on it, because <b>no test had ever put an import's log under an
/// assertion</b>. Hence this file rather than one more assertion somewhere else.
/// </para>
/// <para>
/// Driven straight through the runner — no orchestrator, no git, no adoption — for the same
/// reason <see cref="ExportLoggingTests"/> is: the subject is what the runner logs, and the
/// gates above it would only add ways for the test to not reach it. Two CODESYS launches.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(CodesysCollection.Name)]
public sealed class ImportLoggingTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Versio", "importlog", Guid.NewGuid().ToString("N")[..8]);

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
    public async Task An_import_is_logged_with_its_own_numbers_and_names_what_it_wrote()
    {
        var install = new CodesysLocator().FindInstalls().FirstOrDefault(i => i.Profiles.Count > 0);
        Skip.If(install is null, "no CODESYS installation with a profile");

        var fixture = Core.Tests.TestPaths.Project("RefrigeratorControl.project");
        Skip.If(!File.Exists(fixture), "fixture project missing");

        Directory.CreateDirectory(_root);
        var project = Path.Combine(_root, "RefrigeratorControl.project");
        File.Copy(fixture, project);

        var buffer = new InMemoryLogSink();
        var recorder = new VersioLogger(new SystemClock());
        recorder.AddSink(buffer);

        var mirror = Path.Combine(_root, "RefrigeratorControl");
        var runner = new LocalCodesysRunner(
            install!.ExecutablePath, new ScriptExtractor(), recorder);

        async Task RunAsync(CodesysOperation operation) => Assert.True(
            (await runner.RunAsync(new CodesysJob
            {
                Operation = operation,
                Profile = install.Profiles[0],
                Projects =
                [
                    new ProjectJob
                    {
                        BindingId = "log-1", ProjectPath = project, Path = mirror,
                    },
                ],
                Timeout = TimeSpan.FromMinutes(5),
            }, null, CancellationToken.None)).Succeeded);

        await RunAsync(CodesysOperation.Export);

        // One real edit, so the import has something true to report. A no-op import would log
        // an honest summary of nothing and prove only that the blanks are gone.
        var tasksFile = Path.Combine(
            mirror, "Device", "Plc Logic", "Application", "Task Configuration.tasks.yaml");
        Assert.True(File.Exists(tasksFile), "the export wrote no task file to edit");

        var text = (await File.ReadAllTextAsync(tasksFile)).Replace("\r\n", "\n");
        Assert.Contains("    priority: \"1\"\n", text, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            tasksFile, text.Replace("    priority: \"1\"\n", "    priority: \"5\"\n"),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // Where the export's log ends and the import's begins. Both runs share this buffer,
        // and the export's own summary legitimately starts with "walked" - so an assertion
        // that the exporter's vocabulary is absent has to be scoped to the import, or it
        // fails on a line that is perfectly correct.
        var exportLines = buffer.Snapshot().Count;

        await RunAsync(CodesysOperation.Import);

        // The summary line, told apart from the seven "imported <file> as native XML" lines
        // that also begin with those six letters. Matching on the prefix alone found eight.
        static bool IsSummary(string line) =>
            line.StartsWith("import: ", StringComparison.Ordinal)
            || line.StartsWith("import applied nothing", StringComparison.Ordinal);

        var lines = buffer.Snapshot().Skip(exportLines).Select(e => e.Message).ToList();
        foreach (var line in lines.Where(l => IsSummary(l)
                                              || l.StartsWith("set ", StringComparison.Ordinal)
                                              || l.StartsWith("walked", StringComparison.Ordinal)))
        {
            output.WriteLine(line);
        }

        // THE SUMMARY, in the import's own words. Asserted as a whole line rather than by
        // fragments: the defect was a line that was individually plausible everywhere and
        // wrong taken together, so a test matching one field at a time could have passed
        // against it.
        var summary = Assert.Single(lines, IsSummary);
        output.WriteLine($"summary: {summary}");

        // The exporter's vocabulary must not appear on an import line. This is the assertion
        // that fails if the switch loses its branch, and it is stated as an absence because
        // that is exactly how the bug presented — nothing wrong was written, the right things
        // were simply missing.
        Assert.DoesNotContain(lines, l => l.StartsWith("walked", StringComparison.Ordinal));

        // The one field the old line got right, since it is common to both scripts. Keeping it
        // here means a regression that reverted to the exporter's format would still have to
        // fail on the assertions above rather than on this one.
        Assert.Contains("0 warning(s)", summary, StringComparison.Ordinal);

        // Real counts, not blanks. The import applied one file and left the rest alone, so
        // both a changed number and an unchanged number have to appear — a formatter that
        // dropped every zero-valued field would still pass "1 updated" on its own.
        Assert.Contains("1 updated", summary, StringComparison.Ordinal);
        Assert.Contains("unchanged", summary, StringComparison.Ordinal);
        Assert.Contains("native", summary, StringComparison.Ordinal);

        // Nothing failed, so those words must be absent rather than present as zeroes.
        Assert.DoesNotContain("FAILED", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("partly applied", summary, StringComparison.Ordinal);

        // AND THE WRITE ITSELF, named. `task_set` had no case at all and fell through to the
        // default, so Versio changing a task's priority logged the literal string "· task_set"
        // at Debug. This is the one event in an import that changes what the machine runs.
        var wrote = Assert.Single(lines, l => l.StartsWith("set MainTask.priority",
                                                          StringComparison.Ordinal));
        output.WriteLine($"write: {wrote}");
        Assert.Contains("= 5", wrote, StringComparison.Ordinal);

        // The export-only file is still announced by name, which is the case that was fixed
        // earlier and must not regress while this switch is being edited.
        Assert.Contains(lines, l => l.Contains("not applied to the project", StringComparison.Ordinal)
                                    && l.Contains("Task Configuration.xml", StringComparison.Ordinal)
                                    && l.Contains("edit Task Configuration.tasks.yaml instead",
                                                  StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task An_import_that_loses_a_file_says_so_in_its_summary()
    {
        // THE OTHER HALF, and the one that matters more. The test above proves a clean import
        // reports honestly; this proves a BROKEN one does, which is the case the old blank
        // line hid completely — `failed` was one of the four fields that came out empty.
        //
        // **Nothing in the suite had ever run an import that fails.** Every existing import
        // test asserts a success or a refusal, so "the summary is wrong when something goes
        // wrong" had no way to be caught. That is the same shape as G11 itself.
        var install = new CodesysLocator().FindInstalls().FirstOrDefault(i => i.Profiles.Count > 0);
        Skip.If(install is null, "no CODESYS installation with a profile");

        var fixture = Core.Tests.TestPaths.Project("RefrigeratorControl.project");
        Skip.If(!File.Exists(fixture), "fixture project missing");

        Directory.CreateDirectory(_root);
        var project = Path.Combine(_root, "RefrigeratorControl.project");
        File.Copy(fixture, project);

        var buffer = new InMemoryLogSink();
        var recorder = new VersioLogger(new SystemClock());
        recorder.AddSink(buffer);

        var mirror = Path.Combine(_root, "RefrigeratorControl");
        var runner = new LocalCodesysRunner(
            install!.ExecutablePath, new ScriptExtractor(), recorder);

        async Task RunAsync(CodesysOperation operation) => await runner.RunAsync(new CodesysJob
        {
            Operation = operation,
            Profile = install.Profiles[0],
            Projects =
            [
                new ProjectJob { BindingId = "log-2", ProjectPath = project, Path = mirror },
            ],
            Timeout = TimeSpan.FromMinutes(5),
        }, null, CancellationToken.None);

        await RunAsync(CodesysOperation.Export);

        // Break one native file in a way that keeps it well-formed XML, so it reaches
        // import_native as a document and fails as an object rather than being rejected as
        // unparseable. A truncated file would test the wrong layer.
        var broken = Path.Combine(mirror, "Device", "Plc Logic", "Application", "Signals.xml");
        var xml = await File.ReadAllTextAsync(broken);
        Assert.Contains("Type=\"System.Guid\"", xml, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            broken, xml.Replace("Type=\"System.Guid\"", "Type=\"System.Nonsense\""),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var exportLines = buffer.Snapshot().Count;
        await RunAsync(CodesysOperation.Import);

        static bool IsSummary(string line) =>
            line.StartsWith("import: ", StringComparison.Ordinal)
            || line.StartsWith("import applied nothing", StringComparison.Ordinal);

        var lines = buffer.Snapshot().Skip(exportLines).Select(e => e.Message).ToList();
        var summary = Assert.Single(lines, IsSummary);
        output.WriteLine($"summary: {summary}");

        // The failure is on the line, in capitals, and the warning count is no longer the only
        // true number on it.
        Assert.Contains("1 FAILED", summary, StringComparison.Ordinal);
        Assert.Contains("1 warning(s)", summary, StringComparison.Ordinal);

        // And the run is still mostly a success, which is the distinction the summary exists
        // to draw: six native files did import. A line reading only "FAILED" would be as
        // useless as one reading only "0 failed".
        Assert.Contains("native", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.StartsWith("walked", StringComparison.Ordinal));

        // NOT COVERED HERE, and stated rather than left to look covered: `native_partial`,
        // the branch where CODESYS reports a per-object exception through the handler's
        // progress callback and carries on. This file takes the other route — import_native
        // RAISES, so it lands in the pre-existing NATIVE_IMPORT_FAILED path, which is why the
        // summary reads "1 FAILED" and not "1 partly applied".
        //
        // The only trigger anybody has observed for the callback route is G11 itself, and
        // Phase 4 removed it. So the partial branch is written from the G11 measurement and
        // has not executed since. Manufacturing one was tried with malformed XML and produced
        // the raise instead. Left honest rather than pretended: if a way to force it turns up,
        // this is the test to extend.
        Assert.DoesNotContain("partly applied", summary, StringComparison.Ordinal);
    }
}
