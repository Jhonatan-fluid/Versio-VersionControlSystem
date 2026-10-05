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
                  <Section Name="InOut">
                    <Member Name="Drive" Datatype="&quot;MotL&quot;" />
                  </Section>
                  <Section Name="Temp">
                    <Member Name="Level" Datatype="&quot;AnaVal&quot;" />
                  </Section>
                </Sections>
              </Interface>
              <Name>Motor</Name>
            </AttributeList>
          </SW.Blocks.FB>
        </Document>
        """;

    private const string DataType = """
        <?xml version="1.0" encoding="utf-8"?>
        <Document>
          <SW.Types.PlcStruct ID="0">
            <AttributeList>
              <Interface>
                <Sections>
                  <Section Name="None">
                    <Member Name="Value" Datatype="Real" />
                    <Member Name="ST" Datatype="Byte">
                      <StartValue>BYTE#16#80</StartValue>
                    </Member>
                  </Section>
                </Sections>
              </Interface>
              <Name>AnaVal</Name>
            </AttributeList>
          </SW.Types.PlcStruct>
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
          "objectCount": 5,
          "objects": [
            { "guid": "db1", "name": "Limits", "type": "DB", "form": "NATIVE",
              "path": "Blocks/Limits.xml", "hash": "sha256:0", "bytes": 1 },
            { "guid": "db2", "name": "Pid", "type": "DB", "form": "SCL_REVIEW",
              "path": "Blocks/Pid.scl", "hash": "sha256:0", "bytes": 1 },
            { "guid": "db2", "name": "Pid", "type": "DB", "form": "NATIVE",
              "path": "Blocks/Pid.xml", "hash": "sha256:0", "bytes": 1 },
            { "guid": "fb1", "name": "Motor", "type": "FB", "form": "SCL",
              "path": "Blocks/Motor.scl", "hash": "sha256:0", "bytes": 1 },
            { "guid": "fb1", "name": "Motor", "type": "FB", "form": "NATIVE",
              "path": "Blocks/Motor.xml", "hash": "sha256:0", "bytes": 1 },
            { "guid": "udt1", "name": "AnaVal", "type": "UDT", "form": "SCL",
              "path": "Types/AnaVal.udt", "hash": "sha256:0", "bytes": 1 },
            { "guid": "udt1", "name": "AnaVal", "type": "UDT", "form": "XML_REVIEW",
              "path": "Types/AnaVal.xml", "hash": "sha256:0", "bytes": 1 }
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
        File.WriteAllLines(Path.Combine(blocks, "Motor.scl"),
        [
            "FUNCTION_BLOCK \"Motor\"",
            "VERSION : 0.1",
            "   VAR_INPUT",
            "      Enable : Bool;",
            "   END_VAR",
            "",
            "BEGIN",
            "   #Enable := TRUE;",
            "END_FUNCTION_BLOCK",
        ]);

        var types = Path.Combine(repo.Path, "plc", "Station", "Types");
        Directory.CreateDirectory(types);
        File.WriteAllText(Path.Combine(types, "AnaVal.udt"),
            "TYPE \"AnaVal\"\nVERSION : 0.1\n   STRUCT\n      Value : Real;\n   END_STRUCT;\nEND_TYPE\n");
        // With a byte-order mark, as the real export writes it: the committed copy then
        // arrives from `git show` with the BOM as a character, which is the path that left
        // the owner's changed UDT unmarked until the parser learned to skip it.
        File.WriteAllText(Path.Combine(types, "AnaVal.xml"), DataType, new System.Text.UTF8Encoding(true));
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
    public async Task A_data_type_renders_as_its_member_table_not_its_scl()
    {
        // The .udt is the primary file and reads as SCL; the table comes from the native XML
        // beside it, the same route a data block's .scl takes. Shown as plain text until
        // 2026-10-04.
        using var repo = new TempRepo();
        GivenATrackedPortalProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        var node = await Row(shell, "AnaVal");
        Assert.EndsWith(".udt", node.PrimaryFile, StringComparison.OrdinalIgnoreCase);

        shell.Sync.SelectedNode = node;

        var table = Assert.IsType<MemberTable>(shell.RenderedContent);
        Assert.Equal("AnaVal", table.Name);
        Assert.True(table.IsDataType);
        Assert.Equal(["Value", "ST"], table.Members.Select(m => m.Name));
    }

    [Fact]
    public async Task A_function_block_renders_its_interface_over_its_code()
    {
        // Source only until 2026-10-04. The code is still there, under the table: tabling the
        // interface must not take away what the Rendered tab always showed.
        using var repo = new TempRepo();
        GivenATrackedPortalProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = await Row(shell, "Motor");

        var render = Assert.IsType<InterfaceRender>(shell.RenderedContent);
        Assert.True(render.Table.IsInterface);
        Assert.Equal(["Input", "Enable", "InOut", "Drive", "Temp", "Level"],
            render.Table.Members.Select(m => m.Name));
        // Only the body (owner, 2026-10-05): no declaration - that is the table above - no
        // BEGIN or END_FUNCTION_BLOCK, the shared indent gone, numbered from 1. The file's own
        // line is kept underneath, and Raw still has the whole file.
        var lines = render.Source.Rows.OfType<Versio.Core.Iec.SourceCodeLine>().ToList();
        var only = Assert.Single(lines);
        Assert.Equal("#Enable := TRUE;", only.Text);
        Assert.Equal(1, only.Number);
        Assert.Equal(8, only.FileLine);
        Assert.Contains("END_FUNCTION_BLOCK", render.Source.PseudoDiff, StringComparison.Ordinal);
    }

    /// <summary>Commit the data type only, so the block stays in the tree and the type is clean.</summary>
    private static void CommitTheDataType(TempRepo repo)
    {
        repo.Git("add plc/Station/Types");
        repo.Git("commit -q -m types");
    }

    private static MemberRow Member(ShellViewModel shell, string name) =>
        Assert.Single(Assert.IsType<InterfaceRender>(shell.RenderedContent).Table.Members, m => m.Name == name);

    [Fact]
    public async Task A_member_typed_by_an_exported_type_links_to_it_and_a_library_type_does_not()
    {
        using var repo = new TempRepo(identity: true);
        GivenATrackedPortalProject(repo);
        CommitTheDataType(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Sync.SelectedNode = await Row(shell, "Motor");

        var level = Member(shell, "Level");
        Assert.Equal("udt1", level.LinkGuid);
        Assert.False(level.ReferenceChanged, "AnaVal is committed and untouched");

        // MotL is referenced by name and was never exported: text, not a link to nowhere.
        var drive = Member(shell, "Drive");
        Assert.Equal("MotL", drive.TypeReference);
        Assert.False(drive.HasLink);
    }

    [Fact]
    public async Task A_changed_type_marks_the_member_and_its_link_opens_the_type_on_its_diff()
    {
        // The owner's case: the UDT changed, the FB's own diff is empty, and the FB row must
        // say so - then one press shows what changed.
        using var repo = new TempRepo(identity: true);
        GivenATrackedPortalProject(repo);
        CommitTheDataType(repo);

        File.AppendAllText(Path.Combine(repo.Path, "plc", "Station", "Types", "AnaVal.udt"), "// edited\n");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Sync.SelectedNode = await Row(shell, "Motor");

        var level = Member(shell, "Level");
        Assert.True(level.ReferenceChanged);
        Assert.False(Member(shell, "Enable").ReferenceChanged);

        await shell.OpenObjectAsync(level.LinkGuid!);

        var type = Assert.IsType<MemberTable>(shell.RenderedContent);
        Assert.Equal("AnaVal", type.Name);

        // The tree went there too (owner, 2026-10-05): the type's row is the selected one,
        // its folders are open, and the block's row let go - so the tree and the pane agree
        // about what is being looked at.
        var selected = Assert.IsType<TreeRow>(shell.Sync.SelectedNode);
        Assert.Equal("AnaVal", selected.Name);
        Assert.True(selected.IsSelected);
        Assert.All(Ancestors(shell.Sync.Tree, selected), a => Assert.True(a.IsExpanded, a.Name + " stayed shut"));
        Assert.False((await Row(shell, "Motor")).IsSelected);

        // Rendered first, always - and the table says what changed. Here only the SCL got a
        // comment, so the members are the same and the table says so rather than marking
        // nothing under a row that reads "modified". The line-level diff waits on Raw.
        Assert.True(shell.ShowRenderedPane);
        await Eventually.Until(
            () => shell.RenderedContent is MemberTable { ChangeSummary: var s }
                  && s.Contains("not in its members", StringComparison.Ordinal),
            "the table says the change is outside its members");
        await Eventually.Until(
            () => shell.SelectedDiffPath?.EndsWith("AnaVal.udt", StringComparison.OrdinalIgnoreCase) == true
                  && shell.SelectedDiff.Contains("// edited", StringComparison.Ordinal),
            "the type's diff is ready on Raw");
    }

    [Fact]
    public async Task A_changed_member_is_marked_on_the_table_with_what_it_was()
    {
        // The owner's point, 2026-10-05: "it needs to show on the table". A default value
        // changed in the type's native file; the table marks that row and keeps the old value.
        using var repo = new TempRepo(identity: true);
        GivenATrackedPortalProject(repo);
        CommitTheDataType(repo);

        var xml = Path.Combine(repo.Path, "plc", "Station", "Types", "AnaVal.xml");
        File.WriteAllText(xml, File.ReadAllText(xml)
            .Replace("BYTE#16#80", "BYTE#16#00", StringComparison.Ordinal)
            .Replace("<Member Name=\"Value\" Datatype=\"Real\" />",
                     "<Member Name=\"Value\" Datatype=\"Real\" /><Member Name=\"Quality\" Datatype=\"Int\" />",
                     StringComparison.Ordinal));

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Sync.SelectedNode = await Row(shell, "AnaVal");

        Assert.True(shell.ShowRenderedPane);
        await Eventually.Until(
            () => shell.RenderedContent is MemberTable { HasChangeSummary: true },
            "the table was marked");

        var table = Assert.IsType<MemberTable>(shell.RenderedContent);
        Assert.Equal("Since the last commit: 1 changed, 1 added - marked below.", table.ChangeSummary);

        var st = Assert.Single(table.Members, m => m.Name == "ST");
        Assert.True(st.IsModified);
        Assert.Contains("BYTE#16#80", st.Was, StringComparison.Ordinal);
        Assert.True(Assert.Single(table.Members, m => m.Name == "Quality").IsAdded);
        Assert.Equal(MemberChange.None, Assert.Single(table.Members, m => m.Name == "Value").Change);
    }

    private static IEnumerable<TreeRow> Ancestors(IEnumerable<TreeRow> roots, TreeRow target)
    {
        foreach (var root in roots)
        {
            if (ReferenceEquals(root, target))
            {
                return [];
            }

            if (root.Node.Children.Count > 0 && root.IsExpanded
                && Ancestors(root.Children, target) is var below
                && (below.Any() || root.Children.Contains(target)))
            {
                return below.Prepend(root);
            }
        }

        return [];
    }

    [Fact]
    public async Task From_the_explorer_a_link_selects_in_the_explorer_and_stays_there()
    {
        // The panel on screen is tried first: a link followed while browsing the Explorer
        // must not throw the user over to the Project panel just because it lists the object
        // too.
        using var repo = new TempRepo();
        GivenATrackedPortalProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.ActivePanel = ShellPanel.Explorer;
        await shell.Explorer.RefreshAsync();

        await shell.OpenObjectAsync("udt1");

        Assert.Equal(ShellPanel.Explorer, shell.ActivePanel);
        var selected = Assert.IsType<TreeRow>(shell.Explorer.SelectedNode);
        Assert.Equal("AnaVal", selected.Name);
        Assert.True(selected.IsSelected);
        Assert.Equal("AnaVal", Assert.IsType<MemberTable>(shell.RenderedContent).Name);
    }

    [Fact]
    public async Task A_link_to_an_object_in_neither_tree_changes_nothing()
    {
        using var repo = new TempRepo();
        GivenATrackedPortalProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Sync.SelectedNode = await Row(shell, "Motor");
        await shell.OpenObjectAsync("no-such-guid");

        // Compared by name, not by instance: the table may be swapped for its change-marked
        // copy while this runs, which is the same view of the same block.
        Assert.Equal("Motor", Assert.IsType<InterfaceRender>(shell.RenderedContent).Table.Name);

        // And the rail stayed put: a link to nothing does not move anybody to another panel.
        Assert.Equal(ShellPanel.Project, shell.ActivePanel);
    }
}
