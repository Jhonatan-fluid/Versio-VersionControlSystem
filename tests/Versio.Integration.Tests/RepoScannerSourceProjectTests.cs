using Versio.Core.Plc;
using Versio.Infrastructure.Bindings;
using Versio.Plc.Codesys;
using Versio.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Integration.Tests;

/// <summary>
/// The scanner finding a source project: a folder its vendor saved as readable files, reported
/// apart from the projects Versio tracks, and never walked into.
/// </summary>
/// <remarks>
/// <para>
/// <b>Apart, because everything downstream of <c>ProjectFiles</c> tracks.</b> A folder that
/// reached that list would be auto-adopted, fail an export it could never run, and block every
/// commit in the repository. See <c>docs/fbs/PLAN.md</c> §1.
/// </para>
/// <para>
/// Real disk, the real CODESYS format, no git and no CODESYS.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RepoScannerSourceProjectTests(ITestOutputHelper output)
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "versio-source-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Write(string root, string relative, string text = "x")
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task A_source_project_is_reported_apart_from_tracked_projects_and_not_walked_into()
    {
        var root = NewRoot();
        try
        {
            Write(root, "plc/Classic.project");
            Write(root, "plc/Machine.fbsproj/Device.device.xml.v3");
            Write(root, "plc/Machine.fbsproj/Device.device.xml.v3^/PLC_PRG.prg.st");

            // A project file inside the folder must not be found: the folder is not walked, and
            // if it were, this would surface as a second, tracked project.
            Write(root, "plc/Machine.fbsproj/.auxiliaries/stray.project");

            var logger = new TestLogger(output);
            var snapshot = await new RepoScanner(root, [CodesysProjectFormat.Instance], logger)
                .ScanAsync(git: null, CancellationToken.None);

            Assert.Equal(["plc/Classic.project"], snapshot.ProjectFiles);
            Assert.Equal(["plc/Machine.fbsproj"], snapshot.SourceProjects);
            Assert.Contains(logger.Entries, e => e.Message.Contains("1 source project(s)", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_format_that_claims_no_source_extension_finds_none()
    {
        // The default. Every format Versio drove before File-Based Storage inherits it, and
        // must see exactly what it saw before.
        var root = NewRoot();
        try
        {
            Write(root, "plc/Machine.fbsproj/PLC_PRG.prg.st");

            var snapshot = await new RepoScanner(root, [new NoSourceFormat()]).ScanAsync(git: null, CancellationToken.None);

            Assert.Empty(snapshot.SourceProjects);
            Assert.Empty(snapshot.ProjectFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_registry_answers_for_a_folder_and_never_for_a_file()
    {
        PlcFormats.Register(CodesysProjectFormat.Instance);

        Assert.Same(CodesysProjectFormat.Instance, PlcFormats.ForSourceProject("plc/Machine.fbsproj"));
        Assert.Same(CodesysProjectFormat.Instance, PlcFormats.ForSourceProject("plc/Machine.FBSPROJ/"));
        Assert.Null(PlcFormats.ForSourceProject("plc/Machine.project"));

        // The other way round is the one that matters: nothing asking "is this a project to
        // track" may be told yes for a source project's folder.
        Assert.Null(PlcFormats.ForPath("plc/Machine.fbsproj"));
    }

    /// <summary>A format that inherits every default: it claims <c>.noproj</c> files and no folders.</summary>
    private sealed class NoSourceFormat : IPlcProjectFormat
    {
        public PlcVendor Vendor => new("NoSource");

        public string DisplayName => "No source";

        public PlcCapabilities Capabilities => new();

        public IReadOnlyList<string> ProjectExtensions => [".noproj"];

        public bool IsTemplate(string path) => false;

        public string ConventionalExportPath(string projectPath) => projectPath + ".export";

        public string? OccupancyMarkerFor(string projectPath) => null;

        public IReadOnlyList<string> TransientPatternsFor(string projectPath) => [];

        public ProjectOccupancy ParseOccupancy(string markerContents) => throw new NotSupportedException();

        public bool NeedsTextToPlan(string relativePath) => false;

        public ImportPlan PlanImport(
            Core.Projects.ProjectManifest? manifest,
            IReadOnlyList<MirrorFile> mirror,
            IReadOnlyDictionary<string, string> departed) => throw new NotSupportedException();

        public Core.Projects.ObjectTypeName? DisplayNameFor(string type) => null;
    }
}
