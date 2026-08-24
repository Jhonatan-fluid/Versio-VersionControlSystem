using Versio.Core.Plc;
using Versio.Core.Projects;
using Versio.Plc.TiaPortal;
using Versio.TestSupport;
using Xunit;

namespace Versio.Plc.TiaPortal.Tests;

public sealed class TiaPortalFormatTests
{
    [Fact]
    public void The_format_claims_the_working_extensions_and_refuses_archives()
    {
        var format = TiaPortalProjectFormat.Instance;
        Assert.Equal(PlcVendor.TiaPortal, format.Vendor);
        Assert.Contains(".ap21", format.ProjectExtensions);
        Assert.False(format.IsTemplate(@"plc\Line.ap21"));
        Assert.True(format.IsTemplate(@"plc\Line.al21"));
    }

    [Fact]
    public void The_payload_is_the_directory_containing_the_pointer_file()
    {
        var payload = TiaPortalProjectFormat.Instance.PayloadPathFor(@"C:\plc\Station\Station.ap21");
        Assert.Equal(@"C:\plc\Station", payload);
        Assert.False(TiaPortalProjectFormat.Instance.CountsTowardProjectHash("Logs/run.log"));
        Assert.False(TiaPortalProjectFormat.Instance.CountsTowardProjectHash("Vci/workspace.bin"));
        Assert.False(TiaPortalProjectFormat.Instance.CountsTowardProjectHash("Station.info"));
        Assert.False(TiaPortalProjectFormat.Instance.CountsTowardProjectHash("System/~PEData.1"));
        Assert.True(TiaPortalProjectFormat.Instance.CountsTowardProjectHash("System/PEData.plf"));
        Assert.Equal(@"C:\plc\Station\Station.info",
            TiaPortalProjectFormat.Instance.OccupancyMarkerFor(@"C:\plc\Station\Station.ap21"));
        Assert.Equal(OccupancyVerdict.Held,
            TiaPortalProjectFormat.Instance.ParseOccupancy("anything").Verdict);
        var transients = TiaPortalProjectFormat.Instance.TransientPatternsFor(@"C:\plc\Station\Station.ap21");
        Assert.Contains(transients, p => p.Replace('\\', '/').EndsWith("/Station.info", StringComparison.Ordinal));
        Assert.Contains(transients, p => p.Replace('\\', '/').EndsWith("/System/~*", StringComparison.Ordinal));
    }

    [Fact]
    public void Conventional_export_is_the_pointer_stem_not_a_hardcoded_project_suffix()
    {
        Assert.Equal("plc/Station",
            TiaPortalProjectFormat.Instance.ConventionalExportPath("plc/Station.ap21"));
        Assert.Null(TiaPortalProjectFormat.Instance.ConventionalTemplatePath("plc/Station.ap21"));
        Assert.Equal(TiaPortalLog.Category, TiaPortalProjectFormat.Instance.LogCategory);
    }

    [Fact]
    public void A_held_export_waits_rather_than_copying_the_directory_payload()
    {
        // Exclusive open, and the program is the folder. Copy-aside is the first
        // vendor's sidecar trick; this format refuses it so Sync waits both ways.
        Assert.False(TiaPortalProjectFormat.Instance.Capabilities.ExportHeldViaCopy);
    }
}

public sealed class TiaPortalSclHeaderTests
{
    [Fact]
    public void A_quoted_function_block_header_names_the_object()
    {
        const string scl = """
            FUNCTION_BLOCK "Motor"
            VAR
            END_VAR
            """;

        Assert.Equal("Motor", TiaPortalSclHeader.ObjectName(scl));
        Assert.Equal("FB", TiaPortalSclHeader.ObjectType(scl));
    }

    [Fact]
    public void A_file_named_wrong_does_not_supply_the_name()
    {
        Assert.Null(TiaPortalSclHeader.ObjectName("this is a comment\n// no header"));
    }

    [Fact]
    public void A_quoted_data_block_header_names_the_object()
    {
        const string scl = """
            DATA_BLOCK "ReportValues_DB"
            "ReportValues"
            BEGIN
            END_DATA_BLOCK
            """;

        Assert.Equal("ReportValues_DB", TiaPortalSclHeader.ObjectName(scl));
        Assert.Equal("DB", TiaPortalSclHeader.ObjectType(scl));
    }
}

