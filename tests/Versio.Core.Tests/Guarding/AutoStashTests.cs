using Versio.Core.Git;
using Versio.Core.Guarding;
using Versio.Core.Sync;
using Xunit;

namespace Versio.Core.Tests.Guarding;

/// <summary>
/// The auto-stash rules: what is ours, what is still there, and what may be acted on.
/// </summary>
/// <remarks>
/// <para>
/// Every failure row in <c>SAFEGUARDS.md</c> §4.6 is a test here, because each of them is a way
/// to lose somebody's work: restoring the wrong stash, dropping one that was not ours,
/// recording one that was never created, or offering back one that has already been popped.
/// </para>
/// <para>
/// Pure — no git, no filesystem, no clock of its own. The git behaviours these rules are built
/// on were measured against real git and are recorded in §4.9b; what is tested here is what
/// Versio does with the answers.
/// </para>
/// </remarks>
public sealed class AutoStashTests
{
    private const string Sha1 = "2a4b8cdc9f1e4b6a8d3c5e7f9a1b2c3d4e5f6a7b";
    private const string Sha2 = "b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0";
    private const string Sha3 = "c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2";

    private static readonly DateTimeOffset Now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static StashEntry Live(string id, string @ref, string subject = "On main: work") =>
        new() { CommitId = id, Ref = @ref, Subject = subject };

    private static AutoStashRecord Ours(
        string id, string branch = "main", int minutesAgo = 0) => new()
    {
        Branch = branch,
        StashCommitId = id,
        CreatedUtc = Now.AddMinutes(-minutesAgo),
        Message = AutoStash.Marker(branch, "abc1234"),
    };

    private static AutoStashState State(params AutoStashRecord[] records) =>
        new() { Entries = records };

    // ------------------------------------------------------------------ taking one (§4.3)

    [Fact]
    public void The_stash_that_appeared_is_the_one_that_was_not_there_before()
    {
        var before = new[] { Live(Sha2, "stash@{0}") };
        var after = new[] { Live(Sha1, "stash@{0}"), Live(Sha2, "stash@{1}") };

        Assert.Equal(Sha1, AutoStash.NewStash(before, after));
    }

    [Fact]
    public void A_push_that_created_nothing_reports_nothing()
    {
        // The correctness check, not bookkeeping: `git stash push` on a clean tree prints
        // "No local changes to save" and exits 0 without creating an entry. Trusting the exit
        // code would record a stash that does not exist and then offer to restore it — and
        // the caller's answer to null is to abandon the switch rather than check out anyway.
        var same = new[] { Live(Sha2, "stash@{0}") };

        Assert.Null(AutoStash.NewStash(same, same));
        Assert.Null(AutoStash.NewStash([], []));
    }

    [Fact]
    public void The_positions_moving_underneath_does_not_invent_a_new_stash()
    {
        // Somebody dropping stash@{0} in a terminal renumbers everything below it. Matching on
        // the ref would see "stash@{0}" holding a different id and call it new.
        var before = new[] { Live(Sha1, "stash@{0}"), Live(Sha2, "stash@{1}") };
        var after = new[] { Live(Sha2, "stash@{0}") };

        Assert.Null(AutoStash.NewStash(before, after));
    }

    // ---------------------------------------------------------------- coming back (§4.5)

    [Fact]
    public void Returning_to_a_branch_offers_what_is_still_there()
    {
        var arrival = AutoStash.OnArrival(
            "main", State(Ours(Sha1)), [Live(Sha1, "stash@{0}")]);

        var offered = Assert.Single(arrival.Restorable);
        Assert.Equal(Sha1, offered.Record.StashCommitId);
        Assert.True(arrival.HasWork);
        Assert.Empty(arrival.Stale);
    }

    [Fact]
    public void The_ref_is_resolved_now_rather_than_remembered()
    {
        // apply takes a SHA and drop does not, so the positional ref has to be re-read at the
        // moment of use. Here the stash has moved to position 2 since it was taken.
        var arrival = AutoStash.OnArrival(
            "main",
            State(Ours(Sha1)),
            [Live(Sha3, "stash@{0}"), Live(Sha2, "stash@{1}"), Live(Sha1, "stash@{2}")]);

        Assert.Equal("stash@{2}", Assert.Single(arrival.Restorable).Ref);
    }

    [Fact]
    public void A_stash_popped_from_a_terminal_is_stale_rather_than_missing()
    {
        // A normal thing for a git user to do. The entry is cleared and nothing is said in
        // the dialog; it must not read as an error, and it must not be offered back.
        var arrival = AutoStash.OnArrival("main", State(Ours(Sha1)), []);

        Assert.Empty(arrival.Restorable);
        Assert.False(arrival.HasWork);
        Assert.Equal(Sha1, Assert.Single(arrival.Stale).StashCommitId);
    }

    [Fact]
    public void Another_branch_stash_is_not_offered_here()
    {
        var arrival = AutoStash.OnArrival(
            "main", State(Ours(Sha1, branch: "feature/pump")), [Live(Sha1, "stash@{0}")]);

        Assert.Empty(arrival.Restorable);
        Assert.Empty(arrival.Stale);
    }

