using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// The project tree: the export's manifest and git's status, assembled into the shape the
/// engineer works in.
/// </summary>
/// <remarks>
/// The roll-up rules get most of the attention here because they are the part most likely to
/// be quietly wrong: a folder that under-reports its descendants hides a change behind a
/// collapsed node, which looks exactly like no change at all.
/// </remarks>
public sealed class ProjectTreeBuilderTests
{
    private const string Export = "plc/Machine";

    private static ProjectObject Obj(
        string name, string path, string guid, string type = "POU", string form = "ST_DECL") =>
        new() { Name = name, Path = path, Guid = guid, Type = type, Form = form };

    private static ProjectManifest Manifest(params ProjectObject[] objects) =>
        new() { Project = "Machine.project", Objects = objects };

    private static FileChange Change(string path, ChangeKind kind) =>
        new() { Path = path, Staged = ChangeKind.Unmodified, Unstaged = kind };

    private static ProjectTreeNode Build(
        ProjectManifest manifest, IReadOnlyList<FileChange> changes, bool changedOnly = false,
        string search = "") =>
        ProjectTreeBuilder.Build(manifest, Export, changes,
            new ProjectTreeOptions { ChangedOnly = changedOnly, Search = search });

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

    private static ProjectTreeNode Find(ProjectTreeNode root, string name) =>
        Flatten(root).First(n => n.Name == name);

    [Fact]
    public void Folders_come_from_the_export_paths()
    {
        var tree = Build(Manifest(
            Obj("Main", "Device/Plc Logic/Application/Main.st", "g1"),
            Obj("Helper", "Device/Plc Logic/Library/Helper.st", "g2")), []);

        var device = Assert.Single(tree.Children);
        Assert.Equal("Device", device.Name);
        Assert.True(device.IsFolder);

        var logic = Assert.Single(device.Children);
        Assert.Equal("Plc Logic", logic.Name);
        Assert.Equal(["Application", "Library"], logic.Children.Select(c => c.Name));
    }

    [Fact]
    public void An_object_carries_its_type_as_a_reader_writes_it()
    {
        // The tree drew the raw type lowercased - "method", "gvl_persistent" - while the
        // vocabulary that names it sat unused beside it. IEC's words need no vendor pack.
        var tree = Build(Manifest(Obj("Start", "App/FB/Start.st", "g1", "METHOD")), []);

        var node = Find(tree, "Start");
        Assert.Equal("METHOD", node.Type);
        Assert.Equal("Method", node.TypeName);
    }

    [Fact]
    public void An_object_the_export_could_not_write_carries_the_warning_and_a_note_does_not()
    {
        // A refused file is the previous export or nothing; the row must say so where the
        // reader looks, not only in the log (the owner's refused OB, 2026-10-30). A note —
        // an unknown type GUID — is not a refusal: the file is there and is this export's.
        var manifest = Manifest(
            Obj("Main", "Device/App/Main.st", "g1"),
            Obj("Helper", "Device/App/Helper.st", "g2")) with
        {
            Warnings =
            [
                new ExportWarning { Code = "XML_EXPORT_FAILED", Path = "Device/App/Main.st", Detail = "Inconsistent blocks cannot be exported." },
                new ExportWarning { Code = "UNKNOWN_TYPE_GUID", Path = "Device/App/Helper.st", Detail = "{guid}" },
            ],
        };

        var tree = Build(manifest, []);

        Assert.Equal("Inconsistent blocks cannot be exported.", Find(tree, "Main").ExportWarning);
        Assert.Null(Find(tree, "Helper").ExportWarning);
    }

    [Fact]
    public void An_unknown_type_is_named_exactly_as_the_exporter_wrote_it()
    {
        // Fall back, never fabricate - ObjectTypeNames' rule, reaching the tree unchanged.
        var tree = Build(Manifest(Obj("Thing", "App/Thing.xml", "g1", "SOME_FUTURE_TYPE")), []);

        Assert.Equal("SOME_FUTURE_TYPE", Find(tree, "Thing").TypeName);
    }

    [Fact]
    public void The_root_is_named_after_the_project_not_the_directory()
    {
        var tree = Build(Manifest(Obj("Main", "Main.st", "g1")), []);

        Assert.Equal("Machine.project", tree.Name);
    }

