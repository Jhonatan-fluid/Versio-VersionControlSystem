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
