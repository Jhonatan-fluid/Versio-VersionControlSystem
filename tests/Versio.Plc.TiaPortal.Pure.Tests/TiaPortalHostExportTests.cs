using System.Text;
using System.Text.Json;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Versio.Plc.TiaPortal;
using Versio.Plc.TiaPortal.Host;
using Xunit;

namespace Versio.Plc.TiaPortal.Tests;

/// <summary>
/// Every test class that drives the host in-process, serialised: the host writes its protocol
/// to <c>Console.Out</c>, which is one stream per process, and two classes swapping it under
/// each other interleave their lines — two of nine export tests went red on the first full run
/// of the suite and green alone, 2026-10-29.
/// </summary>
[CollectionDefinition(Name)]
public sealed class HostConsoleCollection
{
    public const string Name = "tia-host-console";
}

/// <summary>
/// The host's export walk, driven without a Portal: fakes that count how often Portal is
/// asked for each object, so the one thing the real gate cannot see — whether a re-export
/// <i>asked</i> for an unchanged object — is pinned here.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> The skip is a decision not to call <c>Export</c> and
/// <c>GenerateSource</c>, and its failure modes are silent in both directions: a skip that
/// never fires costs two minutes per save and the gate stays green; a skip that fires wrongly
/// leaves a stale mirror and the gate's unchanged fixture cannot tell. A fake block that
/// counts its calls can tell both. Every case here is one of the conditions
/// <see cref="ExportBaseline"/> states, exercised through the walk rather than the class, so a
/// condition the walk stopped consulting would go red.
/// </remarks>
[Collection(HostConsoleCollection.Name)]
public sealed class TiaPortalHostExportTests
{
    private const string Portal = "TIA Portal V21; Siemens.Engineering.Base 21.0.0.0";

    private static readonly object ConsoleGate = new();