    [Fact]
    public void One_object_with_two_layers_is_one_node()
    {
        // A GVL exports both .gvl.st and .gvl.xml. Two rows would invite the question of
        // which one is the real object.
        var tree = Build(Manifest(
            Obj("Glob_Var", "App/Glob_Var.gvl.st", "same-guid", "GVL"),
            Obj("Glob_Var", "App/Glob_Var.gvl.xml", "same-guid", "GVL", "NATIVE")), []);

        var node = Assert.Single(Flatten(tree), n => !n.IsFolder);
        Assert.Equal("Glob_Var", node.Name);
        Assert.Equal(2, node.Files.Count);
        Assert.Contains("plc/Machine/App/Glob_Var.gvl.st", node.Files);
        Assert.Contains("plc/Machine/App/Glob_Var.gvl.xml", node.Files);
    }

    [Fact]
    public void An_object_is_changed_when_any_of_its_layers_is()
    {
        // Editing the .st and leaving the .xml alone still means the object changed. A node
        // reporting the quieter of its two files would hide it.
        var tree = Build(
            Manifest(
                Obj("Glob_Var", "App/Glob_Var.gvl.st", "g", "GVL"),
                Obj("Glob_Var", "App/Glob_Var.gvl.xml", "g", "GVL", "NATIVE")),
            [Change("plc/Machine/App/Glob_Var.gvl.st", ChangeKind.Modified)]);

        Assert.Equal(ChangeKind.Modified, Find(tree, "Glob_Var").Status);
    }

    [Fact]
    public void Git_status_is_matched_through_the_export_path()
    {
        // The manifest is relative to the export root; git is relative to the repository.
        // Getting this wrong silently produces a tree where nothing ever looks changed.
        var tree = Build(
            Manifest(Obj("Main", "Device/Application/Main.st", "g1")),
            [Change("plc/Machine/Device/Application/Main.st", ChangeKind.Modified)]);

        Assert.Equal(ChangeKind.Modified, Find(tree, "Main").Status);
    }

    [Fact]
    public void A_folder_counts_every_changed_descendant_not_just_its_children()
    {
        // The whole point of the badge: a change is never hidden behind a collapsed node.
        var tree = Build(
            Manifest(
                Obj("A", "Device/App/Deep/A.st", "g1"),
                Obj("B", "Device/App/Deep/B.st", "g2"),
                Obj("C", "Device/App/C.st", "g3")),
            [
                Change("plc/Machine/Device/App/Deep/A.st", ChangeKind.Modified),
                Change("plc/Machine/Device/App/Deep/B.st", ChangeKind.Added),
            ]);

        Assert.Equal(2, Find(tree, "Device").ChangedDescendants);
        Assert.Equal(2, Find(tree, "App").ChangedDescendants);
        Assert.Equal(2, Find(tree, "Deep").ChangedDescendants);
        Assert.Equal(2, tree.ChangedDescendants);
    }

    [Fact]
    public void A_folder_badge_shows_the_state_that_deserves_most_attention()
    {
        var tree = Build(
            Manifest(
                Obj("Edited", "App/Edited.st", "g1"),
                Obj("Gone", "App/Gone.st", "g2")),
            [
                Change("plc/Machine/App/Edited.st", ChangeKind.Modified),
                Change("plc/Machine/App/Gone.st", ChangeKind.Deleted),
            ]);

        // A deletion outranks an edit: it is the one somebody might not have meant.
        Assert.Equal(ChangeKind.Deleted, Find(tree, "App").RollUp);
    }

    [Fact]
    public void A_conflict_outranks_everything()
    {
        var tree = Build(
            Manifest(
                Obj("Clash", "App/Clash.st", "g1"),
                Obj("Gone", "App/Gone.st", "g2")),
            [
                new FileChange
                {
                    Path = "plc/Machine/App/Clash.st",
                    Staged = ChangeKind.Conflicted,
                    Unstaged = ChangeKind.Conflicted,
                },
                Change("plc/Machine/App/Gone.st", ChangeKind.Deleted),
            ]);

        Assert.Equal(ChangeKind.Conflicted, Find(tree, "App").RollUp);
    }

    [Fact]
    public void Nothing_changed_means_no_badge_anywhere()
    {
        var tree = Build(Manifest(
            Obj("A", "App/A.st", "g1"),
            Obj("B", "App/B.st", "g2")), []);

        Assert.All(Flatten(tree), n =>
        {
            Assert.Equal(0, n.ChangedDescendants);
            Assert.Equal(ChangeKind.Unmodified, n.RollUp);
            Assert.False(n.HasChanges);
        });
    }

