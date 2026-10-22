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
        //
        // Not hanging is half the contract. The other half: BOTH objects still appear.
        // The root set used to be "no parent, or parent not in the manifest", and every
        // member of a cycle has a parent that IS in the manifest — so none was a root,
        // Attach never ran for any of them, and the whole loop vanished from the panel
        // without the visited guard ever being consulted. This assertion was
        // Assert.NotEmpty(Flatten(tree)), which the root folder satisfies by itself.
        var tree = Build(Manifest(
            Obj("A", "A.st", "a", parent: "b"),
            Obj("B", "B.st", "b", parent: "a")));

        var rows = Flatten(tree).Select(n => n.Name).ToList();
        Assert.Contains("A", rows);
        Assert.Contains("B", rows);
    }

    [Fact]
    public void An_object_claiming_itself_as_owner_still_appears()
    {
        // The one-element cycle, same reasoning: its parent is in the manifest (it is
        // itself), so it qualified as nobody's root and vanished.
        var tree = Build(Manifest(
            Obj("clsRoom", "clsRoom.st", "room"),
            Obj("Loop", "Loop.st", "self", parent: "self")));

        var rows = Flatten(tree).Select(n => n.Name).ToList();
        Assert.Contains("clsRoom", rows);
        Assert.Contains("Loop", rows);
    }

    [Fact]
    public void A_cycle_beside_an_honest_member_still_keeps_every_object()
    {
        // The shape the first fix missed. Its fixtures were all-cycle, so the members
        // lookup came out empty and the early return handed every leaf back — the tests
        // were green while the root set still excluded cycle members whenever one honest
        // member existed, which is every real manifest (methods under their blocks).
        // Caught by an adversarial re-review that built exactly this manifest.
        var tree = Build(Manifest(
            Obj("clsRoom", "clsRoom.st", "room"),
            Obj("InitRoom", "clsRoom.InitRoom.st", "init", "METHOD", parent: "room"),
            Obj("A", "A.st", "a", parent: "b"),
            Obj("B", "B.st", "b", parent: "a")));

        var rows = Flatten(tree).Select(n => n.Name).ToList();
        Assert.Contains("A", rows);
        Assert.Contains("B", rows);

        // And the honest member still nests where it belongs.
        Assert.Equal("InitRoom",
            Assert.Single(Find(tree, "clsRoom").Children).Name);
    }

    [Fact]
    public void A_member_whose_owner_is_inside_a_cycle_still_appears()
    {
        // Its chain enters the loop and never reaches ground, so it cannot be nested
        // honestly. Flat and present beats nested and gone.
        var tree = Build(Manifest(
            Obj("A", "A.st", "a", parent: "b"),
            Obj("B", "B.st", "b", parent: "a"),
            Obj("C", "C.st", "c", parent: "a")));

        var rows = Flatten(tree).Select(n => n.Name).ToList();
        Assert.Contains("A", rows);
        Assert.Contains("B", rows);
        Assert.Contains("C", rows);
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

    /// <summary>
    /// A schema-9 owner: its members are in a directory named after it, and the folders the
    /// engineer made inside the POU are directories below that.
    /// </summary>
    /// <remarks>
    /// The shape the owner reported on 2026-08-18 — <c>FB_Alarm</c> with <c>Private</c> and
    /// <c>Protected</c> holding one method each, a plain method beside them and a property.
    /// </remarks>
    private static ProjectManifest FoldersInsideAPou() =>
        new()
        {
            Schema = 9,
            Project = "Machine.project",
            Objects =
            [
                Obj("FB_Alarm", "FB_Alarm.st", "fb"),
                Obj("M_Reset", "FB_Alarm/M_Reset.st", "m", "METHOD", "fb"),
                Obj("_M_Reset", "FB_Alarm/Private/_M_Reset.st", "p1", "METHOD", "fb"),
                Obj("_M_Protected", "FB_Alarm/Protected/_M_Protected.st", "p2", "METHOD", "fb"),
                Obj("P_Faulted", "FB_Alarm/P_Faulted.xml", "prop", "PROPERTY", "fb"),
            ],
        };

    [Fact]
    public void A_folder_inside_a_pou_is_a_row_inside_the_pou()
    {
        // What the owner asked for by name: "folders for private and protected are not shown".
        // Attaching members by GUID alone drew all four as loose methods of FB_Alarm, because
        // ownership and folder membership are different facts and only the first was read.
        var fb = Find(Build(FoldersInsideAPou()), "FB_Alarm");

        // FOLDERS FIRST, then the objects — the IDE's own order, measured on 3.5.22.20.
        Assert.Equal(["Private", "Protected", "M_Reset", "P_Faulted"],
                     fb.Children.Select(c => c.Name));

        var private_ = fb.Children[0];
        Assert.Equal(ProjectNodeKind.Folder, private_.Kind);
        Assert.Equal("_M_Reset", Assert.Single(private_.Children).Name);
        Assert.Equal("_M_Protected", Assert.Single(fb.Children[1].Children).Name);

        // The folder is a real path under the owner's directory, not a synthetic label: the
        // panel keys expansion state on it, so a made-up path collapses on every refresh.
        Assert.Equal($"{Export}/{Folder}/FB_Alarm/Private", private_.Path);
    }

    [Fact]
    public void A_change_inside_a_folder_inside_a_pou_still_raises_the_pou()
    {
        // Two levels of roll-up, which is the whole reason a badge exists. An edited method
        // two chevrons deep that left FB_Alarm quiet is a change nobody would find.
        var tree = Build(
            FoldersInsideAPou(),
            [Change("FB_Alarm/Private/_M_Reset.st", ChangeKind.Modified)]);

        var fb = Find(tree, "FB_Alarm");

        Assert.Equal(1, fb.ChangedDescendants);
        Assert.Equal(ChangeKind.Modified, fb.RollUp);
        Assert.Equal(ChangeKind.Modified, Find(tree, "Private").RollUp);
        Assert.Equal(ChangeKind.Modified, Find(tree, "Application").RollUp);
    }

    [Fact]
    public void A_pre_schema_9_member_beside_its_owner_gains_no_folder()
    {
        // The layout every committed mirror is in until it is next exported: the member sits
        // in the SAME directory as its owner, so there is nothing below it and the count of
        // folders comes out zero rather than negative. A tree that invented a folder here
        // would put every existing project behind a chevron that is not in the IDE.
        var tree = Build(new ProjectManifest
        {
            Schema = 8,
            Project = "Machine.project",
            Objects =
            [
                Obj("clsLight", "clsLight.st", "c"),
                Obj("Light", "clsLight.Light.xml", "l", "METHOD", "c"),
            ],
        });

        var light = Find(tree, "clsLight");
        Assert.Equal("Light", Assert.Single(light.Children).Name);
        Assert.DoesNotContain(Flatten(tree), n => n.Kind == ProjectNodeKind.Folder
                                                  && n.Name == "clsLight");
    }

    [Fact]
    public void A_blocks_methods_come_before_its_properties_each_in_name_order()
    {
        // The owner, 2026-10-22: one alphabetical run put Busy and P_Busy among the methods.
        // Anything that is not a property (an action, a vendor's other method spelling) stays
        // with the methods.
        var tree = Build(Manifest(
            Obj("FB_Logger", "FB_Logger.st", "fb"),
            Obj("Busy", "FB_Logger.Busy.st", "p1", "PROPERTY", parent: "fb"),
            Obj("WriteLog", "FB_Logger.WriteLog.st", "m1", "METHOD", parent: "fb"),
            Obj("Error", "FB_Logger.Error.st", "p2", "PROPERTY", parent: "fb"),
            Obj("Register", "FB_Logger.Register.st", "m2", "METHOD", parent: "fb"),
            Obj("Cycle", "FB_Logger.Cycle.st", "a1", "ACTION", parent: "fb")));

        Assert.Equal(
            ["Cycle", "Register", "WriteLog", "Busy", "Error"],
            Find(tree, "FB_Logger").Children.Select(c => c.Name));
    }
}
