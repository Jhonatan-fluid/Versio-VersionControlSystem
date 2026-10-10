using Versio.Core.Iec;
using Xunit;

namespace Versio.Core.Tests.Iec;

/// <summary>
/// The Rendered pane's numbering must be the IDE's, or a compile error names a line the
/// reader cannot find. The IDE's numbering is whatever <see cref="StTextCodec.Split"/> hands
/// it, so that is what every test here measures against.
/// </summary>
public class StSourceLayoutTests
{
    private const string Declaration =
        "FUNCTION_BLOCK FB_Alarm\nVAR_INPUT\n\tCondition : BOOL;\nEND_VAR";

    private const string Implementation =
        "// Evaluate Inputs\n\t_M_EvaluateInputs();\n\n// Reset Latch\nIF NOT Latch THEN\n\tCondition := FALSE;\nEND_IF";

    [Fact]
    public void Implementation_is_numbered_from_one_behind_a_divider()
    {
        var rows = StSourceLayout.Lay(StTextCodec.Join(Declaration, Implementation));

        var divider = Assert.Single(rows.OfType<SourceDivider>(),
            d => d.Label == StSourceLayout.ImplementationLabel);
        var after = rows.Skip(rows.ToList().IndexOf(divider) + 1).Cast<SourceCodeLine>().ToList();

        Assert.Equal(1, after[0].Number);
        Assert.Equal("// Evaluate Inputs", after[0].Text);
        Assert.Equal(StTextCodec.ImplementationDelimiter, divider.FileText);
    }

    [Fact]
    public void Every_number_names_the_line_the_ide_receives()
    {
        // The invariant the whole feature exists for, stated against the codec itself: line
        // N of a section in the pane is line N of what Split hands the IDE.
        var file = StTextCodec.Join(Declaration, Implementation);
        var (decl, impl) = StTextCodec.Split(file);
        var rows = StSourceLayout.Lay(file);

        var implementationAt = rows.ToList().FindIndex(
            r => r is SourceDivider { Label: StSourceLayout.ImplementationLabel });

        AssertSection(decl, rows.Take(implementationAt));
        AssertSection(impl, rows.Skip(implementationAt + 1));

        static void AssertSection(string ide, IEnumerable<SourceRow> section)
        {
            var ideLines = ide.Split('\n');
            var shown = section.OfType<SourceCodeLine>().ToList();

            Assert.Equal(ideLines.Length, shown.Count);
            foreach (var line in shown)
            {
                // TrimStart: Split trims the section, so the IDE's first line has lost the
                // indentation the file's still carries.
                Assert.Equal(ideLines[line.Number - 1].TrimStart(), line.Text.TrimStart());
            }
        }
    }

    [Fact]
    public void The_codecs_framing_blank_lines_are_not_drawn()
    {
        // Join writes one blank line either side of the delimiter. Split trims them away,
        // so they are not lines of the PLC's source and the divider replaces them.
        var rows = StSourceLayout.Lay(StTextCodec.Join(Declaration, Implementation));
        var lines = rows.OfType<SourceCodeLine>().ToList();

        Assert.Equal(4 + 7, lines.Count);
        Assert.Equal("END_VAR", lines[3].Text);
    }

    [Fact]
    public void Extra_blank_lines_a_hand_edit_left_at_a_section_edge_are_dropped_too()
    {
        var rows = StSourceLayout.Lay(
            "\n\nPROGRAM Main\n\n\n" + StTextCodec.ImplementationDelimiter + "\n \t\n\nx := 1;\n\n");

        Assert.Equal(
            [
                new SourceDivider(StSourceLayout.DeclarationLabel, null),
                new SourceCodeLine(1, "PROGRAM Main", 3),
                new SourceDivider(StSourceLayout.ImplementationLabel, StTextCodec.ImplementationDelimiter),
                new SourceCodeLine(1, "x := 1;", 9),
            ],
            rows);
    }

    [Fact]
    public void Blank_lines_inside_a_section_keep_their_numbers()
    {
        var rows = StSourceLayout.Lay(StTextCodec.Join("PROGRAM Main", "a := 1;\n\nb := 2;"));

        // Join: declaration, blank, delimiter, blank, then a := 1 on file line 5.
        Assert.Contains(new SourceCodeLine(2, string.Empty, 6), rows);
        Assert.Contains(new SourceCodeLine(3, "b := 2;", 7), rows);
    }

    [Fact]
    public void No_two_rows_are_equal_even_where_sections_share_a_blank_line_number()
    {
        // Line 2 is blank in both halves. As bare (Number, Text) records they were equal, and
        // WPF selects every equal item when one is clicked.
        var rows = StSourceLayout.Lay(StTextCodec.Join("PROGRAM Main\n\nVAR END_VAR", "a;\n\nb;"));

        Assert.Equal(2, rows.OfType<SourceCodeLine>().Count(l => l is { Number: 2, Text: "" }));
        Assert.Equal(rows.Count, rows.Distinct().Count());
    }

    [Fact]
    public void A_declaration_only_file_is_numbered_as_the_file_with_no_divider()
    {
        var rows = StSourceLayout.Lay(StTextCodec.DeclarationOnly("TYPE E : (A, B);\nEND_TYPE"));

        Assert.Equal(
            [new SourceCodeLine(1, "TYPE E : (A, B);", 1), new SourceCodeLine(2, "END_TYPE", 2)],
            rows);
    }

    [Fact]
    public void A_trailing_newline_is_not_a_line_of_its_own()
    {
        // The old pseudo-diff numbered the empty tail after the last newline as line N+1.
        Assert.Equal(2, StSourceLayout.Lay("a\nb\n").Count);
        Assert.Equal(2, StSourceLayout.Lay("a\r\nb\r\n").Count);
    }

