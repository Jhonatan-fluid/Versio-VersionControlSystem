using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// The history and stash listings, parsed from the format the client asks git for.
/// </summary>
/// <remarks>
/// Written against output shapes copied from a real repository. The interesting cases are all
/// about data that contains the things a naive parser would have split on — a subject with a
/// tab in it, a branch name with a slash, a stash message with a colon.
/// </remarks>
public sealed class GitLogParserTests
{
    private const char U = GitLogParser.Unit;
    private const char R = GitLogParser.Record;

    private static string Commit(params string[] fields) => string.Join(U, fields) + R;

    [Fact]
    public void A_commit_carries_both_ids_its_subject_and_its_author()
    {
        var commits = GitLogParser.ParseCommits(Commit(
            "ab1b6e1f4c2d9e8a7b6c5d4e3f2a1b0c9d8e7f60", "ab1b6e1",
            "Complete the vendor seam in the app", "jhonatanevm",
            "2026-08-12T09:50:03-07:00", "2 hours ago"));

        var commit = Assert.Single(commits);
        Assert.Equal("ab1b6e1f4c2d9e8a7b6c5d4e3f2a1b0c9d8e7f60", commit.Id);
        Assert.Equal("ab1b6e1", commit.ShortId);
        Assert.Equal("Complete the vendor seam in the app", commit.Subject);
        Assert.Equal("jhonatanevm", commit.Author);
        Assert.Equal("2 hours ago", commit.Age);
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 9, 50, 3, TimeSpan.FromHours(-7)), commit.When);
    }

    [Fact]
    public void A_commit_carries_the_branches_and_tags_that_point_at_it()
    {
        var commit = Assert.Single(GitLogParser.ParseCommits(Commit(
            "ab1b6e1f4c2d9e8a7b6c5d4e3f2a1b0c9d8e7f60", "ab1b6e1", "Subject", "someone",
            "2026-08-12T09:50:03-07:00", "2 hours ago",
            "HEAD -> main, origin/main, origin/HEAD, tag: v0.1.1")));

        Assert.Equal(
            [
                new CommitRef("main", IsTag: false, IsHead: true),
                new CommitRef("origin/main", IsTag: false, IsHead: false),
                new CommitRef("v0.1.1", IsTag: true, IsHead: false),
            ],
            commit.Refs);
    }

    [Fact]
    public void A_commit_with_no_names_and_a_record_without_the_field_both_read_as_none()
    {
        // The field is empty for most commits, and absent from a format that predates it.
        Assert.Empty(Assert.Single(GitLogParser.ParseCommits(Commit(
            "a", "a", "s", "x", "2026-08-12T09:50:03-07:00", "now", ""))).Refs);
        Assert.Empty(Assert.Single(GitLogParser.ParseCommits(Commit(
            "a", "a", "s", "x", "2026-08-12T09:50:03-07:00", "now"))).Refs);
    }

    [Fact]
    public void A_detached_HEAD_is_its_own_label()
    {
        Assert.Equal(
            [new CommitRef("HEAD", IsTag: false, IsHead: true)],
            GitLogParser.ParseRefs("HEAD"));
    }

    [Fact]
    public void Several_commits_come_back_in_the_order_git_listed_them()
    {
        // Newest first is git's order and the one the panel wants; re-sorting here would only
        // be a chance to disagree with `git log`.
        var commits = GitLogParser.ParseCommits(
            Commit("h1", "s1", "newest", "a", "2026-08-12T10:00:00Z", "1 hour ago") + "\n" +
            Commit("h2", "s2", "older", "a", "2026-08-11T10:00:00Z", "1 day ago"));

        Assert.Equal(["newest", "older"], commits.Select(c => c.Subject));
    }

    [Fact]
    public void A_subject_containing_a_tab_survives()
    {
        // The reason the separators are 0x1F and 0x1E rather than a tab or a pipe: a commit
        // subject can contain either, and splitting on one would cut a message in half.
        var commits = GitLogParser.ParseCommits(Commit(
            "h", "s", "fix:\tthe | thing", "a", "2026-08-12T10:00:00Z", "now"));

        Assert.Equal("fix:\tthe | thing", Assert.Single(commits).Subject);
    }

    [Fact]
    public void An_unparseable_date_leaves_the_commit_intact_without_one()
    {
        // The date is decoration; the subject is the row. Losing the row over the decoration
        // would be the wrong trade.
        var commits = GitLogParser.ParseCommits(Commit(
            "h", "s", "subject", "a", "not a date", "a while ago"));

        var commit = Assert.Single(commits);
        Assert.Null(commit.When);
        Assert.Equal("subject", commit.Subject);
    }

    [Fact]
    public void A_truncated_record_is_dropped_rather_than_thrown_over()
    {
        var commits = GitLogParser.ParseCommits(
            "h" + U + "s" + R + "\n" +
            Commit("h2", "s2", "good", "a", "2026-08-12T10:00:00Z", "now"));

        Assert.Equal("good", Assert.Single(commits).Subject);
    }

    [Fact]
    public void No_output_is_no_commits_not_a_crash()
    {
        Assert.Empty(GitLogParser.ParseCommits(null));
        Assert.Empty(GitLogParser.ParseCommits(string.Empty));
        Assert.Empty(GitLogParser.ParseCommits("\n\n"));
    }

    [Fact]
    public void A_plain_stash_reports_its_branch_and_drops_gits_bookkeeping()
    {
        // "WIP on main: 9852b29 Dogfood…" names the commit the stash was taken against, not
        // the work in it. The id is noise in a list; the subject after it is at least a hint.
        var stashes = GitLogParser.ParseStashes(string.Join(U,
            "2a4b8cdc9f1e4b6a8d3c5e7f9a1b2c3d4e5f6a7b", "stash@{0}",
            "WIP on main: 9852b29 Dogfood: commit the binding", "3 minutes ago") + R);

        var stash = Assert.Single(stashes);
        Assert.Equal("stash@{0}", stash.Ref);
        Assert.Equal("main", stash.Branch);
        Assert.Equal("Dogfood: commit the binding", stash.Message);
        Assert.Equal("3 minutes ago", stash.Age);
    }

    [Fact]
    public void A_stash_saved_with_a_message_keeps_the_message_whole()
    {
        // `git stash push -m` produces "On <branch>: <message>", and the message routinely
        // contains a colon of its own — which is exactly what a naive split would truncate.
        var stashes = GitLogParser.ParseStashes(string.Join(U,
            "b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0", "stash@{1}",
            "On feature/pump: wip: retune the PID", "1 day ago") + R);

        var stash = Assert.Single(stashes);
        Assert.Equal("feature/pump", stash.Branch);
        Assert.Equal("wip: retune the PID", stash.Message);
    }

    [Fact]
    public void A_subject_in_neither_shape_is_passed_through_rather_than_mangled()
    {
        var stashes = GitLogParser.ParseStashes(string.Join(U,
            "c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2", "stash@{0}",
            "something git has not written before", "now") + R);

        var stash = Assert.Single(stashes);
        Assert.Equal(string.Empty, stash.Branch);
        Assert.Equal("something git has not written before", stash.Message);
    }

    [Fact]
    public void A_wip_subject_whose_second_token_is_not_a_commit_id_keeps_every_word()
    {
        // Only strip the id when what is there actually looks like one. Dropping the first
        // word unconditionally would eat a real message.
        var stashes = GitLogParser.ParseStashes(string.Join(U,
            "d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3", "stash@{0}",
            "WIP on main: rework the export", "now") + R);

        Assert.Equal("rework the export", Assert.Single(stashes).Message);
    }

    [Fact]
    public void A_stash_carries_the_only_identity_that_survives_a_drop()
    {
        // %gd is positional and renumbers, %gs is a sentence and %cr is relative to now, so
        // before %H was in the format nothing in a StashEntry could name one particular stash
        // twice. The auto-stash record is keyed on this.
        var stashes = GitLogParser.ParseStashes(string.Join(U,
            "2a4b8cdc9f1e4b6a8d3c5e7f9a1b2c3d4e5f6a7b", "stash@{0}",
            "On main: versio-autostash main @ abc1234", "3 minutes ago") + R);

        var stash = Assert.Single(stashes);
        Assert.Equal("2a4b8cdc9f1e4b6a8d3c5e7f9a1b2c3d4e5f6a7b", stash.CommitId);
        Assert.Equal("stash@{0}", stash.Ref);

        // And our own marker splits like any other message, so a stash Versio took reads back
        // with the branch it was taken on.
        Assert.Equal("main", stash.Branch);
        Assert.Equal("versio-autostash main @ abc1234", stash.Message);
    }

    [Fact]
    public void The_format_asks_git_for_that_identity()
    {
        // The format and the parser are one decision in two places: a field added to one and
        // not the other shifts every column. Records() then rejects the record for arity and
        // stash listing comes back empty rather than wrong - the better failure, and still
        // worth not having.
        Assert.StartsWith("--format=%H", GitLogParser.StashFormat, StringComparison.Ordinal);
        Assert.Equal(4, GitLogParser.StashFormat.Split("%x1f").Length);
    }

    [Fact]
    public void Stash_entries_keep_their_positions_in_order()
    {
        // The refs are positional and renumber on a drop, so the order they arrive in is the
        // order they must be shown in — anything acting on one re-reads the list first.
        var stashes = GitLogParser.ParseStashes(
            string.Join(U, "aaa1", "stash@{0}", "On main: newest", "now") + R + "\n" +
            string.Join(U, "bbb2", "stash@{1}", "On main: older", "2 days ago") + R);

        Assert.Equal(["stash@{0}", "stash@{1}"], stashes.Select(s => s.Ref));
    }
}
