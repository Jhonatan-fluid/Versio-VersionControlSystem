using System.Text.Json;
using Versio.Core.Projects;
using Versio.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// <c>versio_devices.py</c> shaping rules, exercised under CPython.
/// </summary>
/// <remarks>
/// Duplicate parameter names were measured on the coupler; unique names must stay as the
/// name so a CycleTime diff reads as CycleTime. The module imports no scriptengine.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PythonDeviceRecordTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Duplicate_parameter_names_are_keyed_with_the_stable_id()
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var json = Run("""
            import versio_devices as D

            class P(object):
                def __init__(self, name, ident, value):
                    self.name = name
                    self.id = ident
                    self.value = value

            class Conn(object):
                host_parameters = [
                    P(u"Cycle Time", u"10", u"2"),
                    P(u"Cycle Time", u"11", u"4"),
                    P(u"MasterCycleTime", u"1", u"4000"),
                ]

            def safe(fn, default=None):
                try:
                    return fn()
                except Exception:
                    return default

            out = json.dumps(D.read_parameters(Conn(), safe), sort_keys=True)
            """);

        output.WriteLine(json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("4000", root.GetProperty("MasterCycleTime").GetString());
        Assert.Equal("2", root.GetProperty("Cycle Time#10").GetString());
        Assert.Equal("4", root.GetProperty("Cycle Time#11").GetString());
        Assert.False(root.TryGetProperty("Cycle Time", out _));
    }

    [SkippableFact]
    public void A_third_duplicate_name_does_not_overwrite_the_second()
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var json = Run("""
            import versio_devices as D

            class P(object):
                def __init__(self, name, ident, value):
                    self.name = name
                    self.id = ident
                    self.value = value

            class Conn(object):
                host_parameters = [
                    P(u"N", u"", u"first"),
                    P(u"N", u"", u"second"),
                    P(u"N", u"", u"third"),
                ]

            def safe(fn, default=None):
                try:
                    return fn()
                except Exception:
                    return default

            out = json.dumps(D.read_parameters(Conn(), safe), sort_keys=True)
            """);

        output.WriteLine(json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("first", root.GetProperty("N").GetString());
        Assert.Equal("second", root.GetProperty("N#2").GetString());
        Assert.Equal("third", root.GetProperty("N#3").GetString());
    }

    [SkippableFact]
    public void Unreadable_slot_indexes_stay_distinct()
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var json = Run("""
            import versio_devices as D

            class Slot(object):
                type = u"085766fd-043e-4545-8e8d-d651d56d5d3b"
                def get_name(self):
                    return u"<Empty>"
                @property
                def index(self):
                    raise RuntimeError("no index")

            class Dev(object):
                def get_children(self):
                    return [Slot(), Slot(), Slot()]

            def safe(fn, default=None):
                try:
                    return fn()
                except Exception:
                    return default

            def type_of(guid):
                return u"DEVICE_SLOT"

            seen = []
            def on_error(code, detail):
                seen.append(code)

            slots = D.read_slots(Dev(), safe, type_of, on_error)
            out = json.dumps({"slots": slots, "codes": seen}, sort_keys=True)
            """);

        output.WriteLine(json);
        using var doc = JsonDocument.Parse(json);
        var slots = doc.RootElement.GetProperty("slots");
        Assert.Equal(3, slots.EnumerateObject().Count());
        Assert.Equal("<Empty>", slots.GetProperty("unindexed-0").GetString());
        Assert.Equal("<Empty>", slots.GetProperty("unindexed-2").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("codes").GetArrayLength());
    }

    [SkippableFact]
    public void A_parameter_key_with_a_colon_round_trips_through_the_emitter()
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var yaml = Run("""
            import versio_devices as D
            import versio_yaml as Y

            connectors = [{
                "connectorId": 1,
                "interface": u"Ethercat",
                "parameters": {u"Mode: DC-Synchron": u"1"},
            }]
            out = D.catalog_document(Y, u"Device", u"Device", {}, connectors)
            """);

        output.WriteLine(yaml);
        var catalog = DeviceCatalog.Parse(yaml);
        Assert.Equal("1", Assert.Single(catalog.Connectors).Parameters["Mode: DC-Synchron"]);
    }

    [SkippableFact]
    public void The_emitted_document_is_what_the_neutral_reader_parses()
    {
        Skip.IfNot(PythonIsAvailable(), "python is not on PATH");

        var yaml = Run("""
            import versio_devices as D
            import versio_yaml as Y

            slots = {u"0": u"<Empty>", u"7": u"<Empty>"}
            connectors = [{
                "connectorId": 1,
                "interface": u"Common.Ethercat",
                "parameters": {u"MasterCycleTime": u"4000", u"Cycle Time#10": u"2"},
            }]
            out = D.catalog_document(Y, u"BNI", u"Device/EtherCAT_Master/BNI", slots, connectors)
            """);

        output.WriteLine(yaml);
        var catalog = DeviceCatalog.Parse(yaml);
        Assert.True(catalog.IsValid);
        Assert.Equal("BNI", catalog.Name);
        Assert.Equal("<Empty>", catalog.Slots["0"]);
        Assert.Equal("<Empty>", catalog.Slots["7"]);
        var connector = Assert.Single(catalog.Connectors);
        Assert.Equal("1", connector.ConnectorId);
        Assert.Equal("4000", connector.Parameters["MasterCycleTime"]);
        Assert.Equal("2", connector.Parameters["Cycle Time#10"]);
    }

    private static string Run(string body) =>
        CPythonHarness.RunJson("{}", body, jsonOut: false);

    private static bool PythonIsAvailable() => CPythonHarness.PythonIsAvailable();
}
