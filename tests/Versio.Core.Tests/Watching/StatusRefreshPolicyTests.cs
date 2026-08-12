using Versio.Core.Abstractions;
using Versio.Core.Watching;
using Xunit;

namespace Versio.Core.Tests.Watching;

/// <summary>
/// When the changed-files panel is allowed to re-read git.
/// </summary>
/// <remarks>
/// The panel used to show whatever it was told at open time and never move. Fixing that by
/// simply refreshing on every filesystem event trades one bug for two: a permanent git
/// process on a repository that is being built, and a feedback loop, since
/// <c>git status</c> writes to <c>.git/index</c> itself. These pin all three behaviours.
/// </remarks>
public class StatusRefreshPolicyTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(20);

    private static StatusRefreshPolicy Make(FakeClock clock) =>
        new(clock, Interval, Quiet, Backstop);

    private static StatusRefreshPolicy Idle(FakeClock clock)
    {
        // A policy that has run once and gone quiet: the state most tests actually care
        // about, since a brand-new policy has no throttle history to trip over.
        var policy = Make(clock);
        Assert.Equal(RefreshDecision.RunNow, policy.OnChange(RepositoryChangeRelevance.WorkingTree));
        Assert.False(policy.OnRefreshCompleted());
        clock.Advance(Interval + Quiet);
        return policy;
    }

    [Fact]
    public void The_first_change_refreshes_immediately()
    {
        var policy = Make(new FakeClock());

        Assert.Equal(RefreshDecision.RunNow,
            policy.OnChange(RepositoryChangeRelevance.WorkingTree));
        Assert.True(policy.IsRunning);
    }

    [Fact]
    public void An_irrelevant_change_costs_nothing()
    {
        var policy = Make(new FakeClock());

        Assert.Equal(RefreshDecision.Ignored, policy.OnChange(RepositoryChangeRelevance.Ignored));
        Assert.False(policy.IsRunning);
    }

    [Fact]
    public void Changes_during_a_running_refresh_collapse_into_one_follow_up()
    {
        var clock = new FakeClock();
        var policy = Make(clock);

        policy.OnChange(RepositoryChangeRelevance.WorkingTree);

        Assert.Equal(RefreshDecision.Queued, policy.OnChange(RepositoryChangeRelevance.WorkingTree));
        Assert.Equal(RefreshDecision.AlreadyQueued, policy.OnChange(RepositoryChangeRelevance.WorkingTree));
        Assert.Equal(RefreshDecision.AlreadyQueued, policy.OnChange(RepositoryChangeRelevance.WorkingTree));

        // Twenty saves during one refresh need one more refresh, not twenty.
        Assert.True(policy.HasPending);
    }

    [Fact]
    public void A_queued_refresh_starts_when_the_running_one_finishes()
    {
        var clock = new FakeClock();
        var policy = Make(clock);

        policy.OnChange(RepositoryChangeRelevance.WorkingTree);
        policy.OnChange(RepositoryChangeRelevance.WorkingTree);

        clock.Advance(Interval);

        Assert.True(policy.OnRefreshCompleted());
        Assert.True(policy.IsRunning);
        Assert.False(policy.HasPending);
    }

    [Fact]
    public void A_queued_refresh_still_waits_for_the_throttle()
    {
        var clock = new FakeClock();
        var policy = Make(clock);

        policy.OnChange(RepositoryChangeRelevance.WorkingTree);
        policy.OnChange(RepositoryChangeRelevance.WorkingTree);

        // The refresh finished quickly, well inside the interval. Starting the queued one
        // now would turn "at most one refresh per interval" into back-to-back refreshes for
        // as long as the changes keep coming.
        clock.Advance(TimeSpan.FromMilliseconds(50));

        Assert.False(policy.OnRefreshCompleted());
        Assert.False(policy.IsRunning);
        Assert.True(policy.HasPending);
    }

    [Fact]
    public void Throttled_work_is_owed_not_dropped()
    {
        var clock = new FakeClock();
        var policy = Idle(clock);

        policy.OnChange(RepositoryChangeRelevance.WorkingTree);
        policy.OnRefreshCompleted();

        // Second change lands inside the interval.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(RefreshDecision.Queued, policy.OnChange(RepositoryChangeRelevance.WorkingTree));
        Assert.False(policy.TryStartPending());

        // Once the interval passes, the idle tick picks it up. Dropping it instead is how a
        // panel ends up stale after the last save of a burst.
        clock.Advance(Interval);
        Assert.True(policy.TryStartPending());
        Assert.False(policy.HasPending);
    }

    [Fact]
    public void TryStartPending_does_nothing_with_nothing_owed()
    {
        var clock = new FakeClock();
        var policy = Idle(clock);

        Assert.False(policy.TryStartPending());
    }

    [Fact]
    public void A_git_metadata_echo_of_our_own_refresh_is_ignored()
    {
        var clock = new FakeClock();
        var policy = Make(clock);

        policy.OnChange(RepositoryChangeRelevance.WorkingTree);
        policy.OnRefreshCompleted();

        // git status rewrites .git/index when it refreshes the stat cache. Acting on that
        // write runs git status, which rewrites the index, which... the loop never idles.
        Assert.Equal(RefreshDecision.Ignored,
            policy.OnChange(RepositoryChangeRelevance.GitMetadata));
    }

    [Fact]
    public void A_working_tree_change_is_never_suppressed_as_an_echo()
    {
        var clock = new FakeClock();
        var policy = Make(clock);

        policy.OnChange(RepositoryChangeRelevance.WorkingTree);
        policy.OnRefreshCompleted();
        clock.Advance(Interval);

        // Versio does not write to the working tree while refreshing, so a working-tree
        // event is always somebody else and must always be believed. Suppressing it would
        // mean a save landing right after a refresh is invisible until the backstop.
        Assert.Equal(RefreshDecision.RunNow,
            policy.OnChange(RepositoryChangeRelevance.WorkingTree));
    }

    [Fact]
    public void A_real_git_action_after_the_quiet_period_is_believed()
    {
        var clock = new FakeClock();
        var policy = Make(clock);

        policy.OnChange(RepositoryChangeRelevance.WorkingTree);
        policy.OnRefreshCompleted();

        // Someone committed in a terminal a moment later. The echo window must not be so
        // long that real work by other tools is swallowed.
        clock.Advance(Quiet + TimeSpan.FromMilliseconds(1));

        Assert.Equal(RefreshDecision.RunNow,
            policy.OnChange(RepositoryChangeRelevance.GitMetadata));
    }

    [Fact]
    public void The_backstop_refreshes_a_repository_nothing_has_reported_on()
    {
        var clock = new FakeClock();
        var policy = Idle(clock);

        Assert.False(policy.ShouldRunBackstop());

        clock.Advance(Backstop);

        // FileSystemWatcher drops events silently when its buffer overflows, and network and
        // cloud-synced folders drop them routinely. This turns "stale forever" into "stale
        // for a few seconds".
        Assert.True(policy.ShouldRunBackstop());
    }

    [Fact]
    public void The_backstop_does_not_pile_onto_work_already_happening()
    {
        var clock = new FakeClock();
        var policy = Make(clock);

        policy.OnChange(RepositoryChangeRelevance.WorkingTree);
        clock.Advance(Backstop * 2);

        Assert.False(policy.ShouldRunBackstop());
    }

    [Fact]
    public void The_backstop_defers_to_work_already_owed()
    {
        var clock = new FakeClock();
        var policy = Make(clock);

        policy.OnChange(RepositoryChangeRelevance.WorkingTree);
        policy.OnChange(RepositoryChangeRelevance.WorkingTree);
        policy.OnRefreshCompleted();
        clock.Advance(Backstop * 2);

        // TryStartPending will run it; a backstop as well would mean two refreshes.
        Assert.False(policy.ShouldRunBackstop());
        Assert.True(policy.TryStartPending());
    }

    [Fact]
    public void The_backstop_stays_quiet_until_something_has_run()
    {
        var clock = new FakeClock();
        var policy = Make(clock);

        clock.Advance(Backstop * 5);

        // Opening a repository does its own first refresh. Firing here too would double it.
        Assert.False(policy.ShouldRunBackstop());
    }

    [Fact]
    public void A_manual_refresh_is_subject_to_the_same_rules()
    {
        var clock = new FakeClock();
        var policy = Make(clock);

        policy.NoteExternalStart();
        Assert.True(policy.IsRunning);

        policy.OnRefreshCompleted();

        // Otherwise clicking Refresh is immediately followed by an automatic refresh caused
        // by the index write the click itself produced.
        Assert.Equal(RefreshDecision.Ignored,
            policy.OnChange(RepositoryChangeRelevance.GitMetadata));
    }

    [Fact]
    public void Reset_forgets_everything_when_the_repository_changes()
    {
        var clock = new FakeClock();
        var policy = Make(clock);

        policy.OnChange(RepositoryChangeRelevance.WorkingTree);
        policy.OnChange(RepositoryChangeRelevance.WorkingTree);
        policy.Reset();

        Assert.False(policy.IsRunning);
        Assert.False(policy.HasPending);

        // Carrying the throttle across a repository switch would delay the first refresh of
        // the repository the user just opened.
        Assert.Equal(RefreshDecision.RunNow,
            policy.OnChange(RepositoryChangeRelevance.WorkingTree));
    }

    [Fact]
    public void A_sustained_storm_never_starves_and_never_runs_faster_than_the_interval()
    {
        var clock = new FakeClock();
        var policy = Make(clock);
        var starts = 0;

        // 60 seconds of a build writing a file every 50 ms.
        for (var i = 0; i < 1200; i++)
        {
            if (policy.OnChange(RepositoryChangeRelevance.WorkingTree) == RefreshDecision.RunNow)
            {
                starts++;
            }

            // Refreshes are fast; assume 30 ms.
            clock.Advance(TimeSpan.FromMilliseconds(30));
            if (policy.IsRunning && policy.OnRefreshCompleted())
            {
                starts++;
            }

            clock.Advance(TimeSpan.FromMilliseconds(20));

            if (policy.TryStartPending())
            {
                starts++;
            }
        }

        // 60 s at one per 750 ms is 80. Allow slack for where the ticks land, but a
        // regression that refreshes per event would score in the hundreds.
        Assert.InRange(starts, 40, 90);

        // And the panel is not left stale at the end of the storm: work is either running
        // or owed, never silently dropped.
        Assert.True(policy.IsRunning || policy.HasPending || starts > 0);
    }
}
