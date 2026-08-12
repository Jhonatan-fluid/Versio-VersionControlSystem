using Versio.Core.Serialization;
using Xunit;

namespace Versio.Core.Tests.Serialization;

/// <summary>
/// Decides which CODESYS factory creates a missing object. Getting it wrong creates the
/// WRONG KIND of object, which is worse than creating none — so anything ambiguous must
/// come back as Unknown.
/// </summary>
public class StDeclarationParserTests
{
    [Fact]
    public void A_program_is_recognised()
    {
        var d = StDeclarationParser.Parse("PROGRAM PLC_PRG\nVAR\nEND_VAR");

        Assert.Equal(PouKind.Program, d.Kind);
        Assert.Equal("PLC_PRG", d.Name);
        Assert.True(d.CanCreate);
    }

    [Fact]
    public void A_function_block_is_recognised()
    {
        var d = StDeclarationParser.Parse("FUNCTION_BLOCK Conveyor\nVAR_INPUT\nEND_VAR");

        Assert.Equal(PouKind.FunctionBlock, d.Kind);
        Assert.Equal("Conveyor", d.Name);
    }

    [Fact]
    public void A_function_carries_its_return_type()
    {
        // create_pou needs return_type, and it is only stated here.
        var d = StDeclarationParser.Parse("FUNCTION Add : INT\nVAR_INPUT\n a : INT;\nEND_VAR");

        Assert.Equal(PouKind.Function, d.Kind);
        Assert.Equal("Add", d.Name);
        Assert.Equal("INT", d.ReturnOrBaseType);
    }

    [Fact]
    public void A_function_returning_a_user_type_works()
    {
        var d = StDeclarationParser.Parse("FUNCTION Make : ST_Point");

        Assert.Equal("ST_Point", d.ReturnOrBaseType);
    }

    [Fact]
    public void EXTENDS_is_captured()
    {
        var d = StDeclarationParser.Parse("FUNCTION_BLOCK Child EXTENDS Parent\nVAR\nEND_VAR");

        Assert.Equal("Child", d.Name);
        Assert.Equal("Parent", d.Extends);
    }

    [Fact]
    public void IMPLEMENTS_is_captured()
    {
        var d = StDeclarationParser.Parse("FUNCTION_BLOCK FB IMPLEMENTS IMotor\nVAR\nEND_VAR");

        Assert.Equal(["IMotor"], d.Implements);
    }

    [Fact]
    public void Multiple_interfaces_are_captured()
    {
        var d = StDeclarationParser.Parse(
            "FUNCTION_BLOCK FB EXTENDS Base IMPLEMENTS IMotor, IDrive\nVAR\nEND_VAR");

        Assert.Equal("Base", d.Extends);
        Assert.Equal(["IMotor", "IDrive"], d.Implements);
    }

    [Theory]
    [InlineData("FUNCTION_BLOCK ABSTRACT FB")]
    [InlineData("FUNCTION_BLOCK FINAL FB")]
    [InlineData("FUNCTION_BLOCK INTERNAL FB")]
    public void Modifiers_between_the_keyword_and_the_name_are_skipped(string decl)
    {
        Assert.Equal("FB", StDeclarationParser.Parse(decl).Name);
    }

    [Fact]
    public void An_interface_is_recognised()
    {
        var d = StDeclarationParser.Parse("INTERFACE IMotor\nMETHOD Start : BOOL");

        Assert.Equal(PouKind.Interface, d.Kind);
        Assert.Equal("IMotor", d.Name);
    }

    [Fact]
    public void A_struct_DUT_is_recognised()
    {
        var d = StDeclarationParser.Parse("TYPE ST_Point :\nSTRUCT\n x : INT;\nEND_STRUCT\nEND_TYPE");

        Assert.Equal(PouKind.Dut, d.Kind);
        Assert.Equal(DutKind.Structure, d.Dut);
        Assert.Equal("ST_Point", d.Name);
    }

