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
        // The comment above the pragma is still the variable's, and the row starts at that
        // comment (2026-10-23; at the pragma before): every line the row draws a cell from is
        // the row's, so a changed comment or attribute marks the row, review coverage vouches
        // for the line through it, and a review comment on either line lands here.
        Assert.Equal("SCADA", scada.Comment);
        Assert.Equal(new LineSpan(10, 12), scada.Lines);

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

        // The doc comment is drawn nowhere. The header says nothing the table does not draw (its
        // name is the title, its kind the table's kind, its type the Return row), so it is framing
        // rather than unshown. Everything else is a row, a heading, an attribute or a comment
        // given to a row.
        Assert.Equal([new UnshownLine(2, "/// Refreshes a block's SCADA Meta identity.")], table.Unshown);
    }

    /// <summary>
    /// The framing: the header, the section keywords and blank lines, as spans. What review coverage
    /// vouches for through the table (docs/web/PLAN.md, C4), and nothing a row or an attribute draws.
    /// </summary>
    [Fact]
    public void The_declarations_own_syntax_and_its_blank_lines_are_framing()
    {
        var table = StDeclarationTable.Parse("FUNCTION_BLOCK FB\nVAR_INPUT\n\tx : INT;\n\n\t// doc\n\ty : INT;\nEND_VAR\n// stray\nVAR\nEND_VAR", "x")!;

        // Line 1 the header, 2 VAR_INPUT, 4 blank, 7 END_VAR, 9 VAR, 10 END_VAR (file lines, from 1);
        // the stray comment is unshown, the rows are drawn.
        Assert.Equal([new LineSpan(1, 2), new LineSpan(4, 4), new LineSpan(7, 7), new LineSpan(9, 10)], table.Structural);
        Assert.Equal(["// stray"], table.Unshown.Select(u => u.Text));
        Assert.Equal(new LineSpan(5, 6), Row(table, "y").Lines);

        // A header that says more (EXTENDS) is unshown, not framing.
        var extended = StDeclarationTable.Parse("FUNCTION_BLOCK FB EXTENDS Base\nVAR\nEND_VAR", "x")!;
        Assert.Equal([new LineSpan(2, 3)], extended.Structural);
        Assert.Equal(["EXTENDS Base"], extended.Unshown.Select(u => u.Text));

        // Shifted with the rows when the declaration is read out of a larger file.
        Assert.Equal([new LineSpan(10, 11), new LineSpan(13, 13), new LineSpan(16, 16), new LineSpan(18, 19)],
            StDeclarationTable.Parse("FUNCTION_BLOCK FB\nVAR_INPUT\n\tx : INT;\n\n\t// doc\n\ty : INT;\nEND_VAR\n// stray\nVAR\nEND_VAR", "x", firstLine: 10)!.Structural);
    }

    [Fact]
    public void A_pragma_that_is_not_an_attribute_and_a_comment_between_sections_are_unshown()
    {
        var table = StDeclarationTable.Parse(
            "FUNCTION_BLOCK B EXTENDS A\nVAR_INPUT\n\tx : INT;\nEND_VAR\n// between\n{warning 'check'}\nVAR\n\ty : INT;\nEND_VAR", "x")!;

        // The header stands as what it says beyond its keywords: the kind is the table's kind.
        Assert.Equal(["EXTENDS A", "// between", "{warning 'check'}"], table.Unshown.Select(u => u.Text).ToList());
        Assert.Empty(table.Attributes);
    }

    [Fact]
    public void A_functions_return_type_is_drawn_and_its_keywords_are_framing_so_nothing_stands_for_the_header()
    {
        var table = StDeclarationTable.Parse("METHOD PRIVATE M : BOOL // ok\nVAR_INPUT\n\tx : INT;\nEND_VAR", "x")!;

        // Until 2026-10-23 "METHOD PRIVATE" stood in for the line, so a header that gained PUBLIC
        // warned "1 declaration change is not shown" beside a code block showing every change (the
        // owner's MoveHome). The keyword is the kind and the modifier is not reported — stated as a
        // cost on HeaderResidue — and the type with its comment is the Return row.
        Assert.Empty(table.Unshown);
        Assert.Equal(new LineSpan(1, 1), Row(table, "M").Lines);
    }

    [Fact]
    public void Several_declarations_on_one_line_are_each_their_own_row()
    {
        // Review 2026-10-25, item 9: "a : INT; b : BOOL;" tabled one variable a of type
        // "INT; b : BOOL", and nothing said b was missing. IEC 61131-3 puts no line breaks in its
        // grammar; a statement ends at its semicolon, wherever the next one starts.
        var table = StDeclarationTable.Parse(
            "FUNCTION_BLOCK B\nVAR\n\ta : INT; b : BOOL; // both\n\tc : STRING := 'x;y'; d : ARRAY[0..1] OF INT;\n\te : INT; f :\n\t\tBOOL;\nEND_VAR", "x")!;

        Assert.Equal(
            ["[Static]", "a : INT", "b : BOOL", "c : STRING := 'x;y'", "d : ARRAY[0..1] OF INT", "e : INT", "f : BOOL"],
            Shape(table));

        // The line's comment is written beside both; the line is both rows'. A declaration that
        // starts after another's semicolon and ends on the next line spans from where it started.
        Assert.Equal("both", Row(table, "a").Comment);
        Assert.Equal("both", Row(table, "b").Comment);
        Assert.Equal(new LineSpan(3, 3), Row(table, "b").Lines);
        Assert.Equal(new LineSpan(5, 6), Row(table, "f").Lines);
        Assert.Empty(table.Unshown);
    }

    [Fact]
    public void A_semicolon_on_a_line_of_its_own_ends_the_declaration_above_it()
    {
        // Review 2026-10-25, third round, item 1: "a : INT", a line holding only ";", then
        // "b : BOOL;" tabled one variable a of type "INT b : BOOL", and nothing said b was
        // missing — a regression from the second round's item 9, whose pieces skipped an empty
        // statement without closing the declaration still open above it.
        var table = StDeclarationTable.Parse(
            "FUNCTION_BLOCK B\nVAR\n\ta : INT\n\t; // ends a\n\tb : BOOL;\n\tc : INT\n\t; d : INT;\nEND_VAR", "x")!;

        Assert.Equal(["[Static]", "a : INT", "b : BOOL", "c : INT", "d : INT"], Shape(table));

        // The declaration ends on the semicolon's line, and that line's comment is its.
        Assert.Equal(new LineSpan(3, 4), Row(table, "a").Lines);
        Assert.Equal("ends a", Row(table, "a").Comment);
        Assert.Equal(new LineSpan(5, 5), Row(table, "b").Lines);
        Assert.Equal(new LineSpan(6, 7), Row(table, "c").Lines);
        Assert.Equal(new LineSpan(7, 7), Row(table, "d").Lines);
        Assert.Empty(table.Unshown);
    }

    [Fact]
    public void A_section_written_on_one_line_yields_its_rows_and_an_empty_one_is_framing()
    {
        // Review 2026-10-25, item 9: "VAR a : INT; END_VAR" on one line opened a section, drew no
        // row, claimed the line as framing, and so reported nothing unshown either.
        var table = StDeclarationTable.Parse(
            "FUNCTION_BLOCK B\nVAR_INPUT x : INT; END_VAR\nVAR CONSTANT k : INT := 1; END_VAR\nVAR END_VAR\nVAR\n\ty : INT;\nEND_VAR", "x")!;

        Assert.Equal(["[Input]", "x : INT", "[Constant]", "k : INT := 1", "[Static]", "y : INT"], Shape(table));
        Assert.Equal(new LineSpan(2, 2), Row(table, "x").Lines);
        Assert.Empty(table.Unshown);

        // "VAR END_VAR" says nothing a row would draw: framing, like a VAR on a line of its own.
        // And the END_VAR on the one-line section closed it: y is under the next VAR, not this one.
        Assert.Contains(new LineSpan(4, 5), table.Structural);

        var globals = StDeclarationTable.Parse("VAR_GLOBAL CONSTANT g : INT := 2; END_VAR", "Glob_Var")!;
        Assert.Equal(["g : INT := 2"], Shape(globals));
        Assert.Empty(globals.Unshown);
    }

    [Fact]
    public void A_one_line_section_whose_declaration_draws_no_row_is_reported_not_shown()
    {
        // The section keyword claimed its whole line; on a line that also holds a declaration the
        // table could not draw, that claim hid the declaration. Only a row may claim such a line.
        var table = StDeclarationTable.Parse("FUNCTION_BLOCK B\nVAR x INT; END_VAR", "x")!;

        Assert.DoesNotContain(table.Members, m => !m.IsSectionHeading);
        Assert.Equal(["VAR x INT; END_VAR"], table.Unshown.Select(u => u.Text));
    }

    [Fact]
    public void Unshown_lines_are_numbered_as_file_lines()
    {
        var table = StDeclarationTable.Parse(UpdateMeta, "x", firstLine: 10)!;

        Assert.Equal([11], table.Unshown.Select(u => u.Line).ToList());
        Assert.Equal(new LineSpan(10, 10), table.Attributes[0].Lines);
        Assert.Equal(new LineSpan(20, 20), Row(table, "Scada").Attributes[0].Lines);
    }
}
