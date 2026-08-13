using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// Methods, actions, transitions and properties, drawn under the object that owns them.
/// </summary>
/// <remarks>
/// <para>
/// The export writes a member <i>beside</i> its owner — <c>clsLight.st</c> and
/// <c>clsLight.Light.xml</c> in one folder — so a tree built from paths alone put them
/// side by side. On a real project that reads as nonsense: one folder held <c>clsRoom</c>,
/// <c>clsBetterRoom</c>, <c>clsCellarRoom</c>, <c>clsCheapRoom</c> and four separate rows
/// called <c>InitRoom</c>, with nothing saying which room each belonged to.
/// </para>
/// <para>
/// Ownership comes from <see cref="ProjectObject.Parent"/>, written by manifest schema 4.
/// The fixture project this repository tests against has no members at all, which is why
/// none of this was visible for the life of the tree.
/// </para>
/// </remarks>
public sealed class ProjectTreeMembersTests
{
    private const string Export = "plc/Machine";
    private const string Folder = "Device/Plc Logic/Application";

    private static ProjectObject Obj(
        string name, string path, string guid, string type = "POU", string parent = "") =>
        new()
        {
            Name = name,
            Path = $"{Folder}/{path}",
            Guid = guid,
            Type = type,
            Form = "ST_DECL",
            Parent = parent,
        };

    private static ProjectManifest Manifest(params ProjectObject[] objects) =>
        new() { Schema = 4, Project = "Machine.project", Objects = objects };

    private static FileChange Change(string path, ChangeKind kind) =>
        new() { Path = $"{Export}/{Folder}/{path}", Staged = ChangeKind.Unmodified, Unstaged = kind };

    private static ProjectTreeNode Build(
        ProjectManifest manifest, IReadOnlyList<FileChange>? changes = null,
        bool changedOnly = false, string search = "") =>
        ProjectTreeBuilder.Build(manifest, Export, changes ?? [],
            new ProjectTreeOptions { ChangedOnly = changedOnly, Search = search });

    private static ProjectTreeNode Find(ProjectTreeNode node, string name) =>
        Flatten(node).First(n => n.Name == name);

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

    /// <summary>Two rooms, each with its own InitRoom — the shape that made this visible.</summary>
    private static ProjectManifest TwoRooms() => Manifest(
        Obj("clsRoom", "clsRoom.st", "room"),
        Obj("InitRoom", "clsRoom.InitRoom.st", "room-init", "METHOD", parent: "room"),
        Obj("clsCellarRoom", "clsCellarRoom.st", "cellar"),
        Obj("InitRoom", "clsCellarRoom.InitRoom.st", "cellar-init", "METHOD", parent: "cellar"));

    [Fact]
    public void A_method_is_a_child_of_its_function_block()
    {
        var app = Find(Build(TwoRooms()), "Application");

        Assert.Equal(["clsCellarRoom", "clsRoom"], app.Children.Select(c => c.Name));

        var room = Find(app, "clsRoom");
        Assert.Equal("InitRoom", Assert.Single(room.Children).Name);
        Assert.Equal("room-init", Assert.Single(room.Children).Guid);
    }

    [Fact]
    public void Members_nest_as_deep_as_the_project_does()
    {
        // A property owns its accessors, so this is a chain rather than one level. Written
        // as three generations because two would pass on a builder that only ever nests once.
        var tree = Build(Manifest(
            Obj("clsLight", "clsLight.st", "light"),
            Obj("Light", "clsLight.Light.xml", "prop", "PROPERTY", parent: "light"),
            Obj("Get", "clsLight.Light.Get.st", "get", "METHOD", parent: "prop")));

        var property = Assert.Single(Find(tree, "clsLight").Children);
        Assert.Equal("Light", property.Name);
        Assert.Equal("Get", Assert.Single(property.Children).Name);
    }