public sealed class TiaPortalImportPlannerTests
{
    [Fact]
    public void Bookkeeping_and_review_xml_are_skipped()
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
                    Type = "FB",
                    Form = "SCL",
                    Path = "Device/PLC/Program blocks/Motor.scl",
                    Hash = "sha256:aaa",
                },
                new ProjectObject
                {
                    Guid = "uid-1",
                    Name = "Motor",
                    Type = "FB",
                    Form = ExportCoverage.NativeReviewForm,
                    Path = "Device/PLC/Program blocks/Motor.xml",
                    Hash = "sha256:bbb",
                    NormalizedHash = "sha256:n",
                },
            ],
        };

        var plan = TiaPortalImportPlanner.Plan(manifest,
        [
            new MirrorFile { Path = "_manifest.json" },
            new MirrorFile { Path = "Device/PLC/Program blocks/Motor.scl", Text = "FUNCTION_BLOCK \"Motor\"" },
            new MirrorFile { Path = "Device/PLC/Program blocks/Motor.xml" },
            new MirrorFile { Path = "README.md" },
        ]);

        Assert.Contains(plan.Files, f => f.Path == "_manifest.json" && f.Skip == PlannedSkip.Bookkeeping);
        Assert.Contains(plan.Files, f => f.Path.EndsWith("Motor.xml", StringComparison.Ordinal)
                                         && f.Skip == PlannedSkip.ExportOnly
                                         && f.ImportableSibling!.EndsWith(".scl", StringComparison.Ordinal));
        Assert.Contains(plan.Files, f => f.Path.EndsWith("Motor.scl", StringComparison.Ordinal)
                                         && f.Action == PlannedAction.ApplyText);
        Assert.Contains(plan.Files, f => f.Path == "README.md" && f.Skip == PlannedSkip.Stray);
    }

    [Fact]
    public void A_device_comment_catalog_is_applied_and_never_deleted()
    {
        var yaml = """
            schema: 1
            vendor: tia
            name: Station
            connectors:
              - connectorId: Station/CPU
                interface: CPU
                parameters:
                  Comment: hello
            """;
        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects =
            [
                new ProjectObject
                {
                    Guid = "device:Station/Station.devices.yaml",
                    Name = "Station",
                    Type = "DEVICE",
                    Form = ExportCoverage.DeviceCommentForm,
                    Path = "Station/Station.devices.yaml",
                    Hash = "sha256:aaa",
                },
            ],
        };

        var apply = TiaPortalImportPlanner.Plan(manifest,
        [
            new MirrorFile { Path = "Station/Station.devices.yaml", Text = yaml },
        ]);
        Assert.Contains(apply.Files, f => f.Path.EndsWith(".devices.yaml", StringComparison.Ordinal)
                                          && f.Action == PlannedAction.ApplyText);
        Assert.Empty(apply.Deletions);

        var gone = TiaPortalImportPlanner.Plan(manifest, [new MirrorFile { Path = "_manifest.json" }]);
        Assert.Empty(gone.Deletions);
    }

    [Fact]
    public void A_codesys_devices_form_is_not_applied_as_comment()
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

        var plan = TiaPortalImportPlanner.Plan(manifest,
        [
            new MirrorFile { Path = "Device/Coupler.devices.yaml" },
        ]);
        Assert.Contains(plan.Files, f => f.Path.EndsWith(".devices.yaml", StringComparison.Ordinal)
                                         && f.Skip == PlannedSkip.ExportOnly);
    }

    [Fact]
    public void The_host_device_comment_form_string_matches_core()
    {
        // HostDeviceCatalog writes this literal; it cannot reference ExportCoverage.
        Assert.Equal("DEVICE_COMMENT", ExportCoverage.DeviceCommentForm);
    }

    [Fact]
    public void The_host_records_db_source_as_the_review_form()
    {
        // HostExport writes this literal; it cannot reference ExportCoverage.
        Assert.Equal("SCL_REVIEW", ExportCoverage.SclReviewForm);
        var host = Path.Combine(TestPaths.Root, "src", "Versio.Plc.TiaPortal.Host", "HostExport.cs");
        Assert.True(File.Exists(host), host + " missing — the check would pass vacuously");
        var src = File.ReadAllText(host);
        Assert.Contains("\"SCL_REVIEW\"", src, StringComparison.Ordinal);
        Assert.Contains("IsDataBlock", src, StringComparison.Ordinal);
    }

    [Fact]
    public void A_data_block_scl_is_review_and_the_xml_imports()
    {
        // The inverse of G12 for DBs: GenerateSource is what a person reads, SimaticML
        // is what import applies. Making the .scl importable first is how property
        // text emptied every accessor.
        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects =
            [
                new ProjectObject
                {
                    Guid = "db-1",
                    Name = "ReportValues_DB",
                    Type = "DB",
                    Form = ExportCoverage.SclReviewForm,
                    Path = "Device/PLC/Program blocks/ReportValues_DB.scl",
                    Hash = "sha256:aaa",
                },
                new ProjectObject
                {
                    Guid = "db-1",
                    Name = "ReportValues_DB",
                    Type = "DB",
                    Form = "NATIVE",
                    Path = "Device/PLC/Program blocks/ReportValues_DB.xml",
                    Hash = "sha256:bbb",
                    NormalizedHash = "sha256:n",
                },
            ],
        };

        var plan = TiaPortalImportPlanner.Plan(manifest,
        [
            new MirrorFile
            {
                Path = "Device/PLC/Program blocks/ReportValues_DB.scl",
                Text = "DATA_BLOCK \"ReportValues_DB\"",
            },
            new MirrorFile { Path = "Device/PLC/Program blocks/ReportValues_DB.xml" },
        ]);

        Assert.Contains(plan.Files, f => f.Path.EndsWith("ReportValues_DB.scl", StringComparison.Ordinal)
                                         && f.Skip == PlannedSkip.ExportOnly
                                         && f.ImportableSibling!.EndsWith(".xml", StringComparison.Ordinal));
        Assert.Contains(plan.Files, f => f.Path.EndsWith("ReportValues_DB.xml", StringComparison.Ordinal)
                                         && f.Action == PlannedAction.ApplyNative);
        Assert.Empty(plan.Deletions);
    }

    [Fact]
    public void A_missing_scl_file_is_a_deletion_and_the_review_xml_does_not_vote()
    {
        var body = "FUNCTION_BLOCK \"Motor\"\nEND_FUNCTION_BLOCK";
        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects =
            [
                new ProjectObject
                {
                    Guid = "uid-1",
                    Name = "Motor",
                    Type = "FB",
                    Form = "SCL",
                    Path = "Device/PLC/Program blocks/Motor.scl",
                    Hash = Versio.Core.Hashing.ContentHash.Of(body),
                },
                new ProjectObject
                {
                    Guid = "uid-1",
                    Name = "Motor",
                    Type = "FB",
                    Form = ExportCoverage.NativeReviewForm,
                    Path = "Device/PLC/Program blocks/Motor.xml",
                    Hash = "sha256:bbb",
                    NormalizedHash = "sha256:n",
                },
            ],
        };

        var plan = TiaPortalImportPlanner.Plan(manifest,
        [
            new MirrorFile { Path = "_manifest.json" },
            new MirrorFile { Path = "Device/PLC/Program blocks/Motor.xml" },
        ]);

        var deletion = Assert.Single(plan.Deletions);
        Assert.Equal("uid-1", deletion.Guid);
        Assert.Equal("Motor", deletion.Name);
        Assert.Null(deletion.CannotVerify);
        Assert.Empty(plan.Renames);
    }

    [Fact]
    public void A_folder_move_of_identical_scl_is_a_rename_not_a_delete_and_create()
    {
        var body = "FUNCTION_BLOCK \"Motor\"\nEND_FUNCTION_BLOCK";
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
                    Type = "FB",
                    Form = "SCL",
                    Path = "Device/PLC/Program blocks/Motor.scl",
                    Hash = hash,
                },
            ],
        };

        var plan = TiaPortalImportPlanner.Plan(manifest,
        [
            new MirrorFile
            {
                Path = "Device/PLC/Library/Motor.scl",
                Text = body,
            },
        ]);

        var rename = Assert.Single(plan.Renames);
        Assert.Equal("uid-1", rename.Guid);
        Assert.Equal("Motor", rename.OldName);
        Assert.Equal("Motor", rename.NewName);
        Assert.True(rename.IsMove);
        Assert.False(rename.IsRename);
        Assert.Empty(plan.Deletions);
        Assert.Contains(plan.Files, f => f.Path.EndsWith("Motor.scl", StringComparison.Ordinal)
                                         && f.Skip == PlannedSkip.RenameTarget);
    }

    [Fact]
    public void A_renamed_and_edited_scl_file_pairs_from_departed_content()
    {
        var oldBody = """
            FUNCTION_BLOCK "Motor"
            VAR
            END_VAR
            Motor := TRUE;
            END_FUNCTION_BLOCK
            """;
        var newBody = """
            FUNCTION_BLOCK "Pump"
            VAR
            END_VAR
            Motor := TRUE;
            END_FUNCTION_BLOCK
            """;

        var manifest = new ProjectManifest
        {
            Schema = ExportCoverage.CurrentSchema,
            Objects =
            [
                new ProjectObject
                {
                    Guid = "uid-1",
                    Name = "Motor",
                    Type = "FB",
                    Form = "SCL",
                    Path = "Device/PLC/Program blocks/Motor.scl",
                    Hash = Versio.Core.Hashing.ContentHash.Of(oldBody),
                    LineSketch = Versio.Core.Diff.LineSketch.Of(oldBody),
                },
            ],
        };

        var plan = TiaPortalImportPlanner.Plan(
            manifest,
            [new MirrorFile { Path = "Device/PLC/Program blocks/Pump.scl", Text = newBody }],
            departed: new Dictionary<string, string>
            {
                ["Device/PLC/Program blocks/Motor.scl"] = oldBody,
            });

        var rename = Assert.Single(plan.Renames);
        Assert.Equal("Motor", rename.OldName);
        Assert.Equal("Pump", rename.NewName);
        Assert.True(rename.IsRename);
        Assert.Empty(plan.Deletions);
        Assert.Contains(plan.Files, f => f.Action == PlannedAction.ApplyText && f.Name == "Pump");
    }

    [Fact]
    public void A_new_scl_file_is_created_from_the_header_not_the_filename()
    {
        var plan = TiaPortalImportPlanner.Plan(
            new ProjectManifest { Schema = ExportCoverage.CurrentSchema, Objects = [] },
            [new MirrorFile { Path = "Bar.scl", Text = "FUNCTION_BLOCK \"Foo\"" }]);

        var file = Assert.Single(plan.Files, f => f.Action == PlannedAction.Create);
        Assert.Equal("Foo", file.Name);
    }
}