    private static readonly DateTime Stamp1 = new(2026, 10, 29, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_first_export_asks_Portal_for_everything_and_records_stamps()
    {
        using var dir = new TempDir();
        var fx = new Fixture();

        var output = Export(fx.Project, dir.Path);

        Assert.Equal(1, fx.Pump.Exports);
        Assert.Equal(1, fx.Pump.Generates);
        Assert.Equal(1, fx.Valve.Exports);
        Assert.Equal(1, fx.Udt.Exports);
        Assert.Equal(1, fx.Udt.Generates);
        var manifest = Manifest(dir.Path);
        Assert.Equal(ExportBaseline.StampSchema, manifest.Schema);
        Assert.Equal(Portal, manifest.ToolchainVersion);
        Assert.Equal(PlcVendor.TiaPortal, manifest.Vendor);
        var pump = manifest.Objects.Where(o => o.Guid == "Dev/PLC/FB/Pump#3").ToList();
        Assert.Equal(2, pump.Count);
        Assert.All(pump, o => Assert.Equal(Stamp1.ToString("o"), o.Stamp));
        Assert.Contains("full export: no previous manifest", Summary(output));
        Assert.Contains("3 blocks and types exported (full export)", Summary(output));
    }

    [Fact]
    public void A_second_export_of_an_unchanged_project_asks_Portal_for_nothing_and_keeps_the_records()
    {
        using var dir = new TempDir();
        var fx = new Fixture();
        Export(fx.Project, dir.Path);
        var first = File.ReadAllText(Path.Combine(dir.Path, "_manifest.json"));
        var pumpScl = File.ReadAllText(Path.Combine(dir.Path, "Dev", "PLC", "Program blocks", "Pump.scl"));

        var output = Export(fx.Project, dir.Path);

        Assert.Equal(1, fx.Pump.Exports);
        Assert.Equal(1, fx.Pump.Generates);
        Assert.Equal(1, fx.Valve.Exports);
        Assert.Equal(1, fx.Udt.Exports);
        Assert.Equal(1, fx.Udt.Generates);
        // Same records, same files: the manifest is byte-identical and the mirror untouched.
        Assert.Equal(first, File.ReadAllText(Path.Combine(dir.Path, "_manifest.json")));
        Assert.Equal(pumpScl, File.ReadAllText(Path.Combine(dir.Path, "Dev", "PLC", "Program blocks", "Pump.scl")));
        // No `object` event for a kept object: the runner's ChangedPaths are what was written.
        Assert.DoesNotContain(output.Events, e => e.Type == "object" && (e.String("path") ?? "").EndsWith(".scl", StringComparison.Ordinal));
        Assert.Contains("Dev/PLC: 3 blocks and types: 3 kept (unchanged since the last export), 0 exported", Summary(output));
    }

    [Fact]
    public void An_edited_block_is_exported_alone_and_the_mirror_carries_its_new_text()
    {
        using var dir = new TempDir();
        var fx = new Fixture();
        Export(fx.Project, dir.Path);

        fx.Valve.Body = "#Open := TRUE; // edited";
        fx.Valve.ModifiedDate = Stamp1.AddMinutes(5);
        var output = Export(fx.Project, dir.Path);

        Assert.Equal(1, fx.Pump.Exports);
        Assert.Equal(2, fx.Valve.Exports);
        Assert.Equal(2, fx.Valve.Generates);
        Assert.Contains("edited", File.ReadAllText(Path.Combine(dir.Path, "Dev", "PLC", "Program blocks", "Valve.scl")), StringComparison.Ordinal);
        var valve = Manifest(dir.Path).Objects.First(o => o.Guid == "Dev/PLC/FB/Valve#4" && o.Form == "SCL");
        Assert.Equal(Stamp1.AddMinutes(5).ToString("o"), valve.Stamp);
        Assert.Contains("2 kept (unchanged since the last export), 1 exported (1 changed)", Summary(output));
    }

    [Fact]
    public void A_deleted_block_leaves_the_manifest_and_a_renamed_one_is_a_new_identity()
    {
        using var dir = new TempDir();
        var fx = new Fixture();
        Export(fx.Project, dir.Path);

        fx.Blocks.Blocks.Remove(fx.Pump);
        fx.Valve.Name = "Valve2";
        var output = Export(fx.Project, dir.Path);

        var manifest = Manifest(dir.Path);
        Assert.DoesNotContain(manifest.Objects, o => o.Guid == "Dev/PLC/FB/Pump#3");
        Assert.DoesNotContain(manifest.Objects, o => o.Guid == "Dev/PLC/FB/Valve#4");
        Assert.Contains(manifest.Objects, o => o.Guid == "Dev/PLC/FB/Valve2#4" && o.Path == "Dev/PLC/Program blocks/Valve2.scl");
        // The rename did not move the stamp, and it still exported: the name is the identity.
        Assert.Equal(2, fx.Valve.Exports);
        Assert.Contains("1 kept (unchanged since the last export), 1 exported (1 new or renamed)", Summary(output));
    }

    [Fact]
    public void A_block_moved_to_another_group_is_exported_at_its_new_place()
    {
        using var dir = new TempDir();
        var fx = new Fixture();
        Export(fx.Project, dir.Path);

        fx.Blocks.Blocks.Remove(fx.Pump);
        var pumps = new FakeGroup("Pumps");
        pumps.Blocks.Add(fx.Pump);
        fx.Blocks.Groups.Add(pumps);
        var output = Export(fx.Project, dir.Path);

        Assert.Equal(2, fx.Pump.Exports);
        var pump = Manifest(dir.Path).Objects.Where(o => o.Guid == "Dev/PLC/FB/Pump#3").ToList();
        Assert.All(pump, o => Assert.StartsWith("Dev/PLC/Program blocks/Pumps/", o.Path, StringComparison.Ordinal));
        Assert.Contains("1 exported (1 moved)", Summary(output));
    }

    [Fact]
    public void A_mirror_file_edited_by_hand_is_exported_again_from_Portal()
    {
        using var dir = new TempDir();
        var fx = new Fixture();
        Export(fx.Project, dir.Path);
        var scl = Path.Combine(dir.Path, "Dev", "PLC", "Program blocks", "Pump.scl");
        File.WriteAllText(scl, "FUNCTION_BLOCK \"Pump\"\n// somebody typed here\nEND_FUNCTION_BLOCK\n");

        var output = Export(fx.Project, dir.Path);

        Assert.Equal(2, fx.Pump.Exports);
        Assert.DoesNotContain("somebody typed here", File.ReadAllText(scl), StringComparison.Ordinal);
        Assert.Contains("1 exported (1 with a mirror file missing or edited)", Summary(output));
    }

    [Fact]
    public void A_previous_manifest_by_another_Portal_is_a_full_export()
    {
        using var dir = new TempDir();
        var fx = new Fixture();
        Export(fx.Project, dir.Path, "TIA Portal V20; Siemens.Engineering 20.0.0.0");

        var output = Export(fx.Project, dir.Path);

        Assert.Equal(2, fx.Pump.Exports);
        Assert.Equal(2, fx.Valve.Exports);
        Assert.Equal(2, fx.Udt.Exports);
        Assert.Contains("full export: the previous export was by 'TIA Portal V20", Summary(output));
    }

    [Fact]
    public void A_manifest_whose_stamps_were_forced_to_mismatch_is_a_full_export()
    {
        // The mutation check in pure form: the same edit the gate makes to the real mirror.
        using var dir = new TempDir();
        var fx = new Fixture();
        Export(fx.Project, dir.Path);
        var manifestPath = Path.Combine(dir.Path, "_manifest.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace(Stamp1.ToString("o"), "2000-01-01T00:00:00.0000000Z", StringComparison.Ordinal));

        var output = Export(fx.Project, dir.Path);

        Assert.Equal(2, fx.Pump.Exports);
        Assert.Equal(2, fx.Valve.Exports);
        Assert.Equal(2, fx.Udt.Exports);
        Assert.Contains("3 exported (3 changed)", Summary(output));
    }

    [Fact]
    public void An_object_without_a_stamp_is_exported_every_time()
    {
        using var dir = new TempDir();
        var fx = new Fixture();
        fx.Pump.ModifiedDate = null;
        Export(fx.Project, dir.Path);

        var output = Export(fx.Project, dir.Path);

        Assert.Equal(2, fx.Pump.Exports);
        Assert.Equal(1, fx.Valve.Exports);
        Assert.Equal("", Manifest(dir.Path).Objects.First(o => o.Guid == "Dev/PLC/FB/Pump#3").Stamp);
        Assert.Contains("1 exported (1 without a stamp)", Summary(output));
    }

    // ---- driving the host -----------------------------------------------------------------

    private static TiaPortalOutput Export(FakeProject project, string outDir, string toolchain = Portal)
    {
        lock (ConsoleGate)
        {
            var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try
            {
                HostApp.Export(project, outDir, toolchain);
            }
            finally
            {
                Console.SetOut(original);
            }

            return TiaPortalProtocol.Parse(writer.ToString());
        }
    }

    private static string Summary(TiaPortalOutput output) =>
        string.Join("\n", output.Events.Where(e => e.Type == "summary").Select(e => e.String("msg")));

    private static ProjectManifest Manifest(string dir) =>
        ProjectManifest.Parse(File.ReadAllText(Path.Combine(dir, "_manifest.json")));

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "versio-tia-export-test-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Temp.
            }
        }
    }

    // ---- fakes shaped like what the export walk reads by name ------------------------------
    //
    // The walk reads Project.Devices → DeviceItems → GetService<SoftwareContainer>().Software
    // (a type named PlcSoftware), then BlockGroup.Blocks / .Groups and TypeGroup.Types /
    // .Groups; on each object Name, Number, ProgrammingLanguage, IsKnowHowProtected,
    // ModifiedDate, Export(FileInfo, <enum with None>) and GenerateSource(FileInfo). The
    // block's CLR type name decides its manifest type (BlockType), hence FakeFB.

    private sealed class Fixture
    {
        public Fixture()
        {
            Pump = new FakeFB("Pump", 3, Stamp1);
            Valve = new FakeFB("Valve", 4, Stamp1);
            Udt = new FakeUdt("UDT_Pump", Stamp1);
            Blocks = new FakeGroup("Program blocks");
            Blocks.Blocks.Add(Pump);
            Blocks.Blocks.Add(Valve);
            var types = new FakeTypeGroup();
            types.Types.Add(Udt);
            Project = new FakeProject(new FakeDevice("Dev", new PlcSoftware("PLC", Blocks, types)));
        }

        public FakeFB Pump { get; }

        public FakeFB Valve { get; }

        public FakeUdt Udt { get; }

        public FakeGroup Blocks { get; }

        public FakeProject Project { get; }
    }

    public enum FakeExportOptions
    {
        None,
    }

    private sealed class FakeProject(params FakeDevice[] devices)
    {
        public IReadOnlyList<object> Devices { get; } = devices;
    }

    private sealed class FakeDevice(string name, PlcSoftware software)
    {
        public string Name { get; } = name;

        public IReadOnlyList<object> DeviceItems { get; } = [];

        public T? GetService<T>()
            where T : class =>
            typeof(T) == typeof(Siemens.Engineering.HW.Features.SoftwareContainer)
                ? new Siemens.Engineering.HW.Features.SoftwareContainer { Software = software } as T
                : null;
    }

    private sealed class PlcSoftware(string name, FakeGroup blocks, FakeTypeGroup types)
    {
        public string Name { get; } = name;

        public FakeGroup BlockGroup { get; } = blocks;

        public FakeTypeGroup TypeGroup { get; } = types;

        public object? TagTableGroup => null;

        public object? ExternalSourceGroup => null;
    }

    private sealed class FakeGroup(string name)
    {
        public string Name { get; } = name;

        public List<object> Blocks { get; } = [];

        public List<object> Groups { get; } = [];
    }

    private sealed class FakeTypeGroup
    {
        public List<object> Types { get; } = [];

        public List<object> Groups { get; } = [];
    }

    private sealed class FakeFB(string name, int number, DateTime? stamp)
    {
        public string Name { get; set; } = name;

        public int Number { get; } = number;

        public string ProgrammingLanguage => "SCL";

        public bool IsKnowHowProtected => false;

        public DateTime? ModifiedDate { get; set; } = stamp;

        public string Body { get; set; } = "#Open := FALSE;";

        public int Exports { get; private set; }

        public int Generates { get; private set; }

        public void Export(FileInfo file, FakeExportOptions options)
        {
            Exports++;
            File.WriteAllText(file.FullName, $"<Document><SW.Blocks.FB Name=\"{Name}\" Number=\"{Number}\"><Body>{Body}</Body></SW.Blocks.FB></Document>");
        }

        public void GenerateSource(FileInfo file)
        {
            Generates++;
            File.WriteAllText(file.FullName, $"FUNCTION_BLOCK \"{Name}\"\nBEGIN\n{Body}\nEND_FUNCTION_BLOCK\n", new UTF8Encoding(true));
        }
    }

    private sealed class FakeUdt(string name, DateTime? stamp)
    {
        public string Name { get; } = name;

        public DateTime? ModifiedDate { get; set; } = stamp;

        public int Exports { get; private set; }

        public int Generates { get; private set; }

        public void Export(FileInfo file, FakeExportOptions options)
        {
            Exports++;
            File.WriteAllText(file.FullName, $"<Document><SW.Types.PlcStruct Name=\"{Name}\" /></Document>");
        }

        public void GenerateSource(FileInfo file)
        {
            Generates++;
            File.WriteAllText(file.FullName, $"TYPE \"{Name}\"\nSTRUCT\n  Speed : Real;\nEND_STRUCT;\nEND_TYPE\n", new UTF8Encoding(true));
        }
    }
}
