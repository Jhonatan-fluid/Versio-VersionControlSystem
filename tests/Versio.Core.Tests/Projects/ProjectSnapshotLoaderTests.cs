using Versio.Core.Logging;
using Versio.Core.Projects;
using Versio.TestSupport;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// The loader the web viewer and the desktop's History panel share: a file list and a reader
/// in, the tracked projects at that version out.
/// </summary>
/// <remarks>
/// <para>
/// A dictionary stands in for GitHub's blobs and for <c>git show</c>. The wordings asserted
/// below are the ones the web page showed before the loader moved out of
/// <c>RepoModelLoader</c> on 2026-10-27; <c>ViewServiceTests</c> asserts the same strings
/// through the web, which is how a change to either side is noticed.
/// </para>
/// <para>
/// The reader is counted as well as answered: a host pays one round trip (or one git spawn)
/// per read, so a loader that asked for a path it was not told exists, or asked twice, would be
/// a cost nothing else measures.
/// </para>
/// </remarks>
public sealed class ProjectSnapshotLoaderTests
{
    private const string ConfigPath = ".versio/repo.json";
    private const string ManifestPath = "plc/Machine/_manifest.json";
    private const string MotorPath = "plc/Machine/App/FB_Motor.st";
    private const string CatalogPath = "plc/Machine/App/Library Manager.libraries.yaml";

    private const string Config = """
        { "schema": 1, "bindings": [ { "id": "abc123", "projectPath": "plc/Machine.project", "exportPath": "plc/Machine" } ] }
        """;

    private const string Manifest = """
        {
          "schema": 3, "bindingId": "abc123", "project": "Machine.project", "objectCount": 2,
          "objects": [
            { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_DECL", "path": "App/FB_Motor.st" },
            { "guid": "g2", "name": "Library Manager", "type": "LIBRARY_MANAGER", "form": "LIBRARIES", "path": "App/Library Manager.libraries.yaml" }
          ],
          "skipped": [], "warnings": []
        }
        """;

    private static readonly string[] TrackedFiles = [ConfigPath, "plc/Machine.project", ManifestPath, MotorPath, CatalogPath, "README.md"];

    /// <summary>The real exporter's catalog, so the parse below is the one the panel does.</summary>
    private static string CatalogYaml() =>
        File.ReadAllText(Path.Combine(TestPaths.Project("RefrigeratorControl"), "Device", "Plc Logic", "Application", "Library Manager.libraries.yaml"));

    private static Dictionary<string, SnapshotRead> Tracked() => new(StringComparer.Ordinal)
    {
        [ConfigPath] = SnapshotRead.Of(Config),
        [ManifestPath] = SnapshotRead.Of(Manifest),
        [CatalogPath] = SnapshotRead.Of(CatalogYaml()),
    };

    private static async Task<(ProjectSnapshot Snapshot, List<string> Asked)> Load(
        IReadOnlyList<string> files, Dictionary<string, SnapshotRead> texts, string state = "tracked")
    {
        var asked = new List<string>();
        Task<SnapshotRead> Read(string path)
        {
            lock (asked)
            {
                asked.Add(path);
            }

            return Task.FromResult(texts.TryGetValue(path, out var found) ? found : SnapshotRead.Absent);
        }

        var snapshot = await ProjectSnapshotLoader.LoadAsync(files, Read, state, NullLogger.Instance, LogCategory.App, "test", CancellationToken.None);
        return (snapshot, asked);
    }

    [Fact]
    public async Task A_tracked_project_is_read_from_its_config_manifest_and_catalogs_at_that_version()
    {
        var (snapshot, asked) = await Load(TrackedFiles, Tracked(), "as of 1a2b3c4");

        var project = Assert.Single(snapshot.Projects);
        Assert.Equal(("plc/Machine.project", "plc/Machine", "as of 1a2b3c4"), (project.ProjectPath, project.ExportPath, project.ExportState));
        Assert.Equal(2, project.Manifest!.Objects.Count);
        Assert.Equal(CatalogPath, Assert.Single(project.Libraries).Path);
        Assert.Equal("FB_Motor", snapshot.Objects.Lookup(MotorPath)?.Name);
        Assert.True(snapshot.HasConfig);
        Assert.True(snapshot.IsVersio);
        Assert.Empty(snapshot.Problems);
        // Config, manifest, catalog: one read each, and never the object or the README.
        Assert.Equal([ConfigPath, ManifestPath, CatalogPath], asked.Order());
    }

