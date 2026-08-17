using Versio.Core.Git;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// Resolving a changed path back to the object the export wrote it from.
/// </summary>
/// <remarks>
/// The rule under test that matters most is the negative one: a path the manifest does not
/// describe resolves to nothing and is shown as a path. Guessing a type from an extension would
/// label an engineer's <c>notes.st</c> as a POU, and a confidently wrong object name is worse
/// than the raw path — the path is at least what git is about to commit.
/// </remarks>
public sealed class ObjectIndexTests
{
    private static ProjectObject Obj(
        string path, string name, string type, string guid, string form = "ST_FULL") =>
        new() { Path = path, Name = name, Type = type, Guid = guid, Form = form };

    private static WorkspaceProject Project(
        string projectPath = "plc/Machine.project",
        string exportPath = "plc/Machine",
        string projectName = "Machine.project",
        params ProjectObject[] objects) =>
        new()
        {
            ProjectPath = projectPath,
            ExportPath = exportPath,
            Manifest = new ProjectManifest
            {
                Schema = 4,
                Project = projectName,
                Objects = objects,
            },
        };

    [Fact]
    public void An_exported_path_resolves_to_its_object()
    {
        var index = ObjectIndex.Of([
            Project(objects: Obj("App/FB_Motor.st", "FB_Motor", "POU", "g1")),
        ]);

        var found = index.Lookup("plc/Machine/App/FB_Motor.st");

        Assert.NotNull(found);
        Assert.Equal("FB_Motor", found.Name);
        Assert.Equal("POU", found.Type);
        Assert.Equal("POU", found.TypeLabel);
        Assert.Equal("Machine", found.Project);
    }

    [Fact]
    public void A_path_the_manifest_does_not_describe_resolves_to_nothing()
    {
        var index = ObjectIndex.Of([
            Project(objects: Obj("App/FB_Motor.st", "FB_Motor", "POU", "g1")),
        ]);

        // Under the export root, and still not an object: the manifest describes the others.
        Assert.Null(index.Lookup("plc/Machine/_manifest.json"));

        // An ordinary repository file that happens to look like exported source.
        Assert.Null(index.Lookup("docs/notes.st"));

        // Nothing to do with any project.
        Assert.Null(index.Lookup("README.md"));
    }

    [Fact]
    public void Both_layers_of_one_object_resolve_to_the_same_object()
    {
        var index = ObjectIndex.Of([
            Project(objects:
            [
                Obj("App/Glob_Var.gvl.st", "Glob_Var", "GVL", "g1", "GVL_BOTH"),
                Obj("App/Glob_Var.gvl.xml", "Glob_Var", "GVL", "g1", "GVL_BOTH"),
            ]),
        ]);

        var st = index.Lookup("plc/Machine/App/Glob_Var.gvl.st");
        var xml = index.Lookup("plc/Machine/App/Glob_Var.gvl.xml");

        Assert.Equal("Glob_Var", st?.Name);
        Assert.Equal("Glob_Var", xml?.Name);
        Assert.Equal(st?.Guid, xml?.Guid);
    }

    [Fact]
    public void A_library_reference_file_is_flagged_as_export_only()
    {
        var index = ObjectIndex.Of([
            Project(objects:
            [
                Obj("App/Library Manager.libraries.yaml", "Library Manager", "LIBRARY_MANAGER",
                    "g1", ExportCoverage.LibrariesForm),
                Obj("App/FB_Motor.st", "FB_Motor", "POU", "g2"),
            ]),
        ]);

        Assert.True(index.Lookup("plc/Machine/App/Library Manager.libraries.yaml")?.IsExportOnly);
        Assert.False(index.Lookup("plc/Machine/App/FB_Motor.st")?.IsExportOnly);
    }

