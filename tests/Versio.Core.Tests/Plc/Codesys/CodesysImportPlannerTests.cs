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

            // All three files, not just the one being resolved. The listing used to carry
            // only the accessor, which is a mirror that cannot exist — and once deletions
            // were planned it stopped being harmless: the two absent ancestors read as
            // objects somebody had removed. Every test here describes a real directory now.
            [File("App/clsLight.xml"),
             File("App/clsLight.Bright.xml"),
             File("App/clsLight.Bright.Get.xml")]);

        Assert.Equal(["App", "clsLight", "Bright", "Get"],
                     One(plan, "App/clsLight.Bright.Get.xml").Resolve);
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

    // ── Deletions, and every precondition that stops one ───────────────────────
    //
    // P3 measured what remove() does to an object other objects reference: it removes it,
    // saves cleanly, and warns about nothing. There is no toolchain backstop, so each rule
    // below is not belt-and-braces — it is the only thing between a stale mirror and somebody
    // losing work. Each has its own test for that reason.

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
