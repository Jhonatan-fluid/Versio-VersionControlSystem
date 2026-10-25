using Versio.Plc.Codesys;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// Every case here is taken from a real measured difference between an export and its
/// re-export after <c>import_native</c> on CODESYS 3.5.22.20.
/// </summary>
public class NativeXmlNormalizerTests
{
    [Fact]
    public void A_bumped_UniqueIdGenerator_is_not_a_change()
    {
        // Measured: a visualisation came back with the counter advanced 26 -> 29.
        const string a = """<Single Name="UniqueIdGenerator" Type="string">26</Single>""";
        const string b = """<Single Name="UniqueIdGenerator" Type="string">29</Single>""";

        Assert.NotEqual(a, b);
        Assert.True(NativeXmlNormalizer.AreEquivalent(a, b));
    }

    [Fact]
    public void A_regenerated_reference_GUID_is_not_a_change()
    {
        // Measured: EventPOUGuid was re-minted by CODESYS on import.
        const string a = """<Single Name="EventPOUGuid" Type="System.Guid">ac6c9991-dfa0-4172-9770-3ebcbacdf556</Single>""";
        const string b = """<Single Name="EventPOUGuid" Type="System.Guid">288f98d1-b9d3-4ed7-99e7-12de96a16159</Single>""";

        Assert.True(NativeXmlNormalizer.AreEquivalent(a, b));
    }

    [Fact]
    public void GUID_masking_can_be_turned_off_for_export_to_export_comparison()
    {
        // Two exports of the SAME project have stable GUIDs, so a difference there is real
        // and must not be masked away.
        const string a = """<Single Name="X" Type="System.Guid">ac6c9991-dfa0-4172-9770-3ebcbacdf556</Single>""";
        const string b = """<Single Name="X" Type="System.Guid">288f98d1-b9d3-4ed7-99e7-12de96a16159</Single>""";

        Assert.False(NativeXmlNormalizer.AreEquivalent(a, b, maskGuids: false));
    }

    [Fact]
    public void Reordered_properties_inside_an_unordered_container_are_not_a_change()
    {
        // Measured: UseInitValue and DescriptionTextId came back swapped, same content.
        const string a = """
            <List>
            <Single Type="string">UseInitValue</Single>
            <Single Type="string">DescriptionTextId</Single>
            </List>
            """;
        const string b = """
            <List>
            <Single Type="string">DescriptionTextId</Single>
            <Single Type="string">UseInitValue</Single>
            </List>
            """;

        Assert.NotEqual(a, b);
        Assert.True(NativeXmlNormalizer.AreEquivalent(a, b));
    }

    [Fact]
    public void Values_swapped_between_two_multi_line_siblings_are_still_a_change()
    {
        // Sorting LINES inside a container threw away which line belonged to which child:
        // with every line of both children in one sorted bag, A holding x and B holding y
        // normalised to exactly the same text as A holding y and B holding x. The export
        // compares a fresh export against the file on disk and keeps the old file when the
        // two are "equivalent" — so this change never reached the mirror (the 2026-10-24
        // review's item 20). Siblings are sorted as whole elements now.
        const string a = """
            <List>
            <Single Name="A">
            <Id>1</Id>
            <Value>x</Value>
            </Single>
            <Single Name="B">
            <Id>2</Id>
            <Value>y</Value>
            </Single>
            </List>
            """;
        const string b = """
            <List>
            <Single Name="A">
            <Id>1</Id>
            <Value>y</Value>
            </Single>
            <Single Name="B">
            <Id>2</Id>
            <Value>x</Value>
            </Single>
            </List>
            """;

        Assert.False(NativeXmlNormalizer.AreEquivalent(a, b));
    }

    [Fact]
    public void Reordered_multi_line_siblings_are_not_a_change_and_their_insides_keep_their_order()
    {
        const string a = """
            <List>
            <Single Name="B">
            <Id>2</Id>
            <Value>y</Value>
            </Single>
            <Single Name="A">
            <Id>1</Id>
            <Value>x</Value>
            </Single>
            </List>
            """;
        const string b = """
            <List>
            <Single Name="A">
            <Id>1</Id>
            <Value>x</Value>
            </Single>
            <Single Name="B">
            <Id>2</Id>
            <Value>y</Value>
            </Single>
            </List>
            """;
        const string insideReordered = """
            <List>
            <Single Name="A">
            <Value>x</Value>
            <Id>1</Id>
            </Single>
            <Single Name="B">
            <Id>2</Id>
            <Value>y</Value>
            </Single>
            </List>
            """;

        Assert.True(NativeXmlNormalizer.AreEquivalent(a, b));
        // A child's own lines are ordered content (Id before Value is a real difference).
        Assert.False(NativeXmlNormalizer.AreEquivalent(a, insideReordered));
    }

