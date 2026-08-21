using Versio.Core.Projects;
using Versio.TestSupport;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// The reader for <c>*.devices.yaml</c>.
/// </summary>
/// <remarks>
/// Hand-written samples here cover the shapes the emitter produces (slots as a top-level
/// nested map, parameters as a nested map on each connector) and the ones a real export
/// cannot easily be made to produce: truncated, junk, a vendor that is not the first one.
/// The coupler on the committed fixture is the collision that made the format exist.
/// Never throws: same rule as <see cref="LibraryCatalog.Parse"/>.
/// </remarks>
public sealed class DeviceCatalogTests
{
    private const string Sample = """
        schema: 1
        vendor: codesys
        name: EtherCAT_Master
        path: Device/EtherCAT_Master
        slotCount: 2
        slots:
          "0": "<Empty>"
          "1": "<Empty>"
        connectorCount: 1
        connectors:
          - connectorId: 1
            interface: Common.Ethercat
            moduleType: 2
            parameters:
              MasterCycleTime: "4000"
              "---#9": ""
              "Cycle Time#10": "2"
              Unique: kept
        """;

    [Fact]
    public void The_real_export_keeps_eight_empty_slots_as_distinct_keys()
    {
        // The two halves never meet in the same process: versio_devices.py writes this
        // inside CODESYS, DeviceCatalog.Parse reads it here. A hand-written sample only
        // proves the parser agrees with whoever wrote the sample. The coupler on the
        // committed fixture is the collision that made the export exist — eight children
        // named <Empty> that used to share one %3CEmpty%3E.xml.
        var yaml = File.ReadAllText(Path.Combine(
            TestPaths.Project("RefrigeratorControl"),
            "Device", "EtherCAT_Master", "BNI_XG3_508_0C5_R015",
            "BNI_XG3_508_0C5_R015.devices.yaml"));

        var catalog = DeviceCatalog.Parse(yaml);

        Assert.True(catalog.IsValid);
        Assert.Equal(1, catalog.Schema);
        Assert.Equal("codesys", catalog.Vendor);
        Assert.Equal(8, catalog.Slots.Count);
        Assert.Equal(
            Enumerable.Range(0, 8).Select(i => i.ToString()).ToArray(),
            catalog.Slots.Keys.OrderBy(k => int.Parse(k)).ToArray());
        Assert.All(catalog.Slots.Values, v => Assert.Equal("<Empty>", v));
        Assert.Contains(catalog.Connectors, c => c.Parameters.Keys.Any(k => k.Contains('#')));
    }

    [Fact]
    public void A_quoted_parameter_key_containing_a_colon_survives()
    {
        // CatalogYaml.Split used to cut at the first colon. Device names are free text and
        // the emitter quotes a colon, so "Mode: DC-Synchron": "1" became key "Mode.
        var catalog = DeviceCatalog.Parse(
            """
            schema: 1
            name: Device
            connectors:
              - connectorId: 1
                interface: Ethercat
                parameters:
                  "Mode: DC-Synchron": "1"
                  Unique: kept
            """);

        var parameters = Assert.Single(catalog.Connectors).Parameters;
        Assert.Equal("1", Assert.Contains("Mode: DC-Synchron", parameters));
        Assert.Equal("kept", Assert.Contains("Unique", parameters));
    }

    [Fact]
    public void A_document_with_slots_and_nested_parameters_parses()
    {
        var catalog = DeviceCatalog.Parse(Sample);

        Assert.True(catalog.IsValid);
        Assert.Equal(1, catalog.Schema);
        Assert.Equal("codesys", catalog.Vendor);
        Assert.Equal("EtherCAT_Master", catalog.Name);
        Assert.Equal("Device/EtherCAT_Master", catalog.Path);

        Assert.Equal(2, catalog.Slots.Count);
        Assert.Equal("<Empty>", catalog.Slots["0"]);
        Assert.Equal("<Empty>", catalog.Slots["1"]);

        var connector = Assert.Single(catalog.Connectors);
        Assert.Equal("1", connector.ConnectorId);
        Assert.Equal("Common.Ethercat", connector.Interface);
        Assert.Equal("2", connector.ModuleType);
        Assert.Equal("4000", Assert.Contains("MasterCycleTime", connector.Parameters));
        Assert.Equal("", Assert.Contains("---#9", connector.Parameters));
        Assert.Equal("2", Assert.Contains("Cycle Time#10", connector.Parameters));
        Assert.Equal("kept", Assert.Contains("Unique", connector.Parameters));
    }

