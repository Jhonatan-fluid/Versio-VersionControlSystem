using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Which command-line argument is the repository.
/// </summary>
/// <remarks>
/// <para>
/// This existed as "the first argument that does not start with <c>--</c>", which is right
/// until a flag takes a value. <c>Versio.exe --trace-bindings out.txt C:\repo</c> opened
/// <c>out.txt</c>, found it was not a directory, and started with no repository — logged, but
/// only as a line nobody was reading.
/// </para>
/// <para>
/// <b>The damage was to the diagnostics, not the user.</b> That flag exists to verify the UI,
/// and every "clean binding trace" taken with it was a clean trace over an <i>empty</i> window:
/// no repository, no project rows, no git panel content, so most bindings had never been
/// evaluated at all. A verification that quietly checks nothing is worse than none, because it
/// is reported as a pass.
/// </para>
/// </remarks>
public sealed class CommandLineTests
{
    [Fact]
    public void A_bare_path_is_the_repository()
    {
        Assert.Equal(@"C:\repo", App.RepositoryArgument([@"C:\repo"]));
    }

    [Fact]
    public void The_value_of_a_flag_is_not_the_repository()
    {
        // The bug, exactly as it was hit.
        Assert.Equal(@"C:\repo",
            App.RepositoryArgument(["--trace-bindings", @"C:\out.txt", @"C:\repo"]));
    }

    [Fact]
    public void A_path_before_a_flag_is_still_found()
    {
        Assert.Equal(@"C:\repo",
            App.RepositoryArgument([@"C:\repo", "--trace-bindings", @"C:\out.txt"]));
    }

    [Fact]
    public void A_flag_that_takes_no_value_does_not_swallow_the_path()
    {
        // Why the flags are listed rather than inferred: "skip whatever follows a flag" would
        // break here, and --spike is used exactly like this.
        Assert.Equal(@"C:\repo", App.RepositoryArgument(["--spike", @"C:\repo"]));
        Assert.Equal(@"C:\repo", App.RepositoryArgument(["--measure", @"C:\repo"]));
    }

    [Fact]
    public void With_no_path_there_is_none_to_report()
    {
        // Null rather than a guess: the caller falls back to the working directory, and a
        // wrong guess here is what the whole bug was.
        Assert.Null(App.RepositoryArgument([]));
        Assert.Null(App.RepositoryArgument(["--spike"]));
        Assert.Null(App.RepositoryArgument(["--trace-bindings", @"C:\out.txt"]));
    }

    [Fact]
    public void A_trailing_flag_with_its_value_missing_does_not_throw()
    {
        // A truncated command line is somebody mid-typing in a shell, not a crash.
        Assert.Null(App.RepositoryArgument(["--trace-bindings"]));
    }
}
