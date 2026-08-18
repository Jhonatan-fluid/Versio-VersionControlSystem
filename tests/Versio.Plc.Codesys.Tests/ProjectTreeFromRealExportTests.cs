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

        EveryMemberLivesInsideItsOwner(manifest, all);
    }

    /// <summary>
    /// The mirror path is the project path: every member inside its owner, folders and all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The RULE over whatever the project contains, not a list of names.</b> The first
    /// version of this named <c>FB_Alarm</c>, <c>M_Reset</c> and <c>_M_Reset</c> and pinned
    /// their paths, and it went red twice in one morning — once when the owner reorganised
    /// the real project into <c>Library/Alarm/POUs</c>, and once when they renamed a method.
    /// A test that fails because somebody used the tool for its purpose is a test that will
    /// be deleted, and it was measuring the fixture rather than the exporter.
    /// </para>
    /// <para>
    /// So it asserts the invariant every recorded member must satisfy, over all of them, and
    /// separately insists there ARE some — which is the half that keeps it honest. The
    /// specific shapes (a folder as a row, the order inside a POU) are pinned instantly and
    /// against a manifest it owns in <c>ProjectTreeMembersTests</c>; what only a real export
    /// can prove is that CODESYS's own tree comes out of the walk looking like this at all.
    /// </para>
    /// <para>
    /// <b>Only a real export can prove this half</b>: the rule lives in the walk inside
    /// <c>versio_export.py</c>, which cannot run without <c>scriptengine</c>. It rides along
    /// on the export this test has already paid for.
    /// </para>
    /// </remarks>
    private void EveryMemberLivesInsideItsOwner(
        ProjectManifest manifest, IReadOnlyList<ProjectTreeNode> all)
    {
        // The schema is what tells a reader which of the two layouts this mirror is in, so a
        // mirror in the new shape claiming the old number would be read wrong by the planner
        // and silently resolve to nothing.
        Assert.Equal(ExportCoverage.CurrentSchema, manifest.Schema);
        Assert.True(manifest.Schema >= ExportCoverage.PathIsChainSchema);

        var byGuid = manifest.Objects
            .GroupBy(o => o.Guid, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var members = manifest.Objects
            .Where(o => !string.IsNullOrEmpty(o.Parent))
            .ToList();

        // NON-VACUOUS. Every assertion below is over `members`, so an export that recorded no
        // owners at all would satisfy them by having nothing to check — which is precisely the
        // defect this whole layout came from.
        Assert.True(members.Count >= 3,
            $"the fixture must carry members for this to mean anything; it has {members.Count}");

        var foldered = 0;

        foreach (var member in members)
        {
            Assert.True(byGuid.TryGetValue(member.Parent, out var owner),
                $"{member.Path} names owner {member.Parent}, which the manifest does not record");

            // The owner's directory: its own file path with the extension taken off. Every
            // suffix this export writes is the tail of the file name, so cutting at the first
            // dot after the last slash is the owner's directory whatever its form.
            var slash = owner!.Path.LastIndexOf('/');
            var dot = owner.Path.IndexOf('.', slash + 1);
            var ownerDirectory = dot < 0 ? owner.Path : owner.Path[..dot];

            Assert.StartsWith(ownerDirectory + "/", member.Path, StringComparison.Ordinal);

            // Anything between the owner's directory and the file is a folder the engineer
            // made inside the POU, and it must be a row in the tree rather than part of a name.
            var inside = member.Path[(ownerDirectory.Length + 1)..];
            if (inside.Contains('/'))
            {
                foldered++;
                var folder = inside[..inside.IndexOf('/')];
                Assert.Contains(all, n => n.IsFolder && n.Name == folder
                                          && n.Path.EndsWith($"{ownerDirectory}/{folder}",
                                                             StringComparison.Ordinal));
            }

            // And no owner prefix survives anywhere: the directory says it, and saying it
            // twice would give FB_Alarm/FB_Alarm.M_Reset.st.
            var fileName = member.Path[(member.Path.LastIndexOf('/') + 1)..];
            Assert.False(fileName.StartsWith(owner.Name + ".", StringComparison.Ordinal),
                         $"{member.Path} still carries its owner's name as a prefix");
        }

        output.WriteLine($"{members.Count} member(s), {foldered} of them inside a folder");

        // At least one member really is inside a folder, or the folder half of the rule is
        // being asserted over an empty set. The fixture has carried two since 2026-08-17.
        Assert.True(foldered >= 1, "no member is inside a folder, so that rule proved nothing");

        // A member's node hangs off its owner's node, not off a directory that shares its name.
        foreach (var member in members.Take(5))
        {
            var owner = byGuid[member.Parent];
            var ownerNode = all.Single(n => n.Guid == owner.Guid);
            Assert.Contains(Flatten(ownerNode).Skip(1), n => n.Guid == member.Guid);
        }
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
