using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Versio.Plc.TiaPortal.Host;
using Xunit;

namespace Versio.Plc.TiaPortal.Tests;

/// <summary>
/// The host's export at the level of one file, without a Portal in the room: what it does with
/// the file Openness wrote, and what it leaves when Openness refuses to write one. The host's
/// sources compile into this project (see the csproj), so these drive the real methods against
/// fakes. The walk's decision not to ask Portal at all is <see cref="TiaPortalHostExportTests"/>.
/// </summary>
/// <remarks>
/// Both rules were found on 2026-10-29 on the owner's project, from one screenshot: a second
/// export of an untouched project showed 1,354 files changed by the export stamp alone, and
/// the one block the owner had edited was missing from the mirror, because Portal refused to
/// export it uncompiled and the host had already deleted the previous file. See Findings.
/// </remarks>
[Collection(HostConsoleCollection.Name)]
public sealed class TiaPortalHostExportFileTests
{
    private const string Stamped =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
        + "<Document>\r\n"
        + "  <Engineering version=\"V20\" />\r\n"
        + "  <DocumentInfo>\r\n"
        + "    <Created>2026-10-29T20:49:45.6025282Z</Created>\r\n"
        + "    <ExportSetting>None</ExportSetting>\r\n"
        + "  </DocumentInfo>\r\n"
        + "  <SW.Blocks.OB ID=\"0\">\r\n"
        + "    <AttributeList>\r\n"
        + "      <Name>Main</Name>\r\n"
        + "    </AttributeList>\r\n"
        + "  </SW.Blocks.OB>\r\n"
        + "</Document>\r\n";

