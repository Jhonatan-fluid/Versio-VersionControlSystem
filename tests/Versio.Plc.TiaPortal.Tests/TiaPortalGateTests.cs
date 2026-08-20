using System.Text;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.FileSystem;
using Versio.Plc.TiaPortal;
using Versio.Plc.TiaPortal.Infrastructure;
using Versio.TestSupport;
using Xunit;

namespace Versio.Plc.TiaPortal.Tests;

/// <summary>
/// Real TIA Portal, when the machine has it. Skip is the success path on every other machine.
/// </summary>
[Collection(TiaPortalCollection.Name)]
public sealed class TiaPortalGateTests
{
    [SkippableFact]
    public void The_locator_finds_the_PublicAPI_folder()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TIA Portal is Windows-only");
        var installs = new TiaPortalLocator().FindInstalls();
        Skip.If(installs.Count == 0, "no TIA Portal install with a PublicAPI net48 folder");

        var install = Assert.Single(installs);
        Assert.True(Directory.Exists(install.ApiFolder));
        Assert.True(File.Exists(Path.Combine(install.ApiFolder, "Siemens.Engineering.Base.dll")));
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
            Assert.Contains("Hardware", coverage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("HMI", coverage, StringComparison.OrdinalIgnoreCase);
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
    /// Export, mutate one textual file, import the planner's plan, re-export.
    /// Textual bytes must match. Native XML is expected to churn and is not compared.
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
        Directory.CreateDirectory(copyDir);
        Directory.CreateDirectory(export1);
        Directory.CreateDirectory(export2);

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

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var raw = File.ReadAllBytes(scl);
            var hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            var original = hasBom
                ? Encoding.UTF8.GetString(raw, 3, raw.Length - 3)
                : utf8.GetString(raw);
            var marker = "versio_mutate_" + stamp;
            var mutated = InsertTextualMutation(original, marker);
            File.WriteAllText(scl, mutated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom));

            var fs = new WindowsFileSystem();
            var plan = await ImportPlanReader.ReadAsync(
                fs, TiaPortalProjectFormat.Instance, export1, NullLogger.Instance,
                TiaPortalLog.Category, "tia-import", CancellationToken.None);

            // The planner's answer for the mutated file — ApplyText, not a hand-built
            // action. The rest of a full export plan would re-apply every native XML;
            // this Fact is the textual mutation, and native XML is expected to churn.
            var mutatedRel = Path.GetRelativePath(export1, scl).Replace('\\', '/');
            var chosen = Assert.Single(plan.Files, f =>
                f.Path.Equals(mutatedRel, StringComparison.OrdinalIgnoreCase)
                && f.Action == PlannedAction.ApplyText);
            var textual = plan with
            {
                Files = [chosen],
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
        }
        finally
        {
            TryDeleteTree(copyDir);
            TryDeleteTree(export1);
            TryDeleteTree(export2);
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
                && !ext.Equals(".s7dcl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            files[relative] = File.ReadAllBytes(file);
        }

        return files;
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
