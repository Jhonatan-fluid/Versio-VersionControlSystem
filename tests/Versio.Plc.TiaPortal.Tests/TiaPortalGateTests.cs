using System.Text;
using System.Text.RegularExpressions;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.FileSystem;
using Versio.Plc.TiaPortal;
using Versio.Plc.TiaPortal.Infrastructure;
using Versio.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Plc.TiaPortal.Tests;

/// <summary>
/// Real TIA Portal, when the machine has it. Skip is the success path on every other machine.
/// </summary>
[Collection(TiaPortalCollection.Name)]
public sealed class TiaPortalGateTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void The_locator_finds_the_PublicAPI_folder()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TIA Portal is Windows-only");
        var installs = new TiaPortalLocator().FindInstalls();
        Skip.If(installs.Count == 0, "no TIA Portal install with an Openness API folder");

        // One install per release, each with the Openness entry assembly of its own layout:
        // Siemens.Engineering.Base.dll from V21 on, Siemens.Engineering.dll before. This
        // asserted a single install with the modular file until 2026-10-29, when a machine
        // with V20 beside V21 showed the locator had never seen the older layout.
        Assert.Equal(installs.Count, installs.Select(i => i.Version).Distinct().Count());
        foreach (var install in installs)
        {
            Assert.True(Directory.Exists(install.ApiFolder), install.ApiFolder);
            var entry = install.ApiShape == TiaPortalInstall.ModularApi
                ? "Siemens.Engineering.Base.dll"
                : "Siemens.Engineering.dll";
            Assert.True(File.Exists(Path.Combine(install.ApiFolder, entry)),
                $"{install.Version}: {entry} under {install.ApiFolder}");
        }
    }

    [SkippableFact]
    public async Task Probe_opens_the_committed_fixture_or_explains_why_not()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TIA Portal is Windows-only");
        var installs = new TiaPortalLocator().FindInstalls();
        Skip.If(installs.Count == 0, "no TIA Portal install");

        var fixture = TestPaths.TiaPortalProject();
        Skip.If(fixture is null, "committed TIA fixture is missing or is an LFS pointer — git lfs pull");

        var host = LocalTiaPortalRunner.FindHost();
        Skip.If(host is null, "TIA Portal host was not copied to the output directory");

        var toolchain = new TiaPortalToolchain(NullLogger.Instance);
        var status = await toolchain.ProbeAsync(CancellationToken.None);
        Skip.If(!status.Available, status.Explanation);

        var runner = toolchain.CreateRunner(status);
        var outDir = Path.Combine(Path.GetTempPath(), "versio-tia-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);

        try
        {
            var result = await runner.RunAsync(
                new PlcJob
                {
                    Operation = PlcOperation.Probe,
                    Profile = status.Profile ?? "",
                    Projects =
                    [
                        new PlcProjectJob
                        {
                            BindingId = "tia-probe",
                            ProjectPath = fixture!,
                            Path = outDir,
                        },
                    ],
                    Timeout = TimeSpan.FromMinutes(5),
                },
                progress: null,
                CancellationToken.None);

            SkipIfMeasuredBlocker(result);
            Assert.True(result.Succeeded, result.FailureReason);
            Assert.True(File.Exists(Path.Combine(outDir, "report.json")));
        }
        finally
        {
            try
            {
                Directory.Delete(outDir, recursive: true);
            }
            catch (IOException)
            {
                // Temp.
            }
        }
    }

    /// <summary>
    /// Probe a project on this machine, named by <c>VERSIO_TIA_PROBE_PROJECT</c>, with
    /// whichever installed Portal its extension names. Skips unless the variable is set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The committed fixture is V21, so the two Facts above can only ever measure the modular
    /// Openness layout. This is how the single-assembly layout of V20 was measured on
    /// 2026-10-29, against the owner's own V20 project, without copying a private project into
    /// the repository: the probe reads and writes nothing into the project's folder beyond
    /// what Portal itself does on open. Set the variable to the absolute <c>.apNN</c> path.
    /// </para>
    /// <para>
    /// <c>VERSIO_TIA_PROBE_HOST</c> names the host executable to drive, and should be the one
    /// Portal's Openness access list already names (the installed Versio's
    /// <c>%LOCALAPPDATA%\Versio\current\tia\Versio.Plc.TiaPortal.Host.exe</c>, DRIVING §2.3).
    /// The list is keyed by path and hash, so the host this test project builds beside itself
    /// is a stranger to it and Portal puts up the access dialog — which on 2026-10-29 nobody
    /// was at the screen to answer, and the first attempt ran into the twenty-minute timeout
    /// having measured nothing. The host binary knows nothing about install layouts, so the
    /// installed one is as good a subject as the fresh one.
    /// </remarks>
    [SkippableFact]
    public async Task Probe_opens_the_project_named_by_the_environment_with_its_own_release()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TIA Portal is Windows-only");
        var project = Environment.GetEnvironmentVariable("VERSIO_TIA_PROBE_PROJECT");
        Skip.If(string.IsNullOrWhiteSpace(project), "VERSIO_TIA_PROBE_PROJECT is not set");
        Skip.If(!File.Exists(project), $"VERSIO_TIA_PROBE_PROJECT does not exist: {project}");

        var log = new TestLogger(output);
        var toolchain = new TiaPortalToolchain(log);
        var status = await toolchain.ProbeAsync(CancellationToken.None);
        Skip.If(!status.Available, status.Explanation);

        var release = TiaPortalProjectFormat.ReleaseOf(project!);
        var installs = new TiaPortalLocator(logger: log).FindInstalls();
        Skip.If(installs.All(i => i.Version != release),
            $"{Path.GetFileName(project)} needs Portal {release}; installed: {string.Join(", ", installs.Select(i => i.Version))}");

        var hostOverride = Environment.GetEnvironmentVariable("VERSIO_TIA_PROBE_HOST");
        Skip.If(!string.IsNullOrWhiteSpace(hostOverride) && !File.Exists(hostOverride),
            $"VERSIO_TIA_PROBE_HOST does not exist: {hostOverride}");
        var runner = string.IsNullOrWhiteSpace(hostOverride)
            ? toolchain.CreateRunner(status)
            : new LocalTiaPortalRunner(hostOverride, status.Profile ?? "", log);
        var outDir = Path.Combine(Path.GetTempPath(), "versio-tia-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);

        try
        {
            var result = await runner.RunAsync(
                new PlcJob
                {
                    Operation = PlcOperation.Probe,
                    Profile = status.Profile ?? "",
                    Projects =
                    [
                        new PlcProjectJob
                        {
                            BindingId = "tia-probe-env",
                            ProjectPath = project!,
                            Path = outDir,
                        },
                    ],
                    Timeout = TimeSpan.FromMinutes(10),
                },
                progress: null,
                CancellationToken.None);

            SkipIfMeasuredBlocker(result);
            Assert.True(result.Succeeded, result.FailureReason);
            Assert.True(File.Exists(Path.Combine(outDir, "report.json")));
        }
        finally
        {
            try
            {
                Directory.Delete(outDir, recursive: true);
            }
            catch (IOException)
            {
                // Temp.
            }
        }
    }

    [SkippableFact]
    public async Task Export_writes_a_manifest_and_names_the_coverage_skips()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TIA Portal is Windows-only");
        var installs = new TiaPortalLocator().FindInstalls();
        Skip.If(installs.Count == 0, "no TIA Portal install");

        var fixture = TestPaths.TiaPortalProject();
        Skip.If(fixture is null, "committed TIA fixture is missing or is an LFS pointer — git lfs pull");

        var toolchain = new TiaPortalToolchain(NullLogger.Instance);
        var status = await toolchain.ProbeAsync(CancellationToken.None);
        Skip.If(!status.Available, status.Explanation);
        Skip.If(LocalTiaPortalRunner.FindHost() is null, "TIA Portal host was not copied to the output directory");

        var runner = toolchain.CreateRunner(status);
        var outDir = Path.Combine(Path.GetTempPath(), "versio-tia-export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);

        try
        {
            var result = await runner.RunAsync(
                new PlcJob
                {
                    Operation = PlcOperation.Export,
                    Profile = status.Profile ?? "",
                    Projects =
                    [
                        new PlcProjectJob
                        {
                            BindingId = "tia-export",
                            ProjectPath = fixture!,
                            Path = outDir,
                        },
                    ],
                    Timeout = TimeSpan.FromMinutes(20),
                },
                progress: null,
                CancellationToken.None);

            SkipIfMeasuredBlocker(result);
            Assert.True(result.Succeeded, result.FailureReason);
            Assert.True(File.Exists(Path.Combine(outDir, "_manifest.json")));
            var coverage = File.ReadAllText(Path.Combine(outDir, "_coverage.md"));
            Assert.DoesNotContain("devices and networks are a coverage skip", coverage);
            Assert.Contains("devices.yaml", coverage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("HMI", coverage, StringComparison.OrdinalIgnoreCase);
            var yaml = Directory.EnumerateFiles(outDir, "*.devices.yaml", SearchOption.AllDirectories)
                .FirstOrDefault();
            Assert.True(yaml is not null, "export wrote no *.devices.yaml");
            var manifest = File.ReadAllText(Path.Combine(outDir, "_manifest.json"));
            Assert.Contains("DEVICE_COMMENT", manifest, StringComparison.Ordinal);
            Assert.Contains("SCL_REVIEW", manifest, StringComparison.Ordinal);
            Assert.DoesNotContain("\"path\": \"Hardware\"", manifest, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(outDir, recursive: true);
            }
            catch (IOException)
            {
                // Temp.
            }
        }
    }

    [SkippableFact(Timeout = 1_200_000)]
    public async Task Two_unchanged_textual_exports_match_byte_for_byte()
    {
        // Native XML is expected to churn (G12). Textual bytes of an unchanged
        // project must not. Do not fold a generate failure into this: the import
        // Fact already saw the textual set move.
        Skip.IfNot(OperatingSystem.IsWindows(), "TIA Portal is Windows-only");
        var installs = new TiaPortalLocator().FindInstalls();
        Skip.If(installs.Count == 0, "no TIA Portal install");

        var fixture = TestPaths.TiaPortalProject();
        Skip.If(fixture is null, "committed TIA fixture is missing or is an LFS pointer — git lfs pull");

        var toolchain = new TiaPortalToolchain(NullLogger.Instance);
        var status = await toolchain.ProbeAsync(CancellationToken.None);
        Skip.If(!status.Available, status.Explanation);
        Skip.If(LocalTiaPortalRunner.FindHost() is null, "TIA Portal host was not copied to the output directory");

        var stamp = Guid.NewGuid().ToString("N")[..8];
        var exportDir = Path.Combine(Path.GetTempPath(), "versio-tia-det-" + stamp);
        Directory.CreateDirectory(exportDir);

        try
        {
            var runner = toolchain.CreateRunner(status);
            var first = await runner.RunAsync(
                Job(PlcOperation.Export, status.Profile ?? "", fixture!, exportDir),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(first);
            Assert.True(first.Succeeded, first.FailureReason);

            var a = TextualBytes(exportDir);

            // Second export into the same tree — that is what the app does. A fresh
            // directory would hide Openness refusing an existing XML file.
            var second = await runner.RunAsync(
                Job(PlcOperation.Export, status.Profile ?? "", fixture!, exportDir),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(second);
            Assert.True(second.Succeeded, second.FailureReason);

            var b = TextualBytes(exportDir);
            Assert.True(a.Count > 0, "first export wrote no textual files");
            Assert.Equal(a.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
                b.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

            foreach (var key in a.Keys)
            {
                Assert.True(b.TryGetValue(key, out var right), key);
                Assert.True(
                    TextFromUtf8(a[key]) == TextFromUtf8(right!),
                    "textual export differed: " + key);
            }
        }
        finally
        {
            TryDeleteTree(exportDir);
        }
    }

    /// <summary>
    /// Export, mutate one <c>.scl</c> and one <c>.udt</c>, import the planner's plan,
    /// re-export and find both mutations — then delete the added statement, import
    /// again, and prove the deletion round-trips too. Native XML is expected to churn
    /// and is not compared.
    /// </summary>
    /// <remarks>
    /// Runs against a copy of the committed fixture, never the OneDrive original and
    /// never the LFS tree in place. Close TIA Portal and Versio first — exclusive
    /// <c>Projects.Open</c> is a human precondition; a held project skips.
    /// </remarks>
    [SkippableFact(Timeout = 1_200_000)]
    public async Task Import_then_reexport_keeps_textual_bytes()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TIA Portal is Windows-only");
        var installs = new TiaPortalLocator().FindInstalls();
        Skip.If(installs.Count == 0, "no TIA Portal install");

        var fixture = TestPaths.TiaPortalProject();
        Skip.If(fixture is null, "committed TIA fixture is missing or is an LFS pointer — git lfs pull");

        var toolchain = new TiaPortalToolchain(NullLogger.Instance);
        var status = await toolchain.ProbeAsync(CancellationToken.None);
        Skip.If(!status.Available, status.Explanation);
        Skip.If(LocalTiaPortalRunner.FindHost() is null, "TIA Portal host was not copied to the output directory");

        var stamp = Guid.NewGuid().ToString("N")[..8];
        var copyDir = Path.Combine(Path.GetTempPath(), "versio-tia-copy-" + stamp);
        var export1 = Path.Combine(Path.GetTempPath(), "versio-tia-imp1-" + stamp);
        var export2 = Path.Combine(Path.GetTempPath(), "versio-tia-imp2-" + stamp);
        var export3 = Path.Combine(Path.GetTempPath(), "versio-tia-imp3-" + stamp);
        Directory.CreateDirectory(copyDir);
        Directory.CreateDirectory(export1);
        Directory.CreateDirectory(export2);
        Directory.CreateDirectory(export3);

        try
        {
            await new WindowsFileSystem().CopyTreeAsync(
                Path.GetDirectoryName(fixture)!, copyDir, CancellationToken.None);
            var copyProject = Path.Combine(copyDir, Path.GetFileName(fixture)!);

            var runner = toolchain.CreateRunner(status);
            var exported = await runner.RunAsync(
                Job(PlcOperation.Export, status.Profile ?? "", copyProject, export1),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(exported);
            Assert.True(exported.Succeeded, exported.FailureReason);

            var scl = Directory.EnumerateFiles(export1, "*.scl", SearchOption.AllDirectories)
                .FirstOrDefault(p => Path.GetFileName(p).Equals("Level.scl", StringComparison.OrdinalIgnoreCase))
                ?? Directory.EnumerateFiles(export1, "*.scl", SearchOption.AllDirectories)
                    .Select(p => new FileInfo(p))
                    .OrderBy(f => f.Length)
                    .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()?.FullName;
            Skip.If(scl is null, "export wrote no .scl — cannot mutate textual");

            var marker = "versio_mutate_" + stamp;
            MutateInPlace(scl!, text => InsertTextualMutation(text, marker));

            // A UDT beside the block: the type path generates through the same door
            // but verifies via a .udt source, which a block-shaped verify cannot
            // stand in for — a hardcoded .scl there refused every type import.
            var udt = Directory.EnumerateFiles(export1, "*.udt", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .OrderBy(f => f.Length)
                .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()?.FullName;
            Skip.If(udt is null, "export wrote no .udt — cannot mutate a type");
            var udtMarker = "versio_udt_" + stamp;
            MutateInPlace(udt!, text => InsertUdtMember(text, udtMarker));

            var yaml = Directory.EnumerateFiles(export1, "*.devices.yaml", SearchOption.AllDirectories)
                .FirstOrDefault();
            Skip.If(yaml is null, "export wrote no *.devices.yaml — cannot mutate Comment");
            // Longer than 240 characters on purpose: the host's attribute reader clipped
            // every string to 240 for the probe's listing, so a long comment was exported
            // cut, stored cut and verified against its own cut (the 2026-10-24 review's
            // item 27). A marker this long survives only when the whole comment does.
            var hwMarker = "versio_hw_" + stamp + "_" + new string('x', 300);
            MutateInPlace(yaml, text => SetStationComment(text, "CleaningStation", hwMarker));

            var fs = new WindowsFileSystem();
            var plan = await ImportPlanReader.ReadAsync(
                fs, TiaPortalProjectFormat.Instance, export1, NullLogger.Instance,
                TiaPortalLog.Category, "tia-import", CancellationToken.None);

            // The planner's answer for the mutated files — ApplyText, not hand-built
            // actions. The rest of a full export plan would re-apply every native XML;
            // this Fact is the textual mutation, and native XML is expected to churn.
            var textual = plan with
            {
                Files =
                [
                    PlannedApplyText(plan, export1, scl!),
                    PlannedApplyText(plan, export1, udt!),
                    PlannedApplyText(plan, export1, yaml),
                ],
                Deletions = [],
                Renames = [],
            };

            var imported = await runner.RunAsync(
                Job(PlcOperation.Import, status.Profile ?? "", copyProject, export1, textual),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(imported);
            Assert.True(imported.Succeeded, imported.FailureReason +
                (imported.Warnings.Count == 0 ? "" : " warnings: " + string.Join("; ", imported.Warnings)));

            var reexported = await runner.RunAsync(
                Job(PlcOperation.Export, status.Profile ?? "", copyProject, export2),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(reexported);
            Assert.True(reexported.Succeeded, reexported.FailureReason);

            // Layered verdict: the mutation must be present in some textual re-export.
            // Native XML is expected to churn and is never compared. A full-tree
            // textual identity of an unchanged export is still PHASE2 item 7.3.
            var after = TextualBytes(export2);
            var name = Path.GetFileName(scl)!;
            var counterpart = after.FirstOrDefault(kv =>
                kv.Key.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)
                || kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            var snippet = counterpart.Key is null
                ? "(re-export has no " + name + "; files: "
                  + string.Join(", ", after.Keys.Select(Path.GetFileName)) + ")"
                : TextFromUtf8(counterpart.Value);
            Assert.True(
                after.Any(kv => TextFromUtf8(kv.Value).Contains(marker, StringComparison.Ordinal)),
                "mutation " + marker + " missing after re-export. import wrote: "
                + string.Join(", ", imported.ChangedPaths)
                + (imported.Warnings.Count == 0 ? "" : "; warnings: " + string.Join("; ", imported.Warnings))
                + "; re-export of " + name + ":" + Environment.NewLine + snippet);
            Assert.True(
                after.Any(kv => TextFromUtf8(kv.Value).Contains(udtMarker, StringComparison.Ordinal)),
                "UDT member " + udtMarker + " missing after re-export. import wrote: "
                + string.Join(", ", imported.ChangedPaths)
                + (imported.Warnings.Count == 0 ? "" : "; warnings: " + string.Join("; ", imported.Warnings)));
            Assert.True(
                after.Any(kv => kv.Key.EndsWith(".devices.yaml", StringComparison.OrdinalIgnoreCase)
                                && TextFromUtf8(kv.Value).Contains(hwMarker, StringComparison.Ordinal)),
                "device Comment " + hwMarker + " missing after re-export. import wrote: "
                + string.Join(", ", imported.ChangedPaths)
                + (imported.Warnings.Count == 0 ? "" : "; warnings: " + string.Join("; ", imported.Warnings)));

            // The inverse edit. An added line is something the host's verify can look
            // FOR; a deletion adds nothing, so a generate that silently no-ops would
            // read as success — and the next export would rewrite the mirror from the
            // unchanged object, resurrecting the line. Delete the marker statement
            // from the re-exported mirror, import, re-export: the marker must be gone.
            var sclAfter = Directory.EnumerateFiles(export2, "*.scl", SearchOption.AllDirectories)
                .First(p => TextFromUtf8(File.ReadAllBytes(p)).Contains(marker, StringComparison.Ordinal));
            MutateInPlace(sclAfter, text => RemoveLinesContaining(text, marker));

            var plan2 = await ImportPlanReader.ReadAsync(
                fs, TiaPortalProjectFormat.Instance, export2, NullLogger.Instance,
                TiaPortalLog.Category, "tia-import", CancellationToken.None);
            var removal = plan2 with
            {
                Files = [PlannedApplyText(plan2, export2, sclAfter)],
                Deletions = [],
                Renames = [],
            };

            var imported2 = await runner.RunAsync(
                Job(PlcOperation.Import, status.Profile ?? "", copyProject, export2, removal),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(imported2);
            Assert.True(imported2.Succeeded, imported2.FailureReason +
                (imported2.Warnings.Count == 0 ? "" : " warnings: " + string.Join("; ", imported2.Warnings)));

            var reexported2 = await runner.RunAsync(
                Job(PlcOperation.Export, status.Profile ?? "", copyProject, export3),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(reexported2);
            Assert.True(reexported2.Succeeded, reexported2.FailureReason);

            var final = TextualBytes(export3);
            Assert.True(final.Count > 0, "re-export after the deletion wrote no textual files");
            Assert.False(
                final.Any(kv => TextFromUtf8(kv.Value).Contains(marker, StringComparison.Ordinal)),
                "deleted line " + marker + " came back after re-export — the generate "
                + "no-opped and the mirror would silently revert the user's edit");
        }
        finally
        {
            TryDeleteTree(copyDir);
            TryDeleteTree(export1);
            TryDeleteTree(export2);
            TryDeleteTree(export3);
        }
    }

    /// <summary>
    /// Openness membership, exclusive hold, and the unanswered AllowList dialog are
    /// measured blockers, not product bugs. The skip strings live here because
    /// "Security error. The operation has timed out." names neither Access nor
    /// Openness — the first probe of this machine went red until that phrase was added.
    /// </summary>
    private static void SkipIfMeasuredBlocker(PlcResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        Skip.If(
            result.FailureReason?.Contains("Openness", StringComparison.OrdinalIgnoreCase) == true
            || result.FailureReason?.Contains("Access", StringComparison.OrdinalIgnoreCase) == true
            || result.FailureReason?.Contains("held", StringComparison.OrdinalIgnoreCase) == true
            || result.FailureReason?.Contains("Security error", StringComparison.OrdinalIgnoreCase) == true
            || result.FailureReason?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true,
            result.FailureReason);
    }

    private static PlcJob Job(
        PlcOperation operation, string profile, string project, string path,
        ImportPlan? plan = null) =>
        new()
        {
            Operation = operation,
            Profile = profile,
            Save = operation == PlcOperation.Import,
            Timeout = TimeSpan.FromMinutes(20),
            Projects =
            [
                new PlcProjectJob
                {
                    BindingId = "tia-gate",
                    ProjectPath = project,
                    Path = path,
                    Plan = plan,
                },
            ],
        };

    private static Dictionary<string, byte[]> TextualBytes(string root)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (!ext.Equals(".scl", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".udt", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".s7dcl", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".devices.yaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            files[relative] = File.ReadAllBytes(file);
        }

        return files;
    }

    /// <summary>
    /// Set Comment on one named DeviceItem. Other items may not be writable
    /// (hwprobe: the nested server module listed no Comment CanWrite).
    /// </summary>
    private static string SetStationComment(string yaml, string interfaceName, string comment)
    {
        var needle = "interface: " + interfaceName;
        var start = yaml.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(start >= 0, "devices.yaml has no interface " + interfaceName);
        var next = yaml.IndexOf("\n  - ", start, StringComparison.Ordinal);
        if (next < 0)
        {
            next = yaml.Length;
        }

        var block = yaml[start..next];
        var replaced = Regex.Replace(
            block,
            @"Comment:\s*(?:""[^""]*""|\S*)",
            "Comment: \"" + comment + "\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(1));
        Assert.Contains(comment, replaced, StringComparison.Ordinal);
        return yaml[..start] + replaced + yaml[next..];
    }

    /// <summary>
    /// A statement in the body that must compile. Empty IF / extra VAR / header comments
    /// are ignored: GenerateBlocksFromSource rebuilds the interface from the object, and
    /// an IF whose body is only a comment left the block unexportable after Save.
    /// A self-assignment on an existing TEMP plus a // marker matches Siemens's own
    /// comment shape in Level.scl.
    /// </summary>
    private static string InsertTextualMutation(string original, string ident)
    {
        var nl = original.Contains('\r') ? "\r\n" : "\n";
        var begin = original.IndexOf("BEGIN", StringComparison.OrdinalIgnoreCase);
        if (begin >= 0)
        {
            var eol = original.IndexOfAny(['\r', '\n'], begin);
            if (eol >= 0)
            {
                var statement = original.Contains("#ReleaseSpd_Real", StringComparison.Ordinal)
                    ? "\t#ReleaseSpd_Real := #ReleaseSpd_Real; // " + ident
                    : "\t// " + ident;
                return original[..eol] + nl + statement + nl + original[eol..];
            }
        }

        return original.TrimEnd() + nl + "// " + ident + nl;
    }

    /// <summary>BOM-preserving edit: the mirror is written without one, Siemens with one.</summary>
    private static void MutateInPlace(string path, Func<string, string> transform)
    {
        var raw = File.ReadAllBytes(path);
        var hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        var original = hasBom
            ? Encoding.UTF8.GetString(raw, 3, raw.Length - 3)
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(raw);
        File.WriteAllText(path, transform(original),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom));
    }

    /// <summary>A real member before the first END_STRUCT — a comment might not survive generate.</summary>
    private static string InsertUdtMember(string original, string ident)
    {
        var nl = original.Contains('\r') ? "\r\n" : "\n";
        var idx = original.IndexOf("END_STRUCT", StringComparison.OrdinalIgnoreCase);
        Assert.True(idx >= 0, "UDT source has no END_STRUCT to grow a member in");
        var lineStart = original.LastIndexOfAny(['\r', '\n'], idx) + 1;
        return original[..lineStart] + "      " + ident + " : Bool;" + nl + original[lineStart..];
    }

    private static string RemoveLinesContaining(string text, string needle)
    {
        var nl = text.Contains('\r') ? "\r\n" : "\n";
        var kept = text.Replace("\r\n", "\n").Split('\n')
            .Where(line => !line.Contains(needle, StringComparison.Ordinal));
        return string.Join(nl, kept);
    }

    /// <summary>The planner's own ApplyText row for this file — never a hand-built action.</summary>
    private static PlannedFile PlannedApplyText(ImportPlan plan, string root, string absolute)
    {
        var rel = Path.GetRelativePath(root, absolute).Replace('\\', '/');
        return Assert.Single(plan.Files, f =>
            f.Path.Equals(rel, StringComparison.OrdinalIgnoreCase)
            && f.Action == PlannedAction.ApplyText);
    }

    private static string TextFromUtf8(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp.
        }
    }
}
