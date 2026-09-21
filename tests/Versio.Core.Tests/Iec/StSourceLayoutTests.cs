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
}
