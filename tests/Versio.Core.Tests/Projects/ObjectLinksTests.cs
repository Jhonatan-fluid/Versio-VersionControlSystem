using Versio.Core.Diff;
using Versio.Core.Iec;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// Links between structures by name (docs/web/FINISH.md, Step 3c): an identifier in one object's
/// source opens the object of that name in the same project; the names a source refers to are
/// its words outside comments and strings.
/// </summary>
public sealed class ObjectLinksTests
{
    private static ProjectObject Obj(string path, string name, string type, string guid) =>
        new() { Path = path, Name = name, Type = type, Guid = guid, Form = "ST_FULL" };

    private static WorkspaceProject Project(string projectPath, string exportPath, string projectName, params ProjectObject[] objects) =>
        new() { ProjectPath = projectPath, ExportPath = exportPath, Manifest = new ProjectManifest { Schema = 4, Project = projectName, Objects = objects } };

    private static readonly ObjectIndex Index = ObjectIndex.Of([
        Project("plc/Machine.project", "plc/Machine", "Machine.project",
            Obj("App/FB_Motor.st", "FB_Motor", "POU", "g1"),
            Obj("App/FB_Pump.st", "FB_Pump", "POU", "g2"),
            Obj("App/GVL.gvl.st", "GVL", "GVL", "g3"),
            Obj("App/GVL.gvl.xml", "GVL", "GVL", "g3")),
        Project("plc/Other.project", "plc/Other", "Other.project",
            Obj("App/FB_Valve.st", "FB_Valve", "POU", "g9")),
    ]);

    [Fact]
    public void An_identifier_links_to_the_object_of_that_name_in_the_same_project_and_never_to_itself_or_another_project()
    {
        var links = ObjectLinks.Resolver(Index, "plc/Machine/App/FB_Motor.st", files => files.Paths[0])!;

        Assert.Equal("plc/Machine/App/FB_Pump.st", links("FB_Pump"));
        // IEC names are case-insensitive; the GVL's first file is its .st.
        Assert.Equal("plc/Machine/App/GVL.gvl.st", links("gvl"));
        Assert.Null(links("FB_Motor"));
        Assert.Null(links("FB_Valve"));
        Assert.Null(links("BOOL"));
        // A file no object owns links nothing at all.
        Assert.Null(ObjectLinks.Resolver(Index, "docs/notes.st", files => files.Paths[0]));
    }

    [Fact]
    public void The_source_carries_a_link_on_the_word_and_not_inside_a_comment_a_string_or_a_keyword()
    {
        var links = ObjectLinks.Resolver(Index, "plc/Machine/App/FB_Motor.st", files => files.Paths[0]);
        const string Text = "VAR\n\tpump : FB_Pump; // FB_Pump drives it\n\tname : STRING := 'FB_Pump';\nEND_VAR\nGVL.Running := TRUE;";

        var html = SourceHtml.Fragment(StSourceLayout.Lay(Text), LanguageCatalog.StructuredText, links: links);

        Assert.Equal(1, html.Split("data-link=\"plc/Machine/App/FB_Pump.st\"").Length - 1);
        Assert.Contains("<span class=\"cd-t-link\" data-link=\"plc/Machine/App/GVL.gvl.st\" title=\"Open plc/Machine/App/GVL.gvl.st\">GVL</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-link=\"plc/Machine/App/FB_Motor.st\"", html, StringComparison.Ordinal);
        // Without a resolver the fragment is as it always was.
        Assert.DoesNotContain("data-link", SourceHtml.Fragment(StSourceLayout.Lay(Text), LanguageCatalog.StructuredText), StringComparison.Ordinal);
    }

    [Fact]
    public void The_names_a_source_refers_to_are_its_plain_words_outside_comments_and_strings()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FB_Pump", "FB_Motor", "GVL", "FB_Valve" };
        const string Text = "pump : fb_pump; (* FB_Valve is not used *)\nGVL.Running := 'FB_Motor';";

        var names = ObjectLinks.NamesReferenced(Text, LanguageCatalog.StructuredText, candidates);

        Assert.Equal(["fb_pump", "GVL"], names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(ObjectLinks.NamesReferenced("", LanguageCatalog.StructuredText, candidates));
    }

    [Fact]
    public void A_projects_objects_are_listed_once_each_with_their_files()
    {
        var objects = Index.ObjectsIn("Machine");

        Assert.Equal(["FB_Motor", "FB_Pump", "GVL"], objects.Select(o => o.Object.Name));
        Assert.Equal(["plc/Machine/App/GVL.gvl.st", "plc/Machine/App/GVL.gvl.xml"], objects[2].Files.Paths);
        Assert.Empty(Index.ObjectsIn("Nowhere"));
    }
}
