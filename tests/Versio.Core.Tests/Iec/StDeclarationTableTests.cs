using Versio.Core.Iec;
using Versio.Core.Projects;
using Versio.Core.Text;
using Xunit;

namespace Versio.Core.Tests.Iec;

/// <summary>
/// IEC 61131-3 declarations read into the member table a TIA interface already fills, so a
/// block reads the same way on every platform: variables as a table, logic underneath.
/// </summary>
/// <remarks>
/// The declarations are the fixture's own (RefrigeratorControl's FB_Alarm, ST_AlarmSettings,
/// E_AlarmCategory, Glob_Var), written out here so that this pure suite needs no files.
/// </remarks>
public class StDeclarationTableTests
{
    private const string FbAlarm = """
        {attribute 'linkalways'}
        FUNCTION_BLOCK FB_Alarm
        VAR PERSISTENT
        	Settings 		: ST_AlarmSettings;
        	Configuration	: ST_AlarmHmiConfiguration;
        END_VAR
        VAR_INPUT
        	Condition 		: BOOL;			// Input Condition to trigger alarm
        END_VAR
        VAR
        	// State Machine
        	State 			: E_AlarmState := E_AlarmState.Off;
        	NextState 		: E_AlarmState := E_AlarmState.Off;

        	// Timer
        	DelayTimer		: TON;

        	{attribute 'instance-path'}
        	{attribute 'no-init'}
        	FullInstancePath		: STRING;
        END_VAR
        """;

    private static IReadOnlyList<string> Shape(MemberTable table) =>
        table.Members.Select(m => m.IsSectionHeading
            ? $"[{m.Name}]"
            : $"{m.Name} : {m.Datatype}{(m.StartValue.Length > 0 ? " := " + m.StartValue : "")}").ToList();

    private static MemberRow Row(MemberTable table, string name) =>
        Assert.Single(table.Members, m => !m.IsSectionHeading && m.Name == name);

    [Fact]
    public void A_function_block_is_an_interface_in_sections_named_as_tia_names_them()
    {
        var table = StDeclarationTable.Parse(FbAlarm, "ignored")!;

        Assert.Equal("FB_Alarm", table.Name);
        Assert.True(table.IsInterface);
        Assert.Equal(
        [
            "[Static (persistent)]", "Settings : ST_AlarmSettings", "Configuration : ST_AlarmHmiConfiguration",
            "[Input]", "Condition : BOOL",
            "[Static]", "State : E_AlarmState := E_AlarmState.Off", "NextState : E_AlarmState := E_AlarmState.Off",
            "DelayTimer : TON", "FullInstancePath : STRING",
        ], Shape(table));
    }

    [Fact]
    public void A_trailing_comment_belongs_to_its_line_and_otherwise_the_lines_just_above_do()
    {
        var table = StDeclarationTable.Parse(FbAlarm, "x")!;

        Assert.Equal("Input Condition to trigger alarm", Row(table, "Condition").Comment);
        Assert.Equal("State Machine", Row(table, "State").Comment);

        // A blank line ends the run: "// Timer" is not NextState's, and a pragma between a
        // comment and its variable does not break it.
        Assert.Equal("", Row(table, "NextState").Comment);
        Assert.Equal("Timer", Row(table, "DelayTimer").Comment);
    }

    [Fact]
    public void A_user_type_is_a_reference_for_the_host_to_link_and_an_elementary_one_is_not()
    {
        var table = StDeclarationTable.Parse(FbAlarm, "x")!;

        Assert.Equal("ST_AlarmSettings", Row(table, "Settings").TypeReference);
        Assert.Equal("TON", Row(table, "DelayTimer").TypeReference);
        Assert.Equal("", Row(table, "Condition").TypeReference);
        Assert.Equal("", Row(table, "FullInstancePath").TypeReference);
    }

    [Fact]
    public void A_method_returns_under_its_own_name_and_its_plain_var_is_temp()
    {
        var table = StDeclarationTable.Parse("""
            METHOD PUBLIC M_Check : BOOL
            VAR_INPUT
            	Limit : INT := 10;
            END_VAR
            VAR
            	i : INT;
            END_VAR
            """, "x")!;

        Assert.Equal("M_Check", table.Name);
        Assert.Equal(["[Return]", "M_Check : BOOL", "[Input]", "Limit : INT := 10", "[Temp]", "i : INT"], Shape(table));
    }

