using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// Fixtures are real <c>git status --porcelain=v2 --branch -z</c> output shapes, with NUL
/// separators written explicitly as \0.
/// </summary>
public class PorcelainV2ParserTests
{
    private const string Nul = "\0";

    private static string Entries(params string[] entries) =>
        string.Concat(entries.Select(e => e + Nul));

    [Fact]
    public void A_clean_repo_on_a_branch()
    {
        var status = PorcelainV2Parser.Parse(Entries(
            "# branch.oid 9291d00abc",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +0 -0"));

        Assert.Equal("main", status.Branch.Name);
        Assert.Equal("origin/main", status.Branch.Upstream);
        Assert.True(status.IsClean);
        Assert.False(status.Branch.IsDetached);
    }

    [Fact]
    public void Ahead_and_behind_counts_are_read()
    {
        var status = PorcelainV2Parser.Parse(Entries(
            "# branch.head feature/x",
            "# branch.ab +3 -2"));

        Assert.Equal(3, status.Branch.Ahead);
        Assert.Equal(2, status.Branch.Behind);
        Assert.False(status.Branch.UpstreamGone);
    }

    [Fact]
    public void An_upstream_without_an_ahead_behind_line_is_flagged_gone()
    {
        // Git states "the upstream's tracking ref is gone" BY OMISSION: branch.upstream is
        // printed and branch.ab is not, because there is nothing left to count against. This
        // is the shape a pruned repository reports after the forge deleted the branch
        // post-merge, and it used to decay to 0/0 = "Up to date" (FINDINGS 2026-08-23).
        var status = PorcelainV2Parser.Parse(Entries(
            "# branch.oid 9291d00abc",
            "# branch.head continue",
            "# branch.upstream origin/continue"));

        Assert.True(status.Branch.UpstreamGone);
        Assert.Equal("origin/continue", status.Branch.Upstream);
    }

    [Fact]
    public void No_upstream_at_all_is_not_flagged_gone()
    {
        // A branch never published also has no branch.ab line; "gone" is only meaningful
        // when there is a configured upstream to be gone.
        var status = PorcelainV2Parser.Parse(Entries(
            "# branch.oid 9291d00abc",
            "# branch.head feature/x"));

        Assert.False(status.Branch.UpstreamGone);
    }

    [Fact]
    public void A_zero_zero_ahead_behind_line_is_in_sync_rather_than_gone()
    {
        // The line being PRESENT with zeros is a healthy in-sync branch. Only its absence
        // beside an upstream means the ref is gone - the two must not be conflated, or every
        // clean branch turns amber.
        var status = PorcelainV2Parser.Parse(Entries(
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +0 -0"));

        Assert.False(status.Branch.UpstreamGone);
    }

    [Fact]
    public void A_detached_head_is_recognised()
    {
        var status = PorcelainV2Parser.Parse(Entries("# branch.head (detached)"));

        Assert.True(status.Branch.IsDetached);
    }

    [Fact]
    public void An_unborn_branch_is_recognised()
    {
        // A freshly `git init`ed repo, before the first commit.
        var status = PorcelainV2Parser.Parse(Entries(
            "# branch.oid (initial)",
            "# branch.head main"));

        Assert.True(status.Branch.IsUnborn);
    }

    [Fact]
    public void A_modified_unstaged_file()
    {
        var status = PorcelainV2Parser.Parse(Entries(
            "1 .M N... 100644 100644 100644 abc def plc/MyMachine/Main.st"));

        var change = Assert.Single(status.Changes);
        Assert.Equal("plc/MyMachine/Main.st", change.Path);
        Assert.Equal(ChangeKind.Unmodified, change.Staged);
        Assert.Equal(ChangeKind.Modified, change.Unstaged);
        Assert.False(change.IsStaged);
    }

    [Fact]
    public void A_staged_added_file()
    {
        var status = PorcelainV2Parser.Parse(Entries(
            "1 A. N... 000000 100644 100644 000000 abc plc/MyMachine/New.st"));

        var change = Assert.Single(status.Changes);
        Assert.Equal(ChangeKind.Added, change.Staged);
        Assert.True(change.IsStaged);
        Assert.Equal(ChangeKind.Added, change.Effective);
    }

    [Fact]
    public void A_file_both_staged_and_modified_keeps_both_states()
    {
        // Collapsing these loses information the user acts on.
        var status = PorcelainV2Parser.Parse(Entries(
            "1 MM N... 100644 100644 100644 abc def plc/MyMachine/Main.st"));

        var change = Assert.Single(status.Changes);
        Assert.Equal(ChangeKind.Modified, change.Staged);
        Assert.Equal(ChangeKind.Modified, change.Unstaged);
    }

    [Fact]
    public void A_rename_carries_its_original_path_and_score()
    {
        // The original path is a SEPARATE NUL-terminated field, not part of the line.
        // This is what makes a moved POU show as a move rather than delete + add.
        var status = PorcelainV2Parser.Parse(
            "2 R. N... 100644 100644 100644 abc def R100 plc/New.st\0plc/Old.st\0");

        var change = Assert.Single(status.Changes);
        Assert.Equal("plc/New.st", change.Path);
        Assert.Equal("plc/Old.st", change.OriginalPath);
        Assert.Equal(100, change.SimilarityScore);
        Assert.Equal(ChangeKind.Renamed, change.Staged);
    }

    [Fact]
    public void A_rename_does_not_swallow_the_entry_that_follows_it()
    {
        // If the extra NUL field were mishandled, the next entry would be consumed as the
        // original path and silently disappear from the status.
        var status = PorcelainV2Parser.Parse(
            "2 R. N... 100644 100644 100644 abc def R100 plc/New.st\0plc/Old.st\0" +
            "1 .M N... 100644 100644 100644 abc def plc/Other.st\0");

        Assert.Equal(2, status.Changes.Count);
        Assert.Equal("plc/Other.st", status.Changes[1].Path);
    }

    [Fact]
    public void Untracked_files_are_reported()
    {
        var status = PorcelainV2Parser.Parse(Entries("? plc/Untracked.project"));

        var change = Assert.Single(status.Changes);
        Assert.Equal("plc/Untracked.project", change.Path);
        Assert.Equal(ChangeKind.Untracked, change.Unstaged);
        Assert.False(change.IsStaged);
    }

    [Fact]
    public void Ignored_entries_are_skipped()
    {
        var status = PorcelainV2Parser.Parse(Entries("! .versio/local/state.json"));

        Assert.Empty(status.Changes);
    }

    [Fact]
    public void A_conflicted_file_is_flagged()
    {
        var status = PorcelainV2Parser.Parse(Entries(
            "u UU N... 100644 100644 100644 100644 aaa bbb ccc plc/MyMachine/Main.st"));

        var change = Assert.Single(status.Changes);
        Assert.True(change.IsConflicted);
        Assert.Equal(ChangeKind.Conflicted, change.Effective);
        Assert.True(status.HasConflicts);
    }

    [Fact]
    public void Paths_with_spaces_and_non_ascii_survive_intact()
    {
        // The reason for -z. Without it git quotes and escapes these, and CODESYS object
        // names routinely contain both.
        var status = PorcelainV2Parser.Parse(Entries(
            "1 .M N... 100644 100644 100644 abc def plc/Kühlschrank Regelung/Signals ähnlich.st"));

        Assert.Equal("plc/Kühlschrank Regelung/Signals ähnlich.st",
            Assert.Single(status.Changes).Path);
    }

    [Fact]
    public void A_path_containing_a_space_is_not_split_into_fields()
    {
        var status = PorcelainV2Parser.Parse(Entries(
            "1 .M N... 100644 100644 100644 abc def Device/Plc Logic/Application/Task Configuration.xml"));

        Assert.Equal("Device/Plc Logic/Application/Task Configuration.xml",
            Assert.Single(status.Changes).Path);
    }

    [Fact]
    public void A_realistic_mixed_status_parses_completely()
    {
        var status = PorcelainV2Parser.Parse(
            Entries(
                "# branch.oid 9291d00",
                "# branch.head main",
                "# branch.upstream origin/main",
                "# branch.ab +2 -0",
                "1 M. N... 100644 100644 100644 a b plc/M/Main.st",
                "1 .M N... 100644 100644 100644 c d plc/M/Signals.xml",
                "1 A. N... 000000 100644 100644 0 e plc/M/New.st") +
            "2 R. N... 100644 100644 100644 f g R95 plc/M/Renamed.st\0plc/M/Old.st\0" +
            Entries("? plc/Other.project"));

        Assert.Equal(5, status.Changes.Count);
        Assert.Equal(2, status.Branch.Ahead);
        Assert.True(status.HasStagedChanges);
        Assert.False(status.HasConflicts);
        Assert.Equal(3, status.Staged.Count());
    }

    [Fact]
    public void Empty_and_null_output_yield_an_empty_status()
    {
        Assert.True(PorcelainV2Parser.Parse(null).IsClean);
        Assert.True(PorcelainV2Parser.Parse("").IsClean);
    }

    [Fact]
    public void A_malformed_entry_is_skipped_rather_than_throwing()
    {
        // A truncated read must not take down the status refresh.
        var status = PorcelainV2Parser.Parse(Entries(
            "1 too short",
            "1 .M N... 100644 100644 100644 abc def plc/Good.st"));

        Assert.Equal("plc/Good.st", Assert.Single(status.Changes).Path);
    }
}
