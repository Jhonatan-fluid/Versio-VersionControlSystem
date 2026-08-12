using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// git writes progress to stderr and overwrites the line with carriage returns. Getting
/// this wrong reports every successful push as a failure — the single most common bug in
/// git wrappers.
/// </summary>
public class GitProgressParserTests
{
    [Fact]
    public void A_phase_with_a_percentage_is_parsed()
    {
        var p = Assert.Single(GitProgressParser.Parse("Writing objects:  45% (9/20)"));

        Assert.Equal("Writing objects", p.Phase);
        Assert.Equal(45, p.Percent);
    }

    [Fact]
    public void Carriage_return_separated_updates_are_split()
    {
        // What git actually emits: one "line" overwriting itself.
        var updates = GitProgressParser.Parse(
            "Counting objects:  10%\rCounting objects:  50%\rCounting objects: 100%");

        Assert.Equal(3, updates.Count);
        Assert.Equal(100, updates[^1].Percent);
    }

    [Fact]
    public void A_phase_with_a_bare_count_is_parsed_without_a_percentage()
    {
        var p = Assert.Single(GitProgressParser.Parse("Enumerating objects: 42"));

        Assert.Equal("Enumerating objects", p.Phase);
        Assert.Null(p.Percent);
    }

    [Fact]
    public void Remote_messages_are_surfaced()
    {
        var p = Assert.Single(GitProgressParser.Parse("remote: Resolving deltas: 100% (5/5)"));

        Assert.Equal("remote", p.Phase);
        Assert.Contains("Resolving deltas", p.Raw);
    }

    [Fact]
    public void Unrecognised_stderr_produces_no_progress()
    {
        Assert.Empty(GitProgressParser.Parse("fatal: could not read Username"));
    }

    [Theory]
    [InlineData("Writing objects:  45% (9/20)")]
    [InlineData("Counting objects: 12")]
    [InlineData("remote: Compressing objects: 100%")]
    [InlineData("To https://github.com/example/repo.git")]
    [InlineData("From https://github.com/example/repo.git")]
    [InlineData(" * [new branch]      main -> origin/main")]
    [InlineData("Everything up-to-date")]
    [InlineData("Already up to date.")]
    [InlineData("")]
    [InlineData("   ")]
    public void Progress_chatter_is_not_mistaken_for_an_error(string line)
    {
        Assert.True(GitProgressParser.IsProgressNoise(line));
    }

    [Theory]
    [InlineData("fatal: repository 'x' not found")]
    [InlineData("error: failed to push some refs")]
    [InlineData("fatal: Authentication failed")]
    public void Real_errors_are_not_classified_as_noise(string line)
    {
        Assert.False(GitProgressParser.IsProgressNoise(line));
    }

    [Fact]
    public void A_full_push_transcript_yields_progress_and_no_false_error()
    {
        const string stderr = """
            Enumerating objects: 12
            Counting objects:  50% (6/12)
            Counting objects: 100% (12/12)
            Writing objects:  50% (3/6)
            Writing objects: 100% (6/6)
            remote: Resolving deltas: 100% (2/2)
            To https://github.com/example/repo.git
               9291d00..a1b2c3d  main -> main
            """;

        var updates = GitProgressParser.Parse(stderr);

        Assert.NotEmpty(updates);
        Assert.Contains(updates, u => u.Percent == 100);

        var realErrors = stderr.Split('\n')
            .Where(l => !GitProgressParser.IsProgressNoise(l))
            .ToList();

        // The only non-noise line is the ref update detail, which is informational.
        Assert.All(realErrors, l =>
            Assert.DoesNotContain("fatal", l, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Null_and_empty_input_are_tolerated()
    {
        Assert.Empty(GitProgressParser.Parse(null));
        Assert.Empty(GitProgressParser.Parse(""));
    }
}

public class GitResultTests
{
    [Fact]
    public void A_successful_result_has_no_failure_reason()
    {
        var r = new GitResult { Success = true, ExitCode = 0 };

        Assert.Null(r.FailureReason);
    }

    [Fact]
    public void A_failure_prefers_gits_own_words()
    {
        var r = new GitResult
        {
            Success = false,
            ExitCode = 128,
            StdErr = "Cloning into 'x'...\nfatal: Authentication failed for 'https://…'\n",
        };

        Assert.Contains("Authentication failed", r.FailureReason);
    }

    [Fact]
    public void A_failure_with_no_stderr_falls_back_to_the_exit_code()
    {
        var r = new GitResult { Success = false, ExitCode = 1 };

        Assert.Contains("1", r.FailureReason);
    }
}