public sealed class TiaPortalProtocolTests
{
    [Fact]
    public void Protocol_lines_are_separated_from_chatter()
    {
        var output = TiaPortalProtocol.Parse(
            "licence nag\n@@VERSIO@@{\"t\":\"object\",\"path\":\"Motor.scl\"}\n@@VERSIO@@{\"t\":\"done\"}\n");

        Assert.Equal(["licence nag"], output.Chatter);
        Assert.Equal("object", output.Events[0].Type);
        Assert.Equal("Motor.scl", output.Events[0].String("path"));
        Assert.True(output.Completed);
    }
}

public sealed class TiaPortalObjectTypeNamesTests
{
    [Fact]
    public void Udt_and_tag_table_have_this_vendor_display_names()
    {
        var udt = TiaPortalObjectTypeNames.TryFor("UDT");
        Assert.NotNull(udt);
        Assert.Equal("UDT", udt.Value.Chip);
        Assert.Equal("PLC data type", udt.Value.One);

        var tags = TiaPortalObjectTypeNames.TryFor("TAG_TABLE");
        Assert.NotNull(tags);
        Assert.Equal("tag table", tags.Value.One);
        Assert.Null(TiaPortalObjectTypeNames.TryFor("NOPE"));
    }
}

public sealed class TiaPortalJobTests
{
    [Fact]
    public void An_import_job_without_a_plan_refuses_to_serialize()
    {
        var job = new TiaPortalJob
        {
            Operation = "import",
            ProjectPath = "Station.ap21",
            Path = "mirror",
            ApiFolder = "api",
        };

        Assert.Throws<InvalidOperationException>(() => job.ToJson());
    }

