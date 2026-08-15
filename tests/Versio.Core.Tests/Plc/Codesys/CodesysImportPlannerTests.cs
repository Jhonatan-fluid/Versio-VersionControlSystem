using Versio.Core.Plc;
using Versio.Core.Plc.Codesys;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Plc.Codesys;

/// <summary>
/// The import planner, exercised the way <c>versio_import.py</c> can never be: in
/// milliseconds, without CODESYS, one rule at a time.
/// </summary>
/// <remarks>
/// The interesting assertions are the ones the old filename-derived walk got wrong — G3
/// (name from the stem, not the declaration), G4 (encoded stems compared to raw names),
/// G5 (the owner split on a dotted name) and G6 (a stray file handed to the toolchain).
/// Each one appears here with the input that used to break it.
/// </remarks>
public class CodesysImportPlannerTests
{
    private static ProjectObject Obj(
        string path, string name, string type, string form,
        string guid = "", string parent = "") => new()
    {
        Path = path, Name = name, Type = type, Form = form,
        Guid = guid.Length > 0 ? guid : System.Guid.NewGuid().ToString(),
        Parent = parent,
    };

    private static ProjectManifest Manifest(params ProjectObject[] objects) => new()
    {
        Schema = ExportCoverage.CurrentSchema, Objects = objects,
    };

    private static MirrorFile File(string path, string? text = null) =>
        new() { Path = path, Text = text };

    private static PlannedFile One(ImportPlan plan, string path) =>
        Assert.Single(plan.Files, f => f.Path == path);

    // ── Files the manifest records ──────────────────────────────────────────────

    [Fact]
    public void A_recorded_textual_file_is_applied_under_its_verbatim_name()
    {
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Simulation.st", "Simulation", "POU", "ST_FULL", guid: "g1")),
            [File("App/Simulation.st")]);

