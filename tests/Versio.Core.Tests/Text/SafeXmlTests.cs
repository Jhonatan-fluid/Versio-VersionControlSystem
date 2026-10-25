using System.Xml;
using System.Xml.Linq;
using Versio.Core.Text;
using Xunit;

namespace Versio.Core.Tests.Text;

/// <summary>
/// The XML every pack parses through: the same tree <c>XDocument.Parse</c> gave for a real
/// export, and a refusal, in the exception every caller already handles, for a hostile one.
/// </summary>
public sealed class SafeXmlTests
{
    private const string Export =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Document>\n  <Block Name=\"FB_Pump\">\n    <Member Name=\"Start\" Datatype=\"Bool\" />\n    <Text>  keep  </Text>\n  </Block>\n</Document>";

    [Fact]
    public void A_real_export_parses_to_the_tree_XDocument_Parse_gave()
    {
        Assert.Equal(XDocument.Parse(Export).ToString(), SafeXml.Parse(Export).ToString());
        Assert.Equal(
            XDocument.Parse(Export, LoadOptions.PreserveWhitespace).ToString(SaveOptions.DisableFormatting),
            SafeXml.Parse(Export, LoadOptions.PreserveWhitespace).ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public void Entity_expansion_is_refused_not_rationed()
    {
        // The "billion laughs": ten levels of ten, a few hundred bytes that expand without end.
        var entities = string.Concat(Enumerable.Range(1, 9).Select(i =>
            $"<!ENTITY l{i} \"{string.Concat(Enumerable.Repeat($"&l{i - 1};", 10))}\">"));
        var bomb = $"<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY l0 \"lol\">{entities}]><d>&l9;</d>";

        Assert.Throws<XmlException>(() => SafeXml.Parse(bomb));
    }

    [Fact]
    public void Even_a_small_expansion_is_refused_so_the_cap_is_not_what_stops_it()
    {
        // The bomb above would also be stopped by .NET's own ten-million-character entity limit,
        // so it cannot tell "refused" from "rationed". This one expands to a thousand characters,
        // well under that limit: it parses if DTDs are allowed, and must not here.
        const string small = "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]><d>&c;</d>";

        Assert.Throws<XmlException>(() => SafeXml.Parse(small));
    }

    [Fact]
    public void An_external_entity_is_refused()
    {
        const string xxe = "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><d>&x;</d>";

        Assert.Throws<XmlException>(() => SafeXml.Parse(xxe));
    }

    [Fact]
    public void A_document_over_the_cap_is_refused()
    {
        var big = "<d>" + new string('x', 5_000) + "</d>";

        Assert.Throws<XmlException>(() => SafeXml.Parse(big, maxCharacters: 1_000));
        Assert.NotNull(SafeXml.Parse(big, maxCharacters: 10_000).Root);
    }

    [Fact]
    public void A_document_nested_past_the_depth_cap_is_refused_before_a_tree_is_built()
    {
        // XDocument's tree building is quadratic in nesting depth: 30,000 levels was
        // measured at 70+ seconds of CPU while a bare XmlReader streams the same text in
        // 51 ms. The character cap cannot catch it — the cost is the shape, not the size —
        // so a streaming pre-pass refuses it in the exception every caller already
        // handles. The small cap here keeps the test instant; the wall-clock assertion
        // keeps the point: a refusal that built the tree first would take minutes.
        var deep = string.Concat(Enumerable.Repeat("<e>", 30_000))
                   + "x"
                   + string.Concat(Enumerable.Repeat("</e>", 30_000));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<XmlException>(() => SafeXml.Parse(deep));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5),
            $"the refusal took {watch.Elapsed.TotalSeconds:F1}s");

        Assert.Throws<XmlException>(() => SafeXml.Parse("<a><b><c /></b></a>", maxDepth: 1));
        Assert.NotNull(SafeXml.Parse("<a><b><c /></b></a>", maxDepth: 2).Root);
    }