    [Fact]
    public async Task A_binding_never_exported_at_that_version_is_a_project_without_a_manifest_and_a_problem()
    {
        var files = TrackedFiles.Where(f => f != ManifestPath && f != CatalogPath).ToList();

        var (snapshot, asked) = await Load(files, Tracked());

        var project = Assert.Single(snapshot.Projects);
        Assert.Null(project.Manifest);
        Assert.Contains(snapshot.Problems, p => p.Contains("plc/Machine.project has never been exported here", StringComparison.Ordinal));
        // The manifest is not in the list, so it is not asked for: "absent" is decided from the
        // list, never by a read that fails.
        Assert.Equal([ConfigPath], asked);
    }

    [Fact]
    public async Task A_manifest_the_reader_could_not_read_says_why()
    {
        var texts = Tracked();
        texts[ManifestPath] = SnapshotRead.Failed("RateLimited");

        var (snapshot, _) = await Load(TrackedFiles, texts);

        Assert.Null(Assert.Single(snapshot.Projects).Manifest);
        Assert.Contains($"{ManifestPath} could not be read (RateLimited).", snapshot.Problems);
    }

    [Fact]
    public async Task A_catalog_that_could_not_be_read_keeps_the_project_and_says_so()
    {
        var texts = Tracked();
        texts[CatalogPath] = SnapshotRead.Failed("TooLarge");

        var (snapshot, _) = await Load(TrackedFiles, texts);

        Assert.Empty(Assert.Single(snapshot.Projects).Libraries);
        Assert.Contains($"{CatalogPath} could not be read (TooLarge).", snapshot.Problems);
    }

