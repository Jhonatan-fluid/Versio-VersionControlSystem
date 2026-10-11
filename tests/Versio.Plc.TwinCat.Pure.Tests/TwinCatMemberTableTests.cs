using Versio.Plc.TwinCat;
using Versio.TestSupport;
using Xunit;

namespace Versio.Plc.TwinCat.Tests;

/// <summary>
/// A TwinCAT block's mirror read into the same interface table a TIA or CODESYS block gets, so
/// the three vendors' blocks read alike: variables on top, logic underneath.
/// </summary>
public sealed class TwinCatMemberTableTests
{
    private const string Relative =
        "LibraryDocumentationSample Project/LibPOUs/A_Samples/FB_DocuSample_FunctionBlock.st";

    [Fact]
    public void A_function_block_with_a_doc_comment_above_it_is_an_interface_table()
    {
        // The fixture's FB opens with a long (* … *) version history; the table starts at the
        // FUNCTION_BLOCK line under it.
        var path = Path.Combine(TestPaths.Project("TC3_PlcSample_reStrText"), "TC3_PlcSample_reStrText",
            Relative.Replace('/', Path.DirectorySeparatorChar));

        var table = TwinCatProjectFormat.Instance.TryParseMemberTable(Relative, File.ReadAllText(path));

        Assert.NotNull(table);
        Assert.Equal("FB_DocuSample_FunctionBlock", table.Name);
        Assert.True(table.IsInterface);
        var input = Assert.Single(table.Members, m => m.Name == "nVarInB");
        Assert.Equal(("Input", "INT", "5", "Second input variable"), (input.Section, input.Datatype, input.StartValue, input.Comment));
        Assert.Contains(table.Members, m => m.IsSectionHeading && m.Name == "Output");
    }

    [Fact]
    public void Anything_but_its_st_mirror_is_left_alone()
    {
        Assert.Null(TwinCatProjectFormat.Instance.TryParseMemberTable("x/POUs/MAIN.TcPOU", "<TcPlcObject />"));
    }
}