    [Fact]
    public void The_native_half_of_a_task_configuration_is_flagged_and_its_yaml_half_is_not()
    {
        // The second export-only form, and the one that showed the first was being treated as
        // a special case. A task configuration writes two files under ONE guid: the .tasks.yaml
        // imports, the .xml does not — so the flag has to split a single object's files, which
        // it can only do because the manifest records a form per file rather than per object.
        //
        // The .xml carried NATIVE_R until 2026-08-15. That is a real, importable form — a
        // property's recursive export uses it — so the panel drew this file as editable and an
        // edit to it was discarded in silence. The FB_Motor.Execute.xml row is the one that
        // matters most: it is a real NATIVE_R, and it pins that the fix distinguished the two
        // rather than making every recursive native export look one-way.
        var index = ObjectIndex.Of([
            Project(objects:
            [
                Obj("App/Task Configuration.tasks.yaml", "Task Configuration",
                    "TASK_CONFIGURATION", "g1", "TASK_BOTH"),
                Obj("App/Task Configuration.xml", "Task Configuration",
                    "TASK_CONFIGURATION", "g1", ExportCoverage.TaskNativeForm),
                Obj("App/FB_Motor.Execute.xml", "Execute", "PROPERTY", "g2", "NATIVE_R"),
            ]),
        ]);

        Assert.True(index.Lookup("plc/Machine/App/Task Configuration.xml")?.IsExportOnly);
        Assert.False(index.Lookup("plc/Machine/App/Task Configuration.tasks.yaml")?.IsExportOnly);
        Assert.False(index.Lookup("plc/Machine/App/FB_Motor.Execute.xml")?.IsExportOnly);
    }

    [Fact]
    public void Backslashes_and_a_leading_slash_resolve_the_same_as_git_reports_them()
    {
        var index = ObjectIndex.Of([
            Project(objects: Obj("App/FB_Motor.st", "FB_Motor", "POU", "g1")),
        ]);

        Assert.NotNull(index.Lookup(@"plc\Machine\App\FB_Motor.st"));
        Assert.NotNull(index.Lookup("/plc/Machine/App/FB_Motor.st"));
    }

    [Fact]
    public void A_project_that_has_never_been_exported_contributes_nothing()
    {
        var index = ObjectIndex.Of([
            new WorkspaceProject
            {
                ProjectPath = "plc/Machine.project",
                ExportPath = "plc/Machine",
                Manifest = null,
            },
        ]);

        Assert.Equal(0, index.Count);
        Assert.Null(index.Lookup("plc/Machine/App/FB_Motor.st"));
    }

    [Fact]
    public void Two_projects_are_indexed_side_by_side()
    {
        var index = ObjectIndex.Of([
            Project("a/One.project", "a/One", "One.project", Obj("Main.st", "Main", "POU", "g1")),
            Project("b/Two.project", "b/Two", "Two.project", Obj("Main.st", "Main", "POU", "g2")),
        ]);

        Assert.Equal("One", index.Lookup("a/One/Main.st")?.Project);
        Assert.Equal("Two", index.Lookup("b/Two/Main.st")?.Project);
    }

    [Fact]
    public void An_object_the_exporter_named_nothing_falls_back_to_its_filename()
    {
        var index = ObjectIndex.Of([
            Project(objects: Obj("App/FB_Motor.st", string.Empty, "POU", "g1")),
        ]);

        Assert.Equal("FB_Motor.st", index.Lookup("plc/Machine/App/FB_Motor.st")?.Name);
    }

    [Fact]
    public void An_export_at_the_repository_root_resolves()
    {
        var index = ObjectIndex.Of([
            Project("Machine.project", string.Empty, "Machine.project",
                Obj("App/FB_Motor.st", "FB_Motor", "POU", "g1")),
        ]);

        Assert.Equal("FB_Motor", index.Lookup("App/FB_Motor.st")?.Name);
    }

    [Fact]
    public void A_manifest_with_no_project_name_falls_back_to_the_binding()
    {
        var index = ObjectIndex.Of([
            Project("plc/Machine.project", "plc/Machine", string.Empty,
                Obj("App/FB_Motor.st", "FB_Motor", "POU", "g1")),
        ]);

        Assert.Equal("Machine", index.Lookup("plc/Machine/App/FB_Motor.st")?.Project);
    }

    [Fact]
    public void Nothing_at_all_is_the_empty_index()
    {
        Assert.Equal(0, ObjectIndex.Of([]).Count);
        Assert.Null(ObjectIndex.Empty.Lookup("anything"));
        Assert.Null(ObjectIndex.Empty.Lookup(null));
    }

    [Fact]
    public void An_unknown_type_is_shown_as_the_exporter_wrote_it()
    {
        var index = ObjectIndex.Of([
            Project(objects: Obj("App/Thing.xml", "Thing", "SOME_FUTURE_TYPE", "g1", "NATIVE")),
        ]);

        Assert.Equal("SOME_FUTURE_TYPE", index.Lookup("plc/Machine/App/Thing.xml")?.TypeLabel);
    }

