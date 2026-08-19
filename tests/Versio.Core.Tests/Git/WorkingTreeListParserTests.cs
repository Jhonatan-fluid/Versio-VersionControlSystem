using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

public sealed class WorkingTreeListParserTests
{
    [Fact]
    public void An_empty_listing_is_an_empty_list()
    {
        Assert.Empty(WorkingTreeListParser.Parse(null));
        Assert.Empty(WorkingTreeListParser.Parse(""));
        Assert.Empty(WorkingTreeListParser.Parse("\0"));
    }

    [Fact]
    public void Nul_separated_paths_round_trip_including_spaces_and_non_ascii()
    {
        var paths = WorkingTreeListParser.Parse(
            "src/app.py\0plc/Kühlschrank Regelung.st\0docs/README.md\0");

        Assert.Equal(
            ["src/app.py", "plc/Kühlschrank Regelung.st", "docs/README.md"],
            paths);
    }

    [Fact]
    public void Backslashes_become_forward_slashes_and_duplicates_collapse()
    {
        var paths = WorkingTreeListParser.Parse("src\\app.py\0src/app.py\0");

        Assert.Equal("src/app.py", Assert.Single(paths));
    }
}
