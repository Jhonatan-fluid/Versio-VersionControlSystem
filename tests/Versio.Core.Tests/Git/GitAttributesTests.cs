using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// The <c>.gitattributes</c> rules a tracked project needs.
/// </summary>
/// <remarks>
/// Two failure modes are asserted here rather than assumed away: git line-merging a binary
/// project because no rule was written, and a <c>filter=lfs</c> rule written into a
/// repository where LFS cannot run, which breaks every later checkout for everyone.
/// </remarks>
public sealed class GitAttributesTests
{
    private static readonly string[] Patterns = ["*.project", "*.library"];

    [Fact]
    public void An_empty_file_gains_a_rule_per_pattern()
    {
        var plan = GitAttributes.Plan(string.Empty, Patterns, lfsAvailable: false);

        Assert.True(plan.ChangeNeeded);
        Assert.Equal(Patterns, plan.AddedPatterns);
        Assert.Contains("*.project", plan.Content);
        Assert.Contains("*.library", plan.Content);
    }

    [Fact]
    public void Binary_diff_and_merge_are_always_set()
    {
        // The part that actually matters. Without it git attempts a line merge on a binary
        // compound document and produces a file that opens in nothing.
        var plan = GitAttributes.Plan(string.Empty, ["*.project"], lfsAvailable: false);

        Assert.Contains("binary -diff -merge", plan.Content);
    }

    [Fact]
    public void Lfs_is_written_only_when_git_can_run_it()
    {
        var without = GitAttributes.Plan(string.Empty, ["*.project"], lfsAvailable: false);
        var with = GitAttributes.Plan(string.Empty, ["*.project"], lfsAvailable: true);

        Assert.DoesNotContain("lfs", without.Content, StringComparison.OrdinalIgnoreCase);
        Assert.False(without.UsesLfs);

        Assert.Contains("filter=lfs diff=lfs merge=lfs", with.Content);
        Assert.True(with.UsesLfs);
    }

    [Fact]
    public void An_existing_rule_is_never_rewritten()
    {
        // A repository that already says something about *.project was configured
        // deliberately, possibly to opt out of LFS. Overriding that silently would be a
        // worse bug than the one being fixed.
        const string existing = "*.project text eol=lf\n";

        var plan = GitAttributes.Plan(existing, ["*.project"], lfsAvailable: true);

        Assert.False(plan.ChangeNeeded);
        Assert.Equal(existing, plan.Content);
        Assert.Empty(plan.AddedPatterns);
    }

    [Fact]
    public void Only_the_missing_patterns_are_added()
    {
        var plan = GitAttributes.Plan("*.project binary\n", Patterns, lfsAvailable: false);

        Assert.True(plan.ChangeNeeded);
        Assert.Equal(["*.library"], plan.AddedPatterns);
    }

    [Fact]
    public void Existing_content_survives_untouched()
    {
        const string existing = "# hand written\n*.st text eol=lf\n";

        var plan = GitAttributes.Plan(existing, ["*.project"], lfsAvailable: false);

        Assert.StartsWith(existing, plan.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_no_trailing_newline_does_not_swallow_its_last_rule()
    {
        var plan = GitAttributes.Plan("*.st text", ["*.project"], lfsAvailable: false);

        Assert.Contains("*.st text\n", plan.Content);
        Assert.Contains("*.project", plan.Content);
    }

    [Fact]
    public void Comments_and_macros_are_not_mistaken_for_rules()
    {
        const string existing = "# *.project is handled elsewhere\n[attr]binfile binary\n";

        var plan = GitAttributes.Plan(existing, ["*.project"], lfsAvailable: false);

        Assert.True(plan.ChangeNeeded);
        Assert.Equal(["*.project"], plan.AddedPatterns);
    }

    [Fact]
    public void Crlf_input_is_normalised_so_git_reads_its_own_config_cleanly()
    {
        var plan = GitAttributes.Plan("*.st text\r\n", ["*.project"], lfsAvailable: false);

        Assert.DoesNotContain('\r', plan.Content);
    }

    [Fact]
    public void The_result_is_stable_when_applied_twice()
    {
        // Adoption runs this on every adopt. A planner that appended a duplicate block each
        // time would quietly grow the file forever.
        var first = GitAttributes.Plan(string.Empty, Patterns, lfsAvailable: true);
        var second = GitAttributes.Plan(first.Content, Patterns, lfsAvailable: true);

        Assert.False(second.ChangeNeeded);
        Assert.Equal(first.Content, second.Content);
    }
}
