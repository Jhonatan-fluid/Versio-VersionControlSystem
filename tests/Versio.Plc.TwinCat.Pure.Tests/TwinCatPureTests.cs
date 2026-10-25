using Versio.Core.Plc;
using Versio.Core.Projects;
using Versio.Plc.TwinCat;
using Xunit;

namespace Versio.Plc.TwinCat.Tests;

public sealed class TwinCatFormatTests
{
    [Fact]
    public void The_format_claims_the_working_extensions_and_refuses_archives()
    {
        var format = TwinCatProjectFormat.Instance;
        Assert.Equal(PlcVendor.TwinCat, format.Vendor);
        Assert.Contains(".tsproj", format.ProjectExtensions);
        Assert.False(format.IsTemplate(@"plc\Line.tsproj"));
        Assert.True(format.IsTemplate(@"plc\Line.tszip"));
        Assert.True(format.IsTemplate(@"plc\Line.tspproj"));
    }

    [Fact]
    public void The_payload_is_the_directory_containing_the_pointer_file()
    {
        var payload = TwinCatProjectFormat.Instance.PayloadPathFor(@"C:\plc\Station\Station.tsproj");
        Assert.Equal(@"C:\plc\Station", payload);
        Assert.False(TwinCatProjectFormat.Instance.CountsTowardProjectHash("_Boot/image.bin"));
        Assert.False(TwinCatProjectFormat.Instance.CountsTowardProjectHash("_CompileInfo/x"));
        Assert.False(TwinCatProjectFormat.Instance.CountsTowardProjectHash(".vs/slnx.sqlite"));
        Assert.False(TwinCatProjectFormat.Instance.CountsTowardProjectHash("Station.~u"));
        // The transient pattern claims "stem.~u*"; the hash filter must agree, or an
        // in-flight marker variant makes an unchanged project hash as changed.
        Assert.False(TwinCatProjectFormat.Instance.CountsTowardProjectHash("Station.~u2"));
        Assert.False(TwinCatProjectFormat.Instance.CountsTowardProjectHash("LineIDs.dbg"));
        Assert.True(TwinCatProjectFormat.Instance.CountsTowardProjectHash("LibraryDocumentationSample/MAIN.TcPOU"));
        Assert.Equal(@"C:\plc\Station\Station.~u",
            TwinCatProjectFormat.Instance.OccupancyMarkerFor(@"C:\plc\Station\Station.tsproj"));
        Assert.Equal(OccupancyVerdict.Held,
            TwinCatProjectFormat.Instance.ParseOccupancy("anything").Verdict);
        var transients = TwinCatProjectFormat.Instance.TransientPatternsFor(@"C:\plc\Station\Station.tsproj");
        Assert.Contains(transients, p => p.Replace('\\', '/').Contains("/_Boot/", StringComparison.Ordinal));
        Assert.Contains(transients, p => p.Replace('\\', '/').Contains(".~u", StringComparison.Ordinal));
        Assert.Contains(transients, p => p.Replace('\\', '/').Contains("LineIDs.dbg", StringComparison.Ordinal));
    }

    [Fact]
    public void Conventional_export_is_the_pointer_stem_not_a_hardcoded_project_suffix()
    {
        Assert.Equal("plc/Station",
            TwinCatProjectFormat.Instance.ConventionalExportPath("plc/Station.tsproj"));
        Assert.Null(TwinCatProjectFormat.Instance.ConventionalTemplatePath("plc/Station.tsproj"));
        Assert.Equal(TwinCatLog.Category, TwinCatProjectFormat.Instance.LogCategory);
    }

    [Fact]
    public void A_held_export_copies_the_directory_payload()
    {
        // Occupancy is a path sidecar (.~u). Sync copies the folder and exports
        // the copy, the same seam the first vendor uses for a held .project.
        Assert.True(TwinCatProjectFormat.Instance.Capabilities.ExportHeldViaCopy);
    }
}

