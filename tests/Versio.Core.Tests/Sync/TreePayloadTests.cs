using Versio.Core.Git;
using Versio.Core.Abstractions;
using Versio.Core.Bindings;
using Versio.Core.Locking;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Core.Sync;
using Versio.Core.Tests.Fakes;
using Xunit;

namespace Versio.Core.Tests.Sync;

/// <summary>
/// A project that is a directory, not a file — the seam the second vendor proved was missing.
/// </summary>
/// <remarks>
/// <para>
/// <b>No vendor is named here, on purpose.</b> The orchestrator copies, hashes, backs up and
/// restores whatever <see cref="IPlcProjectFormat.PayloadPathFor"/> returns. A test that
/// constructed a branded format would be the leak this widening exists to avoid; a stand-in
/// with a unique extension is enough to prove the tree path, and the file path stays the
/// existing scenario suite.
/// </para>
/// <para>
/// Break file-copy and only the first vendor's scenarios go red. Break tree-copy and only
/// this file goes red. That split is the mutation check the seam needs.
/// </para>
/// </remarks>
public sealed class TreePayloadTests
{
    private const string Root = @"C:\repo";
    private const string BindingId = "tree-1";

    [Fact]
    public async Task A_tree_payload_hash_ignores_logs_and_notices_the_program()
    {
        var h = Harness.Create();
        h.WritePayload("System/program.bin", "the program");
        h.WritePayload("Logs/session.log", "noise");
        h.Runner.EnqueueSuccess("plc/plant-export/Main.scl");

        var first = await h.ProjectChanged();
        Assert.Equal(SyncVerdict.Completed, first.Verdict);

        h.WritePayload("Logs/session.log", "different noise");
        var idle = await h.ProjectChanged();
        Assert.Equal(SyncVerdict.NoChange, idle.Verdict);

        h.WritePayload("System/program.bin", "edited program");
        h.Runner.EnqueueSuccess("plc/plant-export/Main.scl");
        var moved = await h.ProjectChanged();
        Assert.Equal(SyncVerdict.Completed, moved.Verdict);
    }

    private static Func<CancellationToken, Task<GitStatus>> Clean() =>
        _ => Task.FromResult(new GitStatus { Branch = new BranchInfo { Name = "main", IsCurrent = true } });

    private static readonly string[] Listed =
    [
        "plc/plant/Station.treeproj",
        "plc/plant/System/program.bin",
        "plc/plant/Logs/session.log",
        "plc/plant-export/_manifest.json",
        "plc/plant-export/Main.scl",
    ];

    [Fact]
    public async Task A_directory_project_whose_listed_program_is_absent_is_not_adopted()
    {
        // Third review, 2026-10-17: a sparse checkout kept the pointer and the mirror and
        // dropped the program beside the pointer. Git still listed System/program.bin, the hash
        // was taken over what was left, and adoption recorded it as exported; the gate said
        // Proceed. Listed payload files that count toward the hash must be on disk.
        var h = Harness.Create();

        Assert.Empty(await h.Orchestrator.AdoptCommittedStateAsync(Clean(), Listed, CancellationToken.None));
        var gate = await h.Orchestrator.EvaluateCommitGateAsync(CancellationToken.None);
        Assert.Equal(GateDecision.ExportRequired, gate.Decision);
    }

    [Fact]
    public async Task A_listed_payload_file_that_does_not_count_toward_the_hash_does_not_refuse()
    {
        // The same predicate the hash uses: a missing log changes nothing about the program,
        // so it must not cost an export.
        var h = Harness.Create();
        h.WritePayload("System/program.bin", "the program");

        Assert.Equal([BindingId],
            await h.Orchestrator.AdoptCommittedStateAsync(Clean(), Listed, CancellationToken.None));
    }

    [Fact]
    public async Task Hashing_a_tree_skips_a_pointer_the_toolchain_still_holds()
    {
        // After export the child has exited, but the pointer at the top of a directory
        // payload stays exclusive for seconds. Throwing there used to turn a finished
        // export into "failed to open at startup". The program under the pointer still
        // hashes; the lock on the pointer does not.
        var h = Harness.Create();
        h.WritePayload("System/program.bin", "the program");
        h.Fs.LockedFiles.Add(h.PayloadFile("Station.treeproj"));
        h.Runner.EnqueueSuccess("plc/plant-export/Main.scl");

        var outcome = await h.ProjectChanged();

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
    }

