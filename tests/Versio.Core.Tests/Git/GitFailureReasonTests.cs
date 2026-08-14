using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// Which of git's lines the user actually gets shown.
/// </summary>
/// <remarks>
/// This string goes straight into a notification, so picking the wrong line is the whole
/// failure as far as anybody reading it is concerned. Every fixture here is real git output.
/// </remarks>
public sealed class GitFailureReasonTests
{
    private static GitResult Failed(string stderr) =>
        new() { Success = false, ExitCode = 1, StdErr = stderr };

    [Fact]
    public void A_success_has_no_reason()
    {
        Assert.Null(new GitResult { Success = true, ExitCode = 0 }.FailureReason);
    }

    [Fact]
    public void Silence_falls_back_to_the_exit_code()
    {
        Assert.Contains("code 1", Failed("").FailureReason);
    }

    [Fact]
    public void Advice_after_the_error_is_skipped()
    {
        // The case that found this. Taking the last line outright showed somebody how to turn
        // the message off instead of telling them why the branch survived.
        var stderr =
            "warning: not deleting branch 'tidy' that is not yet merged to\n" +
            "         'refs/remotes/origin/tidy', even though it is merged to HEAD\n" +
            "error: the branch 'tidy' is not fully merged\n" +
            "hint: If you are sure you want to delete it, run 'git branch -D tidy'\n" +
            "hint: Disable this message with \"git config advice.forceDeleteBranch false\"";

        Assert.Equal("error: the branch 'tidy' is not fully merged", Failed(stderr).FailureReason);
    }

    [Fact]
    public void A_warning_before_the_error_is_not_preferred_to_it()
    {
        var stderr = "warning: something worth mentioning\nfatal: the actual problem";

        Assert.Equal("fatal: the actual problem", Failed(stderr).FailureReason);
    }

    [Fact]
    public void An_ordinary_single_line_is_returned_as_is()
    {
        Assert.Equal("fatal: Authentication failed",
                     Failed("fatal: Authentication failed").FailureReason);
    }

    [Fact]
    public void Nothing_but_advice_is_still_better_than_an_exit_code()
    {
        // Git said only hints. Its own words beat "exited with code 1", which explains nothing.
        Assert.Equal("hint: try something else", Failed("hint: try something else").FailureReason);
    }

    [Fact]
    public void Trailing_blank_lines_do_not_become_the_message()
    {
        Assert.Equal("fatal: the problem", Failed("fatal: the problem\n\n  \n").FailureReason);
    }
}