public sealed class TwinCatStHeaderTests
{
    [Fact]
    public void A_function_block_header_names_the_object()
    {
        const string st = """
            FUNCTION_BLOCK Motor
            VAR
            END_VAR
            """;

        Assert.Equal("Motor", TwinCatStHeader.ObjectName(st));
        Assert.Equal("POU", TwinCatStHeader.ObjectType(st));
        Assert.True(TwinCatStHeader.CanCreate(st));
    }

    [Fact]
    public void A_file_named_wrong_does_not_supply_the_name()
    {
        Assert.Null(TwinCatStHeader.ObjectName("this is a comment\n// no header"));
        Assert.False(TwinCatStHeader.CanCreate("this is a comment"));
    }
}

public sealed class TwinCatImportPlannerTests
{
    [Fact]
    public void Bookkeeping_is_skipped_and_st_is_applied()
    {
        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects =
            [
                new ProjectObject
                {
                    Guid = "TIPC^PLC^PLC Project^Motor",
                    Name = "Motor",
                    Type = "POU",
                    Form = "ST_FULL",
                    Path = "PLC/Motor.st",
                    Hash = "sha256:aaa",
                },
            ],
        };

        var plan = TwinCatImportPlanner.Plan(manifest,
        [
            new MirrorFile { Path = "_manifest.json" },
            new MirrorFile { Path = "PLC/Motor.st", Text = "FUNCTION_BLOCK Motor" },
            new MirrorFile { Path = "README.md" },
        ]);