    [Fact]
    public void A_delimiter_sharing_its_line_with_code_falls_back_to_file_numbering()
    {
        // Split cuts this file mid-line. A divider drawn between two rows would claim a cut
        // that is not where the codec makes it, so the file is shown as the file.
        var text = "PROGRAM Main\nx := 1; " + StTextCodec.ImplementationDelimiter + "\ny := 2;\n";
        var rows = StSourceLayout.Lay(text);

        Assert.DoesNotContain(rows, r => r is SourceDivider);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void Only_the_first_delimiter_is_the_separator()
    {
        // Split's rule: the first occurrence cuts. A mid-line first one means no divider at
        // all, even though a whole-line one follows — drawing that one would be a cut the
        // codec never makes.
        var text = "a // x " + StTextCodec.ImplementationDelimiter + "\n"
                   + StTextCodec.ImplementationDelimiter + "\nb\n";

        Assert.DoesNotContain(StSourceLayout.Lay(text), r => r is SourceDivider);
    }

    [Fact]
    public void Nothing_lays_out_as_one_empty_line()
    {
        // An empty file still draws a row, as the pseudo-diff did, rather than an empty pane
        // that reads as a load failure.
        Assert.Equal([new SourceCodeLine(1, string.Empty, 1)], StSourceLayout.Lay(string.Empty));
        Assert.Equal([new SourceCodeLine(1, string.Empty, 1)], StSourceLayout.Lay(null));
    }

    [Fact]
    public void A_body_drops_its_shared_indent_and_blank_edges_and_numbers_from_one()
    {
        // The owner's PT1 (2026-10-05): every body line tab-indented, nesting under that,
        // a blank line before END_FUNCTION_BLOCK. Lines 2..6 of this file are the body.
        var file = "BEGIN\n\n\tIF #On THEN\n\t    #Out := 1;\n\tEND_IF;\n\n\t#Done := TRUE;\nEND_FUNCTION_BLOCK";

        var rows = StSourceLayout.Body(file, 1, 7);

        Assert.Equal(
        [
            new SourceCodeLine(1, "IF #On THEN", 3),
            new SourceCodeLine(2, "    #Out := 1;", 4),
            new SourceCodeLine(3, "END_IF;", 5),
            new SourceCodeLine(4, string.Empty, 6),
            new SourceCodeLine(5, "#Done := TRUE;", 7),
        ], rows);
    }

    [Fact]
    public void A_tab_and_four_spaces_are_not_the_same_indent()
    {
        // Character for character: a body indented with a tab on one line and spaces on
        // another shares no indent, so nothing is cut and nothing is misaligned.
        var rows = StSourceLayout.Body("\tA;\n    B;", 0, 2);

        Assert.Equal(["\tA;", "    B;"], rows.Cast<SourceCodeLine>().Select(r => r.Text));
    }

    [Fact]
    public void An_out_of_range_body_is_clamped_not_thrown()
    {
        Assert.Empty(StSourceLayout.Body("A;", 5, 9));
        Assert.Equal(["A;"], StSourceLayout.Body("A;", -3, 99).Cast<SourceCodeLine>().Select(r => r.Text));
    }

    // A vendor's own file, sections found by its format: a two-line header, a two-line
    // declaration, a two-line body, a closing keyword.
    private const string Sectioned =
        "(* header\n*)\nPROGRAM P\nVAR x : BOOL; END_VAR\nx := TRUE;\n\nEND_PROGRAM";

    [Fact]
    public void Sections_draw_like_the_delimiter_with_the_header_and_the_closing_keyword_left_out()
    {
        var rows = StSourceLayout.Lay(Sectioned, new SourceSections(HeaderEnd: 2, DeclarationEnd: 4, BodyEnd: 6));

        Assert.Equal(
            ["|Declaration", "1 PROGRAM P", "2 VAR x : BOOL; END_VAR", "|Implementation", "1 x := TRUE;"],
            rows.Select(r => r switch
            {
                SourceDivider d => "|" + d.Label,
                SourceCodeLine l => $"{l.Number} {l.Text}",
                _ => "?",
            }));

        // The file line survives the renumbering, so a click still lands on the file.
        Assert.Equal(5, rows.OfType<SourceCodeLine>().Single(l => l.Text == "x := TRUE;").FileLine);
    }

    [Fact]
    public void Sections_with_no_body_are_the_declaration_alone_with_no_divider()
    {
        var rows = StSourceLayout.Lay("(*\n*)\nTYPE T : INT; END_TYPE", new SourceSections(2, 3, BodyEnd: null));

        var line = Assert.IsType<SourceCodeLine>(Assert.Single(rows));
        Assert.Equal((1, "TYPE T : INT; END_TYPE", 3), (line.Number, line.Text, line.FileLine));
    }

    [Fact]
    public void No_sections_is_the_layout_there_always_was()
    {
        var file = StTextCodec.Join(Declaration, Implementation);

        Assert.Equal(StSourceLayout.Lay(file), StSourceLayout.Lay(file, sections: null));
    }

    [Fact]
    public void Sections_that_overrun_the_file_are_clamped_rather_than_thrown()
    {
        // A format reading a file that changed under it must not take the pane down.
        var rows = StSourceLayout.Lay("A;\nB;", new SourceSections(HeaderEnd: 9, DeclarationEnd: 1, BodyEnd: 99));

        Assert.Equal(["Declaration", "Implementation"], rows.OfType<SourceDivider>().Select(d => d.Label));
        Assert.Empty(rows.OfType<SourceCodeLine>());
    }
}
