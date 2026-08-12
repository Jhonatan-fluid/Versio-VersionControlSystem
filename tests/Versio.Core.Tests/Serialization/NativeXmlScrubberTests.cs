using Versio.Core.Serialization;
using Xunit;

namespace Versio.Core.Tests.Serialization;

/// <summary>
/// The determinism guarantee. Verified against real CODESYS 3.5.22.20 output: two
/// consecutive exports of RefrigeratorControl.project were byte-identical across all
/// 10 files (2.9 MB) with 9 Timestamp fields zeroed.
/// </summary>
public class NativeXmlScrubberTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <ExportFile>
          <Object>
            <Single Name="Timestamp" Type="long">133984021473906250</Single>
            <Single Name="Guid" Type="guid">d9b2b2cc-ea99-4c3b-aa42-1e5c49e65b84</Single>
            <Single Name="Id" Type="long">42</Single>
          </Object>
        </ExportFile>
        """;

    [Fact]
    public void Timestamp_is_zeroed()
    {
        var result = NativeXmlScrubber.Scrub(Sample);

        Assert.Equal(1, result.Replacements);
        Assert.Contains("""<Single Name="Timestamp" Type="long">0</Single>""", result.Text);
        Assert.DoesNotContain("133984021473906250", result.Text);
    }

    [Fact]
    public void GUIDs_are_NOT_touched()
    {
        // codescribe tried canonicalising GUIDs and it broke visualisations. They are
        // identity - what makes a moved object a move rather than a delete plus an add.
        var result = NativeXmlScrubber.Scrub(Sample);

        Assert.Contains("d9b2b2cc-ea99-4c3b-aa42-1e5c49e65b84", result.Text);
    }

    [Fact]
    public void Ids_are_NOT_touched()
    {
        var result = NativeXmlScrubber.Scrub(Sample);

        Assert.Contains("""<Single Name="Id" Type="long">42</Single>""", result.Text);
    }

    [Fact]
    public void Scrubbing_is_idempotent()
    {
        // The hash gate re-scrubs content it previously wrote; a non-idempotent scrub
        // would make every export look changed.
        var once = NativeXmlScrubber.Scrub(Sample).Text;
        var twice = NativeXmlScrubber.Scrub(once).Text;

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Two_exports_differing_only_in_timestamp_scrub_to_identical_text()
    {
        // This is the determinism gate, in miniature.
        var a = Sample;
        var b = Sample.Replace("133984021473906250", "133984099999999999");

        Assert.NotEqual(a, b);
        Assert.Equal(NativeXmlScrubber.Scrub(a).Text, NativeXmlScrubber.Scrub(b).Text);
    }

    [Fact]
    public void Multiple_timestamps_are_all_scrubbed()
    {
        var xml = string.Concat(Enumerable.Repeat(
            """<Single Name="Timestamp" Type="long">123</Single>""" + "\n", 9));

        var result = NativeXmlScrubber.Scrub(xml);

        Assert.Equal(9, result.Replacements);
        Assert.DoesNotContain("123", result.Text);
    }

    [Fact]
    public void Crlf_is_normalized()
    {
        var result = NativeXmlScrubber.Scrub("<a>\r\n<b/>\r\n</a>");

        Assert.DoesNotContain("\r", result.Text);
    }

    [Fact]
    public void Output_always_ends_with_exactly_one_newline()
    {
        Assert.EndsWith("\n", NativeXmlScrubber.Scrub("<a/>").Text);
        Assert.EndsWith("</a>\n", NativeXmlScrubber.Scrub("<a></a>\n").Text);
        Assert.DoesNotContain("\n\n", NativeXmlScrubber.Scrub("<a/>").Text);
    }

    [Fact]
    public void Null_and_empty_are_tolerated()
    {
        Assert.Equal(string.Empty, NativeXmlScrubber.Scrub(null).Text);
        Assert.Equal(string.Empty, NativeXmlScrubber.Scrub("").Text);
    }

    [Fact]
    public void IsScrubbed_detects_unscrubbed_content()
    {
        Assert.False(NativeXmlScrubber.IsScrubbed(Sample));
        Assert.True(NativeXmlScrubber.IsScrubbed(NativeXmlScrubber.Scrub(Sample).Text));
    }

    [Fact]
    public void Content_without_timestamps_is_only_normalized()
    {
        var result = NativeXmlScrubber.Scrub("<a><b/></a>");

        Assert.Equal(0, result.Replacements);
        Assert.Equal("<a><b/></a>\n", result.Text);
    }

    [Fact]
    public void A_similarly_named_field_is_not_scrubbed_by_accident()
    {
        // Guard against the regex being too greedy: only the exact Timestamp/long field.
        const string xml = """
            <Single Name="TimestampCreated" Type="long">999</Single>
            <Single Name="Timestamp" Type="string">keep-me</Single>
            """;

        var result = NativeXmlScrubber.Scrub(xml);

        Assert.Equal(0, result.Replacements);
        Assert.Contains("999", result.Text);
        Assert.Contains("keep-me", result.Text);
    }
}