    [Fact]
    public void Stripping_the_stamp_removes_that_line_and_nothing_else()
    {
        var stripped = HostApp.StripExportStamp(Stamped);

        Assert.DoesNotContain("<Created>", stripped, StringComparison.Ordinal);
        Assert.Equal(Stamped.Replace("    <Created>2026-10-29T20:49:45.6025282Z</Created>\r\n", "", StringComparison.Ordinal), stripped);
        // Portal's line endings survive: the file stays Portal's bytes but for the one line.
        Assert.Contains("</DocumentInfo>\r\n", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", stripped, StringComparison.Ordinal);
        // A file without the stamp is left alone.
        Assert.Equal(stripped, HostApp.StripExportStamp(stripped));
    }

    [Fact]
    public void Only_the_headers_stamp_is_stripped_a_Created_line_in_the_body_is_content()
    {
        var withBody = Stamped.Replace(
            "      <Name>Main</Name>\r\n",
            "      <Name>Main</Name>\r\n      <Created>2026-01-01T00:00:00Z</Created>\r\n",
            StringComparison.Ordinal);

        var stripped = HostApp.StripExportStamp(withBody);

        Assert.DoesNotContain("<Created>2026-10-29", stripped, StringComparison.Ordinal);
        Assert.Contains("      <Created>2026-01-01T00:00:00Z</Created>\r\n", stripped, StringComparison.Ordinal);
        // No header at all: nothing to do, and the text is handed back as it came.
        const string headerless = "<Document><SW.Blocks.OB><Created>x</Created></SW.Blocks.OB></Document>";
        Assert.Same(headerless, HostApp.StripExportStamp(headerless));
    }

    [Fact]
    public void A_set_aside_left_by_a_dead_host_is_the_previous_export_and_is_put_back_first()
    {
        // The host died between moving Main.xml aside and Openness writing the new one: the
        // mirror has Main.xml.versio-previous and no Main.xml. On the next run that file is
        // the previous export, so a refusal still keeps it, and a success leaves no orphan.
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "Main.xml");
        File.WriteAllText(path + HostApp.SetAsideSuffix, "the export before the crash");

        Assert.Throws<System.Reflection.TargetInvocationException>(
            () => HostApp.ExportXml(new RefusingBlock(), path));
        Assert.Equal("the export before the crash", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(dir.Path));

        File.Delete(path);
        File.WriteAllText(path + HostApp.SetAsideSuffix, "the export before the crash");
        HostApp.ExportXml(new WritingBlock(), path);
        Assert.Equal("this export", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Scrubbing_the_file_keeps_its_byte_order_mark_and_its_line_endings()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "Main.xml");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Stamped)]);

        HostApp.ScrubExportedFile(path);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3));
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.DoesNotContain("<Created>", text, StringComparison.Ordinal);
        Assert.Contains("\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFEFF", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_exports_that_differ_only_by_their_stamp_hash_alike()
    {
        // The written file has no stamp and an in-session export has one; the deletion
        // check compares the two through this hash, so the stamp must not be in it.
        var later = Stamped.Replace("20:49:45.6025282Z", "21:03:12.0000000Z", StringComparison.Ordinal);
        Assert.Equal(HostApp.HashNormalized(Stamped), HostApp.HashNormalized(later));
        Assert.Equal(HostApp.HashNormalized(Stamped), HostApp.HashNormalized(HostApp.StripExportStamp(Stamped)));
        Assert.NotEqual(
            HostApp.HashNormalized(Stamped),
            HostApp.HashNormalized(Stamped.Replace("<Name>Main</Name>", "<Name>Main_1</Name>", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_refused_export_leaves_the_previous_file_exactly_as_it_was()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "Main.xml");
        File.WriteAllText(path, "the last export");

        var thrown = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => HostApp.ExportXml(new RefusingBlock(), path));

        Assert.Contains("Inconsistent blocks", thrown.InnerException?.Message, StringComparison.Ordinal);
        Assert.Equal("the last export", File.ReadAllText(path));
        Assert.False(File.Exists(path + HostApp.SetAsideSuffix), "the set-aside is put back, not left beside the file");
    }

    [Fact]
    public void A_refused_export_that_wrote_a_partial_file_discards_it_for_the_previous_one()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "Main.xml");
        File.WriteAllText(path, "the last export");

        Assert.Throws<System.Reflection.TargetInvocationException>(
            () => HostApp.ExportXml(new HalfWritingBlock(), path));

        Assert.Equal("the last export", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void A_successful_export_replaces_the_previous_file_and_leaves_no_set_aside()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "Main.xml");
        File.WriteAllText(path, "the last export");

        HostApp.ExportXml(new WritingBlock(), path);

        Assert.Equal("this export", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void A_first_export_has_nothing_to_set_aside()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "Program blocks", "Main.xml");

        HostApp.ExportXml(new WritingBlock(), path);

        Assert.Equal("this export", File.ReadAllText(path));
    }

    [Fact]
    public void The_previous_manifest_entries_for_an_object_are_found_by_path_or_by_identity()
    {
        var previous = new List<Dictionary<string, object?>>
        {
            Row("PLC/PLC/OB/Main#1", "PLC/PLC/Program blocks/Main.xml"),
            Row("PLC/PLC/FB/Motor#2", "PLC/PLC/Program blocks/Motor.scl"),
            Row("PLC/PLC/FB/Motor#2", "PLC/PLC/Program blocks/Motor.xml"),
            Row("PLC/PLC/FB/Valve#3", "PLC/PLC/Program blocks/Valve.scl"),
        };

        // A block's XML and the .scl beside it: both are the object's, both come forward.
        var motor = HostApp.PreviousEntriesFor(previous, "PLC/PLC/Program blocks/Motor.xml", "PLC/PLC/FB/Motor#2");
        Assert.Equal(["PLC/PLC/Program blocks/Motor.scl", "PLC/PLC/Program blocks/Motor.xml"], motor.Select(e => e["path"]));

        // Renumbered since the last export: the path still finds it.
        var main = HostApp.PreviousEntriesFor(previous, "PLC/PLC/Program blocks/Main.xml", "PLC/PLC/OB/Main#7");
        Assert.Equal(["PLC/PLC/Program blocks/Main.xml"], main.Select(e => e["path"]));

        Assert.Empty(HostApp.PreviousEntriesFor(previous, "PLC/PLC/Program blocks/New.xml", "PLC/PLC/FB/New#9"));
    }

    [Fact]
    public void The_previous_manifest_is_read_as_the_rows_this_export_writes_and_its_absence_is_nothing()
    {
        using var dir = new TempDir();
        var manifest = Path.Combine(dir.Path, "_manifest.json");
        Assert.Empty(HostApp.ReadPreviousManifest(manifest));

        File.WriteAllText(manifest, """
            {
              "schema": 9,
              "vendor": "TiaPortal",
              "objects": [
                { "guid": "PLC/PLC/OB/Main#1", "name": "Main", "type": "OB", "form": "NATIVE",
                  "path": "PLC/PLC/Program blocks/Main.xml", "hash": "sha256:a", "nhash": "sha256:n", "parent": "" }
              ],
              "skipped": [],
              "warnings": []
            }
            """);

        var rows = HostApp.ReadPreviousManifest(manifest);
        var row = Assert.Single(rows);
        Assert.Equal("PLC/PLC/OB/Main#1", row["guid"]);
        Assert.Equal("NATIVE", row["form"]);
        Assert.Equal("sha256:n", row["nhash"]);
        Assert.Equal("", row["parent"]);

        File.WriteAllText(manifest, "{ not json");
        Assert.Empty(HostApp.ReadPreviousManifest(manifest));
    }

    [Fact]
    public void A_block_Portal_refuses_keeps_its_last_export_and_its_rows_and_says_so_on_stdout()
    {
        using var dir = new TempDir();
        var mainPath = Path.Combine(dir.Path, "Dev", "PLC", "Program blocks", "Main.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(mainPath)!);
        File.WriteAllText(mainPath, "the last export");
        File.WriteAllText(Path.Combine(dir.Path, "_manifest.json"), PreviousManifest);
        var project = new FakeProject(new FakeDevice("Dev", new PlcSoftware("PLC", new FakeBlockGroup(new RefusingOB()))));

        var stdout = RunExport(project, dir.Path);

        // The file is as the last export left it, with nothing beside it.
        Assert.Equal("the last export", File.ReadAllText(mainPath));
        Assert.Empty(Directory.GetFiles(dir.Path, "*" + HostApp.SetAsideSuffix, SearchOption.AllDirectories));

        // The manifest still knows the object, by the rows the last export recorded.
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.Path, "_manifest.json")));
        var row = Assert.Single(manifest.RootElement.GetProperty("objects").EnumerateArray(),
            o => o.GetProperty("path").GetString() == "Dev/PLC/Program blocks/Main.xml");
        Assert.Equal("sha256:a", row.GetProperty("hash").GetString());
        Assert.Equal("Dev/PLC/OB/Main#1", row.GetProperty("guid").GetString());
        var skip = Assert.Single(manifest.RootElement.GetProperty("skipped").EnumerateArray(),
            s => s.GetProperty("reason").GetString() == "EXPORT_FAILED");
        Assert.Equal("Main", skip.GetProperty("path").GetString());

        // And the refusal reached the protocol, where the runner reads: a warning, not an object.
        var warning = Assert.Single(stdout, l => l.Contains("\"t\":\"warning\"", StringComparison.Ordinal)
                                              && l.Contains("Main.xml", StringComparison.Ordinal));
        Assert.Contains("Portal refused to export it", warning, StringComparison.Ordinal);
        Assert.Contains("Inconsistent blocks", warning, StringComparison.Ordinal);
        Assert.Contains("kept as it was", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(stdout, l => l.Contains("\"t\":\"object\"", StringComparison.Ordinal)
                                        && l.Contains("Main.xml", StringComparison.Ordinal));
    }

    [Fact]
    public void A_block_Portal_exports_lands_without_its_stamp_and_its_row_hashes_as_if_it_had_one()
    {
        using var dir = new TempDir();
        var project = new FakeProject(new FakeDevice("Dev", new PlcSoftware("PLC", new FakeBlockGroup(new WritingOB()))));

        var stdout = RunExport(project, dir.Path);

        var mainPath = Path.Combine(dir.Path, "Dev", "PLC", "Program blocks", "Main.xml");
        var bytes = File.ReadAllBytes(mainPath);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3));
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.DoesNotContain("<Created>", text, StringComparison.Ordinal);
        Assert.Contains("<ExportSetting>None</ExportSetting>\r\n", text, StringComparison.Ordinal);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.Path, "_manifest.json")));
        var row = Assert.Single(manifest.RootElement.GetProperty("objects").EnumerateArray(),
            o => o.GetProperty("path").GetString() == "Dev/PLC/Program blocks/Main.xml");
        Assert.Equal("NATIVE", row.GetProperty("form").GetString());
        // hash is the file as written (no stamp); nhash is what an in-session export (with
        // the stamp) will hash to, which is how the deletion check compares the two.
        Assert.Equal("sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), row.GetProperty("hash").GetString());
        Assert.Equal(HostApp.HashNormalized(Stamped), row.GetProperty("nhash").GetString());
        Assert.Contains(stdout, l => l.Contains("\"t\":\"object\"", StringComparison.Ordinal)
                                  && l.Contains("Dev/PLC/Program blocks/Main.xml", StringComparison.Ordinal));
    }

    /// <summary>Run the host's export with its protocol captured, one line per entry.</summary>
    private static List<string> RunExport(object project, string outDir)
    {
        var writer = new StringWriter();
        var original = Console.Out;
        Console.SetOut(writer);
        try
        {
            HostApp.Export(project, outDir, "TIA Portal V20; Siemens.Engineering 20.0.0.0");
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
    }

    private const string PreviousManifest = """
        {
          "schema": 9,
          "vendor": "TiaPortal",
          "objects": [
            { "guid": "Dev/PLC/OB/Main#1", "name": "Main", "type": "OB", "form": "NATIVE",
              "path": "Dev/PLC/Program blocks/Main.xml", "hash": "sha256:a", "nhash": "sha256:n", "parent": "" }
          ],
          "skipped": [],
          "warnings": []
        }
        """;

    // ---- fakes shaped like what the export walk reads by name ---------------------------
    //
    // WalkPlcSoftware takes Project.Devices, each device's DeviceItems, GetService<SoftwareContainer>()
    // (the stand-in type lives in TiaPortalHostImportTests.cs), and software named PlcSoftware;
    // WalkBlocks takes BlockGroup.Blocks and .Groups; ExportBlock reads Name, Number,
    // ProgrammingLanguage and IsKnowHowProtected, names the type from the object's type name
    // (an "OB" in it), and calls Export(FileInfo, <enum with None>).

    private sealed class FakeProject(params object[] devices)
    {
        public IReadOnlyList<object> Devices { get; } = devices;
    }

    private sealed class FakeDevice(string name, object software)
    {
        public string Name { get; } = name;

        public IReadOnlyList<object> DeviceItems { get; } = [];

        public T? GetService<T>()
            where T : class =>
            typeof(T) == typeof(Siemens.Engineering.HW.Features.SoftwareContainer)
                ? new Siemens.Engineering.HW.Features.SoftwareContainer { Software = software } as T
                : null;
    }

    private sealed class PlcSoftware(string name, object blockGroup)
    {
        public string Name { get; } = name;

        public object BlockGroup { get; } = blockGroup;

        public object? TypeGroup => null;

        public object? TagTableGroup => null;
    }

    private sealed class FakeBlockGroup(params object[] blocks)
    {
        public IReadOnlyList<object> Blocks { get; } = blocks;

        public IReadOnlyList<object> Groups { get; } = [];
    }

    /// <summary>A ladder OB Portal refuses: edited and not compiled since.</summary>
    private sealed class RefusingOB
    {
        public string Name => "Main";

        public int Number => 1;

        public string ProgrammingLanguage => "LAD";

        public bool IsKnowHowProtected => false;

        public void Export(FileInfo file, FakeExportOptions options) => new RefusingBlock().Export(file, options);
    }

    /// <summary>A ladder OB Portal exports, stamp and BOM and all.</summary>
    private sealed class WritingOB
    {
        public string Name => "Main";

        public int Number => 1;

        public string ProgrammingLanguage => "LAD";

        public bool IsKnowHowProtected => false;

        public void Export(FileInfo file, FakeExportOptions options)
        {
            _ = options;
            File.WriteAllBytes(file.FullName, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Stamped)]);
        }
    }

    private static Dictionary<string, object?> Row(string guid, string path) => new()
    {
        ["guid"] = guid,
        ["name"] = Path.GetFileNameWithoutExtension(path),
        ["type"] = "FB",
        ["form"] = "NATIVE",
        ["path"] = path,
        ["hash"] = "sha256:a",
        ["nhash"] = "sha256:n",
        ["parent"] = "",
    };

    /// <summary>What Portal's Openness does with a block edited and not compiled since.</summary>
    private sealed class RefusingBlock
    {
        public void Export(FileInfo file, FakeExportOptions options)
        {
            _ = file;
            _ = options;
            throw new InvalidOperationException(
                "Error when calling method 'Export' of type 'Siemens.Engineering.SW.Blocks.OB'.\r\n\r\n"
                + "Inconsistent blocks and PLC data types (UDT) cannot be exported.");
        }
    }

    private sealed class HalfWritingBlock
    {
        public void Export(FileInfo file, FakeExportOptions options)
        {
            _ = options;
            File.WriteAllText(file.FullName, "half of");
            throw new IOException("the disk went away");
        }
    }

    private sealed class WritingBlock
    {
        public void Export(FileInfo file, FakeExportOptions options)
        {
            _ = options;
            File.WriteAllText(file.FullName, "this export");
        }
    }

    /// <summary>Stands in for <c>Siemens.Engineering.ExportOptions</c>: the host parses "None" from it.</summary>
    private enum FakeExportOptions
    {
        None,
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "versio-host-export-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A handle still closing; the temp folder is nobody's data.
            }
        }
    }
}
