using Versio.Core.Iec;
using Versio.Core.Projects;
using Versio.TestSupport;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// CODESYS objects drawn as TIA's are: a block's variables as a table with its logic underneath,
/// a type's members as a table, a global list as a table. From the fixture's real files, in the
/// classic mirror and in File-Based Storage alike.
/// </summary>
public sealed class CodesysMemberTableTests
{
    private const string Fbs = "Device.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/";
    private const string Classic = "Device/Plc Logic/Application/";

    private static string FbsFile(string relative) => File.ReadAllText(Path.Combine(
        TestPaths.Project("RefrigeratorControl_FBS"), "RefrigeratorControl.fbsproj", relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string ClassicFile(string relative) => File.ReadAllText(Path.Combine(
        TestPaths.Project("RefrigeratorControl"), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static MemberTable Table(string relative, string content)
    {
        var table = CodesysProjectFormat.Instance.TryParseMemberTable(relative, content);
        Assert.NotNull(table);
        return table;
    }

    private static IReadOnlyList<string> Shape(MemberTable table) =>
        table.Members.Select(m => m.IsSectionHeading ? $"[{m.Name}]" : $"{m.Name} : {m.Datatype}").ToList();

    [Fact]
    public void A_file_based_program_is_an_interface_table_and_its_code_is_what_is_under_it()
    {
        const string Relative = Fbs + "PLC_PRG.prg.st";
        var text = FbsFile(Relative);

        var table = Table(Relative, text);
        var body = CodesysProjectFormat.Instance.CodeBody(Relative, text);

        Assert.Equal("PLC_PRG", table.Name);
        Assert.True(table.IsInterface);
        Assert.Equal(["[Static]", "Alarm : FB_Alarm", "Trigger : BOOL", "OnTime : TIME", "OffTime : TIME", "Latch : BOOL"], Shape(table));
        Assert.NotNull(body);
        Assert.Equal("Alarm.P_Settings.Latch := Latch;", StSourceLayout.Body(text, body.Value.Start, body.Value.End).Cast<SourceCodeLine>().First().Text);
    }

    [Fact]
    public void The_classic_mirror_and_file_based_storage_give_the_same_table()
    {
        var classic = Table(Classic + "Library/Alarm/POUs/FB_Alarm.st", ClassicFile(Classic + "Library/Alarm/POUs/FB_Alarm.st"));
        var fbs = Table(Fbs + "Library/Alarm/POUs/FB_Alarm.fb.st", FbsFile(Fbs + "Library/Alarm/POUs/FB_Alarm.fb.st"));

        Assert.Equal(Shape(classic), Shape(fbs));
        Assert.Contains("[Static (persistent)]", Shape(fbs));
        Assert.Contains("Condition : BOOL", Shape(fbs));
    }

    [Fact]
    public void The_classic_code_body_is_everything_after_the_delimiter()
    {
        const string Relative = Classic + "PLC_PRG.st";
        var text = ClassicFile(Relative);

        var body = CodesysProjectFormat.Instance.CodeBody(Relative, text);

        Assert.NotNull(body);
        Assert.Equal(["Alarm.P_Settings.Latch := Latch;", "Alarm.P_Settings.OnDelayTime := OnTime;"],
            StSourceLayout.Body(text, body.Value.Start, body.Value.End).Cast<SourceCodeLine>().Take(2).Select(l => l.Text));
    }

    [Theory]
    [InlineData(Fbs + "Library/Alarm/DUTs/ST_AlarmSettings.struct", true)]
    [InlineData(Classic + "Library/Alarm/DUTs/ST_AlarmSettings.st", false)]
    public void A_structure_is_a_data_type_table(string relative, bool fileBased)
    {
        var table = Table(relative, fileBased ? FbsFile(relative) : ClassicFile(relative));

        Assert.Equal("ST_AlarmSettings", table.Name);
        Assert.True(table.IsDataType);
        Assert.Equal("T#0S", table.Members.Single(m => m.Name == "OnDelayTime").StartValue);
        Assert.Equal("E_AlarmSeverity", table.Members.Single(m => m.Name == "Severity").TypeReference);
    }

    [Theory]
    [InlineData(Fbs + "Glob_Var.gvl", true)]
    [InlineData(Classic + "Glob_Var.gvl.st", false)]
    public void A_global_list_is_a_variable_table_named_after_it(string relative, bool fileBased)
    {
        var table = Table(relative, fileBased ? FbsFile(relative) : ClassicFile(relative));

        Assert.Equal("Glob_Var", table.Name);
        Assert.True(table.IsVariableList);
        Assert.Equal("8.0", table.Members.Single(m => m.Name == "TempActual").StartValue);
        Assert.False(table.HasExternalAccess);
    }

    [Theory]
    [InlineData(Fbs + "Signals.pou.xml.v3", true)]
    [InlineData(Classic + "Signals.xml", false)]
    public void A_ladder_pou_tables_the_declaration_it_carries_so_the_table_sits_over_the_networks(string relative, bool fileBased)
    {
        var text = fileBased ? FbsFile(relative) : ClassicFile(relative);

        var table = Table(relative, text);

        Assert.Equal("Signals", table.Name);
        Assert.True(table.IsInterface);
        Assert.Equal(["[Static]", "TON_0 : TON", "TON_1 : TON", "tSignalTime : TIME", "BLINK_0 : BLINK"], Shape(table));
        Assert.NotNull(CodesysProjectFormat.Instance.TryParseLadder(relative, text));
    }

    [Fact]
    public void A_cfc_pou_is_not_tabled_until_cfc_is_drawn()
    {
        // A table over a thousand lines of XML would bury the one thing the table is for.
        const string Relative = "Device/Plc Logic/Application/PLC_PRG.xml";
        var path = Path.Combine(TestPaths.Project("MachineWithMembers"), Relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.Null(CodesysProjectFormat.Instance.TryParseMemberTable(Relative, File.ReadAllText(path)));
    }

    [Theory]
    [InlineData(Fbs + "Task Configuration.taskconfig.xml.v3^/MainTask.task.xml.v3")]
    [InlineData(Fbs + "Libraries.json")]
    // A visualisation carries an Interface with no Implementation too; its few parameters are
    // not what a reviewer opens a screen to see.
    [InlineData(Fbs + "Visualization.visual.xml.v3")]
    public void Objects_with_no_declaration_are_not_tables(string relative)
    {
        Assert.Null(CodesysProjectFormat.Instance.TryParseMemberTable(relative, FbsFile(relative)));
    }

    [Fact]
    public void A_persistent_variable_list_kept_as_archive_xml_is_a_global_variable_table()
    {
        // It drew as raw XML until 2026-10-23: only a ladder POU's XML declaration was tabled.
        const string Relative = Fbs + "PersistentVars.varpersistent.xml.v3";
        var text = FbsFile(Relative);

        var table = Table(Relative, text);

        Assert.Equal("PersistentVars", table.Name);
        Assert.True(table.IsVariableList);
        Assert.Contains(table.Members, m => m.Name.Contains("Settings", StringComparison.Ordinal) && m.Datatype == "ST_AlarmSettings");
        Assert.Contains(table.Members, m => m.Datatype == "ST_AlarmHmiConfiguration");

        // The rows name the file's own lines, header included, so a row finds its line in Raw.
        var settings = Assert.Single(table.Members, m => m.Datatype == "ST_AlarmSettings");
        Assert.Contains("ST_AlarmSettings", text.Replace("\r\n", "\n").Split('\n')[settings.Lines!.Value.Start - 1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_visualisation_with_parameters_is_still_not_a_table()
    {
        // The fixture's visualisation declares nothing, which would pass with no guard at all.
        // Given a parameter, its Interface is exactly the shape the persistent list has.
        const string Relative = Fbs + "Visualization.visual.xml.v3";
        var text = FbsFile(Relative).Replace("VAR_IN_OUT\n\t\nEND_VAR", "VAR_IN_OUT\n\tSpeed : INT;\nEND_VAR").Replace("VAR_IN_OUT\r\n\t\r\nEND_VAR", "VAR_IN_OUT\r\n\tSpeed : INT;\r\nEND_VAR");
        Assert.Contains("Speed : INT;", text, StringComparison.Ordinal);

        Assert.Null(CodesysProjectFormat.Instance.TryParseMemberTable(Relative, text));
    }

    [Fact]
    public void An_opc_ua_symbol_set_is_each_published_variable_with_its_members_nested_and_their_access()
    {
        // FsLibrary.fbslib's own Symbol Set, copied 2026-10-23 from commit 77e424f: one variable,
        // GVL_Scada.TesAlarm, published read-write with every member of its four structures.
        const string Relative = "Device.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/"
            + "Communication Manager.communicationmanager.xml.v3^/OPC UA Server.opcuacommunication.xml.v3^/Symbol Set.iecsymbolpublishing.xml.v3";
        var text = File.ReadAllText(Path.Combine(TestPaths.Fixtures, "files", "codesys", "Symbol Set.iecsymbolpublishing.xml.v3"));

        var table = Table(Relative, text);

        Assert.Equal("Symbol Set", table.Name);
        Assert.Equal(
            ["0 GVL_Scada.TesAlarm : ST_AlarmScada", "1 Meta : ST_AlarmScadaMeta", "2 InstanceName : STRING(255)",
             "2 InstancePath : STRING(255)", "2 Description : STRING(GVL_Alarm.DEFAULT_DESCRIPTION_LENGTH)",
             "1 Status : ST_AlarmScadaStatus"],
            table.Members.Take(6).Select(m => $"{m.Depth} {m.Name} : {m.Datatype}"));
        Assert.Equal(
            ["GVL_Scada.TesAlarm.Meta", "GVL_Scada.TesAlarm.Settings", "GVL_Scada.TesAlarm.Control.Reset"],
            table.Members.Select(m => m.Path).Where(p => p is "GVL_Scada.TesAlarm.Meta" or "GVL_Scada.TesAlarm.Settings" or "GVL_Scada.TesAlarm.Control.Reset"));
        Assert.All(table.Members, m => Assert.Equal("ReadWrite", m.ExternalAccess));
        // The root and thirteen members: AllEntries' fourteenth, TesAlarm as a member of the
        // GVL's own type entry, is the root itself and is not drawn twice.
        Assert.Equal(14, table.Members.Count);
    }

    [Fact]
    public void A_symbol_type_that_contains_itself_is_drawn_once_rather_than_forever()
    {
        // A linked-list node publishes a member of its own type. Without the branch guard the
        // walk recurses until the stack overflows, which in a background render is a crash.
        const string Text = """
            __V3_META__
            {}
            __V3_CONTENT__
            <?xml version="1.0" encoding="utf-8"?>
            <Single Type="{3cda6efc-db0e-4ab2-9926-6ef9cbceea87}">
            	<Single Name="SymbolSet"><List2 Name="SymbolItems"><Single>
            		<Single Name="FullPath" Type="string">GVL.Head</Single>
            		<Single Name="SymbolType" Type="string">ST_Node</Single>
            		<Single Name="AccessRights" Type="x">Read</Single>
            	</Single></List2></Single>
            	<Single Name="TypeSet">
            		<List2 Name="TypeEntries"><Single>
            			<Single Name="SymbolType" Type="string">ST_Node</Single>
            			<List2 Name="ChildIds"><Single Type="int">1</Single></List2>
            		</Single></List2>
            		<Dictionary2 Name="AllEntries"><Entry>
            			<Key><Single Type="int">1</Single></Key>
            			<Value><Single>
            				<Single Name="Checked" Type="bool">False</Single>
            				<Single Name="Name" Type="string">Next</Single>
            				<Single Name="AccessRights" Type="x">Read</Single>
            				<Single Name="SymbolType" Type="string">ST_Node</Single>
            			</Single></Value>
            		</Entry></Dictionary2>
            	</Single>
            </Single>
            """;

        var table = Table("Symbol Set.iecsymbolpublishing.xml.v3", Text);

        Assert.Equal(["GVL.Head", "GVL.Head.Next"], table.Members.Select(m => m.Path));
        Assert.Equal("not published", table.Members[1].Comment);
    }

    [Fact]
    public void A_symbol_configuration_is_its_settings_with_its_lists_counted_and_its_guids_left_out()
    {
        // FsLibrary.fbslib's own Symbols.symbolconfig.xml.v3, 2026-10-23, trimmed of nothing
        // but the header's GUIDs.
        const string Relative = "Device.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/Symbols.symbolconfig.xml.v3";
        const string Text = """
            __V3_META__
            {
            	"objectGuid": "096b458f-2bf7-48d6-b6d4-02652938b260",
            	"objectTypeGuid": "21d4fe94-4123-4e23-9091-ead220afbd1f",
            	"embeddedObjectTypeGuids": []
            }
            __V3_CONTENT__
            <?xml version="1.0" encoding="utf-8"?>
            <Single xml:space="preserve" Type="{21d4fe94-4123-4e23-9091-ead220afbd1f}" Method="IArchivable">
            	<Single Name="GVLGuid" Type="System.Guid">cc9b0bf5-2a50-4f81-abcd-ddc46bea470f</Single>
            	<List2 Name="SelectionSigns" />
            	<Single Name="LibraryIds" Type="{475e2d23-d370-486a-bf04-61cd7f272d63}" Method="IArchivable">
            		<Dictionary2 Name="Table" />
            	</Single>
            	<Single Name="ExportSymbolComments" Type="bool">True</Single>
            	<Single Name="SupportOPCUA" Type="bool">True</Single>
            	<Single Name="SymbolAttributeFilterType" Type="{920738ca-88e7-4433-88f7-4a50c373436a}">None</Single>
            	<Null Name="SymbolAttributeFilterData" />
            	<Single Name="EnableDirectIoAccess" Type="bool">False</Single>
            </Single>
            """;

        var table = Table(Relative, Text);

        Assert.Equal("Symbols", table.Name);
        Assert.Equal(
            ["SelectionSigns = 0 entries", "LibraryIds = 0 entries", "ExportSymbolComments = True", "SupportOPCUA = True",
             "SymbolAttributeFilterType = None", "SymbolAttributeFilterData = (none)", "EnableDirectIoAccess = False"],
            table.Members.Select(m => $"{m.Name} = {m.StartValue}"));
    }
}
