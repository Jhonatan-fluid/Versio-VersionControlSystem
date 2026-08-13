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

    [Fact]
    public void The_mirror_gets_the_opposite_rule_to_the_project()
    {
        // The project is binary and must never be line-merged; the mirror exists to BE
        // line-merged, and has to arrive byte-identical on every machine that clones it.
        var plan = GitAttributes.Plan(string.Empty, ["*.project"], lfsAvailable: false,
                                      mirrors: ["plc/Machine"]);

        Assert.Contains("plc/Machine/**", plan.Content, StringComparison.Ordinal);
        Assert.Contains("text eol=lf", plan.Content, StringComparison.Ordinal);
        Assert.Contains("binary -diff -merge", plan.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mirror_rule_somebody_already_wrote_is_left_alone()
    {
        // Same rule as everywhere else here: a repository that has already spoken about a
        // pattern was configured deliberately, and overriding it is a worse bug than the one
        // being fixed.
        var plan = GitAttributes.Plan(
            "plc/Machine/** text" + Environment.NewLine, [], lfsAvailable: false,
            mirrors: ["plc/Machine"]);

        Assert.False(plan.ChangeNeeded);
    }

    [Fact]
    public void A_mirror_needing_a_rule_is_enough_on_its_own()
    {
        // The project extensions are repository-wide and are usually already covered by the
        // time a second project is adopted. That must not stop the second project's mirror
        // getting its rule.
        var plan = GitAttributes.Plan(
            "*.project binary -diff -merge" + Environment.NewLine, ["*.project"],
            lfsAvailable: false, mirrors: ["plc/Second"]);

        Assert.True(plan.ChangeNeeded);
        Assert.Equal("plc/Second/**", Assert.Single(plan.AddedPatterns));
    }

    [Fact]
    public void Backslashes_in_an_export_path_become_the_separator_git_reads()
    {
        // repo.json stores whatever the scan produced, and on Windows that can be a
        // backslash. git patterns are forward-slash only.
        var plan = GitAttributes.Plan(string.Empty, [], lfsAvailable: false,
                                      mirrors: [@"plc\Machine"]);

        Assert.Contains("plc/Machine/**", plan.Content, StringComparison.Ordinal);
    }
}