    [Fact]
    public async Task A_null_bindings_list_is_read_as_empty_and_said()
    {
        var texts = Tracked();
        texts[ConfigPath] = SnapshotRead.Of("""{ "schema": 1, "bindings": null }""");

        var (snapshot, _) = await Load(TrackedFiles, texts);

        Assert.Empty(snapshot.Projects);
        Assert.True(snapshot.HasConfig);
        Assert.Contains(snapshot.Problems, p => p.StartsWith($"{ConfigPath}: 'bindings' is null", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_corrupt_config_is_shown_as_plain_folders_with_the_reason()
    {
        var texts = Tracked();
        texts[ConfigPath] = SnapshotRead.Of("null");

        var (snapshot, _) = await Load(TrackedFiles, texts);

        Assert.Empty(snapshot.Projects);
        Assert.Contains(snapshot.Problems, p => p.Contains("could not be read as a Versio configuration (the file holds null, not a JSON object)", StringComparison.Ordinal));
        // The file is there, so the repository is set up for Versio even while it is unreadable:
        // the page says why instead of pretending nobody adopted anything.
        Assert.True(snapshot.IsVersio);
    }

    private const string ManifestWithoutMotor = """
        {
          "schema": 3, "bindingId": "abc123", "project": "Machine.project", "objectCount": 1,
          "objects": [
            { "guid": "g2", "name": "Library Manager", "type": "LIBRARY_MANAGER", "form": "LIBRARIES", "path": "App/Library Manager.libraries.yaml" }
          ],
          "skipped": [], "warnings": []
        }
        """;

    /// <summary>The version after the deletion: the file and its manifest entry are gone.</summary>
    private static async Task<ProjectSnapshot> AfterDeletingMotor()
    {
        var texts = Tracked();
        texts[ManifestPath] = SnapshotRead.Of(ManifestWithoutMotor);
        var (snapshot, _) = await Load(TrackedFiles.Where(f => f != MotorPath).ToList(), texts);
        return snapshot;
    }

    private static (Func<string, Task<SnapshotRead>> Read, List<string> Asked) ParentWith(string manifest)
    {
        var asked = new List<string>();
        return (path =>
        {
            asked.Add(path);
            return Task.FromResult(path == ManifestPath ? SnapshotRead.Of(manifest) : SnapshotRead.Absent);
        }, asked);
    }

    private static Task<ProjectSnapshot> Named(ProjectSnapshot snapshot, IReadOnlyList<string> gone, Func<string, Task<SnapshotRead>> parent) =>
        ProjectSnapshotLoader.WithDeletedObjectsAsync(snapshot, gone, parent, NullLogger.Instance, LogCategory.App, "test", CancellationToken.None);

    /// <summary>
    /// The row a deleting commit could not name: this version's manifest no longer lists the
    /// object, so its name, type and vendor come from the parent's, which did.
    /// </summary>
    [Fact]
    public async Task A_deleted_object_keeps_its_name_and_type_from_the_parents_manifest()
    {
        var snapshot = await AfterDeletingMotor();
        var (parent, asked) = ParentWith(Manifest);

        var named = await Named(snapshot, [MotorPath], parent);

        var project = Assert.Single(named.Projects);
        Assert.Contains(project.Manifest!.Objects, o => o.Name == "FB_Motor" && o.Type == "POU");
        Assert.Contains(project.Manifest.Objects, o => o.Name == "Library Manager");
        // Exactly one read: the parent's manifest, nothing else of the parent.
        Assert.Equal([ManifestPath], asked);

        // And the tree draws it as the object it was, struck.
        var nodes = RepoExplorerBuilder.Build(
            snapshot.Files, named.Projects,
            new ProjectTreeOptions { ChangedOnly = true, HideUnchangedProjects = true },
            changes: [new Versio.Core.Git.FileChange { Path = MotorPath, Staged = Versio.Core.Git.ChangeKind.Deleted, Unstaged = Versio.Core.Git.ChangeKind.Unmodified }]);
        var motor = Assert.Single(Flatten(nodes), n => n.Name == "FB_Motor");
        Assert.Equal((ProjectNodeKind.Object, Versio.Core.Git.ChangeKind.Deleted, "POU"), (motor.Kind, motor.Status, motor.Type));
    }

    [Fact]
    public async Task Nothing_deleted_under_an_export_reads_nothing_of_the_parent()
    {
        var snapshot = await AfterDeletingMotor();
        var (parent, asked) = ParentWith(Manifest);

        Assert.Same(snapshot, await Named(snapshot, [], parent));
        Assert.Same(snapshot, await Named(snapshot, ["README.md"], parent));
        Assert.Empty(asked);
    }

    [Fact]
    public async Task A_deleted_file_the_parent_never_named_stays_a_path()
    {
        var snapshot = await AfterDeletingMotor();
        var (parent, asked) = ParentWith(ManifestWithoutMotor);

        var named = await Named(snapshot, [MotorPath], parent);

        Assert.DoesNotContain(Assert.Single(named.Projects).Manifest!.Objects, o => o.Name == "FB_Motor");
        Assert.Equal([ManifestPath], asked);
        Assert.Empty(named.Problems);
    }

    [Fact]
    public async Task A_parent_manifest_that_cannot_be_read_is_a_problem_and_the_path_stays()
    {
        var snapshot = await AfterDeletingMotor();
        var asked = new List<string>();
        Task<SnapshotRead> Parent(string path)
        {
            asked.Add(path);
            return Task.FromResult(SnapshotRead.Failed("RateLimited"));
        }

        var named = await Named(snapshot, [MotorPath], Parent);

        Assert.Contains(named.Problems, p => p.Contains($"{ManifestPath} at the parent could not be read (RateLimited)", StringComparison.Ordinal));
        Assert.DoesNotContain(Assert.Single(named.Projects).Manifest!.Objects, o => o.Name == "FB_Motor");
    }

    private static IEnumerable<ProjectTreeNode> Flatten(IEnumerable<ProjectTreeNode> nodes) =>
        nodes.SelectMany(n => Flatten(n.Children).Prepend(n));

    [Fact]
    public async Task A_source_project_with_no_config_is_described_from_its_files_without_a_read()
    {
        string[] files = ["Fs/Pump.fbsproj/PLC_PRG.st", "Fs/Pump.fbsproj/.folder_meta.json", "docs/README.md"];

        var (snapshot, asked) = await Load(files, []);

        var project = Assert.Single(snapshot.Projects);
        Assert.Equal(("Fs/Pump.fbsproj", "read from its files"), (project.ProjectPath, project.ExportState));
        Assert.Contains(project.Manifest!.Objects, o => o.Name == "PLC_PRG");
        Assert.False(snapshot.HasConfig);
        Assert.True(snapshot.IsVersio);
        // Described from the list alone: a vendor's source folder costs no read at all.
        Assert.Empty(asked);
    }
}