    [Theory]
    [InlineData("POU", "POU")]
    [InlineData("GVL", "GVL")]
    [InlineData("VISUALISATION", "Visualisation")]
    [InlineData("LIBRARY_MANAGER", "Library manager")]
    [InlineData("METHOD_NORET", "Method")]
    public void A_known_type_is_written_the_way_engineers_write_it(string type, string chip) =>
        Assert.Equal(chip, ObjectTypeNames.Chip(PlcVendor.Codesys, type));

    [Theory]
    [InlineData("POU", 1, "1 POU")]
    [InlineData("POU", 3, "3 POUs")]
    [InlineData("VISUALISATION", 1, "1 visualisation")]
    [InlineData("VISUALISATION", 2, "2 visualisations")]
    [InlineData("LIBRARY_MANAGER", 2, "2 library managers")]
    public void A_counted_type_reads_as_a_sentence(string type, int count, string expected) =>
        Assert.Equal(expected, ObjectTypeNames.Counted(PlcVendor.Codesys, type, count));

    [Fact]
    public void An_indexed_object_is_labelled_in_its_own_manifests_vocabulary()
    {
        // Everything else in this file uses a CODESYS manifest, and PlcVendor.Codesys is the
        // default - so if ObjectIndex stopped copying the vendor across from the manifest,
        // every other assertion here would go on passing. This is the only one that would not.
        //
        // The same near-vacuous shape schema 8 hit: a default that happens to be right makes
        // the wiring invisible. Deleting `Vendor = manifest.Vendor` from ObjectIndex.Of must
        // fail something, and this is that something.
        // A vendor NO OTHER TEST REGISTERS, and that is not fussiness. This was written with
        // PlcVendor.TwinCat and it passed alone and failed in company - the tell this codebase
        // already documents twice. SecondVendorTests registers a fake TwinCAT format into the
        // static PlcFormats registry, xunit runs test classes in parallel by default, and for
        // the moment that registration exists every other class sees it: the label came back as
        // the stand-in's "Runtime" instead of the raw string.
        //
        // Phase C removes the ambient static and this stops being possible. Until then, a test
        // that reads the registry has to pick a vendor nobody else is writing.
        var vendor = new PlcVendor("Aoraki Systems");

        var project = new WorkspaceProject
        {
            ProjectPath = "plc/M.aor",
            ExportPath = "plc/M",
            Manifest = new ProjectManifest
            {
                Schema = 8,
                Vendor = vendor,
                Project = "M.aor",
                Objects = [Obj("App/Logic.st", "Logic", "PLC_LOGIC", "g1")],
            },
        };

        var found = ObjectIndex.Of([project]).Lookup("plc/M/App/Logic.st");

        Assert.NotNull(found);
        Assert.Equal(vendor, found.Vendor);

        // No format is registered for TwinCAT here, so the label falls through to IEC and then
        // to the raw string. That is the honest answer and it still proves the vendor crossed:
        // CODESYS names PLC_LOGIC "PLC logic", so a leaked default would read differently.
        Assert.Equal("PLC_LOGIC", found.TypeLabel);
        Assert.NotEqual(ObjectTypeNames.Chip(PlcVendor.Codesys, "PLC_LOGIC"), found.TypeLabel);
    }

    /// <summary>
    /// The fixture's own manifest, so the index is proven against bytes CODESYS produced rather
    /// than only against ones this test wrote.
    /// </summary>
    [Fact]
    public void The_real_fixture_manifest_resolves_its_own_paths()
    {
        var manifest = ProjectManifest.Parse(File.ReadAllText(
            Path.Combine(TestPaths.Project("RefrigeratorControl"), "_manifest.json")));

        var index = ObjectIndex.Of([
            new WorkspaceProject
            {
                ProjectPath = "fixtures/projects/RefrigeratorControl.project",
                ExportPath = "fixtures/projects/RefrigeratorControl",
                Manifest = manifest,
            },
        ]);

        var found = index.Lookup(
            "fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/Glob_Var.gvl.st");

        Assert.Equal("Glob_Var", found?.Name);
        Assert.Equal("GVL", found?.Type);
        Assert.Equal("RefrigeratorControl", found?.Project);

        // Two library managers in this project, and both are export-only.
        Assert.True(index.Lookup(
            "fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/Library Manager.libraries.yaml")
            ?.IsExportOnly);
    }
}
