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
    public void Objects_with_no_declaration_are_not_tables(string relative)
    {
        Assert.Null(CodesysProjectFormat.Instance.TryParseMemberTable(relative, FbsFile(relative)));
    }
}