        Assert.Contains(plan.Files, f => f.Path == "_manifest.json" && f.Skip == PlannedSkip.Bookkeeping);
        Assert.Contains(plan.Files, f => f.Path.EndsWith("Motor.st", StringComparison.Ordinal)
                                         && f.Action == PlannedAction.ApplyText);
        Assert.Contains(plan.Files, f => f.Path == "README.md" && f.Skip == PlannedSkip.Stray);
    }

    [Fact]
    public void A_missing_st_file_is_a_deletion()
    {
        var body = "FUNCTION_BLOCK Motor\nEND_FUNCTION_BLOCK";
        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects =
            [
                new ProjectObject
                {
                    Guid = "uid-1",
                    Name = "Motor",
                    Type = "POU",
                    Form = "ST_FULL",
                    Path = "PLC/Motor.st",
                    Hash = Versio.Core.Hashing.ContentHash.Of(body),
                },
            ],
        };

        var plan = TwinCatImportPlanner.Plan(manifest,
        [
            new MirrorFile { Path = "_manifest.json" },
        ]);

        var deletion = Assert.Single(plan.Deletions);
        Assert.Equal("uid-1", deletion.Guid);
        Assert.Equal("Motor", deletion.Name);
        Assert.Null(deletion.CannotVerify);
        Assert.Empty(plan.Renames);
    }

    [Fact]
    public void A_folder_move_of_identical_st_is_a_rename_not_a_delete_and_create()
    {
        var body = "FUNCTION_BLOCK Motor\nEND_FUNCTION_BLOCK";
        var hash = Versio.Core.Hashing.ContentHash.Of(body);
        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects =
            [
                new ProjectObject
                {
                    Guid = "uid-1",
                    Name = "Motor",
                    Type = "POU",
                    Form = "ST_FULL",
                    Path = "PLC/Motor.st",
                    Hash = hash,
                },
            ],
        };

        var plan = TwinCatImportPlanner.Plan(manifest,
        [
            new MirrorFile
            {
                Path = "PLC/Library/Motor.st",
                Text = body,
            },
        ]);

        Assert.Empty(plan.Deletions);
        var rename = Assert.Single(plan.Renames);
        Assert.Equal("Motor", rename.OldName);
        Assert.Equal("Motor", rename.NewName);
        Assert.True(rename.IsMove);
        Assert.Contains(plan.Files, f => f.Path.EndsWith("Motor.st", StringComparison.Ordinal)
                                         && f.Skip == PlannedSkip.RenameTarget);
    }

    [Fact]
    public void A_new_st_file_with_a_header_is_a_create()
    {
        var plan = TwinCatImportPlanner.Plan(
            new ProjectManifest { Schema = ExportCoverage.CurrentSchema, Objects = [] },
            [
                new MirrorFile
                {
                    Path = "PLC/Pump.st",
                    Text = "FUNCTION_BLOCK Pump\nEND_FUNCTION_BLOCK",
                },
            ]);

        var created = Assert.Single(plan.Files, f => f.Action == PlannedAction.Create);
        Assert.Equal("Pump", created.Name);
    }

    // The refusal paths below are the planner's protective guards. They are pinned on
    // purpose: gutting the trusted-manifest gate or an ambiguity refusal used to leave
    // every test in this file green, which is exactly the silent-coverage-loss shape
    // CLAUDE.md warns about.

    [Fact]
    public void No_manifest_means_no_deletions_and_a_sentence_saying_why()
    {
        var plan = TwinCatImportPlanner.Plan(null,
        [
            new MirrorFile { Path = "PLC/Motor.st", Text = "FUNCTION_BLOCK Motor" },
        ]);

        Assert.Empty(plan.Deletions);
        Assert.Empty(plan.Renames);
        Assert.NotNull(plan.StructuralChangesUnavailable);
    }

    [Fact]
    public void A_pre_reporting_schema_manifest_cannot_plan_structural_changes()
    {
        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.ReportingSchema - 1,
            Objects =
            [
                new ProjectObject
                {
                    Guid = "uid-1",
                    Name = "Motor",
                    Type = "POU",
                    Form = "ST_FULL",
                    Path = "PLC/Motor.st",
                    Hash = "sha256:aaa",
                },
            ],
        };

        // The object's file is absent from the mirror — but an untrusted manifest may
        // not turn that absence into a deletion.
        var plan = TwinCatImportPlanner.Plan(manifest, []);
        Assert.Empty(plan.Deletions);
        Assert.NotNull(plan.StructuralChangesUnavailable);
    }

    [Fact]
    public void A_stray_st_without_a_header_is_refused_not_created()
    {
        var plan = TwinCatImportPlanner.Plan(
            new ProjectManifest { Schema = ExportCoverage.CurrentSchema, Objects = [] },
            [
                new MirrorFile { Path = "PLC/notes.st", Text = "// just a comment\n" },
            ]);

        var refused = Assert.Single(plan.Files, f => f.Path == "PLC/notes.st");
        Assert.Equal(PlannedAction.Skip, refused.Action);
        Assert.Equal(PlannedSkip.Stray, refused.Skip);
        Assert.Contains(plan.Warnings,
            w => w.Contains("create refused", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_new_files_holding_the_departed_content_refuse_the_rename_guess()
    {
        var body = "FUNCTION_BLOCK Motor\nEND_FUNCTION_BLOCK";
        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects =
            [
                new ProjectObject
                {
                    Guid = "uid-1",
                    Name = "Motor",
                    Type = "POU",
                    Form = "ST_FULL",
                    Path = "PLC/Motor.st",
                    Hash = Versio.Core.Hashing.ContentHash.Of(body),
                },
            ],
        };

        var plan = TwinCatImportPlanner.Plan(manifest,
        [
            new MirrorFile { Path = "PLC/A/Motor.st", Text = body },
            new MirrorFile { Path = "PLC/B/Motor.st", Text = body },
        ]);

        Assert.Single(plan.Deletions);
        Assert.Empty(plan.Renames);
        Assert.Contains(plan.Warnings,
            w => w.Contains("cannot be told", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_departed_objects_with_one_arrival_refuse_the_rename_guess()
    {
        var body = "FUNCTION_BLOCK Motor\nEND_FUNCTION_BLOCK";
        var hash = Versio.Core.Hashing.ContentHash.Of(body);
        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects =
            [
                new ProjectObject
                {
                    Guid = "uid-1",
                    Name = "MotorA",
                    Type = "POU",
                    Form = "ST_FULL",
                    Path = "PLC/MotorA.st",
                    Hash = hash,
                },
                new ProjectObject
                {
                    Guid = "uid-2",
                    Name = "MotorB",
                    Type = "POU",
                    Form = "ST_FULL",
                    Path = "PLC/MotorB.st",
                    Hash = hash,
                },
            ],
        };

        var plan = TwinCatImportPlanner.Plan(manifest,
        [
            new MirrorFile { Path = "PLC/Lib/Motor.st", Text = body },
        ]);

        Assert.Equal(2, plan.Deletions.Count);
        Assert.Empty(plan.Renames);
        Assert.Contains(plan.Warnings,
            w => w.Contains("cannot be told", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hashless_manifest_object_cannot_verify_its_deletion()
    {
        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects =
            [
                new ProjectObject
                {
                    Guid = "uid-1",
                    Name = "Motor",
                    Type = "POU",
                    Form = "ST_FULL",
                    Path = "PLC/Motor.st",
                    Hash = "",
                },
            ],
        };

        var plan = TwinCatImportPlanner.Plan(manifest, []);
        var deletion = Assert.Single(plan.Deletions);
        Assert.NotNull(deletion.CannotVerify);
    }

    [Fact]
    public void An_export_only_form_is_skipped_not_applied()
    {
        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects =
            [
                new ProjectObject
                {
                    Guid = "uid-1",
                    Name = "PlcTask",
                    Type = "TASK",
                    Form = ExportCoverage.TaskNativeForm,
                    Path = "PLC/PlcTask.xml",
                    Hash = "sha256:aaa",
                },
            ],
        };

        var plan = TwinCatImportPlanner.Plan(manifest,
        [
            new MirrorFile { Path = "PLC/PlcTask.xml" },
        ]);

        var skipped = Assert.Single(plan.Files, f => f.Path == "PLC/PlcTask.xml");
        Assert.Equal(PlannedAction.Skip, skipped.Action);
        Assert.Equal(PlannedSkip.ExportOnly, skipped.Skip);
        Assert.Empty(plan.Deletions);
    }

    [Fact]
    public void A_tia_device_comment_form_is_not_applied_and_never_deleted()
    {
        // TwinCAT product writes DEVICES. DEVICE_COMMENT is TIA's measured path;
        // planning ApplyText here would disagree with the host, which throws.
        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects =
            [
                new ProjectObject
                {
                    Guid = "device:Devices/Devices.devices.yaml",
                    Name = "Devices",
                    Type = "DEVICE",
                    Form = ExportCoverage.DeviceCommentForm,
                    Path = "Devices/Devices.devices.yaml",
                    Hash = "sha256:aaa",
                },
            ],
        };

        var apply = TwinCatImportPlanner.Plan(manifest,
        [
            new MirrorFile { Path = "Devices/Devices.devices.yaml" },
        ]);
        var file = Assert.Single(apply.Files, f => f.Path.EndsWith(".devices.yaml", StringComparison.Ordinal));
        Assert.Equal(PlannedAction.Skip, file.Action);
        Assert.Empty(apply.Deletions);

        var gone = TwinCatImportPlanner.Plan(manifest, [new MirrorFile { Path = "_manifest.json" }]);
        Assert.Empty(gone.Deletions);
    }

    [Fact]
    public void A_devices_form_catalog_is_export_only()
    {
        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects =
            [
                new ProjectObject
                {
                    Guid = "g",
                    Name = "Coupler",
                    Type = "DEVICE",
                    Form = ExportCoverage.DevicesForm,
                    Path = "Device/Coupler.devices.yaml",
                    Hash = "sha256:aaa",
                },
            ],
        };

        var plan = TwinCatImportPlanner.Plan(manifest,
        [
            new MirrorFile { Path = "Device/Coupler.devices.yaml" },
        ]);
        Assert.Contains(plan.Files, f => f.Path.EndsWith(".devices.yaml", StringComparison.Ordinal)
                                         && f.Skip == PlannedSkip.ExportOnly);
        Assert.Empty(plan.Deletions);

        var gone = TwinCatImportPlanner.Plan(manifest, [new MirrorFile { Path = "_manifest.json" }]);
        Assert.Empty(gone.Deletions);
    }

    [Fact]
    public void The_host_devices_form_string_matches_core()
    {
        // HostDeviceCatalog writes this literal; it cannot reference ExportCoverage.
        Assert.Equal("DEVICES", ExportCoverage.DevicesForm);
    }
}

public sealed class TwinCatHostTextTests
{
    [Fact]
    public void An_empty_body_in_the_file_clears_a_body_the_object_still_has()
    {
        // The host skipped the write whenever the file had no body — right for a DUT,
        // wrong for a function block whose body the engineer deleted: the old logic
        // stayed in the project and the import reported success (the 2026-10-24 review's
        // item 23).
        Assert.Equal(Versio.Plc.TwinCat.Host.HostText.ImplementationWrite.Clear,
            Versio.Plc.TwinCat.Host.HostText.ImplementationWriteFor("", "x := 1;"));
        Assert.Equal(Versio.Plc.TwinCat.Host.HostText.ImplementationWrite.Leave,
            Versio.Plc.TwinCat.Host.HostText.ImplementationWriteFor("", null));
        Assert.Equal(Versio.Plc.TwinCat.Host.HostText.ImplementationWrite.Leave,
            Versio.Plc.TwinCat.Host.HostText.ImplementationWriteFor("", "  \n"));
        Assert.Equal(Versio.Plc.TwinCat.Host.HostText.ImplementationWrite.Set,
            Versio.Plc.TwinCat.Host.HostText.ImplementationWriteFor("x := 2;", "x := 1;"));
    }

    [Fact]
    public void The_read_back_checks_the_body_both_ways()
    {
        // A cleared body must read back empty; a written one must contain its first code
        // line. The declaration was checked and the body was not, which is how a failed
        // clear passed as success.
        Assert.True(Versio.Plc.TwinCat.Host.HostText.ImplementationTook("", null));
        Assert.True(Versio.Plc.TwinCat.Host.HostText.ImplementationTook("", ""));
        Assert.False(Versio.Plc.TwinCat.Host.HostText.ImplementationTook("", "x := 1;"));
        Assert.True(Versio.Plc.TwinCat.Host.HostText.ImplementationTook("// why\nx := 2;", "x := 2;\n"));
        Assert.False(Versio.Plc.TwinCat.Host.HostText.ImplementationTook("x := 2;", "x := 1;"));
        Assert.True(Versio.Plc.TwinCat.Host.HostText.ImplementationTook("// only a comment", "(* only a comment *)"));
    }

    [Fact]
    public void The_host_codec_splits_exactly_as_the_core_codec_does()
    {
        // HostText duplicates StTextCodec's delimiter and escape because the host cannot
        // reference Core; this is the check that the two have not drifted.
        var packed = Versio.Core.Iec.StTextCodec.Join(
            "FUNCTION_BLOCK FB\n// --- BEGIN IMPLEMENTATION ---\nVAR x : INT; END_VAR",
            "x := 1; // //!VERSIO-ESC!D");
        var core = Versio.Core.Iec.StTextCodec.Split(packed);
        var host = Versio.Plc.TwinCat.Host.HostText.Split(packed);

        Assert.Equal(core.Declaration, host.Declaration);
        Assert.Equal(core.Implementation, host.Implementation);
        Assert.Equal(packed, Versio.Plc.TwinCat.Host.HostText.Join(core.Declaration, core.Implementation));
    }
}

public sealed class TwinCatProtocolTests
{
    [Fact]
    public void The_shared_marker_is_parsed_and_chatter_is_kept()
    {
        var output = TwinCatProtocol.Parse(
            "noise\n@@VERSIO@@{\"t\":\"done\",\"ok\":\"true\"}\nmore\n");
        Assert.True(output.Completed);
        Assert.Contains("noise", output.Chatter);
        Assert.Contains("more", output.Chatter);
    }

    [Fact]
    public void The_host_names_the_xae_it_created()
    {
        // ide_pid is what lets Stop reap the one XAE this run started rather than every XAE
        // that appeared during it (the 2026-10-24 review's item 28).
        var output = TwinCatProtocol.Parse("@@VERSIO@@{\"t\":\"ide_pid\",\"pid\":\"4242\"}\n");

        var evt = Assert.Single(output.Events);
        Assert.Equal("ide_pid", evt.Type);
        Assert.Equal("4242", evt.String("pid"));
    }
}
