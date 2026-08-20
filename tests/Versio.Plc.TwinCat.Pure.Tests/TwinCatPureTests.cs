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
    }

    [Fact]
    public void A_held_export_waits_rather_than_copying_the_directory_payload()
    {
        Assert.False(TwinCatProjectFormat.Instance.Capabilities.ExportHeldViaCopy);
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
}