    [Fact]
    public void An_edited_method_raises_the_badge_on_the_block_holding_it()
    {
        // The reason nesting cannot be cosmetic: a collapsed function block has to say that
        // something under it changed, or the change hides behind a chevron.
        var tree = Build(TwoRooms(), [Change("clsCellarRoom.InitRoom.st", ChangeKind.Modified)]);

        var cellar = Find(tree, "clsCellarRoom");
        Assert.Equal(ChangeKind.Unmodified, cellar.Status);
        Assert.Equal(1, cellar.ChangedDescendants);
        Assert.Equal(ChangeKind.Modified, cellar.RollUp);
        Assert.True(cellar.HasChanges);

        // And the sibling that owns the other InitRoom is untouched by it.
        Assert.False(Find(tree, "clsRoom").HasChanges);
    }

    [Fact]
    public void Changed_only_keeps_an_unchanged_owner_for_the_sake_of_its_member()
    {
        // PruneUnchanged tested "is this a folder" and objects had no children until now, so
        // an unchanged block was treated as a leaf and dropped — taking the edited method
        // with it, which is a change vanishing from a panel about changes.
        var tree = Build(
            TwoRooms(),
            [Change("clsCellarRoom.InitRoom.st", ChangeKind.Modified)],
            changedOnly: true);

        var cellar = Assert.Single(Find(tree, "Application").Children);
        Assert.Equal("clsCellarRoom", cellar.Name);
        Assert.Equal("InitRoom", Assert.Single(cellar.Children).Name);
    }

    [Fact]
    public void A_member_whose_owner_is_not_in_the_manifest_still_appears()
    {
        // A truncated export, or one whose owner type this build skipped. The file is on
        // disk; dropping the only row that leads to it is the worst of the options.
        var tree = Build(Manifest(
            Obj("clsRoom", "clsRoom.st", "room"),
            Obj("Orphan", "clsGone.Orphan.st", "orphan", "METHOD", parent: "vanished")));

        Assert.Equal(["clsRoom", "Orphan"],
            Find(tree, "Application").Children.Select(c => c.Name));
    }

    [Fact]
    public void A_manifest_written_before_schema_4_keeps_the_flat_shape()
    {
        // No parents recorded is not "nothing is a member". An older mirror renders exactly
        // as it did, and starts nesting when the project is next exported.
        var tree = Build(new ProjectManifest
        {
            Schema = 3,
            Project = "Machine.project",
            Objects =
            [
                Obj("clsRoom", "clsRoom.st", "room"),
                Obj("InitRoom", "clsRoom.InitRoom.st", "init", "METHOD"),
            ],
        });

        Assert.Equal(["clsRoom", "InitRoom"],
            Find(tree, "Application").Children.Select(c => c.Name));
    }

    [Fact]
    public void An_owner_that_claims_its_own_member_does_not_hang_the_build()
    {
        // A manifest is a file on disk and can say anything. A cycle here would recurse
        // forever inside a background build, which presents as the panel never appearing.
        var tree = Build(Manifest(
            Obj("A", "A.st", "a", parent: "b"),
            Obj("B", "B.st", "b", parent: "a")));

        Assert.NotEmpty(Flatten(tree));
    }

    [Fact]
    public void Searching_for_a_method_keeps_the_block_that_owns_it()
    {
        // Filtering leaves before nesting would leave the match with no path to it.
        var app = Find(Build(TwoRooms(), search: "InitRoom"), "Application");

        Assert.Equal(["clsCellarRoom", "clsRoom"], app.Children.Select(c => c.Name));
        Assert.Equal("InitRoom", Assert.Single(Find(app, "clsRoom").Children).Name);
    }

    [Fact]
    public void Searching_for_a_block_keeps_everything_inside_it()
    {
        // A block whose name was typed and which then appeared with no methods reads as a
        // block that lost them.
        var app = Find(Build(TwoRooms(), search: "clsCellarRoom"), "Application");

        var cellar = Assert.Single(app.Children);
        Assert.Equal("clsCellarRoom", cellar.Name);
        Assert.Equal("InitRoom", Assert.Single(cellar.Children).Name);
    }
}
