using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// Project-level objects grouped under an explicit POUs node.
/// </summary>
/// <remarks>
/// The fixture's pool Library Manager rendered flat at the root read as a duplicate of the
/// application's — the product owner read it exactly that way, twice, and CODESYS's own
/// answer is a separate pane named POUs. The group is that statement made in Versio's one
/// tree; these tests pin when it appears and, just as deliberately, when it must not.
/// </remarks>
public sealed class ProjectTreePoolTests
{
    private const string Export = "plc/Machine";

    private static ProjectObject Obj(string name, string path, string guid) => new()
    {
        Name = name,
        Path = path,
        Guid = guid,
        Type = "POU",
        Form = "ST_FULL",
    };

    private static ProjectTreeNode Build(params ProjectObject[] objects) =>
        ProjectTreeBuilder.Build(
            new ProjectManifest { Schema = 5, Project = "Machine.project", Objects = objects },
            Export, [], new ProjectTreeOptions { ChangedOnly = false });

    [Fact]
    public void A_root_level_object_beside_foldered_ones_sits_under_POUs()
    {
        var tree = Build(
            Obj("Main", "Device/App/Main.st", "main"),
            Obj("GlobalTextList", "GlobalTextList.xml", "gtl"));

        var pool = Assert.Single(tree.Children,
            c => c.Name == ProjectTreeBuilder.PoolGroupName);
        Assert.True(pool.IsFolder);
        Assert.Equal("GlobalTextList", Assert.Single(pool.Children).Name);

        // The node's path stays the real file path — the group is presentation, and the
        // diff pane, search and expansion all key on paths that exist.
        Assert.Equal($"{Export}/GlobalTextList.xml", pool.Children[0].Path);
    }

    [Fact]
    public void A_flat_manifest_is_not_a_pool()
    {
        // Pre-schema-4 manifests carry no folders at all. Grouping those would file the
        // whole project under POUs, which is worse than flat.
        var tree = Build(
            Obj("Main", "Main.st", "main"),
            Obj("Helper", "Helper.st", "helper"));

        Assert.DoesNotContain(tree.Children,
            c => c.Name == ProjectTreeBuilder.PoolGroupName);
    }

    [Fact]
    public void A_changed_pool_object_still_raises_the_badge_through_the_group()
    {
        var tree = ProjectTreeBuilder.Build(
            new ProjectManifest
            {
                Schema = 5,
                Project = "Machine.project",
                Objects =
                [
                    Obj("Main", "Device/App/Main.st", "main"),
                    Obj("GlobalTextList", "GlobalTextList.xml", "gtl"),
                ],
            },
            Export,
            [new FileChange
            {
                Path = $"{Export}/GlobalTextList.xml",
                Staged = ChangeKind.Unmodified,
                Unstaged = ChangeKind.Modified,
            }],
            new ProjectTreeOptions { ChangedOnly = false });

        var pool = Assert.Single(tree.Children,
            c => c.Name == ProjectTreeBuilder.PoolGroupName);
        Assert.Equal(1, pool.ChangedDescendants);
        Assert.Equal(ChangeKind.Modified, pool.RollUp);
    }
}
