using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Device catalog rows, entered from where the user enters them: a real repository,
/// a real catalog on disk, and the shell's own tree.
/// </summary>
/// <remarks>
/// Every Core test of this feature would pass with the shell wiring deleted. Deleting
/// <c>ReadCatalogs</c>'s Devices arm, the HEAD fetch for <c>.devices.yaml</c>, the
/// <c>ProjectNodeKind.Device</c> pane arm, or the Object/File yaml arm must turn this
/// file red, and nothing else.
/// </remarks>
public sealed class DeviceTreeFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    private const string Catalog = """
        schema: 1
        vendor: codesys
        name: Device
        path: Device
        slotCount: 2
        slots:
          "0": "<Empty>"
          "1": "<Empty>"
        connectorCount: 1
        connectors:
          - connectorId: 1
            interface: Common.Ethercat
            parameters:
              MasterCycleTime: "4000"
        """;

    private static void GivenATrackedProject(TempRepo repo, string? catalog = null)
    {
        var versio = Path.Combine(repo.Path, ".versio");
        Directory.CreateDirectory(versio);

        File.WriteAllText(Path.Combine(versio, "repo.json"), """
        {
          "schema": 1,
          "bindings": [
            {
              "id": "b1",
              "projectPath": "plc/Machine.project",
              "exportPath": "plc/Machine",
              "enabled": true
            }
          ]
        }
        """);

        var device = Path.Combine(repo.Path, "plc", "Machine", "Device");
        Directory.CreateDirectory(device);

        File.WriteAllBytes(
            Path.Combine(repo.Path, "plc", "Machine.project"), [0x00, 0x01, 0x02, 0x03]);

        File.WriteAllText(Path.Combine(repo.Path, "plc", "Machine", "_manifest.json"), """
        {
          "schema": 4,
          "bindingId": "b1",
          "project": "Machine.project",
          "objectCount": 2,
          "objects": [
            { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_FULL",
              "path": "Device/Plc Logic/Application/FB_Motor.st", "hash": "sha256:0", "bytes": 1 },
            { "guid": "g5", "name": "Device", "type": "DEVICE", "form": "DEVICES",
              "path": "Device/Device.devices.yaml", "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [],
          "skipped": []
        }
        """);

        var application = Path.Combine(device, "Plc Logic", "Application");
        Directory.CreateDirectory(application);
        File.WriteAllText(Path.Combine(application, "FB_Motor.st"), "FUNCTION_BLOCK FB_Motor\n");
        File.WriteAllText(Path.Combine(device, "Device.devices.yaml"), (catalog ?? Catalog) + "\n");
    }

    private static TreeRow FindRow(IEnumerable<TreeRow> rows, string name)
    {
        foreach (var row in rows)
        {
            if (row.Name == name)
            {
                return row;
            }

            if (Search(row, name) is { } found)
            {
                return found;
            }
        }

        throw new InvalidOperationException($"no row named '{name}' in the tree");
    }

    private static TreeRow? Search(TreeRow row, string name)
    {
        foreach (var child in row.Children)
        {
            if (child.Name == name)
            {
                return child;
            }

            if (Search(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public async Task The_connectors_reach_the_panel_as_rows_under_the_device()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        var device = FindRow(shell.Sync.Tree, "Device");
        Assert.Equal(ProjectNodeKind.Object, device.Node.Kind);
        Assert.Contains(device.Node.Files, f => f.EndsWith(DeviceCatalog.FileSuffix, StringComparison.Ordinal));

        var connector = Assert.Single(device.Children, c => c.Name == "Common.Ethercat");
        Assert.Equal(ProjectNodeKind.Device, connector.Node.Kind);
        Assert.Equal("MasterCycleTime", Assert.Single(connector.Children).Name);
        Assert.Equal("4000", connector.Children[0].Annotation);
        Assert.False(connector.CanStageOrDiscard);
        Assert.False(connector.Children[0].CanStageOrDiscard);

        Assert.Equal(2, device.Children.Count(c => c.Name == "<Empty>"));
    }

    [Fact]
    public async Task Selecting_the_device_renders_the_catalog_and_a_parameter_renders_its_facts()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = FindRow(shell.Sync.Tree, "Device");
        var catalog = Assert.IsType<DeviceCatalog>(shell.RenderedContent);
        Assert.Equal("Common.Ethercat", Assert.Single(catalog.Connectors).Interface);

        shell.Sync.SelectedNode = FindRow(shell.Sync.Tree, "MasterCycleTime");
        var detail = Assert.IsType<DeviceDetailRender>(shell.RenderedContent);
        Assert.Equal("MasterCycleTime", detail.Title);
        Assert.Contains(detail.Facts, f => f is { Label: "Value", Value: "4000" });
    }

    [Fact]
    public async Task A_connector_with_neither_id_nor_interface_still_renders_its_facts()
    {
        // The tree keys that row as DisplayName ("connector"). The pane used to match
        // only ConnectorId or Interface, so the facts were silently dropped.
        using var repo = new TempRepo();
        GivenATrackedProject(repo, """
            schema: 1
            vendor: codesys
            name: Device
            path: Device
            connectors:
              - moduleType: 1
                parameters:
                  Foo: "bar"
            """);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        var row = FindRow(shell.Sync.Tree, "connector");
        shell.Sync.SelectedNode = row;
        var detail = Assert.IsType<DeviceDetailRender>(shell.RenderedContent);
        Assert.Contains(detail.Facts, f => f is { Label: "Module type", Value: "1" });
        Assert.Contains(detail.Facts, f => f is { Label: "Parameters", Value: "1" });
    }

    [Fact]
    public async Task A_parameter_edit_in_the_working_tree_marks_that_row()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);
        repo.Git("add .");
        repo.Git("commit -qm exported");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        File.WriteAllText(
            Path.Combine(repo.Path, "plc", "Machine", "Device", "Device.devices.yaml"),
            Catalog.Replace("4000", "2000") + "\n");
        await shell.RefreshAsync();

        var device = FindRow(shell.Sync.Tree, "Device");
        var connector = Assert.Single(device.Children, c => c.Name == "Common.Ethercat");
        Assert.Equal(ChangeKind.Modified,
            connector.Children.Single(c => c.Name == "MasterCycleTime").Node.Status);
    }
}