    [Fact]
    public void A_property_is_its_return_type()
    {
        var table = StDeclarationTable.Parse("{attribute 'monitoring' := 'call'}\nPROPERTY_Get P_Active : BOOL", "x")!;

        Assert.Equal("P_Active", table.Name);
        Assert.Equal(["[Return]", "P_Active : BOOL"], Shape(table));
    }

    [Fact]
    public void A_structure_is_a_data_type_with_no_headings()
    {
        var table = StDeclarationTable.Parse("""
            TYPE ST_AlarmSettings :
            STRUCT
            	ShortDesc			: STRING(20);
            	Latch				: BOOL := FALSE;
            	OnDelayTime			: TIME := T#0S;
            END_STRUCT
            END_TYPE
            """, "x")!;

        Assert.Equal("ST_AlarmSettings", table.Name);
        Assert.True(table.IsDataType);
        Assert.Equal(["ShortDesc : STRING(20)", "Latch : BOOL := FALSE", "OnDelayTime : TIME := T#0S"], Shape(table));
    }

    [Fact]
    public void An_enumeration_is_its_values_with_their_numbers_as_defaults()
    {
        var table = StDeclarationTable.Parse("""
            {attribute 'qualified_only'}
            {attribute 'strict'}
            TYPE E_AlarmCategory :
            (
            	General 	:= 0,
            	Power 		:= 1,	// mains
            	Hardware 	:= 4
            ) INT;
            END_TYPE
            """, "x")!;

        Assert.Equal("E_AlarmCategory", table.Name);
        Assert.True(table.IsDataType);
        Assert.Equal(["General : INT := 0", "Power : INT := 1", "Hardware : INT := 4"], Shape(table));
        Assert.Equal("mains", Row(table, "Power").Comment);
        Assert.All(table.Members, m => Assert.Equal("", m.TypeReference));
    }

    [Fact]
    public void A_global_list_takes_its_name_from_the_file_and_has_no_headings_with_one_section()
    {
        var table = StDeclarationTable.Parse("""
            {attribute 'qualified_only'}
            VAR_GLOBAL
            //Inputs
            	TempActual: REAL:=8.0;
            	DoorOpen: BOOL;

            //Outputs
            	Compressor, Lamp: BOOL;
            END_VAR
            """, "Glob_Var")!;

        Assert.Equal("Glob_Var", table.Name);
        Assert.True(table.IsVariableList);
        Assert.False(table.IsInterface);
        Assert.Equal(["TempActual : REAL := 8.0", "DoorOpen : BOOL", "Compressor : BOOL", "Lamp : BOOL"], Shape(table));
        Assert.Equal("Outputs", Row(table, "Lamp").Comment);
    }

    [Fact]
    public void A_namespaced_list_is_named_by_its_namespace_and_a_constant_block_gets_headings()
    {
        var table = StDeclarationTable.Parse("""
            NAMESPACE GVL_Alarm
            VAR_GLOBAL
            	Count : DINT;
            END_VAR
            VAR_GLOBAL CONSTANT
            	AlarmConstant : DINT := 3;
            END_VAR
            END_NAMESPACE
            """, "ignored")!;

        Assert.Equal("GVL_Alarm", table.Name);
        Assert.Equal(["[Global]", "Count : DINT", "[Global (constant)]", "AlarmConstant : DINT := 3"], Shape(table));
    }

    [Fact]
    public void Initial_values_with_brackets_strings_and_line_breaks_stay_whole()
    {
        var table = StDeclarationTable.Parse("""
            PROGRAM P
            VAR
            	Text : STRING := 'a;b := c';
            	Table : ARRAY[1..3] OF INT := [1, 2,
            	                              3];
            	Input AT %IX0.0 : BOOL;
            END_VAR
            """, "x")!;

        Assert.Equal("'a;b := c'", Row(table, "Text").StartValue);
        // Joined across the line break; only the run of indentation in between is not the source's.
        Assert.Equal("[1, 2, 3]", System.Text.RegularExpressions.Regex.Replace(Row(table, "Table").StartValue, @"\s+", " "));
        Assert.Equal("ARRAY[1..3] OF INT", Row(table, "Table").Datatype);
        Assert.Equal("BOOL", Row(table, "Input (AT %IX0.0)").Datatype);
    }

