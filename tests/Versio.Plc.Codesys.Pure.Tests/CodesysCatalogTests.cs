using Versio.Core.Projects;
using Versio.TestSupport;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// File-Based Storage's libraries, tasks and devices read into the records the classic mirror's
/// YAML gives, held to that YAML for the same fixture, plus the text list and project settings as
/// tables.
/// </summary>
/// <remarks>
/// The fixture is one machine saved both ways (<c>RefrigeratorControl</c> classic and
/// <c>RefrigeratorControl_FBS</c>), so wherever both formats say a fact, the two readers must
/// agree on it. Otherwise moving a project to File-Based Storage would show reviewers changes
/// that are not there.
/// </remarks>
public sealed class CodesysCatalogTests
{
    private const string App = "Device.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/";

    private static string Fbs(string relative) => File.ReadAllText(Path.Combine(
        TestPaths.Project("RefrigeratorControl_FBS"), "RefrigeratorControl.fbsproj", relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string Classic(string relative) => File.ReadAllText(Path.Combine(
        TestPaths.Project("RefrigeratorControl"), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static IEnumerable<string> Facts(LibraryCatalog catalog) => catalog.References
        .Select(r => $"{r.Name} | {r.DefaultResolution} | {r.Redirection} | system={r.SystemLibrary} | qualified={r.QualifiedOnly} | " +
                     string.Join(",", r.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")))
        .Order(StringComparer.Ordinal);

    [Theory]
    [InlineData(App + "Libraries.json", "Device/Plc Logic/Application/Library Manager.libraries.yaml")]
    [InlineData("Libraries.json", "Library Manager.libraries.yaml")]
    public void Libraries_json_gives_the_references_the_classic_yaml_gives(string fbs, string classic)
    {
        var fromJson = CodesysProjectFormat.Instance.TryParseLibraryCatalog(fbs, Fbs(fbs));
        var fromYaml = LibraryCatalog.Parse(Classic(classic));

        Assert.NotNull(fromJson);
        Assert.True(fromJson.IsValid);
        Assert.Equal(Facts(fromYaml), Facts(fromJson));
    }

    [Theory]
    [InlineData("MainTask")]
    [InlineData("VISU_TASK")]
    [InlineData("EtherCAT_Task")]
    public void A_task_file_gives_the_task_the_classic_yaml_gives(string name)
    {
        var relative = App + $"Task Configuration.taskconfig.xml.v3^/{name}.task.xml.v3";
        var fromXml = CodesysProjectFormat.Instance.TryParseTask(relative, Fbs(relative));
        var fromYaml = TaskCatalog.Parse(Classic("Device/Plc Logic/Application/Task Configuration.tasks.yaml")).Tasks.Single(t => t.Name == name);

        Assert.NotNull(fromXml);
        Assert.Equal(
            (fromYaml.Name, fromYaml.Priority, fromYaml.Interval, fromYaml.IntervalUnit, fromYaml.CoreBinding,
             fromYaml.WatchdogEnabled, fromYaml.WatchdogTime, fromYaml.WatchdogSensitivity, string.Join(",", fromYaml.Pous)),
            (fromXml.Name, fromXml.Priority, fromXml.Interval, fromXml.IntervalUnit, fromXml.CoreBinding,
             fromXml.WatchdogEnabled, fromXml.WatchdogTime, fromXml.WatchdogSensitivity, string.Join(",", fromXml.Pous)));
    }

    [Theory]
    [InlineData("Device.device.xml.v3", "Device/Device.devices.yaml")]
    [InlineData("Device.device.xml.v3^/EtherCAT_Master.device.xml.v3", "Device/EtherCAT_Master/EtherCAT_Master.devices.yaml")]
    [InlineData("Device.device.xml.v3^/EtherCAT_Master.device.xml.v3^/BNI_XG3_508_0C5_R015.device.xml.v3",
        "Device/EtherCAT_Master/BNI_XG3_508_0C5_R015/BNI_XG3_508_0C5_R015.devices.yaml")]
    public void A_device_file_gives_the_connectors_and_parameters_the_classic_yaml_gives(string fbs, string classic)
    {
        var fromXml = CodesysProjectFormat.Instance.TryParseDeviceCatalog(fbs, Fbs(fbs));
        var fromYaml = DeviceCatalog.Parse(Classic(classic));

        Assert.NotNull(fromXml);
        Assert.Equal(fromYaml.Name, fromXml.Name);
        Assert.Equal(
            fromYaml.Connectors.Select(c => $"{c.ConnectorId} {c.Interface} {c.ModuleType}"),
            fromXml.Connectors.Select(c => $"{c.ConnectorId} {c.Interface} {c.ModuleType}"));

        // Names come from the installed device description on the classic side and from the
        // project file here (see CodesysCatalogs.Device), so they are compared ignoring case, and
        // at most two per connector may differ outright: the fixture's master has exactly two,
        // the description's own spellings ShpwDCStatistik and IgnoreManualAddres. Every value of
        // every parameter whose name matches must agree, and the counts must.
        foreach (var (yaml, xml) in fromYaml.Connectors.Zip(fromXml.Connectors))
        {
            Assert.Equal(yaml.Parameters.Count, xml.Parameters.Count);
            var ours = xml.Parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var unmatched = 0;
            foreach (var (name, value) in yaml.Parameters)
            {
                if (ours.TryGetValue(name, out var mine))
                {
                    Assert.Equal($"{name}={value}", $"{name}={mine}");
                }
                else
                {
                    unmatched++;
                }
            }

            Assert.True(unmatched <= 2, $"connector {yaml.ConnectorId}: {unmatched} parameter names differ");
        }
    }

    [Fact]
    public void The_global_text_list_is_a_table_of_ids_and_texts()
    {
        const string Relative = "GlobalTextList.globaltextlist.xml.v3";

        var table = CodesysProjectFormat.Instance.TryParseMemberTable(Relative, Fbs(Relative));

        Assert.NotNull(table);
        Assert.Equal("GlobalTextList", table.Name);
        Assert.Equal(("TEXT ID", "DEFAULT TEXT"), (table.NameHeader, table.ValueHeader));
        Assert.Equal("Door open", Assert.Single(table.Members, m => m.Name == "44").StartValue);
        Assert.False(table.HasDatatype);
        Assert.False(table.HasExternalAccess);
    }

    [Fact]
    public void The_project_settings_blob_is_decoded_into_settings_by_key()
    {
        const string Relative = "Project Settings.workspace.xml.v3";

        var table = CodesysProjectFormat.Instance.TryParseMemberTable(Relative, Fbs(Relative));

        Assert.NotNull(table);
        Assert.Equal(("SETTING", "VALUE"), (table.NameHeader, table.ValueHeader));
        Assert.Equal("4.1.1.0", Assert.Single(table.Members, m => m.Name == "CurrentUmlCompilerVersion").StartValue);
        Assert.Contains(table.Members, m => m.IsSectionHeading);
    }

    [Theory]
    [InlineData(App + "PLC_PRG.prg.st")]
    [InlineData(App + "Signals.pou.xml.v3")]
    [InlineData("GlobalTextList.globaltextlist.xml.v3")]
    public void Other_objects_are_none_of_these(string relative)
    {
        var text = Fbs(relative);
        var format = CodesysProjectFormat.Instance;

        Assert.Null(format.TryParseLibraryCatalog(relative, text));
        Assert.Null(format.TryParseTask(relative, text));
        Assert.Null(format.TryParseDeviceCatalog(relative, text));
    }
}