    /// <summary>
    /// An element's lines run to the last line a descendant starts on or its text reaches: a
    /// CODESYS declaration is one text node of many lines, and a span ending on its first line
    /// left the rest outside the element (2026-10-23).
    /// </summary>
    [Fact]
    public void An_elements_lines_reach_the_end_of_a_descendants_multi_line_text()
    {
        var doc = SafeXml.Parse("<a>\n  <b>line one\nline two\nline three</b>\n  <c />\n</a>", LoadOptions.SetLineInfo);

        Assert.Equal(new LineSpan(2, 4), SafeXml.LinesOf(doc.Root!.Element("b")!));
        Assert.Equal(new LineSpan(1, 5), SafeXml.LinesOf(doc.Root));
        Assert.Equal(new LineSpan(12, 14), SafeXml.LinesOf(doc.Root.Element("b")!, offset: 10));
    }

    /// <summary>
    /// A newline the file spells as an entity is a line in the value and none in the file. The
    /// span counted the value's newlines, so an element holding <c>&amp;#10;</c> twice on one
    /// physical line claimed two more lines — the next element's — and the smallest-span rule
    /// that pins a comment to an element could hand the next element's line to this one (the
    /// 2026-10-24 review's item 18).
    /// </summary>
    [Theory]
    [InlineData("&#10;")]
    [InlineData("&#xA;")]
    [InlineData("&#13;&#10;")]
    public void A_newline_spelled_as_an_entity_is_not_a_line_of_the_file(string entity)
    {
        var doc = SafeXml.Parse($"<r>\n<a><m>x{entity}y{entity}z</m></a>\n<b/>\n</r>", LoadOptions.SetLineInfo);

        Assert.Equal(new LineSpan(2, 2), SafeXml.LinesOf(doc.Root!.Element("a")!.Element("m")!));
        Assert.Equal(new LineSpan(2, 2), SafeXml.LinesOf(doc.Root.Element("a")!));
        Assert.Equal(new LineSpan(1, 3), SafeXml.LinesOf(doc.Root));
    }

    [Fact]
    public void A_physical_newline_beside_an_encoded_one_is_still_counted()
    {
        var doc = SafeXml.Parse("<r>\n<m>a&#10;b\nc</m>\n<b/>\n</r>", LoadOptions.SetLineInfo);

        Assert.Equal(new LineSpan(2, 3), SafeXml.LinesOf(doc.Root!.Element("m")!));
    }

    [Fact]
    public void A_cdata_section_is_measured_to_its_own_end()
    {
        // The one kind of text that can hold a '<', so it cannot be measured to the next one.
        var doc = SafeXml.Parse("<r>\n<m><![CDATA[a < b\nc\n]]></m>\n<b/>\n</r>", LoadOptions.SetLineInfo);

        Assert.Equal(new LineSpan(2, 4), SafeXml.LinesOf(doc.Root!.Element("m")!));
    }

    [Fact]
    public void Windows_line_endings_count_once()
    {
        var doc = SafeXml.Parse("<r>\r\n<m>a\r\nb\r\nc</m>\r\n<b/>\r\n</r>", LoadOptions.SetLineInfo);

        Assert.Equal(new LineSpan(2, 4), SafeXml.LinesOf(doc.Root!.Element("m")!));
    }

    [Fact]
    public void A_document_parsed_elsewhere_falls_back_to_the_value()
    {
        // XDocument.Parse keeps no file to measure against; the value is all there is.
        var doc = XDocument.Parse("<r>\n<m>x&#10;y</m>\n</r>", LoadOptions.SetLineInfo);

        Assert.Equal(new LineSpan(2, 3), SafeXml.LinesOf(doc.Root!.Element("m")!));
        Assert.Null(SafeXml.HasEncodedLineBreaks((XText)doc.Root.Element("m")!.FirstNode!));
    }

    [Fact]
    public void A_text_node_says_whether_any_of_its_newlines_are_only_in_the_value()
    {
        var doc = SafeXml.Parse("<r>\n<a>x&#10;y</a>\n<b>x\ny</b>\n<c>x</c>\n</r>", LoadOptions.SetLineInfo);

        Assert.True(SafeXml.HasEncodedLineBreaks((XText)doc.Root!.Element("a")!.FirstNode!));
        Assert.False(SafeXml.HasEncodedLineBreaks((XText)doc.Root.Element("b")!.FirstNode!));
        Assert.False(SafeXml.HasEncodedLineBreaks((XText)doc.Root.Element("c")!.FirstNode!));
    }
}