    [Fact]
    public void A_declaration_missing_its_semicolon_stays_in_its_own_section()
    {
        // It must not swallow END_VAR and the next section's variables into one row, and it must
        // not land under the next section either.
        var table = StDeclarationTable.Parse("PROGRAM P\nVAR_INPUT\n\ta : INT\nEND_VAR\nVAR\n\tb : BOOL;\nEND_VAR", "x")!;

        Assert.Equal(["[Input]", "a : INT", "[Static]", "b : BOOL"], Shape(table));
    }

    [Theory]
    [InlineData("INTERFACE I_Alarm\nMETHOD M : BOOL")]
    [InlineData("TYPE Temperature : REAL;\nEND_TYPE")]
    [InlineData("x := 1;")]
    [InlineData("")]
    public void What_is_not_a_table_is_null(string declaration)
    {
        Assert.Null(StDeclarationTable.Parse(declaration, "x"));
    }

    [Fact]
    public void Versios_own_st_is_read_by_its_declaration_half_and_its_body_follows_the_delimiter()
    {
        var file = StTextCodec.Join("PROGRAM PLC_PRG\nVAR\n\tx : BOOL;\nEND_VAR", "x := NOT x;");

        var table = StDeclarationTable.FromSource("Application/PLC_PRG.st", file)!;
        var body = StDeclarationTable.SourceCodeBody(file);

        Assert.Equal(["[Static]", "x : BOOL"], Shape(table));
        Assert.NotNull(body);
        Assert.Equal(["x := NOT x;"], StSourceLayout.Body(file, body.Value.Start, body.Value.End).Cast<SourceCodeLine>().Select(l => l.Text));
        Assert.Null(StDeclarationTable.FromSource("Application/PLC_PRG.xml", file));
    }

    [Fact]
    public void A_table_with_no_access_column_says_so()
    {
        Assert.False(StDeclarationTable.Parse(FbAlarm, "x")!.HasExternalAccess);
        Assert.True(new MemberTable { Members = [new MemberRow { ExternalAccess = "Read" }] }.HasExternalAccess);
    }

    [Fact]
    public void A_nested_block_comment_ends_at_its_own_closer_not_the_inner_one()
    {
        // IEC 61131-3 permits (* … (* … *) … *) and CODESYS enables nested comments by
        // default. A flag instead of a depth ended the comment at the INNER "*)", so
        // everything up to the real closer was tabled as members that do not exist —
        // nStray below used to be a row — while the declaration's real tail was
        // swallowed. The table is what a reviewer reads before committing.
        const string source = """
            FUNCTION_BLOCK FB_Doc
            VAR_INPUT
                nReal : INT;
                (* outer (* inner *) nStray : BOOL; *)
                nAfter : INT;
            END_VAR
            END_FUNCTION_BLOCK
            """;

        var table = StDeclarationTable.Parse(source, "x")!;

        Assert.Equal(["[Input]", "nReal : INT", "nAfter : INT"], Shape(table));
    }

    // The owner's own library (CodesysStandardLibrary, 2026-10-23): F_UpdateMeta gained one
    // {attribute 'linkalways'} that the Rendered tab did not show at all.
    private const string UpdateMeta = """
        {attribute 'linkalways'}
        /// Refreshes a block's SCADA Meta identity.
        FUNCTION F_UpdateMeta
        VAR_INPUT
        	// Programs can use the implicit path.
        	{attribute 'implicit-parameter' := 'instance-path'}
        	InstancePath		: STRING(255) := '';
        END_VAR
        VAR
        	// SCADA
        	{attribute 'symbol' := 'readwrite'}
        	Scada		: ST_AlarmScada;

        	{attribute 'instance-path'}

        	{attribute 'noinit'}
        	FullPath	: STRING(255);
        END_VAR
        """;

    private static IReadOnlyList<string> Chips(IEnumerable<MemberAttribute> attributes) =>
        attributes.Select(a => a.Text).ToList();

    [Fact]
    public void The_attributes_above_the_header_are_the_objects_own()
    {
        var table = StDeclarationTable.Parse(UpdateMeta, "x")!;

        Assert.Equal(["linkalways"], Chips(table.Attributes));
        Assert.Equal(new LineSpan(1, 1), table.Attributes[0].Lines);
        Assert.Equal(["symbol = readwrite"], Chips(StDeclarationTable.Parse(FbAlarmScada, "x")!.Attributes));
    }

    private const string FbAlarmScada = "{attribute 'symbol' := 'readwrite'}\nFUNCTION_BLOCK FB_X\nVAR\n\tx : INT;\nEND_VAR";

