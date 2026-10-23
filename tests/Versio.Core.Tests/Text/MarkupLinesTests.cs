using Versio.Core.Text;
using Xunit;

namespace Versio.Core.Tests.Text;

/// <summary>
/// The framing rule for XML (docs/web/PLAN.md, C4): a line is markup-only when nothing but
/// spacing or a GUID is left once its tags are taken away. Attributes stay; text content does not.
/// </summary>
public sealed class MarkupLinesTests
{
    [Theory]
    [InlineData("<Sections>", true)]
    [InlineData("      </Member>", true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("<?xml version=\"1.0\" encoding=\"utf-8\"?>", true)]
    [InlineData("<Single Name=\"Implementation\" Type=\"{25e509de-33d4-4447-93f8-c9e4ea381c8b}\" Method=\"IArchivable\">", true)]
    [InlineData("<Section Name=\"Static\" />", true)]
    [InlineData("<Single Name=\"Guid\" Type=\"System.Guid\">b18a62c7-546d-4b2a-a116-1c9d0f44cbbb</Single>", true)]
    [InlineData("<G>{b18a62c7-546d-4b2a-a116-1c9d0f44cbbb}</G>", true)]
    [InlineData("<Single Name=\"Id\" Type=\"long\">7</Single>", false)]
    [InlineData("<Name>FB_Pump</Name>", false)]
    [InlineData("<Single Name=\"DefaultViewMode\" Type=\"string\">Ld</Single>", false)]
    [InlineData("<Single Name=\"LineInfoPersistence\" Type=\"string\">42da87a3-0bc0-481b-a792-8c220ef7c373_Decl_LineIds</Single>", false)]
    [InlineData("<Single Name=\"TextBlobForSerialisation\" Type=\"string\">{attribute 'qualified_only'}", false)]
    [InlineData("VAR_GLOBAL PERSISTENT RETAIN", false)]
    [InlineData("<Single Name=\"x\"", false)]
    public void A_line_is_markup_only_when_its_tags_leave_nothing_but_spacing_or_a_guid(string line, bool markup)
    {
        Assert.Equal(markup, MarkupLines.IsMarkupOnly(line));
    }

    private const string Xml = "<Block>\n  <Members>\n    <Member Name=\"a\" />\n    <Member Name=\"b\" />\n  </Members>\n  <Number>5</Number>\n\n</Block>\n";

    [Fact]
    public void Markup_lines_fold_into_spans_with_the_excluded_spans_left_out()
    {
        // Lines 3-4 are the rows'; 6 is a value; 7 is blank and 8 the closing tag; 9 is the empty tail.
        Assert.Equal([new LineSpan(1, 2), new LineSpan(5, 5), new LineSpan(7, 9)], MarkupLines.Structural(Xml, [new LineSpan(3, 4)]));
        // Nothing excluded: the rows' lines are markup too.
        Assert.Equal([new LineSpan(1, 5), new LineSpan(7, 9)], MarkupLines.Structural(Xml, []));
    }

    [Fact]
    public void Spans_are_moved_by_the_offset_and_the_excluded_spans_are_read_as_already_moved()
    {
        Assert.Equal([new LineSpan(11, 12), new LineSpan(15, 15), new LineSpan(17, 19)], MarkupLines.Structural(Xml, [new LineSpan(13, 14)], offset: 10));
    }

    [Fact]
    public void Only_lines_within_the_given_span_are_considered()
    {
        Assert.Equal([new LineSpan(2, 2), new LineSpan(5, 5)], MarkupLines.Structural(Xml, [new LineSpan(3, 4)], within: new LineSpan(2, 6)));
    }
}
