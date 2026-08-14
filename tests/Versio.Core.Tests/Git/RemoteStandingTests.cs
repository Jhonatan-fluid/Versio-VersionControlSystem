using Versio.Core.Git;
using Xunit;

namespace Versio.Core.Tests.Git;

/// <summary>
/// Every state a branch can be in relative to its remote, and the sentence each produces.
/// </summary>
/// <remarks>
/// <para>
/// The wording is asserted rather than only the enum, because the wording <i>is</i> the feature.
/// This type exists to replace <c>↑2 ↓1</c> — which is correct, compact, and asks the reader to
/// remember which arrow means somebody else moved. A badge nobody can decode at a glance is a
/// badge that gets ignored, and the state it was hiding is the one where doing nothing has a
/// cost.
/// </para>
/// <para>
/// The two cases worth reading first are the ones the old rendering got outright wrong: a
/// branch with no upstream reported 0/0 and looked <i>identical to in sync</i> while being
/// invisible to the rest of the team, and "no remote at all" was indistinguishable from it.
/// </para>
/// </remarks>
public sealed class RemoteStandingTests
{
    private static BranchInfo Branch(
        string? upstream = "origin/main", int ahead = 0, int behind = 0,
        bool unborn = false, bool detached = false) => new()
    {
        Name = "main",
        Upstream = upstream,
        Ahead = ahead,
        Behind = behind,
        IsUnborn = unborn,
        IsDetached = detached,
        IsCurrent = true,
    };

    [Fact]
    public void Nothing_open_says_nothing()
    {
        Assert.Equal(TrackingState.NoRepository, RemoteStanding.None.State);
        Assert.Equal(string.Empty, RemoteStanding.None.Headline);
    }

    [Fact]
    public void An_unborn_branch_asks_for_the_first_commit()
    {
        var standing = RemoteStanding.For(Branch(upstream: null, unborn: true), hasRemote: true);

        Assert.Equal(TrackingState.Unborn, standing.State);
        Assert.Contains("No commits yet", standing.Headline);
    }

    [Fact]
    public void A_detached_head_says_the_work_can_be_lost()
    {
        var standing = RemoteStanding.For(Branch(upstream: null, detached: true), hasRemote: true);

        Assert.Equal(TrackingState.Detached, standing.State);

        // The trap: a commit made here is real and reachable only from the reflog. Somebody
        // who reached this by clicking a commit in the history has no idea of that.
        Assert.Contains("lost", standing.Headline);
        Assert.False(standing.CanPush);
        Assert.False(standing.CanPull);
    }

    [Fact]
    public void Unborn_wins_over_detached()
    {
        // A fresh `git init` reports both. "No commits yet" is the more useful thing to say —
        // it names what to do — where "not on a branch" is technically true and unactionable.
        var standing = RemoteStanding.For(
            Branch(upstream: null, unborn: true, detached: true), hasRemote: false);

        Assert.Equal(TrackingState.Unborn, standing.State);
    }

    [Fact]
    public void No_remote_and_no_upstream_are_different_states()
    {
        var noRemote = RemoteStanding.For(Branch(upstream: null), hasRemote: false);
        var noUpstream = RemoteStanding.For(Branch(upstream: null), hasRemote: true);

        // They look identical from the branch alone and have completely different remedies:
        // one is "add a remote", the other is "press Publish". Answering the wrong one sends
        // somebody hunting for a setting that is already correct.
        Assert.Equal(TrackingState.NoRemote, noRemote.State);
        Assert.Equal(TrackingState.NoUpstream, noUpstream.State);
        Assert.NotEqual(noRemote.Headline, noUpstream.Headline);
    }

    [Fact]
    public void No_remote_offers_nothing_and_is_not_alarming()
    {
        var standing = RemoteStanding.For(Branch(upstream: null), hasRemote: false);

        // A local-only repository is an ordinary thing to have, not a fault.
        Assert.Equal("Brush.Text.Secondary", standing.BrushKey);
        Assert.False(standing.CanPublish);
    }

    [Fact]
    public void No_upstream_is_amber_and_offers_publish()
    {
        var standing = RemoteStanding.For(Branch(upstream: null), hasRemote: true);

        // Amber because doing nothing has a cost: the work exists only on this machine and
        // nobody else can see it. This is the case the old rendering showed as 0/0, which
        // looked exactly like being perfectly in sync.
        Assert.Equal("Brush.Status.Modified", standing.BrushKey);
        Assert.True(standing.CanPublish);
        Assert.False(standing.CanPush);
        Assert.False(standing.CanPull);
        Assert.Contains("Nobody else can see", standing.Headline);
    }