    [Fact]
    public void An_enum_DUT_is_recognised()
    {
        var d = StDeclarationParser.Parse("TYPE E_State :\n(\n Idle := 0,\n Running := 1\n);\nEND_TYPE");

        Assert.Equal(DutKind.Enumeration, d.Dut);
        Assert.Equal("E_State", d.Name);
    }

    [Fact]
    public void An_alias_DUT_carries_its_base_type()
    {
        // create_dut REQUIRES baseType for aliases.
        var d = StDeclarationParser.Parse("TYPE MyInt : INT;\nEND_TYPE");

        Assert.Equal(DutKind.Alias, d.Dut);
        Assert.Equal("MyInt", d.Name);
        Assert.Equal("INT", d.ReturnOrBaseType);
    }

    [Fact]
    public void A_union_DUT_is_recognised()
    {
        var d = StDeclarationParser.Parse("TYPE U_Data :\nUNION\n a : INT;\nEND_UNION\nEND_TYPE");

        Assert.Equal(DutKind.Union, d.Dut);
    }

    [Fact]
    public void An_attribute_pragma_before_the_keyword_does_not_hide_it()
    {
        // Real projects open with these. Glob_Var in the fixture starts
        // {attribute 'qualified_only'}.
        var d = StDeclarationParser.Parse("{attribute 'qualified_only'}\nPROGRAM Main\nVAR\nEND_VAR");

        Assert.Equal(PouKind.Program, d.Kind);
        Assert.Equal("Main", d.Name);
    }

    [Fact]
    public void Leading_comments_do_not_hide_the_keyword()
    {
        var d = StDeclarationParser.Parse(
            "// header comment\n(* block\n   comment *)\nFUNCTION_BLOCK FB\nVAR\nEND_VAR");

        Assert.Equal(PouKind.FunctionBlock, d.Kind);
        Assert.Equal("FB", d.Name);
    }

    [Fact]
    public void A_comment_mentioning_a_keyword_does_not_confuse_the_parse()
    {
        var d = StDeclarationParser.Parse("(* this FUNCTION_BLOCK is a program *)\nPROGRAM Real");

        Assert.Equal(PouKind.Program, d.Kind);
        Assert.Equal("Real", d.Name);
    }

    [Theory]
    [InlineData("program main")]
    [InlineData("Program Main")]
    [InlineData("PROGRAM Main")]
    public void Keywords_are_case_insensitive(string decl)
    {
        Assert.Equal(PouKind.Program, StDeclarationParser.Parse(decl).Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("VAR_GLOBAL\n x : INT;\nEND_VAR")]   // a GVL body, no POU keyword
    [InlineData("this is not structured text at all")]
    public void Anything_unrecognised_is_Unknown_and_cannot_be_created(string? decl)
    {
        var d = StDeclarationParser.Parse(decl);

        Assert.Equal(PouKind.Unknown, d.Kind);
        Assert.False(d.CanCreate);
    }

    [Fact]
    public void A_keyword_with_no_name_cannot_be_created()
    {
        // Better to report than to invent a name.
        Assert.False(StDeclarationParser.Parse("PROGRAM").CanCreate);
    }

    [Fact]
    public void The_real_fixture_declaration_parses()
    {
        // Taken verbatim from RefrigeratorControl.project's Simulation POU.
        const string decl = """
            PROGRAM Simulation
            VAR
            	T1: TON;					//The temperature is decreased on a time delay
            	P_Cooling: TIME:=T#500MS;
            	xReduceTemp: BOOL;
            END_VAR
            """;

        var d = StDeclarationParser.Parse(decl);

        Assert.Equal(PouKind.Program, d.Kind);
        Assert.Equal("Simulation", d.Name);
        Assert.True(d.CanCreate);
    }

    [Fact]
    public void CRLF_declarations_parse_identically()
    {
        var lf = StDeclarationParser.Parse("FUNCTION_BLOCK FB\nVAR\nEND_VAR");
        var crlf = StDeclarationParser.Parse("FUNCTION_BLOCK FB\r\nVAR\r\nEND_VAR");

        Assert.Equal(lf.Kind, crlf.Kind);
        Assert.Equal(lf.Name, crlf.Name);
    }
}
