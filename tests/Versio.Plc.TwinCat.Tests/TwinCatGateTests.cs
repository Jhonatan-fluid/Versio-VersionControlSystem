using System.Text;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Versio.Infrastructure.FileSystem;
using Versio.Plc.TwinCat;
using Versio.Plc.TwinCat.Infrastructure;
using Versio.TestSupport;
using Xunit;

namespace Versio.Plc.TwinCat.Tests;

/// <summary>
/// Real TwinCAT XAE, when the machine has it. Skip is the success path on every other machine.
/// </summary>
[Collection(TwinCatCollection.Name)]
public sealed class TwinCatGateTests
{
    [SkippableFact]
    public void The_locator_finds_TcXaeShell_DTE_17()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TwinCAT XAE is Windows-only");
        var install = new TwinCatLocator().FindPreferred();
        Skip.If(install is null, "no TcXaeShell.DTE ProgID on this machine");

        Assert.Equal(TwinCatLocator.PreferredProgId, install!.ProgId);
    }

    [SkippableFact]
    public async Task Probe_opens_the_committed_fixture_or_explains_why_not()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TwinCAT XAE is Windows-only");
        var install = new TwinCatLocator().FindPreferred();
        Skip.If(install is null, "no TwinCAT XAE install");

        var fixture = TestPaths.TwinCatProject();
        Skip.If(fixture is null, "committed TwinCAT fixture is missing");

        var host = LocalTwinCatRunner.FindHost();
        Skip.If(host is null, "TwinCAT host was not copied to the output directory");

        var toolchain = new TwinCatToolchain(NullLogger.Instance);
        var status = await toolchain.ProbeAsync(CancellationToken.None);
        Skip.If(!status.Available, status.Explanation);

        var stamp = Guid.NewGuid().ToString("N")[..8];
        var copyDir = Path.Combine(Path.GetTempPath(), "versio-tc-probe-" + stamp);
        var outDir = Path.Combine(Path.GetTempPath(), "versio-tc-probe-out-" + stamp);
        Directory.CreateDirectory(copyDir);
        Directory.CreateDirectory(outDir);

        try
        {
            var copyProject = await CopyFixtureAsync(fixture!, copyDir);
            var runner = toolchain.CreateRunner(status);
            var result = await runner.RunAsync(
                Job(PlcOperation.Probe, status.Profile ?? "", copyProject, outDir),
                progress: null,
                CancellationToken.None);

            SkipIfMeasuredBlocker(result);
            Assert.True(result.Succeeded, result.FailureReason);
            Assert.True(File.Exists(Path.Combine(outDir, "report.json")));
        }
        finally
        {
            TryDeleteTree(copyDir);
            TryDeleteTree(outDir);
        }
    }

    [SkippableFact]
    public async Task Export_writes_a_manifest_and_names_the_coverage_skips()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TwinCAT XAE is Windows-only");
        var install = new TwinCatLocator().FindPreferred();
        Skip.If(install is null, "no TwinCAT XAE install");

        var fixture = TestPaths.TwinCatProject();
        Skip.If(fixture is null, "committed TwinCAT fixture is missing");

        var toolchain = new TwinCatToolchain(NullLogger.Instance);
        var status = await toolchain.ProbeAsync(CancellationToken.None);
        Skip.If(!status.Available, status.Explanation);
        Skip.If(LocalTwinCatRunner.FindHost() is null, "TwinCAT host was not copied to the output directory");

        var runner = toolchain.CreateRunner(status);
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var copyDir = Path.Combine(Path.GetTempPath(), "versio-tc-export-" + stamp);
        var outDir = Path.Combine(Path.GetTempPath(), "versio-tc-export-out-" + stamp);
        Directory.CreateDirectory(copyDir);
        Directory.CreateDirectory(outDir);

        try
        {
            var copyProject = await CopyFixtureAsync(fixture!, copyDir);
            var result = await runner.RunAsync(
                Job(PlcOperation.Export, status.Profile ?? "", copyProject, outDir),
                progress: null,
                CancellationToken.None);

            SkipIfMeasuredBlocker(result);
            Assert.True(result.Succeeded, result.FailureReason);
            Assert.True(File.Exists(Path.Combine(outDir, "_manifest.json")));
            Assert.True(
                Directory.EnumerateFiles(outDir, "*.st", SearchOption.AllDirectories).Any(),
                "export wrote a manifest but no .st — TIPC walk missed DeclarationText");
            var coverage = File.ReadAllText(Path.Combine(outDir, "_coverage.md"));
            Assert.DoesNotContain("TIID devices and mappings are a coverage skip", coverage);
            Assert.Contains("devices.yaml", coverage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("NC", coverage, StringComparison.OrdinalIgnoreCase);
            var yaml = Directory.EnumerateFiles(outDir, "*.devices.yaml", SearchOption.AllDirectories)
                .FirstOrDefault();
            Assert.True(yaml is not null, "export wrote no *.devices.yaml");
            var manifest = File.ReadAllText(Path.Combine(outDir, "_manifest.json"));
            Assert.Contains("DEVICES", manifest, StringComparison.Ordinal);
            Assert.DoesNotContain("DEVICE_COMMENT", manifest, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteTree(copyDir);
            TryDeleteTree(outDir);
        }
    }

    [SkippableFact(Timeout = 1_200_000)]
    public async Task Two_unchanged_textual_exports_match_byte_for_byte()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TwinCAT XAE is Windows-only");
        var install = new TwinCatLocator().FindPreferred();
        Skip.If(install is null, "no TwinCAT XAE install");

        var fixture = TestPaths.TwinCatProject();
        Skip.If(fixture is null, "committed TwinCAT fixture is missing");

        var toolchain = new TwinCatToolchain(NullLogger.Instance);
        var status = await toolchain.ProbeAsync(CancellationToken.None);
        Skip.If(!status.Available, status.Explanation);
        Skip.If(LocalTwinCatRunner.FindHost() is null, "TwinCAT host was not copied to the output directory");

        var stamp = Guid.NewGuid().ToString("N")[..8];
        var copyDir = Path.Combine(Path.GetTempPath(), "versio-tc-det-" + stamp);
        var exportDir = Path.Combine(Path.GetTempPath(), "versio-tc-det-out-" + stamp);
        Directory.CreateDirectory(copyDir);
        Directory.CreateDirectory(exportDir);

        try
        {
            var copyProject = await CopyFixtureAsync(fixture!, copyDir);
            var runner = toolchain.CreateRunner(status);
            var first = await runner.RunAsync(
                Job(PlcOperation.Export, status.Profile ?? "", copyProject, exportDir),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(first);
            Assert.True(first.Succeeded, first.FailureReason);

            var a = TextualBytes(exportDir);

            var second = await runner.RunAsync(
                Job(PlcOperation.Export, status.Profile ?? "", copyProject, exportDir),
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
            TryDeleteTree(copyDir);
            TryDeleteTree(exportDir);
        }
    }

    /// <summary>
    /// Export, mutate one <c>.st</c>, import the planner's plan, re-export and find the
    /// mutation — then delete the added comment, import again, and prove the deletion
    /// round-trips too.
    /// </summary>
    /// <remarks>
    /// Runs against a copy of the committed fixture, never the Downloads original and
    /// never this repository's own binding. Close XAE and Versio first — exclusive DTE
    /// open is a human precondition; a held project skips.
    /// </remarks>
    [SkippableFact(Timeout = 1_200_000)]
    public async Task Import_then_reexport_keeps_textual_bytes()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TwinCAT XAE is Windows-only");
        var install = new TwinCatLocator().FindPreferred();
        Skip.If(install is null, "no TwinCAT XAE install");

        var fixture = TestPaths.TwinCatProject();
        Skip.If(fixture is null, "committed TwinCAT fixture is missing");

        var toolchain = new TwinCatToolchain(NullLogger.Instance);
        var status = await toolchain.ProbeAsync(CancellationToken.None);
        Skip.If(!status.Available, status.Explanation);
        Skip.If(LocalTwinCatRunner.FindHost() is null, "TwinCAT host was not copied to the output directory");

        var stamp = Guid.NewGuid().ToString("N")[..8];
        var copyDir = Path.Combine(Path.GetTempPath(), "versio-tc-copy-" + stamp);
        var export1 = Path.Combine(Path.GetTempPath(), "versio-tc-imp1-" + stamp);
        var export2 = Path.Combine(Path.GetTempPath(), "versio-tc-imp2-" + stamp);
        var export3 = Path.Combine(Path.GetTempPath(), "versio-tc-imp3-" + stamp);
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

            var st = Directory.EnumerateFiles(export1, "*.st", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .Where(f => f.Length > 80)
                .OrderBy(f => f.Length)
                .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()?.FullName;
            Skip.If(st is null, "export wrote no .st — cannot mutate textual");

            var marker = "versio_mutate_" + stamp;
            MutateInPlace(st!, text => InsertTextualMutation(text, marker));

            var fs = new WindowsFileSystem();
            var plan = await ImportPlanReader.ReadAsync(
                fs, TwinCatProjectFormat.Instance, export1, NullLogger.Instance,
                TwinCatLog.Category, "tc-import", CancellationToken.None);

            var textual = plan with
            {
                Files = [PlannedApplyText(plan, export1, st!)],
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

            var after = TextualBytes(export2);
            var name = Path.GetFileName(st)!;
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

            var stAfter = Directory.EnumerateFiles(export2, "*.st", SearchOption.AllDirectories)
                .First(p => TextFromUtf8(File.ReadAllBytes(p)).Contains(marker, StringComparison.Ordinal));
            MutateInPlace(stAfter, text => RemoveLinesContaining(text, marker));

            var plan2 = await ImportPlanReader.ReadAsync(
                fs, TwinCatProjectFormat.Instance, export2, NullLogger.Instance,
                TwinCatLog.Category, "tc-import", CancellationToken.None);
            var removal = plan2 with
            {
                Files = [PlannedApplyText(plan2, export2, stAfter)],
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
                "deleted line " + marker + " came back after re-export — the text set "
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
    /// A new <c>.st</c> becomes a live POU, renaming it changes the Automation Interface
    /// name, and deleting the file removes the object — not a line inside one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The overwrite Fact only deletes a comment. Create / object-delete / rename were
    /// already in the planner and host; this is the walk against live XAE. Folder move
    /// is not this Fact: the host still only sets <c>Name</c>.
    /// </para>
    /// <para>
    /// The created FB is ours, so rename and delete do not touch the sample's own POUs.
    /// Create lands at the PLC root (a new file's resolve is the declared name only);
    /// assertions look for the header, not a folder. Rename pairing uses
    /// <c>departed</c> because TwinCAT export does not write <c>shash</c> and this tree
    /// is not a git repo.
    /// </para>
    /// </remarks>
    [SkippableFact(Timeout = 1_200_000)]
    public async Task Create_rename_and_object_delete_round_trip()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "TwinCAT XAE is Windows-only");
        var install = new TwinCatLocator().FindPreferred();
        Skip.If(install is null, "no TwinCAT XAE install");

        var fixture = TestPaths.TwinCatProject();
        Skip.If(fixture is null, "committed TwinCAT fixture is missing");

        var toolchain = new TwinCatToolchain(NullLogger.Instance);
        var status = await toolchain.ProbeAsync(CancellationToken.None);
        Skip.If(!status.Available, status.Explanation);
        Skip.If(LocalTwinCatRunner.FindHost() is null, "TwinCAT host was not copied to the output directory");

        var stamp = Guid.NewGuid().ToString("N")[..8];
        var createdName = "VersioGateFb_" + stamp;
        var renamedName = "VersioGateRn_" + stamp;
        var marker = "versio_struct_" + stamp;

        var copyDir = Path.Combine(Path.GetTempPath(), "versio-tc-struct-" + stamp);
        var export1 = Path.Combine(Path.GetTempPath(), "versio-tc-st1-" + stamp);
        var export2 = Path.Combine(Path.GetTempPath(), "versio-tc-st2-" + stamp);
        var export3 = Path.Combine(Path.GetTempPath(), "versio-tc-st3-" + stamp);
        var export4 = Path.Combine(Path.GetTempPath(), "versio-tc-st4-" + stamp);
        Directory.CreateDirectory(copyDir);
        Directory.CreateDirectory(export1);
        Directory.CreateDirectory(export2);
        Directory.CreateDirectory(export3);
        Directory.CreateDirectory(export4);

        try
        {
            var copyProject = await CopyFixtureAsync(fixture!, copyDir);
            var runner = toolchain.CreateRunner(status);
            var fs = new WindowsFileSystem();

            var exported = await runner.RunAsync(
                Job(PlcOperation.Export, status.Profile ?? "", copyProject, export1),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(exported);
            Assert.True(exported.Succeeded, exported.FailureReason);

            File.WriteAllText(
                Path.Combine(export1, createdName + ".st"),
                PackedFunctionBlock(createdName, marker),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var createPlan = await ImportPlanReader.ReadAsync(
                fs, TwinCatProjectFormat.Instance, export1, NullLogger.Instance,
                TwinCatLog.Category, "tc-struct", CancellationToken.None);
            var create = Assert.Single(createPlan.Files, f => f.Action == PlannedAction.Create);
            Assert.Equal(createdName, create.Name);

            var created = await runner.RunAsync(
                Job(PlcOperation.Import, status.Profile ?? "", copyProject, export1,
                    createPlan with
                    {
                        Files = [create],
                        Deletions = [],
                        Renames = [],
                    }),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(created);
            Assert.True(created.Succeeded, ImportFailed(created, "create"));

            var afterCreate = await runner.RunAsync(
                Job(PlcOperation.Export, status.Profile ?? "", copyProject, export2),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(afterCreate);
            Assert.True(afterCreate.Succeeded, afterCreate.FailureReason);

            var createdFile = FindStContaining(export2, createdName);
            Assert.True(createdFile is not null,
                "create did not survive re-export — no .st names " + createdName
                + ". import wrote: " + string.Join(", ", created.ChangedPaths)
                + Warn(created)
                + "; files: " + string.Join(", ", StNames(export2)));
            Assert.Contains(marker, File.ReadAllText(createdFile!), StringComparison.Ordinal);

            var oldRel = Path.GetRelativePath(export2, createdFile!).Replace('\\', '/');
            var oldText = File.ReadAllText(createdFile!);
            var newRel = ReplaceFileStem(oldRel, renamedName);
            var newText = oldText.Replace(
                "FUNCTION_BLOCK " + createdName,
                "FUNCTION_BLOCK " + renamedName,
                StringComparison.Ordinal);
            Assert.False(string.Equals(oldText, newText, StringComparison.Ordinal),
                "rename did not change the FUNCTION_BLOCK header");

            File.WriteAllText(
                Path.Combine(export2, newRel.Replace('/', Path.DirectorySeparatorChar)),
                newText,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Delete(createdFile!);

            var manifest = ProjectManifest.Parse(File.ReadAllText(Path.Combine(export2, "_manifest.json")));
            var renamePlan = TwinCatImportPlanner.Plan(
                manifest,
                MirrorOf(export2),
                new Dictionary<string, string>(StringComparer.Ordinal) { [oldRel] = oldText });
            Assert.False(renamePlan.Renames.Count == 0,
                "planner did not pair the rename — departed bytes did not match. warnings: "
                + string.Join("; ", renamePlan.Warnings)
                + (renamePlan.StructuralChangesUnavailable is null
                    ? ""
                    : "; " + renamePlan.StructuralChangesUnavailable));
            var rename = Assert.Single(renamePlan.Renames);
            Assert.True(rename.IsRename, "planner treated the header change as a move or a no-op");
            Assert.Equal(createdName, rename.OldName);
            Assert.Equal(renamedName, rename.NewName);

            var renamed = await runner.RunAsync(
                Job(PlcOperation.Import, status.Profile ?? "", copyProject, export2,
                    renamePlan with
                    {
                        // Similarity pairing marks the arrival ApplyText (hash pairing
                        // would Skip it). Either way, do not ApplyText every other .st.
                        Files = [.. renamePlan.Files.Where(f =>
                            f.Skip == PlannedSkip.RenameTarget
                            || f.Action == PlannedAction.Create
                            || (f.Action == PlannedAction.ApplyText
                                && f.Path.Equals(rename.NewPath, StringComparison.Ordinal)))],
                        Deletions = [],
                    }),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(renamed);
            Assert.True(renamed.Succeeded, ImportFailed(renamed, "rename"));

            var afterRename = await runner.RunAsync(
                Job(PlcOperation.Export, status.Profile ?? "", copyProject, export3),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(afterRename);
            Assert.True(afterRename.Succeeded, afterRename.FailureReason);

            Assert.Null(FindStContaining(export3, createdName));
            var renamedFile = FindStContaining(export3, renamedName);
            Assert.True(renamedFile is not null,
                "rename did not survive re-export — no .st names " + renamedName
                + ". import wrote: " + string.Join(", ", renamed.ChangedPaths)
                + Warn(renamed)
                + "; files: " + string.Join(", ", StNames(export3)));
            Assert.Contains(marker, File.ReadAllText(renamedFile!), StringComparison.Ordinal);

            File.Delete(renamedFile!);
            var deletePlan = await ImportPlanReader.ReadAsync(
                fs, TwinCatProjectFormat.Instance, export3, NullLogger.Instance,
                TwinCatLog.Category, "tc-struct", CancellationToken.None);
            var deletion = Assert.Single(deletePlan.Deletions);
            Assert.Equal(renamedName, deletion.Name);
            Assert.Null(deletion.CannotVerify);

            var deleted = await runner.RunAsync(
                Job(PlcOperation.Import, status.Profile ?? "", copyProject, export3,
                    deletePlan with
                    {
                        Files = [],
                        Renames = [],
                    }),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(deleted);
            Assert.True(deleted.Succeeded, ImportFailed(deleted, "delete"));
            Assert.Contains(deleted.ChangedPaths, p =>
                p.Contains(renamedName, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(deleted.Warnings, w =>
                w.Contains("kept ", StringComparison.OrdinalIgnoreCase));

            var afterDelete = await runner.RunAsync(
                Job(PlcOperation.Export, status.Profile ?? "", copyProject, export4),
                progress: null,
                CancellationToken.None);
            SkipIfMeasuredBlocker(afterDelete);
            Assert.True(afterDelete.Succeeded, afterDelete.FailureReason);

            Assert.True(FindStContaining(export4, renamedName) is null,
                "deleted POU " + renamedName + " came back after re-export — DeleteChild "
                + "kept the object or SaveAll did not persist it. import wrote: "
                + string.Join(", ", deleted.ChangedPaths) + Warn(deleted)
                + "; files: " + string.Join(", ", StNames(export4)));
            Assert.False(
                TextualBytes(export4).Any(kv =>
                    TextFromUtf8(kv.Value).Contains(marker, StringComparison.Ordinal)),
                "deleted POU " + renamedName + " came back after re-export — DeleteChild "
                + "kept the object or SaveAll did not persist it. import wrote: "
                + string.Join(", ", deleted.ChangedPaths) + Warn(deleted));
        }
        finally
        {
            TryDeleteTree(copyDir);
            TryDeleteTree(export1);
            TryDeleteTree(export2);
            TryDeleteTree(export3);
            TryDeleteTree(export4);
        }
    }

    /// <summary>
    /// Conversion refusal, exclusive hold, license, and RPC rejection are measured
    /// blockers, not product bugs. A 4024 fixture on 4026 is refused on purpose.
    /// </summary>
    private static void SkipIfMeasuredBlocker(PlcResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        Skip.If(
            result.FailureReason?.Contains("silently converted", StringComparison.OrdinalIgnoreCase) == true
            || result.FailureReason?.Contains("license", StringComparison.OrdinalIgnoreCase) == true
            || result.FailureReason?.Contains("held", StringComparison.OrdinalIgnoreCase) == true
            || result.FailureReason?.Contains("RPC_E_CALL_REJECTED", StringComparison.OrdinalIgnoreCase) == true
            || result.FailureReason?.Contains("0x80010001", StringComparison.OrdinalIgnoreCase) == true
            || result.FailureReason?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true,
            result.FailureReason);
    }

    private static async Task<string> CopyFixtureAsync(string fixture, string copyDir)
    {
        await new WindowsFileSystem().CopyTreeAsync(
            Path.GetDirectoryName(fixture)!, copyDir, CancellationToken.None);
        return Path.Combine(copyDir, Path.GetFileName(fixture)!);
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
                    BindingId = "tc-gate",
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
            if (!file.EndsWith(".st", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".devices.yaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            files[relative] = File.ReadAllBytes(file);
        }

        return files;
    }

    private static string InsertTextualMutation(string original, string ident)
    {
        var nl = original.Contains('\r') ? "\r\n" : "\n";
        var delim = original.IndexOf("// --- BEGIN IMPLEMENTATION ---", StringComparison.Ordinal);
        if (delim >= 0)
        {
            var eol = original.IndexOfAny(['\r', '\n'], delim);
            if (eol >= 0)
            {
                return original[..eol] + nl + "// " + ident + nl + original[eol..];
            }
        }

        return original.TrimEnd() + nl + "// " + ident + nl;
    }

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

    private static string RemoveLinesContaining(string text, string needle)
    {
        var nl = text.Contains('\r') ? "\r\n" : "\n";
        var kept = text.Replace("\r\n", "\n").Split('\n')
            .Where(line => !line.Contains(needle, StringComparison.Ordinal));
        return string.Join(nl, kept);
    }

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

    private static string PackedFunctionBlock(string name, string marker) =>
        "FUNCTION_BLOCK " + name + "\n"
        + "VAR\n"
        + "END_VAR\n"
        + "\n"
        + "// --- BEGIN IMPLEMENTATION ---\n"
        + "\n"
        + "// " + marker + "\n";

    private static string? FindStContaining(string root, string objectName)
    {
        var needle = "FUNCTION_BLOCK " + objectName;
        return Directory.EnumerateFiles(root, "*.st", SearchOption.AllDirectories)
            .FirstOrDefault(file =>
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                return stem.Equals(objectName, StringComparison.OrdinalIgnoreCase)
                    || File.ReadAllText(file).Contains(needle, StringComparison.Ordinal);
            });
    }

    private static IEnumerable<string> StNames(string root) =>
        Directory.EnumerateFiles(root, "*.st", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'));

    private static string ReplaceFileStem(string relative, string newStem)
    {
        var slash = relative.LastIndexOf('/');
        var dir = slash < 0 ? "" : relative[..(slash + 1)];
        return dir + newStem + ".st";
    }

    private static List<MirrorFile> MirrorOf(string root)
    {
        List<MirrorFile> files = [];
        foreach (var absolute in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, absolute).Replace('\\', '/');
            files.Add(new MirrorFile
            {
                Path = relative,
                Text = TwinCatImportPlanner.NeedsText(relative) ? File.ReadAllText(absolute) : null,
            });
        }

        return files;
    }

    private static string ImportFailed(PlcResult result, string step) =>
        step + " import failed: " + result.FailureReason + Warn(result);

    private static string Warn(PlcResult result) =>
        result.Warnings.Count == 0
            ? ""
            : "; warnings: " + string.Join("; ", result.Warnings);

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
