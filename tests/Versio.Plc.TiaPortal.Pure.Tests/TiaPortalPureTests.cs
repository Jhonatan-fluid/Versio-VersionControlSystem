using Versio.Core.Plc;
using Versio.Core.Projects;
using Versio.Plc.TiaPortal;
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
    }
}

public sealed class TiaSclHeaderTests
{
    [Fact]
    public void A_quoted_function_block_header_names_the_object()
    {
        const string scl = """
            FUNCTION_BLOCK "Motor"
            VAR
            END_VAR
            """;

        Assert.Equal("Motor", TiaSclHeader.ObjectName(scl));
        Assert.Equal("FB", TiaSclHeader.ObjectType(scl));
    }

    [Fact]
    public void A_file_named_wrong_does_not_supply_the_name()
    {
        Assert.Null(TiaSclHeader.ObjectName("this is a comment\n// no header"));
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

public sealed class TiaProtocolTests
{
    [Fact]
    public void Protocol_lines_are_separated_from_chatter()
    {
        var output = TiaProtocol.Parse(
            "licence nag\n@@VERSIO@@{\"t\":\"object\",\"path\":\"Motor.scl\"}\n@@VERSIO@@{\"t\":\"done\"}\n");

        Assert.Equal(["licence nag"], output.Chatter);
        Assert.Equal("object", output.Events[0].Type);
        Assert.Equal("Motor.scl", output.Events[0].String("path"));
        Assert.True(output.Completed);
    }
}

public sealed class TiaJobTests
{
    [Fact]
    public void An_import_job_without_a_plan_refuses_to_serialize()
    {
        var job = new TiaJob
        {
            Operation = "import",
            ProjectPath = "Station.ap21",
            Path = "mirror",
            ApiFolder = "api",
        };

        Assert.Throws<InvalidOperationException>(() => job.ToJson());
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