        var planned = One(plan, "App/Simulation.st");
        Assert.Equal(PlannedAction.ApplyText, planned.Action);
        Assert.Equal("Simulation", planned.Name);
        Assert.Equal("g1", planned.Guid);
        Assert.Equal(["App", "Simulation"], planned.Resolve);
        Assert.Empty(plan.Warnings);
        Assert.Null(plan.StructuralChangesUnavailable);
    }

    [Theory]
    [InlineData("ST_FULL", PlannedAction.ApplyText)]
    [InlineData("ST_DECL", PlannedAction.ApplyText)]
    [InlineData("ST_IMPL", PlannedAction.ApplyText)]
    [InlineData("NATIVE", PlannedAction.ApplyNative)]
    [InlineData("NATIVE_R", PlannedAction.ApplyNative)]
    [InlineData("NATIVE_WITH_CHILDREN", PlannedAction.ApplyNative)]
    [InlineData("TASK_BOTH", PlannedAction.ApplyTyped)]
    public void Every_importable_form_maps_to_its_import_route(
        string form, PlannedAction expected)
    {
        // The suffix is irrelevant on purpose: for a recorded file the FORM decides, which
        // is what makes a planner-driven import stop guessing from names.
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Thing.any", "Thing", "POU", form)),
            [File("App/Thing.any")]);

        Assert.Equal(expected, One(plan, "App/Thing.any").Action);
    }

    [Fact]
    public void An_export_only_file_is_skipped_and_names_its_importable_sibling()
    {
        // The TASK_NATIVE / TASK_BOTH pair share a GUID — one object, two files. The
        // importer used to probe the disk for the sibling; the manifest carries the answer.
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/Task Configuration.tasks.yaml", "Task Configuration",
                    "TASK_CONFIGURATION", "TASK_BOTH", guid: "shared"),
                Obj("App/Task Configuration.xml", "Task Configuration",
                    "TASK_CONFIGURATION", "TASK_NATIVE", guid: "shared")),
            [File("App/Task Configuration.tasks.yaml"), File("App/Task Configuration.xml")]);

        var native = One(plan, "App/Task Configuration.xml");
        Assert.Equal(PlannedAction.Skip, native.Action);
        Assert.Equal(PlannedSkip.ExportOnly, native.Skip);
        Assert.Equal("App/Task Configuration.tasks.yaml", native.ImportableSibling);

        Assert.Equal(PlannedAction.ApplyTyped,
                     One(plan, "App/Task Configuration.tasks.yaml").Action);
    }

    [Fact]
    public void A_library_file_is_export_only_with_no_sibling_to_point_at()
    {
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Library Manager.libraries.yaml", "Library Manager",
                         "LIBRARY_MANAGER", "LIBRARIES")),
            [File("App/Library Manager.libraries.yaml")]);

        var planned = One(plan, "App/Library Manager.libraries.yaml");
        Assert.Equal(PlannedSkip.ExportOnly, planned.Skip);
        Assert.Null(planned.ImportableSibling);
    }

    [Fact]
    public void A_member_resolves_through_its_owner_chain_not_its_file_name()
    {
        // A property's accessor: two levels of ownership, recorded by GUID. The file name
        // says almost nothing about this — which is the point of recording parents.
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/clsLight.xml", "clsLight", "POU", "NATIVE", guid: "pou"),
                Obj("App/clsLight.Bright.xml", "Bright", "PROPERTY", "NATIVE",
                    guid: "prop", parent: "pou"),
                Obj("App/clsLight.Bright.Get.xml", "Get", "PROPERTY_METHOD", "NATIVE",
                    guid: "get", parent: "prop")),
            [File("App/clsLight.Bright.Get.xml")]);

        Assert.Equal(["App", "clsLight", "Bright", "Get"],
                     One(plan, "App/clsLight.Bright.Get.xml").Resolve);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void A_member_whose_owner_left_the_manifest_falls_back_to_the_stem_and_warns()
    {
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/clsLight.Light.xml", "Light", "METHOD", "NATIVE",
                         parent: "gone")),
            [File("App/clsLight.Light.xml")]);

        Assert.Equal(["App", "clsLight", "Light"],
                     One(plan, "App/clsLight.Light.xml").Resolve);
        Assert.Contains(plan.Warnings, w => w.Contains("owner", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pre_schema_4_member_is_still_owner_split_by_its_file_name()
    {
        // Parents are recorded from manifest schema 4. An older mirror carries none, and its
        // member files still say `Owner.Member` — the split the script always made.
        var manifest = new ProjectManifest
        {
            Schema = 3,
            Objects = [Obj("App/clsLight.Light.xml", "Light", "METHOD", "NATIVE")],
        };

        var plan = CodesysImportPlanner.Plan(manifest, [File("App/clsLight.Light.xml")]);

        Assert.Equal(["App", "clsLight", "Light"],
                     One(plan, "App/clsLight.Light.xml").Resolve);
    }

    [Fact]
    public void A_genuinely_dotted_name_is_not_owner_split_because_the_manifest_says_so()
    {
        // G5's real case: a dotted NAME, not a member. The script's split cannot tell these
        // apart from a filename; the manifest can, because the recorded name IS the whole
        // stem. P5 proved such names cannot be created — but they can already exist.
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/My.Thing.xml", "My.Thing", "POU", "NATIVE")),
            [File("App/My.Thing.xml")]);

        Assert.Equal(["App", "My.Thing"], One(plan, "App/My.Thing.xml").Resolve);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void An_encoded_directory_resolves_as_its_real_name()
    {
        // The resolution half of G4: the mirror's folder is the encoded stem, the project's
        // folder is the raw name, and the script compared one against the other. The plan
        // decodes, so `weird%3Cname%3E` resolves as `weird<name>`.
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("weird%3Cfolder%3E/P.st", "P", "POU", "ST_FULL")),
            [File("weird%3Cfolder%3E/P.st")]);

        Assert.Equal(["weird<folder>", "P"], One(plan, "weird%3Cfolder%3E/P.st").Resolve);
    }

    [Fact]
    public void An_unknown_form_is_refused_with_a_sentence_rather_than_guessed_at()
    {
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/New.device", "New", "DEVICE_THING", "DEVICE_YAML")),
            [File("App/New.device")]);

        var planned = One(plan, "App/New.device");
        Assert.Equal(PlannedSkip.UnknownForm, planned.Skip);
        Assert.Contains(plan.Warnings, w => w.Contains("DEVICE_YAML", StringComparison.Ordinal));
    }

    // ── Files the manifest does not record ─────────────────────────────────────

    [Fact]
    public void A_new_st_file_is_created_under_the_name_its_declaration_states()
    {
        // G3 itself: PROGRAM Foo in Bar.st. The old importer created `Bar`; the plan reads
        // the declaration, creates `Foo`, and says so — because the mismatch between what
        // the user sees and what the project gains is worth a sentence.
        var plan = CodesysImportPlanner.Plan(
            Manifest(),
            [File("App/Bar.st", "PROGRAM Foo\nVAR\nEND_VAR\n\nfoo();")]);

        var planned = One(plan, "App/Bar.st");
        Assert.Equal(PlannedAction.Create, planned.Action);
        Assert.Equal("Foo", planned.Name);
        Assert.Equal(["App", "Foo"], planned.Resolve);
        Assert.Contains(plan.Warnings,
            w => w.Contains("'Foo'", StringComparison.Ordinal)
                 && w.Contains("'Bar'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_new_st_file_whose_declaration_matches_its_name_creates_quietly()
    {
        var plan = CodesysImportPlanner.Plan(
            Manifest(),
            [File("App/Foo.st", "FUNCTION_BLOCK Foo\nVAR\nEND_VAR\n\n;")]);

        Assert.Equal("Foo", One(plan, "App/Foo.st").Name);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void A_new_st_file_with_an_unreadable_header_still_plans_from_its_decoded_stem()
    {
        // The script warns CANNOT_IDENTIFY_DECLARATION when it tries to create this; the
        // plan's job is only to stop the name being the encoded stem.
        var plan = CodesysImportPlanner.Plan(
            Manifest(), [File("App/weird%3Cname%3E.st", "not a declaration at all")]);

        var planned = One(plan, "App/weird%3Cname%3E.st");
        Assert.Equal(PlannedAction.Create, planned.Action);
        Assert.Equal("weird<name>", planned.Name);
    }

    [Fact]
    public void A_new_gvl_is_named_from_its_file_because_a_gvl_body_has_no_header()
    {
        var plan = CodesysImportPlanner.Plan(
            Manifest(), [File("App/Glob_Var.gvl.st", "VAR_GLOBAL\nEND_VAR")]);

        var planned = One(plan, "App/Glob_Var.gvl.st");
        Assert.Equal(PlannedAction.Create, planned.Action);
        Assert.Equal("Glob_Var", planned.Name);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void A_new_native_file_is_offered_to_the_native_importer_with_a_decoded_chain()
    {
        var plan = CodesysImportPlanner.Plan(
            Manifest(), [File("My%20Folder/Conveyor.Execute.xml")]);

        var planned = One(plan, "My%20Folder/Conveyor.Execute.xml");
        Assert.Equal(PlannedAction.ApplyNative, planned.Action);
        Assert.Equal(["My Folder", "Conveyor", "Execute"], planned.Resolve);
    }

    [Fact]
    public void A_stray_file_is_never_offered_to_the_toolchain()
    {
        // G6: the old importer's last branch was import_native, so this markdown would have
        // been handed to CODESYS as native XML.
        var plan = CodesysImportPlanner.Plan(Manifest(), [File("App/README.md")]);

        var planned = One(plan, "App/README.md");
        Assert.Equal(PlannedAction.Skip, planned.Action);
        Assert.Equal(PlannedSkip.Stray, planned.Skip);
        Assert.Contains(plan.Warnings, w => w.Contains("README.md", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unrecorded_yaml_is_a_stray_not_an_apply()
    {
        // The export writes every .tasks.yaml and .libraries.yaml it knows. One the
        // manifest does not record was not written by the export, and applying it would be
        // acting on a file of unknown origin.
        var plan = CodesysImportPlanner.Plan(
            Manifest(), [File("App/Task Configuration.tasks.yaml")]);

        Assert.Equal(PlannedSkip.Stray, One(plan, "App/Task Configuration.tasks.yaml").Skip);
    }

    [Fact]
    public void Bookkeeping_is_skipped_silently()
    {
        var plan = CodesysImportPlanner.Plan(
            Manifest(), [File("_manifest.json"), File("_coverage.md")]);

        Assert.All(plan.Files, f => Assert.Equal(PlannedSkip.Bookkeeping, f.Skip));
        Assert.Empty(plan.Warnings);
    }

    // ── The manifest itself ────────────────────────────────────────────────────

    [Fact]
    public void No_manifest_plans_content_only_and_says_why_structure_is_off_the_table()
    {
        var plan = CodesysImportPlanner.Plan(
            null, [File("App/Foo.st", "PROGRAM Foo\nEND_PROGRAM")]);

        Assert.NotNull(plan.StructuralChangesUnavailable);
        Assert.Empty(plan.MissingFromMirror);
        Assert.Equal(PlannedAction.Create, One(plan, "App/Foo.st").Action);
    }

    [Fact]
    public void A_pre_reporting_schema_manifest_is_believed_about_names_but_not_absences()
    {
        var manifest = new ProjectManifest
        {
            Schema = 1,
            Objects =
            [
                Obj("App/Simulation.st", "Simulation", "POU", "ST_FULL"),
                Obj("App/Gone.st", "Gone", "POU", "ST_FULL"),
            ],
        };

        var plan = CodesysImportPlanner.Plan(manifest, [File("App/Simulation.st")]);

        // The name is still the manifest's — a recorded name is a recorded name.
        Assert.Equal("Simulation", One(plan, "App/Simulation.st").Name);

        // But the absence of Gone.st proves nothing, and the plan says so.
        Assert.NotNull(plan.StructuralChangesUnavailable);
        Assert.Empty(plan.MissingFromMirror);
    }

    [Fact]
    public void A_trusted_manifest_records_what_is_missing_without_acting_on_it()
    {
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/Kept.st", "Kept", "POU", "ST_FULL"),
                Obj("App/Deleted.st", "Deleted", "POU", "ST_FULL"),
                Obj("App/Lib.libraries.yaml", "Lib", "LIBRARY_MANAGER", "LIBRARIES")),
            [File("App/Kept.st")]);

        // The library file is absent too, and deliberately not listed: export-only files
        // are rewritten by every export and applied by nothing, so a missing one says the
        // mirror was tidied, not that an object should die.
        Assert.Equal(["App/Deleted.st"], plan.MissingFromMirror);
        Assert.Null(plan.StructuralChangesUnavailable);
        Assert.DoesNotContain(plan.Files, f => f.Path == "App/Deleted.st");
    }

    [Fact]
    public void The_plan_is_deterministic_and_sorted_whatever_order_the_listing_arrives_in()
    {
        var manifest = Manifest(
            Obj("B/b.st", "b", "POU", "ST_FULL"),
            Obj("A/a.st", "a", "POU", "ST_FULL"));
        MirrorFile[] listing = [File("B/b.st"), File("A/a.st"), File("_coverage.md")];

        var first = CodesysImportPlanner.Plan(manifest, listing);
        var second = CodesysImportPlanner.Plan(manifest, [.. listing.Reverse()]);

        Assert.Equal(first.Files.Select(f => f.Path), second.Files.Select(f => f.Path));
        Assert.Equal(["A/a.st", "B/b.st", "_coverage.md"], first.Files.Select(f => f.Path));
    }

    // ── The committed fixture, planned end to end ──────────────────────────────

    [Fact]
    public void The_committed_fixture_plans_cleanly_with_nothing_stray_and_nothing_unknown()
    {
        // The same bytes the exporter produced and the importer will be handed. If this
        // plan carries a warning or a stray, either the planner or the export layout is
        // wrong — and both halves are committed, so the failure names its commit.
        var root = Path.Combine(TestPaths.Fixtures, "projects", "RefrigeratorControl");
        var manifest = ProjectManifest.Parse(
            System.IO.File.ReadAllText(Path.Combine(root, "_manifest.json")));

        var mirror = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Select(rel => new MirrorFile
            {
                Path = rel,
                Text = CodesysImportPlanner.NeedsText(rel)
                    ? System.IO.File.ReadAllText(Path.Combine(root, rel))
                    : null,
            })
            .ToList();

        var plan = CodesysImportPlanner.Plan(manifest, mirror);

        Assert.Empty(plan.Warnings);
        Assert.Empty(plan.MissingFromMirror);
        Assert.Null(plan.StructuralChangesUnavailable);
        Assert.DoesNotContain(plan.Files, f => f.Skip == PlannedSkip.Stray);
        Assert.DoesNotContain(plan.Files, f => f.Skip == PlannedSkip.UnknownForm);
        Assert.DoesNotContain(plan.Files, f => f.Action == PlannedAction.Create);

        // The classifications that carry the most consequence, spelled out.
        Assert.Equal(PlannedAction.ApplyTyped,
            One(plan, "Device/Plc Logic/Application/Task Configuration.tasks.yaml").Action);

        var taskXml = One(plan, "Device/Plc Logic/Application/Task Configuration.xml");
        Assert.Equal(PlannedSkip.ExportOnly, taskXml.Skip);
        Assert.Equal("Device/Plc Logic/Application/Task Configuration.tasks.yaml",
                     taskXml.ImportableSibling);

        Assert.Equal(["Device", "Plc Logic", "Application", "Simulation"],
                     One(plan, "Device/Plc Logic/Application/Simulation.st").Resolve);

        // The GVL: one object, two importable files, one GUID.
        var gvlSt = One(plan, "Device/Plc Logic/Application/Glob_Var.gvl.st");
        var gvlXml = One(plan, "Device/Plc Logic/Application/Glob_Var.gvl.xml");
        Assert.Equal(PlannedAction.ApplyText, gvlSt.Action);
        Assert.Equal(PlannedAction.ApplyNative, gvlXml.Action);
        Assert.Equal(gvlSt.Guid, gvlXml.Guid);
    }
}
