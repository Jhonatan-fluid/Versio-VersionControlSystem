using Versio.Core.Git;
using Versio.Core.Projects;
using Versio.TestSupport;
using Xunit.Abstractions;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// The tree builder against a manifest CODESYS actually produced.
/// </summary>
/// <remarks>
/// The unit tests build manifests by hand, which proves the rules and not the reading. This
/// exports the fixture project for real and builds a tree from what comes out — the only way
/// to catch a manifest key that got renamed, a path separator that arrives the other way
/// round, or an object type nobody anticipated.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Toolchain", "Codesys")]
[Collection(CodesysCollection.Name)]
public sealed class ProjectTreeFromRealExportTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _repo = Path.Combine(
        Path.GetTempPath(), "Versio", "tree", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_repo))
            {
                foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(_repo, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [SkippableFact]
    public async Task A_real_export_builds_a_tree_shaped_like_the_project()
    {
        var install = new Versio.Plc.Codesys.Infrastructure.CodesysLocator()
            .FindInstalls().FirstOrDefault(i => i.Profiles.Count > 0);
        Skip.If(install is null, "no CODESYS installation with a profile");

        var fixture = TestPaths.Project("RefrigeratorControl.project");
        Skip.If(!File.Exists(fixture), "fixture project missing");

        Directory.CreateDirectory(Path.Combine(_repo, "plc"));
        File.Copy(fixture, Path.Combine(_repo, "plc", "RefrigeratorControl.project"));

        var runner = new Versio.Plc.Codesys.Infrastructure.LocalCodesysRunner(
            install!.ExecutablePath,
            new Versio.Plc.Codesys.Infrastructure.ScriptExtractor(),
            new TestLogger(output));

        var exportRoot = Path.Combine(_repo, "plc", "RefrigeratorControl");
        var result = await runner.RunAsync(
            new Versio.Plc.Codesys.CodesysJob
            {
                Operation = Versio.Plc.Codesys.CodesysOperation.Export,
                Profile = install.Profiles[0],
                Projects =
                [
                    new Versio.Plc.Codesys.ProjectJob
                    {
                        BindingId = "b-tree",
                        ProjectPath = Path.Combine(_repo, "plc", "RefrigeratorControl.project"),
                        Path = exportRoot,
                    },
                ],
            }, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);

        var manifestPath = Path.Combine(exportRoot, "_manifest.json");
        Assert.True(File.Exists(manifestPath), "the export wrote no manifest");

        var manifest = ProjectManifest.Parse(await File.ReadAllTextAsync(manifestPath));
        output.WriteLine($"{manifest.Objects.Count} file(s), {manifest.ObjectCount} object(s)");

        Assert.NotEmpty(manifest.Objects);
        Assert.All(manifest.Objects, o =>
        {
            Assert.False(string.IsNullOrWhiteSpace(o.Path), "a manifest record has no path");
            Assert.False(string.IsNullOrWhiteSpace(o.Type), $"{o.Path} has no type");
        });

        // Everything is new on disk, so the whole export reads as untracked - which is exactly
        // the state right after a first export, and the one the tree must handle.
        var changes = manifest.Objects
            .Select(o => new FileChange
            {
                Path = "plc/RefrigeratorControl/" + o.Path,
                Staged = ChangeKind.Untracked,
                Unstaged = ChangeKind.Untracked,
            })
            .ToList();

        var tree = ProjectTreeBuilder.Build(
            manifest, "plc/RefrigeratorControl", changes,
            new ProjectTreeOptions { ChangedOnly = true });

        var all = Flatten(tree).ToList();
        var objects = all.Where(n => !n.IsFolder).ToList();
        var folders = all.Where(n => n.IsFolder).ToList();

        foreach (var node in objects.OrderBy(n => n.Path, StringComparer.Ordinal))
        {
            output.WriteLine($"  {node.Type,-14} {node.Name,-20} {node.Files.Count} file(s)");
        }

        // The fixture is a device with an application under it, so the tree has real depth -
        // a builder that flattened everything to the root would still produce nodes.
        Assert.True(folders.Count > 1, "the tree has no folder structure");

        // One node per distinct GUID, and every manifest record accounted for as a layer.
        //
        // Note that "objectCount" in the manifest is a RECORD count, not an object count:
        // versio_export.py writes len(records), and there is one record per file. The two
        // differ exactly when an object exports to more than one layer, which is why this
        // asserts against the GUIDs rather than trusting the field's name.
        var distinctGuids = manifest.Objects.Select(o => o.Guid).Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(distinctGuids, objects.Count);
        Assert.Equal(manifest.ObjectCount, manifest.Objects.Count);
        Assert.Equal(manifest.Objects.Count, objects.Sum(n => n.Files.Count));

        // The GVL exports to both .st and .xml, and must be one node carrying two layers.
        Assert.Contains(objects, n => n.Files.Count > 1);

        // Every object is untracked, so every folder must roll that up.
        Assert.Equal(objects.Count, tree.ChangedDescendants);
        Assert.Equal(ChangeKind.Untracked, tree.RollUp);

        AMemberInAFolderStillBelongsToItsPou(manifest, all);
    }

    /// <summary>
    /// A method the IDE shows inside a folder inside a POU is exported as that POU's member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fixture carries the case on purpose</b>: <c>FB_Alarm</c> has <c>M_Reset</c>
    /// directly and <c>_M_Reset</c> inside a folder called <c>Private</c>, which is how the
    /// defect was reported. Removing either from <c>RefrigeratorControl.project</c> makes this
    /// fail rather than pass quietly, which is the point of naming them.
    /// </para>
    /// <para>
    /// <b>Only a real export can prove this half.</b> The rule lives in the walk inside
    /// <c>versio_export.py</c>, which cannot run without <c>scriptengine</c> — so unlike the
    /// importer's side (<c>PythonTreeWalkTests</c>, under a second) this one costs a CODESYS
    /// launch and there is no cheaper substitute. It rides along on the export this test has
    /// already paid for.
    /// </para>
    /// </remarks>
    private void AMemberInAFolderStillBelongsToItsPou(
        ProjectManifest manifest, IReadOnlyList<ProjectTreeNode> all)
    {
        var owner = manifest.Objects.Single(o => o.Name == "FB_Alarm");
        var foldered = manifest.Objects.Single(o => o.Name == "_M_Reset");
        var plain = manifest.Objects.Single(o => o.Name == "M_Reset");

        output.WriteLine($"owner    {owner.Guid} {owner.Path}");
        output.WriteLine($"foldered {foldered.Parent} {foldered.Path}");

        // The manifest records the owner, so the panel nests it and the planner resolves it
        // through the owner chain rather than through its directory.
        Assert.Equal(owner.Guid, foldered.Parent);

        // And it sits BESIDE its owner, named after it — the same shape as the unfoldered
        // member next to it, so re-organising the IDE tree produces no mirror diff at all.
        Assert.Equal(DirectoryOf(plain.Path), DirectoryOf(foldered.Path));
        Assert.Equal("FB_Alarm._M_Reset.st", Path.GetFileName(foldered.Path));

        // The folder leaves nothing behind. A `Private` directory in the mirror would mean
        // the walk still wrote it somewhere, whatever the manifest says.
        Assert.DoesNotContain(manifest.Objects, o => o.Path.Contains("/Private/", StringComparison.Ordinal));

        // And the drawn tree agrees, which is what the owner actually looked at.
        var pou = all.Single(n => n.Name == "FB_Alarm");
        Assert.Contains(pou.Children, c => c.Name == "_M_Reset");
        Assert.Contains(pou.Children, c => c.Name == "M_Reset");
    }

    /// <summary>Not <c>System.IO</c>'s: a mirror path is forward-slashed, always.</summary>
    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static IEnumerable<ProjectTreeNode> Flatten(ProjectTreeNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }
}