    [Fact]
    public void A_variables_attributes_are_its_own_with_its_comment_kept_and_its_lines_starting_at_them()
    {
        var table = StDeclarationTable.Parse(UpdateMeta, "x")!;

        var scada = Row(table, "Scada");
        Assert.Equal(["symbol = readwrite"], Chips(scada.Attributes));
        Assert.Equal("readwrite", scada.Attributes[0].Value);
        // The comment above the pragma is still the variable's, and the row starts at the
        // pragma, so a changed attribute marks the row and a review comment on it lands here.
        Assert.Equal("SCADA", scada.Comment);
        Assert.Equal(new LineSpan(11, 12), scada.Lines);

        Assert.Equal(["implicit-parameter = instance-path"], Chips(Row(table, "InstancePath").Attributes));
        Assert.Empty(table.Members.Where(m => m.IsSectionHeading).SelectMany(m => m.Attributes));
    }

    [Fact]
    public void A_blank_line_does_not_take_an_attribute_from_the_declaration_it_precedes()
    {
        var full = Row(StDeclarationTable.Parse(UpdateMeta, "x")!, "FullPath");

        Assert.Equal(["instance-path", "noinit"], Chips(full.Attributes));
        Assert.Equal(new LineSpan(14, 17), full.Lines);
    }

    [Fact]
    public void A_pragma_after_a_declarations_code_belongs_to_the_next_one()
    {
        var table = StDeclarationTable.Parse("FUNCTION_BLOCK B\nVAR\n\ta : INT; {attribute 'hide'}\n\tb : INT;\nEND_VAR", "x")!;

        Assert.Empty(Row(table, "a").Attributes);
        Assert.Equal(["hide"], Chips(Row(table, "b").Attributes));
    }

    [Fact]
    public void A_structures_members_carry_their_attributes_too()
    {
        var table = StDeclarationTable.Parse("{attribute 'pack_mode' := '1'}\nTYPE S :\nSTRUCT\n\t{attribute 'hide'}\n\tx : INT;\nEND_STRUCT\nEND_TYPE", "x")!;

        Assert.Equal(["pack_mode = 1"], Chips(table.Attributes));
        Assert.Equal(["hide"], Chips(Row(table, "x").Attributes));
        Assert.Empty(table.Unshown);
    }

    [Fact]
    public void Every_line_the_table_draws_is_claimed_and_the_rest_is_unshown()
    {
        var table = StDeclarationTable.Parse(UpdateMeta, "x")!;

        // The doc comment is drawn nowhere; "FUNCTION" stands for the header, whose name is the
        // title. Everything else is a row, a heading, an attribute or a comment given to a row.
        Assert.Equal([new UnshownLine(2, "/// Refreshes a block's SCADA Meta identity."), new UnshownLine(3, "FUNCTION")], table.Unshown);
    }

    [Fact]
    public void A_pragma_that_is_not_an_attribute_and_a_comment_between_sections_are_unshown()
    {
        var table = StDeclarationTable.Parse(
            "FUNCTION_BLOCK B EXTENDS A\nVAR_INPUT\n\tx : INT;\nEND_VAR\n// between\n{warning 'check'}\nVAR\n\ty : INT;\nEND_VAR", "x")!;

        Assert.Equal(["FUNCTION_BLOCK EXTENDS A", "// between", "{warning 'check'}"], table.Unshown.Select(u => u.Text).ToList());
        Assert.Empty(table.Attributes);
    }

    [Fact]
    public void A_functions_return_type_is_drawn_so_only_its_keywords_stand_for_the_header()
    {
        var table = StDeclarationTable.Parse("METHOD PRIVATE M : BOOL // ok\nVAR_INPUT\n\tx : INT;\nEND_VAR", "x")!;

        Assert.Equal([new UnshownLine(1, "METHOD PRIVATE")], table.Unshown);
    }

    [Fact]
    public void Unshown_lines_are_numbered_as_file_lines()
    {
        var table = StDeclarationTable.Parse(UpdateMeta, "x", firstLine: 10)!;

        Assert.Equal([11, 12], table.Unshown.Select(u => u.Line).ToList());
        Assert.Equal(new LineSpan(10, 10), table.Attributes[0].Lines);
        Assert.Equal(new LineSpan(20, 20), Row(table, "Scada").Attributes[0].Lines);
    }
}