    [Fact]
    public void Reordered_siblings_that_carry_guids_are_not_a_change_with_masking_on()
    {
        // The GUID mask used to be "<masked-guid>", and the sibling sort counts tags by
        // shape: it read the mask as an element that opens and never closes, every chunk
        // after the first GUID was swallowed into one, and this pair compared different
        // with masking on — the only setting an import's delete check uses — while
        // comparing equivalent with masking off (the second-round review's item 7). A
        // native object CODESYS had merely reordered was then refused as
        // DELETE_CONTENT_CHANGED.
        const string a = """
            <List>
            <Single Name="B">
            <Guid>6f9619ff-8b86-d011-b42d-00c04fc964ff</Guid>
            <Value>y</Value>
            </Single>
            <Single Name="A">
            <Guid>0f8fad5b-d9cb-469f-a165-70867728950e</Guid>
            <Value>x</Value>
            </Single>
            </List>
            """;
        const string b = """
            <List>
            <Single Name="A">
            <Guid>0f8fad5b-d9cb-469f-a165-70867728950e</Guid>
            <Value>x</Value>
            </Single>
            <Single Name="B">
            <Guid>6f9619ff-8b86-d011-b42d-00c04fc964ff</Guid>
            <Value>y</Value>
            </Single>
            </List>
            """;

        Assert.True(NativeXmlNormalizer.AreEquivalent(a, b, maskGuids: false));
        Assert.True(NativeXmlNormalizer.AreEquivalent(a, b, maskGuids: true));
        Assert.Equal(2, NativeXmlNormalizer.Normalize(a).GuidsMasked);
    }

    [Fact]
    public void A_container_nested_inside_a_sibling_is_sorted_too()
    {
        const string a = "<List>\n<Single Name=\"A\">\n<Properties>\n<b/>\n<a/>\n</Properties>\n</Single>\n</List>";
        const string b = "<List>\n<Single Name=\"A\">\n<Properties>\n<a/>\n<b/>\n</Properties>\n</Single>\n</List>";

        Assert.True(NativeXmlNormalizer.AreEquivalent(a, b));
    }

    [Fact]
    public void Content_OUTSIDE_an_unordered_container_keeps_its_order()
    {
        // Sorting everything would hide real reordering of things that are ordered,
        // such as statements or task priorities.
        const string a = "<A/>\n<B/>";
        const string b = "<B/>\n<A/>";

        Assert.False(NativeXmlNormalizer.AreEquivalent(a, b));
    }

    [Fact]
    public void A_genuinely_changed_value_is_still_detected()
    {
        // The whole point: normalisation must not become a blanket "everything matches".
        const string a = """<Single Name="Core2" Type="int">-1</Single>""";
        const string b = """<Single Name="Core2" Type="int">-2</Single>""";

        Assert.False(NativeXmlNormalizer.AreEquivalent(a, b));
    }

    [Fact]
    public void A_changed_variable_name_is_still_detected()
    {
        const string a = """<Single Type="string">nSpeed</Single>""";
        const string b = """<Single Type="string">nVelocity</Single>""";

        Assert.False(NativeXmlNormalizer.AreEquivalent(a, b));
    }

    [Fact]
    public void Added_content_is_still_detected()
    {
        const string a = "<List>\n<Single Type=\"string\">A</Single>\n</List>";
        const string b = "<List>\n<Single Type=\"string\">A</Single>\n<Single Type=\"string\">B</Single>\n</List>";

        Assert.False(NativeXmlNormalizer.AreEquivalent(a, b));
    }

    [Fact]
    public void Normalization_is_idempotent()
    {
        const string xml = """
            <Single Name="UniqueIdGenerator" Type="string">26</Single>
            <Single Name="G" Type="System.Guid">ac6c9991-dfa0-4172-9770-3ebcbacdf556</Single>
            """;

        var once = NativeXmlNormalizer.Normalize(xml).Text;
        var twice = NativeXmlNormalizer.Normalize(once).Text;

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Counts_report_what_was_masked()
    {
        const string xml = """
            <Single Name="UniqueIdGenerator" Type="string">26</Single>
            <Single Name="A" Type="System.Guid">ac6c9991-dfa0-4172-9770-3ebcbacdf556</Single>
            <Single Name="B" Type="System.Guid">288f98d1-b9d3-4ed7-99e7-12de96a16159</Single>
            """;

        var n = NativeXmlNormalizer.Normalize(xml);

        Assert.Equal(1, n.CountersMasked);
        Assert.Equal(2, n.GuidsMasked);
    }

    [Fact]
    public void Crlf_is_normalized()
    {
        Assert.True(NativeXmlNormalizer.AreEquivalent("<a/>\r\n<b/>", "<a/>\n<b/>"));
    }

    [Fact]
    public void Null_and_empty_are_tolerated()
    {
        Assert.True(NativeXmlNormalizer.AreEquivalent(null, ""));
        Assert.Equal(string.Empty, NativeXmlNormalizer.Normalize(null).Text);
    }

    [Fact]
    public void An_unterminated_container_does_not_hang_or_throw()
    {
        // Truncated or malformed input must degrade, not crash the sync.
        var n = NativeXmlNormalizer.Normalize("<List>\n<Single/>\n");

        Assert.Contains("List", n.Text);
    }
}
