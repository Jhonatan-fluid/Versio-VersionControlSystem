using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

public sealed class ObjectSizeParserTests
{
    [Fact]
    public void An_empty_answer_is_an_empty_list()
    {
        Assert.Empty(ObjectSizeParser.Parse(null));
        Assert.Empty(ObjectSizeParser.Parse(""));
        Assert.Empty(ObjectSizeParser.Parse("\n"));
    }

    [Fact]
    public void Each_line_is_one_answer_in_the_order_the_names_were_sent()
    {
        // Three names in, three answers out, positionally: the middle one was not in the
        // index. A name with a space is echoed back whole on the missing line, and must not
        // be mistaken for a size.
        var sizes = ObjectSizeParser.Parse(
            "20971520 blob\n:0:Device/Plc Logic/Gone.st missing\n5 blob\n");

        Assert.Equal([20971520L, null, 5L], sizes);
    }

    [Fact]
    public void A_resolved_object_that_is_not_a_blob_has_no_file_size()
    {
        // A submodule's gitlink resolves to a commit when that commit happens to be in the
        // object store; its size is not the size of anything the commit records as a file.
        var sizes = ObjectSizeParser.Parse("241 commit\n7 blob\n");

        Assert.Equal([null, 7L], sizes);
    }

    [Fact]
    public void Carriage_returns_and_the_runners_trailing_newline_are_not_answers()
    {
        var sizes = ObjectSizeParser.Parse("12 blob\r\n4 blob\r\n\n");

        Assert.Equal([12L, 4L], sizes);
    }
}