    [Fact]
    public void Two_stashes_on_one_branch_come_back_newest_first()
    {
        // Switching away twice without returning is legitimate, so these are a list and not a
        // single slot — overwriting the first record would strand its stash in git with
        // nothing pointing at it.
        var arrival = AutoStash.OnArrival(
            "main",
            State(Ours(Sha1, minutesAgo: 30), Ours(Sha2, minutesAgo: 5)),
            [Live(Sha1, "stash@{1}"), Live(Sha2, "stash@{0}")]);

        Assert.Equal([Sha2, Sha1], arrival.Restorable.Select(s => s.Record.StashCommitId));
    }

    [Fact]
    public void A_stash_nobody_recorded_is_never_offered()
    {
        // Theirs. Identifying our own work by pattern-matching the subject is how you
        // eventually restore somebody else's stash into their working tree.
        var arrival = AutoStash.OnArrival(
            "main",
            AutoStashState.Empty,
            [Live(Sha1, "stash@{0}", "On main: versio-autostash main @ abc1234")]);

        Assert.Empty(arrival.Restorable);
    }

    // ------------------------------------------------------------- what is already there

    [Fact]
    public void Ours_and_theirs_are_counted_separately()
    {
        var situation = AutoStash.BeforeTaking(
            "main",
            State(Ours(Sha1)),
            [Live(Sha1, "stash@{0}"), Live(Sha2, "stash@{1}"), Live(Sha3, "stash@{2}")]);

        Assert.Equal(1, situation.Ours);
        Assert.Equal(2, situation.Theirs);
        Assert.Contains("1 stash saved for this branch", situation.Describe(),
                        StringComparison.Ordinal);
        Assert.Contains("2", situation.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_whose_stash_has_gone_is_not_counted_as_ours()
    {
        var situation = AutoStash.BeforeTaking("main", State(Ours(Sha1)), []);

        Assert.Equal(0, situation.Ours);
        Assert.Equal(0, situation.Theirs);
        Assert.Equal(string.Empty, situation.Describe());
    }

    [Fact]
    public void Nothing_at_all_is_said_when_the_stash_list_is_empty()
    {
        Assert.Equal(
            string.Empty,
            AutoStash.BeforeTaking("main", AutoStashState.Empty, []).Describe());
    }

    // ---------------------------------------------------------------------- odd corners

    [Fact]
    public void A_detached_head_is_keyed_on_the_commit_it_can_come_back_to()
    {
        // Refusing to stash mid-switch because there is no branch name would be worse than an
        // imperfect key.
        Assert.Equal($"detached@{Sha1[..7]}", AutoStash.KeyFor(null, Sha1));
        Assert.Equal($"detached@{Sha1[..7]}", AutoStash.KeyFor("   ", Sha1));
        Assert.Equal("main", AutoStash.KeyFor("main", Sha1));
    }

    [Fact]
    public void A_stash_whose_branch_was_deleted_is_surfaced_and_never_dropped()
    {
        // It still holds real work, and the branch it belonged to is gone — which is exactly
        // when somebody needs telling rather than tidying up after.
        var orphans = AutoStash.Orphans(
            State(Ours(Sha1, branch: "feature/gone"), Ours(Sha2, branch: "main")),
            ["main"],
            [Live(Sha1, "stash@{0}"), Live(Sha2, "stash@{1}")]);

        Assert.Equal("feature/gone", Assert.Single(orphans).Branch);
    }

    [Fact]
    public void A_detached_key_is_not_reported_as_an_orphan()
    {
        // There is no branch by that name and there never was, so measuring it against the
        // branch list would report every detached stash as orphaned.
        Assert.Empty(AutoStash.Orphans(
            State(Ours(Sha1, branch: "detached@abc1234")), ["main"], [Live(Sha1, "stash@{0}")]));
    }

    [Fact]
    public void An_orphan_whose_stash_has_also_gone_is_not_worth_reporting()
    {
        Assert.Empty(AutoStash.Orphans(
            State(Ours(Sha1, branch: "feature/gone")), ["main"], []));
    }

    [Fact]
    public void The_marker_is_readable_in_a_terminal_and_is_not_an_identity()
    {
        Assert.Equal("versio-autostash main @ abc1234", AutoStash.Marker("main", "abc1234"));
    }

    // -------------------------------------------------------------------- the record set

    [Fact]
    public void Records_are_added_newest_first_and_removed_by_id()
    {
        var state = AutoStash.With(AutoStash.With(AutoStashState.Empty, Ours(Sha1)), Ours(Sha2));

        Assert.Equal([Sha2, Sha1], state.Entries.Select(e => e.StashCommitId));

        var after = AutoStash.Without(state, Sha1);
        Assert.Equal(Sha2, Assert.Single(after.Entries).StashCommitId);
    }

    [Fact]
    public void A_record_carries_the_locks_it_released()
    {
        // The half that makes it a round trip rather than a file copy: --include-untracked
        // excludes ignored files, so .versio/local/ does not travel and the lock would
        // otherwise still claim work that is no longer on disk.
        var record = Ours(Sha1) with
        {
            Locks =
            [
                new StashedLock
                {
                    BindingId = "b-1",
                    Direction = SyncDirection.TextAuthoritative,
                    ProjectHash = "sha256:aaa",
                    MirrorHash = "sha256:bbb",
                },
            ],
        };

        var restored = Assert.Single(AutoStash
            .OnArrival("main", State(record), [Live(Sha1, "stash@{0}")])
            .Restorable);

        var held = Assert.Single(restored.Record.Locks);
        Assert.Equal(SyncDirection.TextAuthoritative, held.Direction);
        Assert.Equal("sha256:bbb", held.MirrorHash);
    }
}
