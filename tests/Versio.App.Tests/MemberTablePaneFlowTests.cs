using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A data block's Rendered pane is the member table a format built, not highlighted XML.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <c>SelectedNode</c>, the same door a click enters by. The templates
/// are XAML; what this pins is the dispatch — and that deleting the
/// <c>PlcFormats.All</c> / <c>TryParseMemberTable</c> arm in
/// <c>BuildRenderedContent</c> makes every fact here go red. That arm is a pure
/// addition in front of <c>SourceRender</c>, so nothing else would notice.
/// </para>
/// <para>
/// Does not reference a vendor parser. The shell asks the registry; the pack that
/// understands the bytes answers.
/// </para>
/// </remarks>
public sealed class MemberTablePaneFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    private const string GlobalDb = """
        <?xml version="1.0" encoding="utf-8"?>
        <Document>
          <SW.Blocks.GlobalDB ID="0">
            <AttributeList>
              <Interface>
                <Sections>
                  <Section Name="Static">
                    <Member Name="High" Datatype="Real">
                      <StartValue>2900.0</StartValue>
                    </Member>
                  </Section>
                </Sections>
              </Interface>
              <Name>Limits</Name>
              <Number>58</Number>
            </AttributeList>
          </SW.Blocks.GlobalDB>
        </Document>
        """;

    private const string InstanceDb = """
        <?xml version="1.0" encoding="utf-8"?>
        <Document>
          <SW.Blocks.InstanceDB ID="0">
            <AttributeList>
              <InstanceOfName>PidCtrl</InstanceOfName>
              <Interface>
                <Sections>
                  <Section Name="Static">
                    <Member Name="Gain" Datatype="Real">
                      <StartValue>1.5</StartValue>
                    </Member>
                  </Section>
                </Sections>
              </Interface>
              <Name>Pid</Name>
            </AttributeList>
          </SW.Blocks.InstanceDB>
        </Document>
        """;

    private const string FunctionBlock = """
        <?xml version="1.0" encoding="utf-8"?>
        <Document>
          <SW.Blocks.FB ID="0">
            <AttributeList>
              <Interface>
                <Sections>
                  <Section Name="Input">
                    <Member Name="Enable" Datatype="Bool">
                      <StartValue>FALSE</StartValue>
                    </Member>
                  </Section>
                </Sections>
              </Interface>
              <Name>Motor</Name>
            </AttributeList>
          </SW.Blocks.FB>
        </Document>
        """;

    private static void GivenATrackedPortalProject(TempRepo repo)
    {
        var versio = Path.Combine(repo.Path, ".versio");
        Directory.CreateDirectory(versio);

        File.WriteAllText(Path.Combine(versio, "repo.json"), """
        {
          "schema": 1,
          "bindings": [
            {
              "id": "b1",
              "projectPath": "plc/Station.ap21",
              "exportPath": "plc/Station",
              "vendor": "TiaPortal",
              "enabled": true
            }
          ]
        }
        """);

        var blocks = Path.Combine(repo.Path, "plc", "Station", "Blocks");
        Directory.CreateDirectory(blocks);

        File.WriteAllText(Path.Combine(repo.Path, "plc", "Station.ap21"), "pointer");

        File.WriteAllText(Path.Combine(repo.Path, "plc", "Station", "_manifest.json"), """
        {
          "schema": 8,
          "vendor": "TiaPortal",
          "bindingId": "b1",
          "project": "Station.ap21",
          "objectCount": 3,
          "objects": [
            { "guid": "db1", "name": "Limits", "type": "DB", "form": "NATIVE",
              "path": "Blocks/Limits.xml", "hash": "sha256:0", "bytes": 1 },
            { "guid": "db2", "name": "Pid", "type": "DB", "form": "SCL_REVIEW",
              "path": "Blocks/Pid.scl", "hash": "sha256:0", "bytes": 1 },
            { "guid": "db2", "name": "Pid", "type": "DB", "form": "NATIVE",
              "path": "Blocks/Pid.xml", "hash": "sha256:0", "bytes": 1 },
            { "guid": "fb1", "name": "Motor", "type": "FB", "form": "NATIVE",
              "path": "Blocks/Motor.xml", "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [],
          "skipped": []
        }
        """);

        File.WriteAllText(Path.Combine(blocks, "Limits.xml"), GlobalDb);
        File.WriteAllText(Path.Combine(blocks, "Pid.xml"), InstanceDb);
        File.WriteAllText(Path.Combine(blocks, "Pid.scl"),
            "DATA_BLOCK \"Pid\"\nVAR\nEND_VAR\n");
        File.WriteAllText(Path.Combine(blocks, "Motor.xml"), FunctionBlock);
    }

    private static async Task<TreeRow> Row(ShellViewModel shell, string name)
    {
        TreeRow? found = null;

        await Eventually.Until(
            () => (found = Find(shell.Sync.Tree, name)) is not null,
            $"a row named '{name}' appeared in the tree");

        return found!;
    }

    private static TreeRow? Find(IEnumerable<TreeRow> rows, string name)
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

        return null;
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
    public async Task A_data_block_renders_as_its_member_table()
    {
        using var repo = new TempRepo();
        GivenATrackedPortalProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = await Row(shell, "Limits");

        var table = Assert.IsType<MemberTable>(shell.RenderedContent);
        Assert.Equal("Limits", table.Name);
        var row = Assert.Single(table.Members);
        Assert.Equal("High", row.Name);
        Assert.Equal("2900.0", row.StartValue);
        Assert.Equal("Read/Write", row.ExternalAccess);
    }

    [Fact]
    public async Task A_textual_primary_file_still_tables_from_the_native_sibling()
    {
        // PrimaryFile prefers .scl. The table is in the XML beside it. Asking
        // only the primary would highlight DATA_BLOCK and never offer the grid.
        using var repo = new TempRepo();
        GivenATrackedPortalProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        var node = await Row(shell, "Pid");
        Assert.EndsWith(".scl", node.PrimaryFile, StringComparison.OrdinalIgnoreCase);

        shell.Sync.SelectedNode = node;

        var table = Assert.IsType<MemberTable>(shell.RenderedContent);
        Assert.Equal("Pid", table.Name);
        Assert.Equal("PidCtrl", table.InstanceOf);
        Assert.Equal("1.5", Assert.Single(table.Members).StartValue);
    }

    [Fact]
    public async Task A_function_block_still_renders_as_source()
    {
        using var repo = new TempRepo();
        GivenATrackedPortalProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = await Row(shell, "Motor");

        var source = Assert.IsType<SourceRender>(shell.RenderedContent);
        Assert.Contains("SW.Blocks.FB", source.PseudoDiff);
    }
}