    [Fact]
    public void Changed_only_keeps_the_path_to_a_change_and_drops_the_rest()
    {
        var tree = Build(
            Manifest(
                Obj("Touched", "Device/App/Touched.st", "g1"),
                Obj("Untouched", "Device/App/Untouched.st", "g2"),
                Obj("Elsewhere", "Device/Other/Elsewhere.st", "g3")),
            [Change("plc/Machine/Device/App/Touched.st", ChangeKind.Modified)],
            changedOnly: true);

        var names = Flatten(tree).Select(n => n.Name).ToList();

        Assert.Contains("Device", names);
        Assert.Contains("App", names);
        Assert.Contains("Touched", names);

        // The folder with nothing in it goes too - keeping empty folders would rebuild the
        // full tree one level at a time and defeat the filter.
        Assert.DoesNotContain("Untouched", names);
        Assert.DoesNotContain("Other", names);
        Assert.DoesNotContain("Elsewhere", names);
    }

    [Fact]
    public void Changed_only_on_a_clean_tree_leaves_a_root_and_nothing_else()
    {
        var tree = Build(Manifest(Obj("A", "App/A.st", "g1")), [], changedOnly: true);

        Assert.Empty(tree.Children);
        Assert.False(tree.HasChanges);
    }

    [Fact]
    public void Search_matches_object_names_and_keeps_their_folders()
    {
        var tree = Build(
            Manifest(
                Obj("Pump", "Device/App/Pump.st", "g1"),
                Obj("Valve", "Device/App/Valve.st", "g2")),
            [], search: "pum");

        var leaves = Flatten(tree).Where(n => !n.IsFolder).ToList();
        Assert.Equal("Pump", Assert.Single(leaves).Name);
        Assert.Contains(Flatten(tree), n => n.Name == "App");
    }

    [Fact]
    public void A_malformed_manifest_yields_an_empty_tree_rather_than_a_crash()
    {
        // Read to draw a tree. It can be mid-write, truncated by a killed export, or written
        // by a newer Versio, and none of those are worth taking the window down for.
        Assert.Empty(ProjectManifest.Parse("{ not json").Objects);
        Assert.Empty(ProjectManifest.Parse(string.Empty).Objects);

        var tree = Build(ProjectManifest.Parse("{ not json"), []);
        Assert.Empty(tree.Children);
    }

    [Fact]
    public void A_real_manifest_round_trips_through_the_parser()
    {
        // The shape versio_export.py actually writes, keys and all.
        const string json = """
        {
          "schema": 1,
          "bindingId": "6d3f8855e205",
          "project": "RefrigeratorControl.project",
          "objectCount": 2,
          "objects": [
            { "guid": "f8cc", "name": "Glob_Var", "type": "GVL", "form": "ST_DECL",
              "path": "Device/Plc Logic/Application/Glob_Var.gvl.st",
              "hash": "sha256:abc", "bytes": 238 },
            { "guid": "f8cc", "name": "Glob_Var", "type": "GVL", "form": "NATIVE",
              "path": "Device/Plc Logic/Application/Glob_Var.gvl.xml",
              "hash": "sha256:def", "bytes": 2550 }
          ],
          "warnings": []
        }
        """;

        var manifest = ProjectManifest.Parse(json);
        Assert.Equal("6d3f8855e205", manifest.BindingId);
        Assert.Equal(2, manifest.Objects.Count);

        var tree = Build(manifest, []);
        Assert.Equal("RefrigeratorControl.project", tree.Name);

        var node = Assert.Single(Flatten(tree), n => !n.IsFolder);
        Assert.Equal("GVL", node.Type);
        Assert.Equal(2, node.Files.Count);
    }

    [Fact]
    public void The_tree_is_stable_whatever_order_the_manifest_arrived_in()
    {
        // The export walks the project in whatever order CODESYS hands it over. A tree that
        // reordered itself between exports would show spurious movement in every diff.
        ProjectObject[] objects =
        [
            Obj("Zeta", "App/Zeta.st", "g1"),
            Obj("alpha", "App/alpha.st", "g2"),
            Obj("Mid", "Other/Mid.st", "g3"),
        ];

        var forward = Build(Manifest(objects), []);
        var reversed = Build(Manifest([.. objects.Reverse()]), []);

        Assert.Equal(
            Flatten(forward).Select(n => n.Path),
            Flatten(reversed).Select(n => n.Path));
    }
}
