using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// Folder+yaml merge and grafted connector / parameter / slot rows.
/// </summary>
/// <remarks>
/// Deleting <c>MergeDeviceFolders</c> leaves the Device folder containing a second object
/// also named Device, and every assertion below that the Device node is an Object with
/// EtherCAT beside its grafted rows goes green-silent. Same class as a dropped subscription.
/// </remarks>
public sealed class ProjectTreeDevicesTests
{
    private const string Export = "plc/Machine";
    private const string DeviceYaml = "Device/Device.devices.yaml";
    private const string EthercatYaml = "Device/EtherCAT_Master/EtherCAT_Master.devices.yaml";
    private const string RepoDeviceYaml = $"{Export}/{DeviceYaml}";

    private static ProjectManifest Manifest() => new()
    {
        Schema = 5,
        Project = "Machine.project",
        Objects =
        [
            new ProjectObject
            {
                Name = "Device",
                Path = DeviceYaml,
                Guid = "dev",
                Type = "DEVICE",
                Form = ExportCoverage.DevicesForm,
            },
            new ProjectObject
            {
                Name = "EtherCAT_Master",
                Path = EthercatYaml,
                Guid = "ecat",
                Type = "DEVICE",
                Form = ExportCoverage.DevicesForm,
            },
            new ProjectObject
            {
                Name = "Main",
                Path = "Device/Plc Logic/Application/Main.st",
                Guid = "main",
                Type = "POU",
                Form = "ST_DECL",
            },
        ],
    };

    private static DeviceCatalog Catalog(
        IReadOnlyList<DeviceConnector>? connectors = null,
        IReadOnlyDictionary<string, string>? slots = null) => new()
    {
        Schema = 1,
        Vendor = "codesys",
        Name = "Device",
        Path = "Device",
        Connectors = connectors ?? [],
        Slots = slots ?? new Dictionary<string, string>(StringComparer.Ordinal),
    };

    private static DeviceConnector Connector(
        string id, string iface, IReadOnlyDictionary<string, string>? parameters = null) => new()
    {
        ConnectorId = id,
        Interface = iface,
        Parameters = parameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
    };

    private static ProjectTreeNode Build(
        DeviceSnapshot? snapshot,
        IReadOnlyList<FileChange>? changes = null) =>
        ProjectTreeBuilder.Build(
            Manifest(), Export, changes ?? [],
            new ProjectTreeOptions { ChangedOnly = false },
            devices: snapshot is null ? [] : [snapshot]);

    private static ProjectTreeNode Device(ProjectTreeNode tree) =>
        Flatten(tree).First(n => n.Name == "Device" && n.Kind != ProjectNodeKind.Folder);

    private static IEnumerable<ProjectTreeNode> Flatten(ProjectTreeNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    [Fact]
    public void The_device_folder_and_its_yaml_become_one_object()
    {
        var device = Device(Build(new DeviceSnapshot
        {
            Path = RepoDeviceYaml,
            Current = Catalog(),
        }));

        Assert.Equal(ProjectNodeKind.Object, device.Kind);
        Assert.Contains(device.Files, f => f.EndsWith(DeviceCatalog.FileSuffix, StringComparison.Ordinal));
        Assert.DoesNotContain(device.Children, c => c.Name == "Device");
        Assert.Contains(device.Children, c => c.Name == "EtherCAT_Master");
        Assert.Contains(device.Children, c => c.Name == "Plc Logic");
    }

    [Fact]
    public void Connectors_hang_under_the_device_with_parameters_under_them()
    {
        var device = Device(Build(new DeviceSnapshot
        {
            Path = RepoDeviceYaml,
            Current = Catalog(
                [
                    Connector("1", "Common.Ethercat",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["MasterCycleTime"] = "4000",
                        }),
                ],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["0"] = "<Empty>",
                    ["1"] = "<Empty>",
                }),
        }));

        var connector = Assert.Single(device.Children, c => c.Kind == ProjectNodeKind.Device && c.Name == "Common.Ethercat");
        Assert.Equal("1 param(s)", connector.Detail);
        var param = Assert.Single(connector.Children);
        Assert.Equal("MasterCycleTime", param.Name);
        Assert.Equal("4000", param.Detail);
        Assert.Equal(ProjectNodeKind.Device, param.Kind);

        Assert.Equal(2, device.Children.Count(c => c.Kind == ProjectNodeKind.Device && c.Name == "<Empty>"));
        Assert.Equal(["0", "1"],
            device.Children.Where(c => c.Name == "<Empty>").Select(c => c.Detail));