    [Fact]
    public void In_sync_names_the_upstream_and_is_quiet()
    {
        var standing = RemoteStanding.For(Branch(), hasRemote: true);

        Assert.Equal(TrackingState.InSync, standing.State);
        Assert.Equal("Brush.Text.Secondary", standing.BrushKey);
        Assert.Contains("origin/main", standing.Headline);
        Assert.False(standing.CanPush);
        Assert.True(standing.CanPull);
    }

    [Fact]
    public void Ahead_is_accent_rather_than_amber()
    {
        var standing = RemoteStanding.For(Branch(ahead: 3), hasRemote: true);

        Assert.Equal(TrackingState.Ahead, standing.State);

        // Work not yet pushed is the normal condition of somebody mid-task. Colouring it like
        // a problem would make the colour mean nothing by the time it is needed.
        Assert.Equal("Brush.Accent.Default", standing.BrushKey);
        Assert.True(standing.CanPush);
        Assert.Contains("3 commits to push", standing.Headline);
    }

    [Fact]
    public void Behind_says_what_it_means_rather_than_drawing_an_arrow()
    {
        var standing = RemoteStanding.For(Branch(behind: 2), hasRemote: true);

        Assert.Equal(TrackingState.Behind, standing.State);
        Assert.Equal("Brush.Status.Modified", standing.BrushKey);

        // The whole point of the type: "the remote has changes you do not have" is actionable
        // where a downward arrow is a puzzle.
        Assert.Contains("the remote has changes you do not have", standing.Headline);
    }

    [Theory]
    [InlineData(1, "1 commit to push.")]
    [InlineData(2, "2 commits to push.")]
    public void The_ahead_count_is_pluralised(int ahead, string expected)
    {
        Assert.Equal(expected, RemoteStanding.For(Branch(ahead: ahead), true).Headline);
    }

    [Fact]
    public void One_commit_behind_reads_as_one_change()
    {
        Assert.Contains("a change you do not have",
                        RemoteStanding.For(Branch(behind: 1), true).Headline);
    }

    [Fact]
    public void Diverged_says_to_pull_first()
    {
        var standing = RemoteStanding.For(Branch(ahead: 2, behind: 3), hasRemote: true);

        Assert.Equal(TrackingState.Diverged, standing.State);

        // --ff-only will refuse this, and the user needs to know why before pressing anything.
        Assert.Contains("Pull first", standing.Headline);
        Assert.Contains("2 to push", standing.Headline);
        Assert.Contains("3 to pull", standing.Headline);
    }

    [Fact]
    public void Push_stays_available_while_diverged()
    {
        // Git will reject it. The button stays live anyway: disabling it hides the state the
        // user has to resolve, where git's own rejection - in a notification that does not
        // expire - is the clearest possible explanation.
        Assert.True(RemoteStanding.For(Branch(ahead: 1, behind: 1), true).CanPush);
    }

    [Fact]
    public void Pull_is_available_whenever_there_is_an_upstream()
    {
        // NOT gated on Behind. Behind-ness is only as fresh as the last fetch, and disabling a
        // safe fast-forward on stale knowledge would leave somebody unable to act on a remote
        // that moved a second ago.
        Assert.True(RemoteStanding.For(Branch(), true).CanPull);
        Assert.True(RemoteStanding.For(Branch(ahead: 5), true).CanPull);
    }

    [Fact]
    public void Counts_go_on_the_button_labels()
    {
        var diverged = RemoteStanding.For(Branch(ahead: 2, behind: 3), true);

        Assert.Equal(" (2)", diverged.PushCountSuffix);
        Assert.Equal(" (3)", diverged.PullCountSuffix);

        var sync = RemoteStanding.For(Branch(), true);
        Assert.Equal(string.Empty, sync.PushCountSuffix);
        Assert.Equal(string.Empty, sync.PullCountSuffix);
    }

    [Fact]
    public void Every_state_says_something_except_the_one_with_nothing_to_say()
    {
        // A badge slot that is sometimes blank for a reason nobody stated is how the old
        // rendering hid "no upstream" behind an empty string.
        BranchInfo[] all =
        [
            Branch(upstream: null, unborn: true),
            Branch(upstream: null, detached: true),
            Branch(upstream: null),
            Branch(),
            Branch(ahead: 1),
            Branch(behind: 1),
            Branch(ahead: 1, behind: 1),
        ];

        foreach (var branch in all)
        {
            foreach (var hasRemote in new[] { true, false })
            {
                var standing = RemoteStanding.For(branch, hasRemote);
                Assert.False(string.IsNullOrWhiteSpace(standing.Headline),
                             $"{standing.State} has no sentence");
                Assert.False(string.IsNullOrWhiteSpace(standing.BrushKey),
                             $"{standing.State} has no brush");
            }
        }
    }
}
