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
}