        // The graft is additive. Deleting the concatenate in AttachDevices leaves these
        // two green-silent and the live tree showing only parameters.
        Assert.Contains(device.Children, c => c.Name == "EtherCAT_Master");
        Assert.Contains(device.Children, c => c.Name == "Plc Logic");
    }

    [Fact]
    public void A_parameter_value_change_marks_that_row()
    {
        var current = Catalog(
        [
            Connector("1", "Common.Ethercat",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["MasterCycleTime"] = "2000",
                    ["Other"] = "same",
                }),
        ]);
        var baseline = Catalog(
        [
            Connector("1", "Common.Ethercat",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["MasterCycleTime"] = "4000",
                    ["Other"] = "same",
                }),
        ]);

        var device = Device(Build(new DeviceSnapshot
        {
            Path = RepoDeviceYaml,
            Current = current,
            Baseline = baseline,
        }));

        var connector = Assert.Single(device.Children, c => c.Name == "Common.Ethercat");
        Assert.Equal(ChangeKind.Modified,
            connector.Children.Single(c => c.Name == "MasterCycleTime").Status);
        Assert.Equal(ChangeKind.Unmodified,
            connector.Children.Single(c => c.Name == "Other").Status);
    }

    [Fact]
    public void An_added_connector_marks_its_parameter_rows_added()
    {
        var current = Catalog(
        [
            Connector("1", "Common.Ethercat",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["MasterCycleTime"] = "4000",
                }),
            Connector("2", "SafetyInterface",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Watchdog"] = "on",
                }),
        ]);
        var baseline = Catalog(
        [
            Connector("1", "Common.Ethercat",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["MasterCycleTime"] = "4000",
                }),
        ]);

        var device = Device(Build(new DeviceSnapshot
        {
            Path = RepoDeviceYaml,
            Current = current,
            Baseline = baseline,
        }));

        var added = Assert.Single(device.Children, c => c.Name == "SafetyInterface");
        Assert.Equal(ChangeKind.Added, added.Status);
        var param = Assert.Single(added.Children);
        Assert.Equal("Watchdog", param.Name);
        Assert.Equal(ChangeKind.Added, param.Status);
        Assert.Equal(1, added.ChangedDescendants);
    }

    [Fact]
    public void Colliding_connector_keys_render_both_rows_and_diff_the_second()
    {
        // Two id-less ports sharing an interface collide on Key. DeviceNodes used to keep
        // a seen-set and drop the second row, so a change to its parameters produced no
        // Modified row anywhere while the catalog table still showed both ports.
        var current = Catalog(
        [
            Connector("", "Common.IoLink",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["CycleTime"] = "4000",
                }),
            Connector("", "Common.IoLink",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["CycleTime"] = "2000",
                }),
        ]);
        var baseline = Catalog(
        [
            Connector("", "Common.IoLink",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["CycleTime"] = "4000",
                }),
            Connector("", "Common.IoLink",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["CycleTime"] = "4000",
                }),
        ]);

        var device = Device(Build(new DeviceSnapshot
        {
            Path = RepoDeviceYaml,
            Current = current,
            Baseline = baseline,
        }));

        var ports = device.Children.Where(c => c.Name == "Common.IoLink").ToList();
        Assert.Equal(2, ports.Count);
        Assert.Equal(ChangeKind.Unmodified, ports[0].Status);
        Assert.Equal(ChangeKind.Modified, ports[1].Status);
        Assert.Equal(ChangeKind.Modified,
            ports[1].Children.Single(c => c.Name == "CycleTime").Status);

        // Distinct row paths (#2 on the second) — selection and the detail pane must be
        // able to tell the ports apart, parameter rows included.
        Assert.NotEqual(ports[0].Path, ports[1].Path);
        Assert.NotEqual(
            ports[0].Children.Single(c => c.Name == "CycleTime").Path,
            ports[1].Children.Single(c => c.Name == "CycleTime").Path);
    }

    [Fact]
    public void An_encoded_device_folder_still_merges_with_its_yaml()
    {
        // Folder Name is the path segment (COM1%20, foo%3Cbar%3E); object Name is the
        // manifest's verbatim name. Merging on Ordinal name equality silently kept the
        // drawn-twice shape the merge exists to prevent.
        var tree = ProjectTreeBuilder.Build(
            new ProjectManifest
            {
                Schema = 5,
                Project = "Machine.project",
                Objects =
                [
                    new ProjectObject
                    {
                        Name = "foo<bar>",
                        Path = "foo%3Cbar%3E/foo%3Cbar%3E.devices.yaml",
                        Guid = "dev",
                        Type = "DEVICE",
                        Form = ExportCoverage.DevicesForm,
                    },
                    new ProjectObject
                    {
                        Name = "Main",
                        Path = "foo%3Cbar%3E/Plc Logic/Main.st",
                        Guid = "main",
                        Type = "POU",
                        Form = "ST_DECL",
                    },
                ],
            },
            Export,
            [],
            new ProjectTreeOptions { ChangedOnly = false },
            devices:
            [
                new DeviceSnapshot
                {
                    Path = $"{Export}/foo%3Cbar%3E/foo%3Cbar%3E.devices.yaml",
                    Current = Catalog(),
                },
            ]);

        var device = Flatten(tree).First(n => n.Name == "foo<bar>");
        Assert.Equal(ProjectNodeKind.Object, device.Kind);
        Assert.DoesNotContain(device.Children, c => c.Name == "foo<bar>");
        Assert.Contains(device.Children, c => c.Name == "Plc Logic");
    }
}
