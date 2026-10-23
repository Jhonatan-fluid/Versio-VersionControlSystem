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
    public void The_newline_the_runner_appends_after_the_last_nul_is_not_a_path()
    {
        // GitCliClient.RunAsync rebuilds stdout from line events and appends '\n' to each, so
        // ls-files -z arrives as "...\0README.md\0\n". The "\n" was kept as a path, and the
        // Explorer drew it as a file with no visible name at the root (owner, 2026-10-23).
        var paths = WorkingTreeListParser.Parse(".gitignore\0README.md\0\n");

        Assert.Equal([".gitignore", "README.md"], paths);
    }

    [Fact]
    public void Backslashes_become_forward_slashes_and_duplicates_collapse()
    {
        var paths = WorkingTreeListParser.Parse("src\\app.py\0src/app.py\0");

        Assert.Equal("src/app.py", Assert.Single(paths));
    }
}