    [Fact]
    public void Empty_or_junk_is_invalid_rather_than_thrown()
    {
        Assert.False(DeviceCatalog.Parse(null).IsValid);
        Assert.False(DeviceCatalog.Parse("").IsValid);
        Assert.False(DeviceCatalog.Parse("not yaml at all {[").IsValid);
        Assert.False(DeviceCatalog.Parse("schema: nope\nconnectors:\n").IsValid);
    }

    [Fact]
    public void A_file_truncated_mid_connector_keeps_what_it_can()
    {
        var truncated = Sample[..(Sample.Length / 2)];
        var catalog = DeviceCatalog.Parse(truncated);

        Assert.True(catalog.IsValid);
        Assert.Equal("EtherCAT_Master", catalog.Name);
    }

    [Fact]
    public void A_vendor_that_is_not_codesys_reads_the_same_way()
    {
        var catalog = DeviceCatalog.Parse(
            """
            schema: 1
            vendor: twincat
            name: Box
            path: TIID/Box
            connectors:
              - connectorId: 1
                interface: Term
                parameters:
                  CycleTime: "1000"
            """);

        Assert.Equal("twincat", catalog.Vendor);
        Assert.Equal("1000", Assert.Single(catalog.Connectors).Parameters["CycleTime"]);
    }

    [Fact]
    public void A_tia_station_catalog_with_comment_parses()
    {
        // The TIA host cannot reference Core. This string is the shape it emits;
        // Parse is the half that pins it. Comment is the measured apply path.
        var catalog = DeviceCatalog.Parse(
            """
            schema: 1
            vendor: tia
            name: ET 200SP station_2
            path: ET 200SP station_2
            apply: comment
            connectors:
              - connectorId: ET 200SP station_2/CleaningStation@1
                interface: CleaningStation
                moduleType: OrderNumber:6ES7 514-2DN03-0AB0/V4.0
                parameters:
                  Classification: CPU
                  Comment: "line comment"
                  PositionNumber: "1"
                  ShortDesignation: CPU 1514SP-2 PN
            """);

        Assert.True(catalog.IsValid);
        Assert.Equal("tia", catalog.Vendor);
        var item = Assert.Single(catalog.Connectors);
        Assert.Equal("ET 200SP station_2/CleaningStation@1", item.ConnectorId);
        Assert.Equal("CleaningStation", item.Interface);
        Assert.Equal("line comment", item.Parameters["Comment"]);
        Assert.Equal("1", item.Parameters["PositionNumber"]);
    }

    [Fact]
    public void A_connector_with_no_identifying_field_is_dropped()
    {
        var catalog = DeviceCatalog.Parse(
            """
            schema: 1
            name: Device
            connectors:
              - ignored: true
              - connectorId: 2
                interface: Kept
            """);

        var only = Assert.Single(catalog.Connectors);
        Assert.Equal("2", only.ConnectorId);
        Assert.Equal("Kept", only.Interface);
    }

    [Fact]
    public void Colliding_connector_keys_uniquify_in_file_order()
    {
        // Nothing upstream guarantees Key is unique — the exporter writes the toolchain's
        // connector list verbatim. The tree used to drop the second of a colliding pair;
        // #2/#3 in file order keeps every connector addressable, the same rule the
        // exporter applies to duplicate parameter names.
        var catalog = DeviceCatalog.Parse(
            """
            schema: 1
            name: Device
            connectors:
              - interface: Common.IoLink
                moduleType: a
              - interface: Common.IoLink
                moduleType: b
              - interface: Common.IoLink
                moduleType: c
              - connectorId: 9
                moduleType: d
            """);

        Assert.Equal(
            ["Common.IoLink", "Common.IoLink#2", "Common.IoLink#3", "9"],
            catalog.KeyedConnectors().Select(kv => kv.Key));
        Assert.Equal(
            ["a", "b", "c", "d"],
            catalog.KeyedConnectors().Select(kv => kv.Value.ModuleType));
    }
}
