using Versio.Core.Diff;
using Versio.Core.Iec;
using Versio.Plc.Codesys.FileBased;
using Versio.TestSupport;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// A File-Based Storage text file split into header, declaration and body, from the fixture's
/// real files, and laid out the way the Rendered view lays it out.
/// </summary>
/// <remarks>
/// FBS 1.0 writes no divider between the declaration and the body, so the split comes from IEC
/// syntax. Each test here is one of the four shapes the fixture has; see
/// <c>docs/fbs/PLAN.md</c> §2.
/// </remarks>
public sealed class CodesysFileBasedSourceTests
{
    private const string Application =
        "Device.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^/";

    private const string Alarm = Application + "Library/Alarm/";

    private static string Read(string relative) => File.ReadAllText(Path.Combine(
        TestPaths.Project("RefrigeratorControl_FBS"), "RefrigeratorControl.fbsproj",
        relative.Replace('/', Path.DirectorySeparatorChar)));

    private static (SourceSections Sections, IReadOnlyList<SourceRow> Rows) Laid(string relative)
    {
        var text = Read(relative);
        var sections = CodesysProjectFormat.Instance.SourceSections(relative, text);
        Assert.NotNull(sections);
        return (sections, StSourceLayout.Lay(text, sections));
    }

    private static IReadOnlyList<string> Section(IReadOnlyList<SourceRow> rows, string label)
    {
        var start = rows.ToList().FindIndex(r => r is SourceDivider d && d.Label == label);
        Assert.True(start >= 0, $"no {label} divider");
        return rows.Skip(start + 1).TakeWhile(r => r is SourceCodeLine).Cast<SourceCodeLine>().Select(l => l.Text).ToList();
    }

    [Fact]
    public void A_program_is_declared_to_its_last_end_var_and_its_body_runs_to_the_closing_keyword()
    {
        var (sections, rows) = Laid(Application + "PLC_PRG.prg.st");

        var declaration = Section(rows, StSourceLayout.DeclarationLabel);
        Assert.Equal("PROGRAM PLC_PRG", declaration[0]);
        Assert.Equal("END_VAR", declaration[^1]);

        var body = Section(rows, StSourceLayout.ImplementationLabel);
        Assert.Equal("Alarm.P_Settings.Latch := Latch;", body[0]);
        Assert.Equal("Alarm();", body[^1]);

        // The header is the vendor's, and END_PROGRAM is not a line the IDE shows.
        Assert.DoesNotContain(rows.OfType<SourceCodeLine>(), l => l.Text.Contains("METADATA", StringComparison.Ordinal) || l.Text == "END_PROGRAM");
        Assert.Equal(7, sections.HeaderEnd);

        // Numbered from 1 in each pane, as the IDE numbers them, with the file line kept.
        var first = rows.OfType<SourceCodeLine>().First(l => l.Text == "Alarm.P_Settings.Latch := Latch;");
        Assert.Equal(1, first.Number);
        Assert.Equal(17, first.FileLine);
    }

    [Fact]
    public void A_method_with_no_var_block_is_declared_by_its_header_line_alone()
    {
        var (_, rows) = Laid(Alarm + "POUs/FB_Alarm.fb.st^/M_Reset.meth.st");

        Assert.Equal(["METHOD PUBLIC M_Reset : BOOL"], Section(rows, StSourceLayout.DeclarationLabel));
        Assert.Equal("// If Alarm is set to latching, reset state, condition and timer", Section(rows, StSourceLayout.ImplementationLabel)[0]);
    }

    [Fact]
    public void A_closing_keyword_glued_to_code_keeps_its_line_whole()
    {
        // P_Active := State = E_AlarmState.Active;END_PROPERTY. Cutting END_PROPERTY off would
        // be the layout editing code; leaving the line whole costs one keyword on screen.
        var (_, rows) = Laid(Alarm + "POUs/FB_Alarm.fb.st^/P_Active.prop.st");

        Assert.Equal(["{attribute 'monitoring' := 'call'}", "PROPERTY_Get P_Active : BOOL"], Section(rows, StSourceLayout.DeclarationLabel));
        Assert.Equal(["P_Active := State = E_AlarmState.Active;END_PROPERTY"], Section(rows, StSourceLayout.ImplementationLabel));
    }

    [Fact]
    public void Only_the_objects_own_closing_keyword_is_hidden()
    {
        // A body that ends in END_IF, its closing keyword glued to a line above: END_IF is code.
        const string Text = "(* METADATA\n{}\n*)\nMETHOD M : BOOL\nIF x THEN y := 1;END_METHOD\nEND_IF\n";

        var sections = CodesysProjectFormat.Instance.SourceSections("x/M.meth.st", Text);

        Assert.NotNull(sections);
        Assert.Equal(["IF x THEN y := 1;END_METHOD", "END_IF"],
            Section(StSourceLayout.Lay(Text, sections), StSourceLayout.ImplementationLabel));
    }

