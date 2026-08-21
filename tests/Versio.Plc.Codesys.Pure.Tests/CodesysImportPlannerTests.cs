using Versio.Core.Iec;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Versio.Plc.Codesys;
using Versio.TestSupport;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

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

    /// <summary>
    /// A mirror written before schema 9, where a member sat <i>beside</i> its owner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a legacy curiosity — the shape of every mirror committed before 2026-08-18.</b>
    /// The exporter now puts a member inside a directory named after its owner, so the
    /// directories are the whole chain; before that they were only half of it and the owner
    /// chain had to be re-applied from the GUIDs on top. Both readings are live, the schema
    /// number is the only thing that distinguishes them, and a test using the wrong helper
    /// asserts against a mirror that never existed.
    /// </para>
    /// <para>
    /// One below the threshold rather than a literal 8, so the pair stays honest if the
    /// threshold ever moves again.
    /// </para>
    /// </remarks>
    private static ProjectManifest LegacyManifest(params ProjectObject[] objects) => new()
    {
        Schema = ExportCoverage.PathIsChainSchema - 1, Objects = objects,
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
        //
        // A PRE-SCHEMA-9 MIRROR, and that is now the whole reason the owner chain is walked
        // at all: these three files sit in one directory, so the directories reach App and
        // stop. On a mirror written today they would be App/clsLight/Bright/Get.xml and the
        // path would say it — see The_directories_are_the_whole_chain_from_schema_9.
        var plan = CodesysImportPlanner.Plan(
            LegacyManifest(
                Obj("App/clsLight.st", "clsLight", "POU", "ST_FULL", guid: "pou"),
                Obj("App/clsLight.Bright.st", "Bright", "PROPERTY", "ST_DECL",
                    guid: "prop", parent: "pou"),
                Obj("App/clsLight.Bright.Get.st", "Get", "PROPERTY_METHOD", "ST_FULL",
                    guid: "get", parent: "prop")),

            // TEXTUAL, and that is not incidental. These were NATIVE until 2026-08-18, which
            // is a manifest the exporter cannot produce — a POU whose payload is the node
            // alone, with its members in separate files, is exactly the shape that loses those
            // members on import, and the planner refuses it now. The chain being asserted is
            // the same either way; the forms were never the point.
            //
            // All three files, not just the one being resolved. The listing used to carry
            // only the accessor, which is a mirror that cannot exist — and once deletions
            // were planned it stopped being harmless: the two absent ancestors read as
            // objects somebody had removed. Every test here describes a real directory now.
            [File("App/clsLight.st"),
             File("App/clsLight.Bright.st"),
             File("App/clsLight.Bright.Get.st")]);

        Assert.Equal(["App", "clsLight", "Bright", "Get"],
                     One(plan, "App/clsLight.Bright.Get.st").Resolve);
        Assert.Empty(plan.Warnings);
        Assert.Empty(plan.Deletions);
    }

    [Fact]
    public void An_ancestor_is_not_deleted_out_from_under_a_surviving_grandchild()
    {
        // The transitive half of the orphan rule, and it exists because a direct-children-only
        // check let this through — found when the resolution test above went red, not by
        // reading the code. A property owns its Get and Set, so chains three deep are ordinary
        // rather than exotic.
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/clsLight.xml", "clsLight", "POU", "NATIVE", guid: "pou"),
                Obj("App/clsLight.Bright.xml", "Bright", "PROPERTY", "NATIVE",
                    guid: "prop", parent: "pou"),
                Obj("App/clsLight.Bright.Get.xml", "Get", "PROPERTY_METHOD", "NATIVE",
                    guid: "get", parent: "prop")),

            // The class and its property are gone from the mirror; the accessor inside them
            // is not. Removing clsLight would take Bright and Get with it, silently.
            [File("App/clsLight.Bright.Get.xml")]);

        Assert.Empty(plan.Deletions);
        Assert.Contains(plan.Warnings, w => w.StartsWith("clsLight:", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Bright:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_directories_are_the_whole_chain_from_schema_9()
    {
        // The same three objects as the legacy test above, in the layout the exporter writes
        // now. The owner chain must NOT be re-applied here: the directories already name
        // clsLight and Bright, and adding them again resolves to
        // App/clsLight/Bright/clsLight/Bright/Get, which exists nowhere.
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/clsLight.st", "clsLight", "POU", "ST_FULL", guid: "pou"),
                Obj("App/clsLight/Bright.st", "Bright", "PROPERTY", "ST_DECL",
                    guid: "prop", parent: "pou"),
                Obj("App/clsLight/Bright/Get.st", "Get", "PROPERTY_METHOD", "ST_FULL",
                    guid: "get", parent: "prop")),
            [File("App/clsLight.st", "PROGRAM clsLight"),
             File("App/clsLight/Bright.st", "VAR Bright : BOOL; END_VAR"),
             File("App/clsLight/Bright/Get.st", "METHOD Get")]);

        Assert.Equal(["App", "clsLight", "Bright", "Get"],
                     One(plan, "App/clsLight/Bright/Get.st").Resolve);
        Assert.Equal(["App", "clsLight", "Bright"],
                     One(plan, "App/clsLight/Bright.st").Resolve);
        Assert.Empty(plan.Warnings);
        Assert.Empty(plan.Deletions);
    }

    [Fact]
    public void A_native_payload_that_would_take_the_objects_members_with_it_is_refused()
    {
        // The measured loss: a graphical POU owning a textual method. import_native replaces,
        // and this payload is the node alone, so applying it removes the method — export 26,
        // import, re-export 25. See PlannedSkip.WouldDestroyMembers.
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/PLC_PRG.xml", "PLC_PRG", "POU", "NATIVE", guid: "pou"),
                Obj("App/PLC_PRG/M_Test.st", "M_Test", "METHOD", "ST_FULL",
                    guid: "m", parent: "pou")),
            [File("App/PLC_PRG.xml"),
             File("App/PLC_PRG/M_Test.st", "METHOD M_Test")]);

        var owner = One(plan, "App/PLC_PRG.xml");
        Assert.Equal(PlannedAction.Skip, owner.Action);
        Assert.Equal(PlannedSkip.WouldDestroyMembers, owner.Skip);

        // The warning NAMES the member, because "this file was not applied" leaves somebody
        // hunting for which of an object's parts was at risk.
        Assert.Contains(plan.Warnings, w => w.Contains("M_Test", StringComparison.Ordinal));

        // And the member is untouched by the refusal — it is the owner's payload that cannot
        // be applied, not the member's. Refusing both would make an edit to the method
        // silently do nothing, which is the failure this is preventing, inverted.
        Assert.Equal(PlannedAction.ApplyText, One(plan, "App/PLC_PRG/M_Test.st").Action);
    }

    [Fact]
    public void A_recursive_native_payload_carries_its_members_and_is_applied()
    {
        // The other half, or "never import a native file" would be the real behaviour and the
        // test above would pass for the wrong reason. NATIVE_R exports the subtree, so
        // replacing with it puts the members back — a PROPERTY and its accessors are exactly
        // this, and refusing them would make every property in every project read-only.
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/FB.st", "FB", "POU", "ST_FULL", guid: "pou"),
                Obj("App/FB/Speed.xml", "Speed", "PROPERTY", "NATIVE_R",
                    guid: "prop", parent: "pou"),
                Obj("App/FB/Speed/Get.xml", "Get", "PROPERTY_METHOD", "NATIVE_R",
                    guid: "get", parent: "prop")),
            [File("App/FB.st", "FUNCTION_BLOCK FB"),
             File("App/FB/Speed.xml"),
             File("App/FB/Speed/Get.xml")]);

        Assert.Equal(PlannedAction.ApplyNative, One(plan, "App/FB/Speed.xml").Action);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void A_native_object_with_no_members_is_applied_as_it_always_was()
    {
        // The floor. Most native files are a visualisation or a graphical POU with nothing
        // inside, and a refusal that caught those would stop imports working at all.
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Live_Visu.vis.xml", "Live_Visu", "VISUALISATION", "NATIVE",
                         guid: "v")),
            [File("App/Live_Visu.vis.xml")]);

        Assert.Equal(PlannedAction.ApplyNative, One(plan, "App/Live_Visu.vis.xml").Action);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void A_member_inside_a_folder_inside_its_owner_resolves_through_the_folder()
    {
        // The case that started all of this: FB_Alarm's method grouped into a folder called
        // Private. On schema 9 the folder is a directory, so the chain simply reads off the
        // path — folder included, which is what the IDE shows and what the import has to walk.
        //
        // The owner GUID is still recorded and still ignored for the path. It answers the
        // question the path cannot: whether FB_Alarm/Private/_M_Reset is the POU's method or
        // an object sitting in folders that happen to be named that way.
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/FB_Alarm.st", "FB_Alarm", "POU", "ST_FULL", guid: "pou"),
                Obj("App/FB_Alarm/M_Reset.st", "M_Reset", "METHOD", "ST_FULL",
                    guid: "m", parent: "pou"),
                Obj("App/FB_Alarm/Private/_M_Reset.st", "_M_Reset", "METHOD", "ST_FULL",
                    guid: "p", parent: "pou")),
            [File("App/FB_Alarm.st", "FUNCTION_BLOCK FB_Alarm"),
             File("App/FB_Alarm/M_Reset.st", "METHOD M_Reset"),
             File("App/FB_Alarm/Private/_M_Reset.st", "METHOD _M_Reset")]);

        Assert.Equal(["App", "FB_Alarm", "Private", "_M_Reset"],
                     One(plan, "App/FB_Alarm/Private/_M_Reset.st").Resolve);
        Assert.Equal(["App", "FB_Alarm", "M_Reset"],
                     One(plan, "App/FB_Alarm/M_Reset.st").Resolve);
        Assert.Empty(plan.Warnings);
        Assert.Empty(plan.Deletions);
    }

    [Fact]
    public void A_member_whose_owner_left_the_manifest_falls_back_to_the_stem_and_warns()
    {
        // Pre-schema-9 again: the stem split is the fallback for a layout that encoded
        // ownership in the file name. A schema-9 mirror has nothing to fall back to and
        // needs nothing — its directories are the chain whether the owner is recorded or not.
        var plan = CodesysImportPlanner.Plan(
            LegacyManifest(Obj("App/clsLight.Light.xml", "Light", "METHOD", "NATIVE",
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

    // ── Deletions, and every precondition that stops one ───────────────────────
    //
    // P3 measured what remove() does to an object other objects reference: it removes it,
    // saves cleanly, and warns about nothing. There is no toolchain backstop, so each rule
    // below is not belt-and-braces — it is the only thing between a stale mirror and somebody
    // losing work. Each has its own test for that reason.

    // ── Renames and moves: Phase 3, step 1 ─────────────────────────────────────
    //
    // A rename in the mirror looks exactly like a deletion and an unrelated create. Treating
    // it as one destroys the object and builds a new one, losing the GUID and with it every
    // reference, task assignment and visualisation binding pointing at it. These tests are
    // about the pairing that tells the two apart — and about refusing to pair when it cannot
    // be told, because renaming the wrong object is worse than not renaming at all.

    /// <summary>Content whose hash is what the manifest records for the file that went.</summary>
    private const string Body = "PROGRAM Renamed\nVAR\nEND_VAR\n\nx();";

    private static string HashOf(string text) => Versio.Core.Hashing.ContentHash.Of(text);

    [Fact]
    public void A_file_renamed_without_being_edited_is_a_rename_not_a_delete_and_a_create()
    {
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Old.st", "Old", "POU", "ST_FULL", guid: "g1")
                with { Hash = HashOf(Body) }),
            [File("App/Renamed.st", Body)]);

        var rename = Assert.Single(plan.Renames);
        Assert.Equal("g1", rename.Guid);
        Assert.Equal("Old", rename.OldName);

        // The DECLARATION's name, not the file stem. G3's rule holds here too: a rename that
        // took its name from the filename would rename the object to whatever somebody called
        // the file, which is the defect the create path was fixed for.
        Assert.Equal("Renamed", rename.NewName);
        Assert.True(rename.IsRename);
        Assert.False(rename.IsMove);

        // BOTH SIDES ARE WITHDRAWN. A rename that left the deletion standing would delete the
        // object it had just renamed; one that left the create standing would leave the
        // project with two of it.
        Assert.Empty(plan.Deletions);
        Assert.Equal(PlannedAction.Skip, One(plan, "App/Renamed.st").Action);
        Assert.Equal(PlannedSkip.RenameTarget, One(plan, "App/Renamed.st").Skip);
    }

    [Fact]
    public void Renaming_only_the_file_of_a_POU_renames_nothing_and_says_why()
    {
        // THE ORDINARY CASE FOR A POU, and it shipped as a false success for one commit. An
        // ST_FULL file opens `PROGRAM Renamed`, so the object's name lives in the CONTENT:
        // rename the file alone and the declaration still says what the object is called, so
        // old and new come out equal. The script would then perform neither operation, find
        // the object where it left it, and count a rename — a fourth silent success in this
        // path, and the one the script's own verification cannot catch, because the object
        // never had to move for the check to pass.
        //
        // Body declares PROGRAM Renamed, so a file called Old.st holding it is exactly this:
        // a file whose name disagrees with its declaration.
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Renamed.st", "Renamed", "POU", "ST_FULL", guid: "g1")
                with { Hash = HashOf(Body) }),
            [File("App/Old.st", Body)]);

        // Nothing renamed — there is nothing to rename it to.
        Assert.Empty(plan.Renames);

        // And nothing DESTROYED either. The pairing still withdraws both sides, so the object
        // survives a file rename that says nothing about what it is called, and no duplicate
        // is built from a file that declares itself the same object.
        Assert.Empty(plan.Deletions);
        Assert.Equal(PlannedSkip.RenameTarget, One(plan, "App/Old.st").Skip);

        Assert.Contains(plan.Warnings,
            w => w.Contains("the declaration inside it still says", StringComparison.Ordinal));
    }

    [Fact]
    public void The_same_object_in_a_different_folder_is_a_move()
    {
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/Old.st", "Old", "POU", "ST_FULL", guid: "g1") with { Hash = HashOf(Body) },
                // A second object under Lib, so the destination folder is one the mirror
                // actually records rather than a directory that exists only on disk.
                Obj("Lib/Other.st", "Other", "POU", "ST_FULL", guid: "g2")),
            [File("Lib/Renamed.st", Body), File("Lib/Other.st")]);

        var rename = Assert.Single(plan.Renames);
        Assert.True(rename.IsMove);
        Assert.Equal(["Lib"], rename.NewParent);
        Assert.Empty(plan.Deletions);
    }

    [Fact]
    public void Two_deleted_files_with_identical_content_are_never_paired_by_guessing()
    {
        // THE CASE THAT MAKES THIS SAFE. Two empty actions have identical bytes, so content
        // cannot say which arrival replaced which departure, and either pairing is a coin
        // toss that renames the wrong object half the time. Both stay a delete and a create:
        // lossy, and never wrong.
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/A.st", "A", "POU", "ST_FULL", guid: "g1") with { Hash = HashOf(Body) },
                Obj("App/B.st", "B", "POU", "ST_FULL", guid: "g2") with { Hash = HashOf(Body) }),
            [File("App/X.st", Body), File("App/Y.st", Body)]);

        Assert.Empty(plan.Renames);
        Assert.Equal(2, plan.Deletions.Count);
        Assert.Contains(plan.Warnings, w => w.Contains("cannot be told", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_departures_sharing_content_with_one_arrival_are_also_refused()
    {
        // THE OTHER SIDE OF THE AMBIGUITY, and the one that does not announce itself. Two
        // arrivals for one departure is the case anybody thinks of; two DEPARTURES and one
        // arrival is the same coin toss seen from the other end, and before this it was not
        // refused — the first deletion in iteration order simply won, silently, and the
        // second was left as a plain delete. Found by re-reading the loop, not by a failure.
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/A.st", "A", "POU", "ST_FULL", guid: "g1") with { Hash = HashOf(Body) },
                Obj("App/B.st", "B", "POU", "ST_FULL", guid: "g2") with { Hash = HashOf(Body) }),
            [File("App/OnlyOne.st", Body)]);

        Assert.Empty(plan.Renames);
        Assert.Equal(2, plan.Deletions.Count);
        Assert.Contains(plan.Warnings, w => w.Contains("cannot be told", StringComparison.Ordinal));
    }

    [Fact]
    public void An_edited_file_that_also_moved_is_left_as_a_delete_and_a_create()
    {
        // Rename-and-edit: the content no longer matches anything, so step 1 cannot see it.
        // It needs the old bytes from git and a similarity score — step 2, not built. Asserted
        // rather than left implicit, because this is the case the pass exists to catch and
        // does not: it is lossy in exactly the way a rename is meant to prevent.
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Old.st", "Old", "POU", "ST_FULL", guid: "g1")
                with { Hash = HashOf(Body) }),
            [File("App/Renamed.st", Body + "\ny();")]);

        Assert.Empty(plan.Renames);
        Assert.Single(plan.Deletions);
        Assert.Equal(PlannedAction.Create, One(plan, "App/Renamed.st").Action);
    }

    [Fact]
    public void A_two_file_object_is_never_renamed_on_the_strength_of_one_half()
    {
        // A GVL is a .gvl.st and a .gvl.xml under one GUID. Pairing on the textual half alone
        // would rename the object while its native half still described the old name.
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/G.gvl.st", "G", "GVL", "ST_DECL", guid: "g1") with { Hash = HashOf(Body) },
                Obj("App/G.gvl.xml", "G", "GVL", "NATIVE", guid: "g1")),
            [File("App/H.gvl.st", Body)]);

        Assert.Empty(plan.Renames);
        Assert.Single(plan.Deletions);
    }

    /// <summary>A full POU body: declaration naming the object, then the implementation.</summary>
    private static string Pou(string name, string body = "x();") =>
        $"PROGRAM {name}\nVAR\nEND_VAR\n{StTextCodec.ImplementationDelimiter}\n{body}";

    [Fact]
    public void A_renamed_POU_is_matched_on_its_body_because_its_name_is_in_its_declaration()
    {
        // THE CASE STEP 1 CANNOT SEE, and the common one. Renaming a POU rewrites
        // `PROGRAM Old` to `PROGRAM New`, so the whole-file hash matches nothing — but the
        // implementation is untouched, and schema 6 records it.
        var body = "MotorOn := TRUE;";
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Old.st", "Old", "POU", "ST_FULL", guid: "g1")
                with
                {
                    Hash = HashOf(Pou("Old", body)),
                    ImplementationHash = HashOf(StTextCodec.Split(Pou("Old", body)).Implementation),
                }),
            [File("App/New.st", Pou("New", body))]);

        var rename = Assert.Single(plan.Renames);
        Assert.Equal("Old", rename.OldName);
        Assert.Equal("New", rename.NewName);
        Assert.Empty(plan.Deletions);

        // AND THE FILE IS APPLIED, not skipped — this is the difference from an exact-content
        // match. The declaration genuinely differs, so the object is renamed and then written
        // into; skipping it would leave the object wearing its new name over its old
        // declaration.
        var applied = One(plan, "App/New.st");
        Assert.Equal(PlannedAction.ApplyText, applied.Action);
        Assert.Equal("g1", applied.Guid);
        Assert.Equal(["App", "New"], applied.Resolve);
    }

    [Fact]
    public void A_POU_whose_file_stem_changed_but_whose_name_did_not_is_updated_not_rebuilt()
    {
        // `Old.st` gone, `Old_2.st` arrived, both declaring PROGRAM Old with the same body but
        // an edited declaration. Nothing moved and nothing was renamed — so the object must be
        // UPDATED, not destroyed and rebuilt under the same name with a new GUID. The code
        // used to let the deletion and the create both stand here, under a comment claiming it
        // did the opposite.
        var body = "MotorOn := TRUE;";
        var old = $"PROGRAM Old\nVAR\nEND_VAR\n{StTextCodec.ImplementationDelimiter}\n{body}";
        var edited = $"PROGRAM Old\nVAR\n  x: INT;\nEND_VAR\n"
                     + $"{StTextCodec.ImplementationDelimiter}\n{body}";

        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Old.st", "Old", "POU", "ST_FULL", guid: "g1")
                with
                {
                    Hash = HashOf(old),
                    ImplementationHash = HashOf(StTextCodec.Split(old).Implementation),
                }),
            [File("App/Old_2.st", edited)]);

        Assert.Empty(plan.Renames);
        Assert.Empty(plan.Deletions);

        var applied = One(plan, "App/Old_2.st");
        Assert.Equal(PlannedAction.ApplyText, applied.Action);
        Assert.Equal("g1", applied.Guid);
        Assert.Equal(["App", "Old"], applied.Resolve);
    }

    [Fact]
    public void Two_POUs_sharing_a_body_are_refused_which_is_what_makes_an_empty_body_safe()
    {
        // Half the POUs in a project have an empty or near-identical body, so the weaker
        // signal is only safe because ambiguity is refused. No special case for emptiness:
        // two bodies the same is two candidates, and two candidates cannot be told apart.
        var body = string.Empty;
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/A.st", "A", "POU", "ST_FULL", guid: "g1") with
                {
                    Hash = HashOf(Pou("A", body)),
                    ImplementationHash = HashOf(StTextCodec.Split(Pou("A", body)).Implementation),
                },
                Obj("App/B.st", "B", "POU", "ST_FULL", guid: "g2") with
                {
                    Hash = HashOf(Pou("B", body)),
                    ImplementationHash = HashOf(StTextCodec.Split(Pou("B", body)).Implementation),
                }),
            [File("App/C.st", Pou("C", body))]);

        Assert.Empty(plan.Renames);
        Assert.Equal(2, plan.Deletions.Count);
    }

    [Fact]
    public void A_POU_on_a_pre_schema_6_mirror_is_not_matched_because_nothing_recorded_its_body()
    {
        // Absent is not "no implementation". Every mirror exported before today has no ihash,
        // and such a rename falls back to a delete and a create — which is what happened
        // before this existed, rather than a new failure.
        var body = "MotorOn := TRUE;";
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Old.st", "Old", "POU", "ST_FULL", guid: "g1")
                with { Hash = HashOf(Pou("Old", body)) }),
            [File("App/New.st", Pou("New", body))]);

        Assert.Empty(plan.Renames);
        Assert.Single(plan.Deletions);
        Assert.Equal(PlannedAction.Create, One(plan, "App/New.st").Action);
    }

    [Fact]
    public void A_POU_renamed_AND_edited_is_matched_on_similarity_against_its_old_content()
    {
        // The last pass, and the only one that can see this: the name changed AND the body
        // changed, so neither the whole-file hash nor the implementation hash matches. Only the
        // departed file's actual bytes can settle it, and the manifest keeps a hash — so they
        // come from HEAD, supplied by the caller.
        var old = "PROGRAM Old\nVAR\n  x: INT;\nEND_VAR\n"
                  + $"{StTextCodec.ImplementationDelimiter}\nx := x + 1;\nIF x > 10 THEN\n"
                  + "  x := 0;\nEND_IF";
        var edited = "PROGRAM New\nVAR\n  x: INT;\nEND_VAR\n"
                     + $"{StTextCodec.ImplementationDelimiter}\nx := x + 2;\nIF x > 10 THEN\n"
                     + "  x := 0;\nEND_IF";

        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Old.st", "Old", "POU", "ST_FULL", guid: "g1")
                with
                {
                    Hash = HashOf(old),
                    ImplementationHash = HashOf(StTextCodec.Split(old).Implementation),
                }),
            [File("App/New.st", edited)],
            new Dictionary<string, string> { ["App/Old.st"] = old });

        var rename = Assert.Single(plan.Renames);
        Assert.Equal("Old", rename.OldName);
        Assert.Equal("New", rename.NewName);
        Assert.Empty(plan.Deletions);

        // Applied, like the implementation-hash pass: the content differs by definition here.
        Assert.Equal(PlannedAction.ApplyText, One(plan, "App/New.st").Action);

        // And it says it used a judgement rather than a certainty, because it did.
        Assert.Contains(plan.Warnings,
            w => w.Contains("content similarity", StringComparison.Ordinal));
    }

    [Fact]
    public void With_no_history_a_renamed_and_edited_POU_is_matched_from_the_recorded_sketch()
    {
        // THE UNTRACKED CASE. A mirror that was never committed has no old copy in HEAD, so
        // the departed file's content exists nowhere — except as the fingerprint the export
        // recorded while the file still existed. Schema 7.
        var old = "PROGRAM Old\nVAR\n  x: INT;\nEND_VAR\n"
                  + $"{StTextCodec.ImplementationDelimiter}\nx := x + 1;\nIF x > 10 THEN\n"
                  + "  x := 0;\nEND_IF\n  y := 2;";
        var edited = old.Replace("PROGRAM Old", "PROGRAM New", StringComparison.Ordinal)
            .Replace("x := x + 1;", "x := x + 3;", StringComparison.Ordinal);

        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Old.st", "Old", "POU", "ST_FULL", guid: "g1")
                with { Hash = HashOf(old), LineSketch = Versio.Core.Diff.LineSketch.Of(old) }),
            [File("App/New.st", edited)],

            // No departed content at all — exactly what an uncommitted mirror gives.
            new Dictionary<string, string>());

        var rename = Assert.Single(plan.Renames);
        Assert.Equal("Old", rename.OldName);
        Assert.Equal("New", rename.NewName);
        Assert.Empty(plan.Deletions);

        // It says the score was ESTIMATED, because the reader deserves to know which
        // instrument was used — a sketch cannot see line order or repetition.
        Assert.Contains(plan.Warnings,
            w => w.Contains("estimated from the export's recorded fingerprint",
                            StringComparison.Ordinal));
    }

    [Fact]
    public void An_unrelated_file_is_not_paired_by_a_sketch_either()
    {
        // The sketch floor is higher than the exact one because the measure is weaker — it
        // compares line SETS. This is what stops "something went, something arrived" becoming
        // a rename when there is no history to check against.
        var old = "PROGRAM Old\nVAR\n  x: INT;\nEND_VAR\n"
                  + $"{StTextCodec.ImplementationDelimiter}\nx := x + 1;\nIF x > 10 THEN\n"
                  + "  x := 0;\nEND_IF";

        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Old.st", "Old", "POU", "ST_FULL", guid: "g1")
                with { Hash = HashOf(old), LineSketch = Versio.Core.Diff.LineSketch.Of(old) }),
            [File("App/Unrelated.st", "PROGRAM Unrelated\nVAR\nEND_VAR\n"
                                      + $"{StTextCodec.ImplementationDelimiter}\nSomethingElse();")],
            new Dictionary<string, string>());

        Assert.Empty(plan.Renames);
        Assert.Single(plan.Deletions);
    }

    [Fact]
    public void Without_history_or_a_sketch_a_renamed_and_edited_POU_stays_a_delete_and_a_create()
    {
        // An uncommitted mirror has no HEAD copy of anything, and that is an ordinary state
        // rather than an error: the pass does nothing and the behaviour is what it was before
        // the pass existed.
        var old = "PROGRAM Old\nVAR\nEND_VAR\n"
                  + $"{StTextCodec.ImplementationDelimiter}\nx := x + 1;";
        var edited = "PROGRAM New\nVAR\nEND_VAR\n"
                     + $"{StTextCodec.ImplementationDelimiter}\nx := x + 2;";

        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Old.st", "Old", "POU", "ST_FULL", guid: "g1")
                with { Hash = HashOf(old) }),
            [File("App/New.st", edited)]);

        Assert.Empty(plan.Renames);
        Assert.Single(plan.Deletions);
    }

    [Fact]
    public void Two_arrivals_that_are_about_equally_similar_are_refused_rather_than_ranked()
    {
        // The same ambiguity discipline as the exact passes, expressed for a score: two
        // candidates within a hair of each other would be separated by one line, which is not
        // a difference with any meaning behind it.
        var old = "PROGRAM Old\nVAR\nEND_VAR\n"
                  + $"{StTextCodec.ImplementationDelimiter}\na();\nb();\nc();\nd();";

        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Old.st", "Old", "POU", "ST_FULL", guid: "g1")
                with { Hash = HashOf(old) }),
            [
                File("App/One.st", "PROGRAM One\nVAR\nEND_VAR\n"
                                   + $"{StTextCodec.ImplementationDelimiter}\na();\nb();\nc();\nz();"),
                File("App/Two.st", "PROGRAM Two\nVAR\nEND_VAR\n"
                                   + $"{StTextCodec.ImplementationDelimiter}\na();\nb();\nc();\ny();"),
            ],
            new Dictionary<string, string> { ["App/Old.st"] = old });

        Assert.Empty(plan.Renames);
        Assert.Single(plan.Deletions);
        Assert.Contains(plan.Warnings,
            w => w.Contains("about as similar", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unrelated_new_file_is_never_paired_however_lonely_the_deletion_is()
    {
        // The floor is what stops "something went and something arrived" becoming a rename.
        var old = "PROGRAM Old\nVAR\n  x: INT;\nEND_VAR\n"
                  + $"{StTextCodec.ImplementationDelimiter}\nx := x + 1;\nIF x > 10 THEN\n"
                  + "  x := 0;\nEND_IF";

        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Old.st", "Old", "POU", "ST_FULL", guid: "g1")
                with { Hash = HashOf(old) }),
            [File("App/Unrelated.st", "PROGRAM Unrelated\nVAR\nEND_VAR\n"
                                      + $"{StTextCodec.ImplementationDelimiter}\nSomethingElse();")],
            new Dictionary<string, string> { ["App/Old.st"] = old });

        Assert.Empty(plan.Renames);
        Assert.Single(plan.Deletions);
        Assert.Equal(PlannedAction.Create, One(plan, "App/Unrelated.st").Action);
    }

    [Fact]
    public void A_stale_manifest_plans_no_renames_for_the_reason_it_plans_no_deletes()
    {
        // What a pre-reporting-schema manifest does not list cannot be told apart from what it
        // never recorded, so a "rename" would be built out of two guesses instead of one.
        var stale = new ProjectManifest
        {
            Schema = 1,
            Objects = [Obj("App/Old.st", "Old", "POU", "ST_FULL", guid: "g1")
                with { Hash = HashOf(Body) }],
        };

        Assert.Empty(CodesysImportPlanner.Plan(stale, [File("App/Renamed.st", Body)]).Renames);
    }

    // ── Which deletions the dialog may promise ─────────────────────────────────
    //
    // CannotVerify is what the confirmation dialog reads to decide between "will be removed"
    // and "will stay", so a wrong answer here is a lie in front of somebody about to destroy
    // work. It is a prediction of the script's own refusal; PythonFileNameParityTests is what
    // says the two agree.

    [Fact]
    public void A_textual_deletion_is_promised_because_its_file_can_be_rebuilt()
    {
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Gone.st", "Gone", "POU", "ST_FULL", guid: "g1")),
            []);

        Assert.Null(Assert.Single(plan.Deletions).CannotVerify);
    }

    [Fact]
    public void A_native_deletion_is_promised_once_the_export_recorded_a_normalised_hash()
    {
        // Schema 5. The raw hash is over bytes that churn between exports, so nhash is the
        // only thing a re-export can be compared against — and recording it is what closed
        // Phase 2's open half.
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Visu.vis.xml", "Visu", "VISUALISATION", "NATIVE", guid: "g1")
                with { Hash = "sha256:raw", NormalizedHash = "sha256:norm" }),
            []);

        Assert.Null(Assert.Single(plan.Deletions).CannotVerify);
    }

    [Fact]
    public void A_native_deletion_from_a_pre_schema_5_mirror_is_refused_and_says_what_to_do()
    {
        // Absent is NOT "unchanged". Every mirror exported before 2026-08-15 has no nhash,
        // and reading that as agreement would delete work on all of them.
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Visu.vis.xml", "Visu", "VISUALISATION", "NATIVE", guid: "g1")
                with { Hash = "sha256:raw" }),
            []);

        var why = Assert.Single(plan.Deletions).CannotVerify;
        Assert.NotNull(why);
        Assert.Contains("export the project again", why, StringComparison.Ordinal);
    }

    [Fact]
    public void One_unverifiable_file_holds_back_the_whole_object()
    {
        // A GVL is two files under one GUID and the script refuses on the FIRST it cannot
        // check, so a per-file answer here would promise removal for an object its second
        // file keeps. Verified by giving the native half no nhash while the textual half is
        // perfectly checkable.
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/G.gvl.st", "G", "GVL", "ST_DECL", guid: "g1"),
                Obj("App/G.gvl.xml", "G", "GVL", "NATIVE", guid: "g1")),
            []);

        Assert.NotNull(Assert.Single(plan.Deletions).CannotVerify);
    }

    [Fact]
    public void An_object_whose_only_file_has_gone_is_proposed_for_deletion()
    {
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/Kept.st", "Kept", "POU", "ST_FULL", guid: "g1"),
                Obj("App/Deleted.st", "Deleted", "POU", "ST_FULL", guid: "g2")
                    with { Hash = "sha256:aaa" }),
            [File("App/Kept.st")]);

        var deletion = Assert.Single(plan.Deletions);
        Assert.Equal("g2", deletion.Guid);
        Assert.Equal("Deleted", deletion.Name);

        // The chain comes from the manifest's recorded path and names — there is no file left
        // to derive one from, which is the reason it was never derived from stems.
        Assert.Equal(["App", "Deleted"], deletion.Resolve);

        var file = Assert.Single(deletion.Files);
        Assert.Equal("App/Deleted.st", file.Path);
        Assert.Equal("sha256:aaa", file.Hash);
        Assert.Equal("ST_FULL", file.Form);

        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void A_GVL_is_deleted_only_when_both_of_its_files_have_gone()
    {
        // One object, two files, one GUID. A per-file rule would see the .st gone and remove
        // the object the surviving .xml still describes.
        var manifest = Manifest(
            Obj("App/Glob_Var.gvl.st", "Glob_Var", "GVL", "GVL_BOTH", guid: "g1"),
            Obj("App/Glob_Var.gvl.xml", "Glob_Var", "GVL", "GVL_BOTH", guid: "g1"));

        var half = CodesysImportPlanner.Plan(manifest, [File("App/Glob_Var.gvl.xml")]);

        Assert.Empty(half.Deletions);

        // And it says so. "I deleted the .st and the GVL is still there" is otherwise
        // unanswerable, and silence here reads as the tool ignoring the edit.
        Assert.Contains(half.Warnings, w =>
            w.Contains("Glob_Var", StringComparison.Ordinal)
            && w.Contains("App/Glob_Var.gvl.xml", StringComparison.Ordinal));

        // The raw list still reports the file as gone, because it HAS gone. The two lists
        // differing is the point of keeping both.
        Assert.Equal(["App/Glob_Var.gvl.st"], half.MissingFromMirror);

        var both = CodesysImportPlanner.Plan(manifest, []);

        var deletion = Assert.Single(both.Deletions);
        Assert.Equal(
            ["App/Glob_Var.gvl.st", "App/Glob_Var.gvl.xml"],
            deletion.Files.Select(f => f.Path));
        Assert.Empty(both.Warnings);
    }

    [Fact]
    public void An_owner_whose_children_are_still_in_the_mirror_is_left_alone()
    {
        // P3 as a precondition. The planner cannot see every reference a project holds, but
        // ownership is the one kind the manifest records — and removing an owner takes its
        // children with it, silently.
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/FB_Motor.xml", "FB_Motor", "POU", "NATIVE", guid: "owner"),
                Obj("App/FB_Motor.Start.st", "Start", "ACTION", "ST_IMPL",
                    guid: "child", parent: "owner")),
            [File("App/FB_Motor.Start.st")]);

        Assert.Empty(plan.Deletions);
        Assert.Contains(plan.Warnings, w =>
            w.Contains("FB_Motor", StringComparison.Ordinal)
            && w.Contains("Start", StringComparison.Ordinal));
    }

    [Fact]
    public void An_owner_goes_when_its_children_have_gone_with_it()
    {
        // The other half of the rule, or "never delete an owner" would be the real behaviour
        // and the test above would be passing for the wrong reason.
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/FB_Motor.xml", "FB_Motor", "POU", "NATIVE", guid: "owner"),
                Obj("App/FB_Motor.Start.st", "Start", "ACTION", "ST_IMPL",
                    guid: "child", parent: "owner")),
            []);

        Assert.Equal(["FB_Motor", "Start"], plan.Deletions.Select(d => d.Name));
    }

    [Fact]
    public void An_export_only_file_going_missing_never_proposes_a_deletion()
    {
        // Its file is rewritten by every export and applied by nothing, so its absence says
        // the mirror was tidied — not that the object should die.
        var plan = CodesysImportPlanner.Plan(
            Manifest(Obj("App/Lib.libraries.yaml", "Lib", "LIBRARY_MANAGER", "LIBRARIES",
                         guid: "g1")),
            []);

        Assert.Empty(plan.Deletions);
        Assert.Empty(plan.MissingFromMirror);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void A_task_configurations_export_only_half_does_not_veto_its_deletion()
    {
        // TASK_BOTH and TASK_NATIVE share one GUID: the .tasks.yaml is importable and the
        // .xml beside it is export-only. Counting the .xml as a file that must also be gone
        // would make a task configuration undeletable for a reason that has nothing to do
        // with the object — the same trap as checking a native file byte for byte.
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                // The EXPORT-ONLY record first, deliberately. An object's files arrive in
                // whatever order the exporter emitted them, and a rule that quietly depends on
                // which one came first is a rule that works until the exporter changes.
                Obj("App/Task Configuration.xml", "Task Configuration",
                    "TASK_CONFIG", "TASK_NATIVE", guid: "g1"),
                Obj("App/Task Configuration.tasks.yaml", "Task Configuration",
                    "TASK_CONFIG", "TASK_BOTH", guid: "g1")),
            [File("App/Task Configuration.xml")]);

        var deletion = Assert.Single(plan.Deletions);
        Assert.Equal("Task Configuration", deletion.Name);
        Assert.Equal(["App/Task Configuration.tasks.yaml"], deletion.Files.Select(f => f.Path));

        // The chain a deletion would be executed against, asserted because resolving to the
        // wrong object is the one mistake here that cannot be undone — and because this is
        // the only case where an object has two records to be described from.
        Assert.Equal(["App", "Task Configuration"], deletion.Resolve);
    }

    [Fact]
    public void An_object_whose_owner_is_not_recorded_is_never_deleted_by_a_guessed_chain()
    {
        // The same input as the fall-back-to-the-stem test above, with the file gone instead
        // of present — and the two must end differently. Resolving by stem is a reasonable
        // guess for an UPDATE: the worst case is text applied to the wrong object, which git
        // undoes. For a deletion the identical guess resolves to some other object and
        // destroys it, and remove() will not object (P3).
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/Owner.Member.st", "Member", "METHOD", "ST_FULL",
                    guid: "g1", parent: "an-owner-nobody-recorded")),
            []);

        Assert.Empty(plan.Deletions);
        Assert.Contains(plan.Warnings, w =>
            w.Contains("Member", StringComparison.Ordinal)
            && w.Contains("guessed", StringComparison.Ordinal));

        // Still reported as a file that has gone — the raw list is honest about the file, and
        // only the vetted set is a decision.
        Assert.Equal(["App/Owner.Member.st"], plan.MissingFromMirror);
    }

    [Fact]
    public void A_manifest_that_cannot_be_trusted_proposes_no_deletion_at_all()
    {
        // What a stale manifest does not list cannot be told apart from what it never
        // recorded, so every absence would look like a deletion.
        var stale = new ProjectManifest
        {
            Schema = 1,
            Objects = [Obj("App/Gone.st", "Gone", "POU", "ST_FULL", guid: "g1")],
        };

        Assert.Empty(CodesysImportPlanner.Plan(stale, []).Deletions);
        Assert.Empty(CodesysImportPlanner.Plan(null, []).Deletions);
    }

    [Fact]
    public void The_deletion_set_is_sorted_whatever_order_the_manifest_arrives_in()
    {
        var plan = CodesysImportPlanner.Plan(
            Manifest(
                Obj("App/Zeta.st", "Zeta", "POU", "ST_FULL", guid: "g3"),
                Obj("App/Alpha.st", "Alpha", "POU", "ST_FULL", guid: "g1"),
                Obj("App/Mid.st", "Mid", "POU", "ST_FULL", guid: "g2")),
            []);

        // A confirmation dialog lists these to a person, and a list that reorders itself
        // between two readings of the same repository is a list nobody can check.
        Assert.Equal(["Alpha", "Mid", "Zeta"], plan.Deletions.Select(d => d.Name));
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

    /// <summary>
    /// The member fixture: ownership from recorded parent GUIDs, under the chain paths
    /// schema 9 made canonical.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rows the first fixture could not reach.</b> <c>RefrigeratorControl</c> has no
    /// member object of any kind — no method, no action, no property — so its manifest
    /// records no <c>parent</c> for anything, and every resolve chain this planner has ever
    /// built in a test came from directory names or from a hand-written manifest asserting the
    /// test’s own assumptions. <c>MachineWithMembers</c> exists to end that: it was built
    /// against real CODESYS by <c>tools\versio_make_member_fixture.py</c> and exported by the
    /// real exporter, so what is asserted below is what the export actually writes.
    /// </para>
    /// <para>
    /// <b>Members sit INSIDE their owner’s directory</b> since schema 9 —
    /// <c>FB_Pump/Start.st</c>, <c>FB_Pump/Stop.action.st</c> — segment for segment what the
    /// CODESYS tree shows. The dotted-stem layout this test originally pinned
    /// (<c>FB_Pump.Start.st</c> beside its owner) is the legacy shape now, exercised through
    /// <c>LegacyManifest</c> rather than through the committed fixture; the fixture caught up
    /// when it was regenerated on 2026-08-21 (schema 10, the devices catalog and per-file
    /// accessors added). What has not changed is G5’s point: <c>FB_Pump/Speed.xml</c> could
    /// be FB_Pump’s property or an object inside a folder that merely shares the name, and
    /// the manifest’s parent GUID is what makes the answer not a guess.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_member_fixture_resolves_by_recorded_parent_under_chain_paths()
    {
        var root = Path.Combine(TestPaths.Fixtures, "projects", "MachineWithMembers");
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

        // The fixture has to still BE the fixture, or everything below passes for the
        // uninteresting reason. These four are the forms the first fixture cannot produce.
        Assert.Contains(manifest.Objects, o => o.Form == "ST_IMPL");
        Assert.Contains(manifest.Objects, o => o.Form == "NATIVE_R");
        Assert.Contains(manifest.Objects, o => o.Type == "METHOD");
        // Six since schema 10: the method, the action, the property's two halves (text and
        // NATIVE_R share the owner), and the property's two accessors, which export as
        // PROPERTY_TEXT files of their own now.
        Assert.Equal(6, manifest.Objects.Count(o => !string.IsNullOrEmpty(o.Parent)));

        Assert.Empty(plan.Warnings);
        Assert.Empty(plan.MissingFromMirror);
        Assert.DoesNotContain(plan.Files, f => f.Skip == PlannedSkip.Stray);
        Assert.DoesNotContain(plan.Files, f => f.Skip == PlannedSkip.UnknownForm);
        Assert.DoesNotContain(plan.Files, f => f.Action == PlannedAction.Create);

        // E1: the chain is the path — Pumps (a folder, and a real object), then FB_Pump —
        // and the recorded parent GUID is what says FB_Pump/ is an owner, not a folder.
        Assert.Equal(
            ["Device", "Plc Logic", "Application", "Pumps", "FB_Pump", "Start"],
            One(plan, "Device/Plc Logic/Application/Pumps/FB_Pump/Start.st").Resolve);

        // E3: the same for the ACTION, whose file carries the .action suffix.
        var stop = One(plan, "Device/Plc Logic/Application/Pumps/FB_Pump/Stop.action.st");
        Assert.Equal(
            ["Device", "Plc Logic", "Application", "Pumps", "FB_Pump", "Stop"], stop.Resolve);

        // A3: an implementation-only form is applied as text, not handed to import_native.
        Assert.Equal(PlannedAction.ApplyText, stop.Action);

        // The PROPERTY exports both halves, and NATIVE_R is the importable one — recursive,
        // carrying its own accessors. It is the form TASK_NATIVE was mistakenly recorded as
        // for a whole phase, and this is the first time the real one has been planned.
        var speed = One(plan, "Device/Plc Logic/Application/Pumps/FB_Pump/Speed.xml");
        Assert.Equal(PlannedAction.ApplyNative, speed.Action);
        Assert.Equal(
            ["Device", "Plc Logic", "Application", "Pumps", "FB_Pump", "Speed"], speed.Resolve);

        // The accessors ARE separate records since the property gained its readable half,
        // but their files skip as export-only — which is what stops them being applied
        // twice, once as text and again inside the recursive payload.
        Assert.Contains(manifest.Objects, o => o.Name == "Get");
        Assert.Contains(manifest.Objects, o => o.Name == "Set");
        var get = One(plan, "Device/Plc Logic/Application/Pumps/FB_Pump/Speed/Get.st");
        Assert.Equal(PlannedAction.Skip, get.Action);
        Assert.Equal(PlannedSkip.ExportOnly, get.Skip);

        // The owner itself is an ordinary POU under a folder, with no recorded parent —
        // the folder is a directory, not an owner.
        Assert.Equal(
            ["Device", "Plc Logic", "Application", "Pumps", "FB_Pump"],
            One(plan, "Device/Plc Logic/Application/Pumps/FB_Pump.st").Resolve);

        // And a DUT gives the declaration-only path a second subject that is not a GVL, so
        // G12’s fix and the ST_DECL form can be told apart.
        var dut = One(plan, "Device/Plc Logic/Application/E_Mode.st");
        Assert.Equal(PlannedAction.ApplyText, dut.Action);
        Assert.Equal(["Device", "Plc Logic", "Application", "E_Mode"], dut.Resolve);
    }

    /// <summary>
    /// E4: a digest-tail stem is never decoded — the recorded name carries the identity, and
    /// without a record the payload does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The first committed mirror ever to carry the shortened stem.</b> The member fixture
    /// gained two external-file objects on 2026-08-17 (P13 measured the door; the fixture
    /// builder walks through it with relative paths, which is what keeps the fixture
    /// machine-independent). One has a 105-character name, so its file on disk is
    /// <c>Commissioning_Checklist_…~ce8286.xml</c> — a stem that decodes to nothing, which is
    /// exactly why identity must come from the manifest and never from the file name.
    /// </para>
    /// <para>
    /// <b>Both of this test's first two runs corrected a claim, which is why its assertions
    /// look nothing like E4's original row.</b> The row said a stem that decodes to nothing
    /// would be "reported unresolvable"; run one expected a Stray and met the planner's
    /// deliberate rule that every unrecorded native is ApplyNative, because a native payload
    /// carries its own name and GUID. Run two — the E2E — then measured <c>import_native</c>
    /// <b>duplicating</b> an external-file object instead of replacing it, so the form became
    /// export-only (<c>EXTERNAL_NATIVE</c>) before the type ever shipped importable. What
    /// stands: <b>no path decodes a digest stem</b>. A recorded external file is an announced
    /// export-only skip carrying the verbatim name; an unrecorded native resolves by its
    /// payload; an unrecorded textual by its declaration (G3). The stem is a label, never an
    /// identity.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_digest_stem_resolves_through_the_manifest_and_strays_without_it()
    {
        var root = Path.Combine(TestPaths.Fixtures, "projects", "MachineWithMembers");
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

        // The fixture has to still BE the fixture: two external files, one of them past
        // MaxStem so its path took the digest tail. If a regeneration loses these, everything
        // below passes for the uninteresting reason.
        var externals = manifest.Objects.Where(o => o.Type == "EXTERNAL_FILE").ToList();
        Assert.Equal(2, externals.Count);
        var longRecord = Assert.Single(externals, o => o.Path.Contains('~', StringComparison.Ordinal));
        Assert.True(longRecord.Name.Length > 80, "the long name no longer overflows MaxStem");
        Assert.DoesNotContain(longRecord.Name, longRecord.Path, StringComparison.Ordinal);

        var plan = CodesysImportPlanner.Plan(manifest, mirror);

        // The stem cannot say "Commissioning_Checklist_…Final.txt"; only the manifest can —
        // and what the manifest says now is EXPORT-ONLY, because the first E2E run of this
        // row measured import_native DUPLICATING an external-file object instead of
        // replacing it. The skip still carries the verbatim recorded name, which is the
        // whole of E4's resolution claim: the digest stem was never the identity.
        Assert.Equal("EXTERNAL_NATIVE", longRecord.Form);
        var planned = One(plan, longRecord.Path);
        Assert.Equal(PlannedAction.Skip, planned.Action);
        Assert.Equal(PlannedSkip.ExportOnly, planned.Skip);
        Assert.Equal(longRecord.Name, planned.Name);

        // No importable sibling, unlike TASK_NATIVE and GVL_NATIVE: the object is entirely
        // IDE-managed, and a sibling pointer here would send somebody to a file that does
        // not exist.
        Assert.Null(planned.ImportableSibling);

        // The plain external file beside it: a name full of dots that is recorded verbatim
        // and never split into owner and member — the same rule E3 proves for members.
        var wiring = One(plan, "Device/Plc Logic/Application/Wiring Notes.txt.xml");
        Assert.Equal(PlannedAction.Skip, wiring.Action);
        Assert.Equal(PlannedSkip.ExportOnly, wiring.Skip);
        Assert.Equal("Wiring Notes.txt", wiring.Name);

        // WITHOUT ITS RECORD the digest file is still an unrecorded NATIVE, and the planner's
        // deliberate rule for those is ApplyNative — the payload carries its own name and
        // GUID, so import_native resolves it without the stem ever being read as a name.
        // What must NOT happen is a decode: the terminal label stays the raw tilde stem, not
        // a guessed reconstruction of the long name the digest destroyed, and no create is
        // planned for it — the stem is a label here, never an identity.
        var without = manifest with
        {
            Objects = [.. manifest.Objects.Where(o => o.Path != longRecord.Path)],
        };

        var replanned = CodesysImportPlanner.Plan(without, mirror);
        var unrecorded = One(replanned, longRecord.Path);
        Assert.Equal(PlannedAction.ApplyNative, unrecorded.Action);
        Assert.Equal("Commissioning_Checklist_Section_Section_Section_Section_Section_Section_"
                     + "Section_~ce8286", unrecorded.Resolve[^1]);
        Assert.NotEqual(longRecord.Name, unrecorded.Resolve[^1]);
        Assert.DoesNotContain(replanned.Files,
            f => f.Path == longRecord.Path && f.Action == PlannedAction.Create);
    }

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

        // THE ONLY WARNING THIS FIXTURE MAY EARN is the members refusal — a graphical POU
        // that owns a method, whose .xml holds the node alone, so applying it would replace
        // the POU and take the method with it (measured; see PlannedSkip.WouldDestroyMembers).
        //
        // Whether the fixture HAS such a POU depends on what the owner is building this week:
        // it had one on 2026-08-18 and not by the afternoon. So the assertion is that nothing
        // else warns — as strict as `Empty` for every other kind, and it does not go red
        // because somebody moved a method. The refusal itself is pinned synthetically by
        // A_native_payload_that_would_take_the_objects_members_with_it_is_refused, where the
        // manifest is this file's to control.
        Assert.All(plan.Warnings,
            w => Assert.Contains("would replace the object", w, StringComparison.Ordinal));

        Assert.All(plan.Files.Where(f => f.Skip == PlannedSkip.WouldDestroyMembers),
            f => Assert.Equal(PlannedAction.Skip, f.Action));

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

        // The GVL: one object, two files, one GUID — and exactly ONE of them applied.
        //
        // THIS ASSERTED ApplyNative FOR THE XML UNTIL 2026-08-16, and it was describing G12.
        // Both halves were importable and the import runs textual before native, so an edit to
        // the .gvl.st was applied to the object and then overwritten by the XML's copy of the
        // old declaration. The XML is recorded GVL_NATIVE now, which ExportCoverage.IsExportOnly
        // names — the same shape TASK_BOTH has had since Phase 4, two rows up.
        var gvlSt = One(plan, "Device/Plc Logic/Application/Glob_Var.gvl.st");
        var gvlXml = One(plan, "Device/Plc Logic/Application/Glob_Var.gvl.xml");
        Assert.Equal(PlannedAction.ApplyText, gvlSt.Action);
        Assert.Equal(PlannedAction.Skip, gvlXml.Action);
        Assert.Equal(PlannedSkip.ExportOnly, gvlXml.Skip);
        Assert.Equal(gvlSt.Guid, gvlXml.Guid);

        // And the skip names the half to edit instead, which is what keeps the log from
        // sending somebody to the IDE for a change they can make in the mirror.
        Assert.Equal("Device/Plc Logic/Application/Glob_Var.gvl.st", gvlXml.ImportableSibling);
    }
}