    [Fact]
    public void An_import_job_keeps_the_plan_on_the_wire()
    {
        var plan = new ImportPlan
        {
            Files =
            [
                new PlannedFile
                {
                    Path = "Device/PLC/Program blocks/Motor.scl",
                    Action = PlannedAction.ApplyText,
                    Name = "Motor",
                    Guid = "uid-1",
                },
            ],
        };

        var json = new TiaPortalJob
        {
            Operation = "import",
            ProjectPath = "Station.ap21",
            Path = "mirror",
            ApiFolder = "api",
            Plan = plan,
        }.ToJson();

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("import", doc.RootElement.GetProperty("op").GetString());
        Assert.True(doc.RootElement.TryGetProperty("plan", out var wire));
        Assert.Equal("Device/PLC/Program blocks/Motor.scl",
            wire.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal("ApplyText", wire.GetProperty("files")[0].GetProperty("action").GetString());
    }
}

public sealed class TiaPortalNativeScrubberTests
{
    [Fact]
    public void Date_attributes_do_not_change_the_comparison_hash()
    {
        const string a = """<Block DateModified="1"><Body/></Block>""";
        const string b = """<Block DateModified="2"><Body/></Block>""";
        Assert.Equal(TiaPortalNativeScrubber.Hash(a), TiaPortalNativeScrubber.Hash(b));
        Assert.Null(TiaPortalNativeScrubber.TryReadUid("""<Block UId="7"/>"""));
        Assert.Null(TiaPortalNativeScrubber.TryReadUid("""<Doc UId="0"><Block UId="1"/></Doc>"""));
        Assert.Null(TiaPortalNativeScrubber.TryReadUid("""<Block UId="0"/>"""));
        Assert.Equal("a1b2c3d4-e5f6-7890-abcd-ef1234567890",
            TiaPortalNativeScrubber.TryReadUid("""<Block UId="a1b2c3d4-e5f6-7890-abcd-ef1234567890"/>"""));
    }
}