    [Theory]
    [InlineData(Alarm + "DUTs/ST_AlarmSettings.struct", "TYPE ST_AlarmSettings :", "END_TYPE")]
    [InlineData(Alarm + "DUTs/E_AlarmCategory.enum", "{attribute 'qualified_only'}", "END_TYPE")]
    [InlineData(Application + "Glob_Var.gvl", "{attribute 'qualified_only'}", "END_NAMESPACE")]
    public void A_type_or_a_global_list_is_its_declaration_with_no_divider(string relative, string first, string last)
    {
        var (sections, rows) = Laid(relative);

        Assert.Null(sections.BodyEnd);
        Assert.DoesNotContain(rows, r => r is SourceDivider);
        var lines = rows.Cast<SourceCodeLine>().ToList();
        Assert.Equal(first, lines[0].Text);
        Assert.Equal(last, lines[^1].Text);
        Assert.Equal(1, lines[0].Number);
    }

    [Fact]
    public void The_code_body_is_what_the_implementation_pane_shows()
    {
        var relative = Application + "PLC_PRG.prg.st";
        var text = Read(relative);

        var body = CodesysProjectFormat.Instance.CodeBody(relative, text);

        Assert.NotNull(body);
        Assert.Equal(StSourceLayout.Body(text, body.Value.Start, body.Value.End).Cast<SourceCodeLine>().Select(l => l.Text),
            Section(StSourceLayout.Lay(text, CodesysProjectFormat.Instance.SourceSections(relative, text)), StSourceLayout.ImplementationLabel));
    }

    [Theory]
    [InlineData("Application/PLC_PRG.st", "PROGRAM PLC_PRG\n// --- BEGIN IMPLEMENTATION ---\nx := 1;\n")]
    [InlineData("x/PLC_PRG.prg.st", "PROGRAM PLC_PRG\nEND_PROGRAM\n")]
    [InlineData("x/Signals.pou.xml.v3", "__V3_META__\n{}\n__V3_CONTENT__\n<Single />")]
    [InlineData("x/PLC_PRG.prg.st", "(* METADATA\n{}\n")]
    public void Anything_without_the_header_is_left_to_the_ordinary_layout(string relative, string text)
    {
        // The first is a classic mirror's .st: Versio's own format, with Versio's own delimiter.
        // The last has a header that never closes, which is a torn file rather than a split.
        Assert.Null(CodesysProjectFormat.Instance.SourceSections(relative, text));
    }

    [Theory]
    [InlineData("x/Glob_Var.gvl", "Structured Text")]
    [InlineData("x/E_State.enum", "Structured Text")]
    [InlineData("x/ST_Settings.struct", "Structured Text")]
    [InlineData("x/Signals.pou.xml.v3", "XML")]
    public void Its_extensions_are_highlighted_as_what_they_hold(string relative, string language)
    {
        Assert.Equal(language, CodesysProjectFormat.Instance.SyntaxFor(relative)?.Name);
    }

    [Fact]
    public void An_extension_it_does_not_own_is_left_to_the_catalog()
    {
        Assert.Null(CodesysProjectFormat.Instance.SyntaxFor("x/PLC_PRG.st"));
        Assert.Null(CodesysProjectFormat.Instance.SyntaxFor("README.md"));
    }

    /// <summary>The file the gate refused, reduced: FsLibrary.fbslib's FB_Template.fb.st.</summary>
    private const string HeaderlessBlock =
        "/// Documentation on the Block\n"
        + "{attribute 'symbol' := 'none'}\n"
        + "FUNCTION_BLOCK FB_Template IMPLEMENTS I_Template\n"
        + "VAR_INPUT\n"
        + "\tHomeFeedback : BOOL;\n"
        + "END_VAR\n"
        + "VAR\n"
        + "\tState : E_TemplateState := E_TemplateState.Off;\n"
        + "END_VAR\n"
        + "State := NextState;\n"
        + "END_FUNCTION_BLOCK\n";

    [Fact]
    public void A_library_file_with_no_metadata_header_still_splits_by_where_it_lives()
    {
        // Seen on a real library on 2026-10-14: FsLibrary.fbslib holds .fb.st files that
        // open with a /// doc comment and no (* METADATA *) header at all. The gate was
        // the header alone — every fixture file has one — so these fell through to the
        // classic mirror's splitter, which needs Versio's own delimiter, and the
        // Rendered pane drew the whole file under a member table that had already tabled
        // its declaration. The .fbslib folder on the path is CODESYS's own statement of
        // the format.
        var path = "FsLibrary.fbslib/00_Template/FB_Template.fb.st";
        var sections = CodesysProjectFormat.Instance.SourceSections(path, HeaderlessBlock);

        Assert.NotNull(sections);
        Assert.Equal(0, sections.HeaderEnd);

        var body = Section(StSourceLayout.Lay(HeaderlessBlock, sections), StSourceLayout.ImplementationLabel);
        Assert.Equal(["State := NextState;"], body);

        Assert.Equal((9, 10), CodesysProjectFormat.Instance.CodeBody(path, HeaderlessBlock));
    }

    [Fact]
    public void A_headerless_file_outside_an_fbs_folder_is_still_not_claimed()
    {
        // The other half of the gate: a classic mirror's .st has Versio's own delimiter
        // and must keep the layout it has. Nothing about its content changed, so nothing
        // about its answer may.
        Assert.Null(CodesysProjectFormat.Instance.SourceSections(
            "Device/Plc Logic/Application/FB_Template.st", HeaderlessBlock));
    }
}
