using Versio.Plc.Codesys.FileBased;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>The File-Based Storage <c>.xml.v3</c> envelope and escaped names, as the fixture writes them.</summary>
public sealed class CodesysV3FileTests
{
    [Fact]
    public void The_content_is_everything_after_the_header()
    {
        const string File = "__V3_META__\n{\n\t\"objectGuid\": \"x\"\n}\n__V3_CONTENT__\n<?xml version=\"1.0\"?>\n<Single />";

        Assert.Equal("<?xml version=\"1.0\"?>\n<Single />", CodesysV3File.Content(File));
        Assert.Equal("<?xml version=\"1.0\"?>\n<Single />", CodesysV3File.Content("﻿" + File));
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><Single />")]
    [InlineData("__V3_META__\n{}\n")]
    [InlineData("<!-- __V3_META__ -->\n__V3_CONTENT__\n<Single />")]
    public void A_file_that_does_not_open_with_the_header_has_no_content(string text)
    {
        // The last one mentions both markers but does not start with the header: reading it from
        // the middle would hand a parser half a document.
        Assert.Null(CodesysV3File.Content(text));
    }

    [Theory]
    [InlineData("a/b^/Signals.pou.xml.v3", "Signals")]
    [InlineData("Plc Logic.plclogic.xml.v3", "Plc Logic")]
    [InlineData(@"x\__3cEmpty__3e 0.slotdevice.xml.v3", "<Empty> 0")]
    [InlineData("FB_Alarm.fb.st^/P_Active.prop.st", "P_Active")]
    [InlineData("Libraries.json", "Libraries")]
    public void An_object_is_named_by_its_file_up_to_the_first_dot_with_escapes_decoded(string path, string name)
    {
        Assert.Equal(name, CodesysV3File.ObjectName(path));
    }

    [Fact]
    public void Only_single_byte_ascii_escapes_are_decoded()
    {
        // __c3 is half of a UTF-8 letter; decoding it alone would show a reviewer a character the
        // IDE does not. It stays as written until a specimen shows how CODESYS spells one.
        Assert.Equal("a<b", CodesysV3File.Decode("a__3cb"));
        Assert.Equal("a__c3b", CodesysV3File.Decode("a__c3b"));
        Assert.Equal("a__3Cb", CodesysV3File.Decode("a__3Cb"));
    }
}
