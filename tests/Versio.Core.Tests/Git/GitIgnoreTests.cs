using Versio.Core.Git;
using Versio.Plc.Codesys;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// Keeping the toolchain's scratch files out of git.
/// </summary>
/// <remarks>
/// Measured on CODESYS 3.5.22.20: opening <c>Machine.project</c> creates <c>Machine.~u</c>
/// while it is held - rewriting it through a transient <c>Machine.~u~</c>, which is why the
/// pattern is <c>.~u*</c> - and leaves <c>Machine-AllUsers.opt</c> and
/// <c>Machine-&lt;user&gt;-&lt;machine&gt;.opt</c> behind afterwards. The per-user one carries a
/// username and a machine name in its filename, so committing it writes both into history.
/// </remarks>
public sealed class GitIgnoreTests
{
    private static readonly string[] Patterns =
        ["plc/Machine.~u*", "plc/Machine-*.opt"];

    [Fact]
    public void A_repository_with_no_gitignore_gets_one()
    {
        // The normal starting point for somebody who has kept PLC projects in a folder until
        // now. Telling them to write a .gitignore before the tool behaves is not an answer.
        var plan = GitIgnore.Plan(string.Empty, Patterns);

        Assert.True(plan.ChangeNeeded);
        Assert.Equal(Patterns, plan.AddedPatterns);
        Assert.Contains("plc/Machine.~u*\n", plan.Content, StringComparison.Ordinal);
        Assert.Contains("plc/Machine-*.opt\n", plan.Content, StringComparison.Ordinal);

        // It says who wrote it and why, because the next person to read this file will not
        // have been here.
        Assert.StartsWith("# Added by Versio", plan.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_file_is_appended_to_rather_than_replaced()
    {
        const string existing = "bin/\nobj/\n";

        var plan = GitIgnore.Plan(existing, Patterns);

        Assert.True(plan.ChangeNeeded);
        Assert.StartsWith(existing, plan.Content, StringComparison.Ordinal);
        Assert.Contains("plc/Machine-*.opt", plan.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_without_a_trailing_newline_does_not_get_its_last_rule_welded_to_ours()
    {
        // "obj/" + "plc/Machine.~u" on one line is a pattern neither of us wrote, and it
        // would silently stop ignoring obj/.
        var plan = GitIgnore.Plan("bin/\nobj/", Patterns);

        Assert.Contains("obj/\n", plan.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("obj/plc", plan.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Running_twice_changes_nothing_the_second_time()
    {
        // Adoption re-runs on every scan. A planner that appended each time would grow the
        // file without bound and show a diff on every startup.
        var first = GitIgnore.Plan(string.Empty, Patterns);
        var second = GitIgnore.Plan(first.Content, Patterns);

        Assert.False(second.ChangeNeeded);
        Assert.Empty(second.AddedPatterns);
        Assert.Equal(first.Content, second.Content);
    }

    [Fact]
    public void Only_the_missing_patterns_are_added()
    {
        var plan = GitIgnore.Plan("plc/Machine.~u*\n", Patterns);

        Assert.True(plan.ChangeNeeded);
        Assert.Equal(["plc/Machine-*.opt"], plan.AddedPatterns);
    }

    [Fact]
    public void A_negated_pattern_counts_as_a_decision_and_is_left_alone()
    {
        // "!plc/Machine-AllUsers.opt" means somebody deliberately wants it tracked. Adding
        // the positive rule underneath would quietly reverse that.
        var plan = GitIgnore.Plan("!plc/Machine-*.opt\n", Patterns);

        Assert.Equal(["plc/Machine.~u*"], plan.AddedPatterns);
        Assert.DoesNotContain("\nplc/Machine-*.opt", plan.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Comments_are_not_mistaken_for_rules()
    {
        var plan = GitIgnore.Plan("# plc/Machine-*.opt is deliberately tracked\n", Patterns);

        Assert.Contains("plc/Machine-*.opt", plan.AddedPatterns);
    }

    [Fact]
    public void Separators_and_a_leading_slash_do_not_produce_a_duplicate()
    {
        // Git compares forward slashes without a leading one. A rule written either other way
        // is the same rule, and re-adding it would be noise in somebody's diff.
        var plan = GitIgnore.Plan("/plc/Machine.~u*\nplc\\Machine-*.opt\n", Patterns);

        Assert.False(plan.ChangeNeeded);
    }

    [Fact]
    public void The_codesys_patterns_are_scoped_to_the_project_not_the_repository()
    {
        // "*.opt" repository-wide would ignore somebody else's files on the strength of one
        // adopted project. That is a decision Versio has no business making.
        var patterns = CodesysProjectFormat.Instance
            .TransientPatternsFor("plc/machines/Machine.project");

        Assert.Equal(
            [
                "plc/machines/Machine.~u*",
                "plc/machines/Machine-*.opt",
                "plc/machines/Machine_project.precompilecache",
            ],
            patterns);
        Assert.All(patterns, p => Assert.StartsWith("plc/machines/", p, StringComparison.Ordinal));
    }

    [Fact]
    public void The_precompile_cache_is_ignored_and_its_dot_becomes_an_underscore()
    {
        // Machine.project produces Machine_project.precompilecache - the extension's dot is
        // replaced, not kept and not dropped. Confirmed across six projects on 3.5.22.20.
        // Getting this wrong is silent: a pattern matching nothing looks exactly like a
        // pattern matching a file that is not there.
        //
        // It is the largest of the three transient files - 277 KB beside the fixture - and it
        // was missing from this list until an import defect turned out to be caused by it.
        // This repository never noticed, because *.precompilecache was typed into its own
        // .gitignore by hand; a repository Versio adopted got no rule at all.
        var patterns = CodesysProjectFormat.Instance.TransientPatternsFor("plc/Machine.project");

        Assert.Contains("plc/Machine_project.precompilecache", patterns);
        Assert.DoesNotContain("plc/Machine.project.precompilecache", patterns);
        Assert.DoesNotContain("plc/Machine.precompilecache", patterns);
    }

    [Fact]
    public void The_project_extension_is_replaced_not_appended()
    {
        // Machine.project is locked by Machine.~u. Appending would produce
        // Machine.project.~u, which matches nothing CODESYS ever writes.
        var patterns = CodesysProjectFormat.Instance.TransientPatternsFor("A.project");

        Assert.Contains("A.~u*", patterns);
        Assert.DoesNotContain("A.project.~u*", patterns);
    }

    [Fact]
    public void A_path_that_is_not_a_project_still_yields_something_sane()
    {
        var patterns = CodesysProjectFormat.Instance.TransientPatternsFor("odd/name");

        Assert.Equal(
            ["odd/name.~u*", "odd/name-*.opt", "odd/name_project.precompilecache"], patterns);
        Assert.Empty(CodesysProjectFormat.Instance.TransientPatternsFor(string.Empty));
    }
}