    [Fact]
    public async Task Copying_an_open_tree_takes_the_directory_not_the_pointer()
    {
        var h = Harness.Create();
        h.WritePayload("System/program.bin", "the program");
        h.Occupancy.Hold();

        var sawTree = false;
        h.Runner.BeforeRun = job =>
        {
            var dir = Path.GetDirectoryName(job.Projects[0].ProjectPath)!;
            sawTree = h.Fs.FileExists(Path.Combine(dir, "System", "program.bin"));
            return Task.CompletedTask;
        };
        h.Runner.EnqueueSuccess("plc/plant-export/Main.scl");

        var outcome = await h.ProjectChanged();

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
        Assert.True(sawTree, "the scratch copy did not include the tree under the pointer file");

        var job = Assert.Single(h.Runner.Jobs);
        Assert.Contains(
            Path.Combine(".versio", "local", "open", BindingId),
            job.Projects[0].ProjectPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Station.treeproj", Path.GetFileName(job.Projects[0].ProjectPath));
        Assert.False(h.Fs.FileExists(job.Projects[0].ProjectPath));
    }

    [Fact]
    public async Task A_tree_backup_is_a_directory_and_restore_puts_the_tree_back()
    {
        var h = Harness.Create();
        h.WritePayload("System/program.bin", "the good version");
        h.Runner.EnqueueSuccess("plc/plant-export/Main.scl");
        Assert.Equal(SyncVerdict.Completed, (await h.ProjectChanged()).Verdict);
        await h.Orchestrator.ReleaseAfterPushAsync("c1", CancellationToken.None);

        h.Fs.Write(Path.Combine(Root, "plc", "plant-export", "Main.scl"), "edited");
        h.Runner.EnqueueSuccess();
        Assert.Equal(SyncVerdict.Completed, (await h.TextChanged()).Verdict);

        var backup = Assert.Single(h.Orchestrator.BackupsFor(BindingId));
        var backupAbs = Path.Combine(Root, backup.Path.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(h.Fs.DirectoryExists(backupAbs));
        Assert.False(h.Fs.FileExists(backupAbs));

        h.WritePayload("System/program.bin", "damaged by the import");

        var outcome = await h.Orchestrator.RestoreBackupAsync(
            BindingId, backup.Path, CancellationToken.None);

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
        Assert.Equal(
            "the good version",
            await h.Fs.ReadAllTextAsync(h.PayloadFile("System/program.bin"), CancellationToken.None));
    }

    [Fact]
    public async Task Restoring_a_tree_removes_files_the_backup_never_had()
    {
        // The restore overlaid the backup onto the live directory: a file added after the
        // backup — a program block the bad import created, say — survived it, and the
        // "restored" project was neither the backup nor the damaged state (the 2026-10-24
        // review's item 21). A restore puts back exactly the tree that was saved.
        var h = Harness.Create();
        h.WritePayload("System/program.bin", "the good version");
        h.Runner.EnqueueSuccess("plc/plant-export/Main.scl");
        Assert.Equal(SyncVerdict.Completed, (await h.ProjectChanged()).Verdict);
        await h.Orchestrator.ReleaseAfterPushAsync("c1", CancellationToken.None);

        h.Fs.Write(Path.Combine(Root, "plc", "plant-export", "Main.scl"), "edited");
        h.Runner.EnqueueSuccess();
        Assert.Equal(SyncVerdict.Completed, (await h.TextChanged()).Verdict);
        var backup = Assert.Single(h.Orchestrator.BackupsFor(BindingId));

        h.WritePayload("System/program.bin", "damaged by the import");
        h.WritePayload("System/added-by-the-import.bin", "new");

        var outcome = await h.Orchestrator.RestoreBackupAsync(
            BindingId, backup.Path, CancellationToken.None);

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
        Assert.Equal(
            "the good version",
            await h.Fs.ReadAllTextAsync(h.PayloadFile("System/program.bin"), CancellationToken.None));
        Assert.False(h.Fs.FileExists(h.PayloadFile("System/added-by-the-import.bin")),
            "a file the backup never had survived the restore");
    }

    [Fact]
    public async Task A_backup_whose_copy_did_not_finish_is_neither_listed_nor_restorable()
    {
        // Review 2026-10-25, fourth round, item 1: a tree copy that stopped partway left a
        // directory the catalog listed like any other, and restoring it put back half a project
        // — the restore deletes what the backup does not have (item 21 of the first round), so
        // a live program file vanished and the restore reported success.
        var h = Harness.Create();
        h.WritePayload("System/program.bin", "the good version");
        h.Runner.EnqueueSuccess("plc/plant-export/Main.scl");
        Assert.Equal(SyncVerdict.Completed, (await h.ProjectChanged()).Verdict);
        await h.Orchestrator.ReleaseAfterPushAsync("c1", CancellationToken.None);

        h.Fs.Write(Path.Combine(Root, "plc", "plant-export", "Main.scl"), "edited");
        h.Runner.EnqueueSuccess();

        // The pointer is copied; the program is not.
        h.Fs.FailCopyAt = 2;
        var import = await h.TextChanged();

        // A failed backup stops the import, and the project is untouched.
        Assert.Equal(SyncVerdict.Failed, import.Verdict);
        Assert.Equal(
            "the good version",
            await h.Fs.ReadAllTextAsync(h.PayloadFile("System/program.bin"), CancellationToken.None));

        // The half-copy is on disk, and it is not a backup.
        var folder = Path.Combine(Root, ".versio", "local", "backups", BindingId);
        var partial = Assert.Single(h.Fs.EnumerateDirectories(folder));
        Assert.True(h.Fs.FileExists(Path.Combine(partial, "Station.treeproj")));
        Assert.False(h.Fs.FileExists(Path.Combine(partial, "System", "program.bin")));
        Assert.Empty(h.Orchestrator.BackupsFor(BindingId));

        // Asked for by path all the same, it is refused and the program survives.
        var restore = await h.Orchestrator.RestoreBackupAsync(
            BindingId, Path.GetRelativePath(Root, partial).Replace('\\', '/'), CancellationToken.None);

        Assert.Equal(SyncVerdict.Failed, restore.Verdict);
        Assert.Contains("never finished", restore.Explanation);
        Assert.Equal(
            "the good version",
            await h.Fs.ReadAllTextAsync(h.PayloadFile("System/program.bin"), CancellationToken.None));

        // The next completed copy sweeps it away, marker and all.
        await h.Orchestrator.AcknowledgeFailureAsync(BindingId, CancellationToken.None);
        h.Fs.Write(Path.Combine(Root, "plc", "plant-export", "Main.scl"), "edited again");
        h.Runner.EnqueueSuccess();
        Assert.Equal(SyncVerdict.Completed, (await h.TextChanged()).Verdict);

        var listed = Assert.Single(h.Orchestrator.BackupsFor(BindingId));
        Assert.False(h.Fs.DirectoryExists(partial), "the unfinished copy was kept");
        Assert.DoesNotContain(h.Fs.EnumerateFiles(folder), f => f.EndsWith(".partial", StringComparison.Ordinal));
        Assert.NotEqual(partial, Path.Combine(Root, listed.Path.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public async Task A_file_payload_is_still_copied_as_one_file()
    {
        // The mutation check the other way: tree support must not rewrite the file path
        // the first vendor has used since the export pipeline existed.
        var h = new SyncOrchestratorTests.Harness();
        h.Codesys.EnqueueSuccess("plc/A/Main.st");

        var outcome = await h.ProjectChanged();

        Assert.Equal(SyncVerdict.Completed, outcome.Verdict);
        Assert.True(h.Fs.FileExists(Path.Combine(Root, "plc", "A.project")));
        Assert.False(h.Fs.DirectoryExists(Path.Combine(Root, "plc", "A.project")));
    }

    [Fact]
    public async Task The_commit_gate_owns_the_program_under_the_pointer()
    {
        // The orchestrator has to hand the gate the payload folder. Without it a commit that
        // records only the program store - not the pointer, not the mirror - is "no tracked
        // project is part of this commit", and the program lands beside a stale export.
        var h = Harness.Create();
        h.WritePayload("System/program.bin", "the program");
        h.Runner.EnqueueSuccess("plc/plant-export/Main.scl");
        Assert.Equal(SyncVerdict.Completed, (await h.ProjectChanged()).Verdict);

        h.WritePayload("System/program.bin", "saved again, not exported");

        var gate = await h.Orchestrator.EvaluateCommitGateAsync(
            ["plc/plant/System/program.bin"], CancellationToken.None);

        Assert.Equal(GateDecision.ExportRequired, gate.Decision);
        Assert.Equal([BindingId], gate.StaleBindings);
    }

    /// <summary>A format whose project is the directory containing the pointer file.</summary>
    private sealed class TreeFormat : IPlcProjectFormat
    {
        public PlcVendor Vendor => new("TreeStandIn");

        public string DisplayName => "Tree stand-in";

        public PlcCapabilities Capabilities => new()
        {
            TextualExport = true,
            TextualImport = true,
            ObjectCreation = true,
            Headless = true,
            TypicalStartupCost = TimeSpan.FromSeconds(1),
        };

        public IReadOnlyList<string> ProjectExtensions => [".treeproj"];

        public bool IsTemplate(string path) => false;

        public string ConventionalExportPath(string projectPath)
        {
            var normalised = projectPath.Replace('\\', '/');
            return normalised.EndsWith(".treeproj", StringComparison.OrdinalIgnoreCase)
                ? normalised[..^".treeproj".Length] + "-export"
                : normalised + ".export";
        }

        public string PayloadPathFor(string projectPath)
        {
            var dir = Path.GetDirectoryName(projectPath);
            return string.IsNullOrEmpty(dir) ? projectPath : dir;
        }

        public bool CountsTowardProjectHash(string relativePath)
        {
            var first = relativePath.Replace('\\', '/').Split('/')[0];
            return !string.Equals(first, "Logs", StringComparison.OrdinalIgnoreCase);
        }

        public string? OccupancyMarkerFor(string projectPath) => null;

        public IReadOnlyList<string> TransientPatternsFor(string projectPath) => [];

        public ProjectOccupancy ParseOccupancy(string markerContents) => ProjectOccupancy.Free;

        public bool NeedsTextToPlan(string relativePath) =>
            relativePath.EndsWith(".scl", StringComparison.OrdinalIgnoreCase);

        public ImportPlan PlanImport(
            Versio.Core.Projects.ProjectManifest? manifest,
            IReadOnlyList<MirrorFile> mirror,
            IReadOnlyDictionary<string, string> departed) =>
            new()
            {
                Files =
                [
                    new PlannedFile
                    {
                        Path = "Main.scl",
                        Action = PlannedAction.ApplyText,
                        Name = "Main",
                    },
                ],
            };

        public Versio.Core.Projects.ObjectTypeName? DisplayNameFor(string type) => null;
    }

    private sealed class Harness
    {
        public FakeFileSystem Fs { get; } = new();
        public FakeCodesysRunner Runner { get; } = new();
        public SyncOrchestratorTests.StubOccupancy Occupancy { get; } = new();
        public SyncOrchestrator Orchestrator { get; }

        private Harness()
        {
            var clock = new FakeClock();
            var binding = new ProjectBinding
            {
                Id = BindingId,
                ProjectPath = "plc/plant/Station.treeproj",
                ExportPath = "plc/plant-export",
                Profile = "V21",
            };

            Fs.Write(PayloadFile("Station.treeproj"), "pointer");
            Fs.Write(Path.Combine(Root, "plc", "plant-export", "_manifest.json"), "{}");
            Fs.Write(Path.Combine(Root, "plc", "plant-export", "Main.scl"),
                "FUNCTION_BLOCK \"Main\"");

            Orchestrator = new SyncOrchestrator(
                Root, [binding],
                new LockOwner
                {
                    Machine = "TEST-PC", User = "tester", SessionId = "s1",
                    ProcessId = 4242,
                    BootId = new DateTimeOffset(2026, 8, 11, 6, 0, 0, TimeSpan.Zero),
                },
                clock, Fs, Runner, new InMemoryLockStore(),
                new OrchestratorOptions { RequireImportConfirmation = false },
                logger: new VersioLogger(clock).AddSink(new InMemoryLogSink()),
                occupancy: Occupancy,
                formats: _ => new TreeFormat());
        }

        public static Harness Create() => new();

        public string PayloadFile(string relative) =>
            Path.Combine(Root, "plc", "plant", relative.Replace('/', Path.DirectorySeparatorChar));

        public void WritePayload(string relative, string content) =>
            Fs.Write(PayloadFile(relative), content);

        public Task<SyncOutcome> ProjectChanged() =>
            Orchestrator.HandleChangeAsync(
                new SyncJob
                {
                    BindingId = BindingId,
                    Source = ChangeSource.Project,
                    Synchronous = true,
                },
                CancellationToken.None);

        public Task<SyncOutcome> TextChanged() =>
            Orchestrator.HandleChangeAsync(
                new SyncJob
                {
                    BindingId = BindingId,
                    Source = ChangeSource.ExportedText,
                    Confirmed = true,
                    Synchronous = true,
                },
                CancellationToken.None);
    }
}
