using Versio.App.ViewModels;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The diff pane opens the textual layer, including TIA's <c>.scl</c>, not SimaticML.
/// </summary>
/// <remarks>
/// <c>PrimaryFile</c> used to prefer only <c>.st</c>. A data block's review copy is
/// <c>.scl</c>, and falling through to the first file opened the XML whenever that
/// suffix was listed first. The chip had the same hole: <c>.scl</c> rendered as the
/// filename rather than as ST.
/// </remarks>
public sealed class ReadableLayerTests
{
    [Fact]
    public void A_data_block_opens_the_scl_even_when_the_xml_is_listed_first()
    {
        var node = new ProjectTreeNode
        {
            Name = "ReportValues_DB",
            Path = "plc/S/Program blocks/ReportValues_DB.xml",
            Kind = ProjectNodeKind.Object,
            Type = "DB",
            Files =
            [
                "plc/S/Program blocks/ReportValues_DB.xml",
                "plc/S/Program blocks/ReportValues_DB.scl",
            ],
        };

        Assert.EndsWith(".scl", new TreeRow(node, depth: 3).PrimaryFile);
    }

    [Fact]
    public void Scl_and_udt_are_the_textual_chip_not_the_filename()
    {
        Assert.Equal("ST", new DiffLayerChoice("plc/S/ReportValues_DB.scl", false, () => { }).Label);
        Assert.Equal("ST", new DiffLayerChoice("plc/S/AnaVal.udt", false, () => { }).Label);
        Assert.Equal("native", new DiffLayerChoice("plc/S/ReportValues_DB.xml", false, () => { }).Label);
    }
}
