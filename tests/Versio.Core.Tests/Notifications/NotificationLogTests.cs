using Versio.Core.Notifications;
using Versio.Core.Abstractions;
using Xunit;

namespace Versio.Core.Tests.Notifications;

/// <summary>
/// What gets said, for how long, and what survives a sweep.
/// </summary>
/// <remarks>
/// Every lifetime here is asserted by moving a fake clock rather than by waiting, which is the
/// entire reason expiry is a rule taking a timestamp instead of a timer owning one. The
/// behaviour that matters most is the one that looks like an omission: a failure has no expiry
/// at all, and no amount of elapsed time removes it.
/// </remarks>
public sealed class NotificationLogTests
{
    private static (NotificationLog Log, FakeClock Clock) Build(int capacity = 24)
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero));
        return (new NotificationLog(clock, capacity), clock);
    }

    [Fact]
    public void A_success_expires_after_four_seconds()
    {
        var (log, clock) = Build();
        log.Post(NotificationSeverity.Success, "Pushed.");

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(log.Prune(clock.UtcNow));
        Assert.Single(log.Live);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(log.Prune(clock.UtcNow));
        Assert.Empty(log.Live);
    }

    [Fact]
    public void A_warning_lasts_twice_as_long_as_a_success()
    {
        var (log, clock) = Build();
        log.Post(NotificationSeverity.Warning, "Pulled — nothing to merge.");

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(log.Prune(clock.UtcNow));

        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Single(log.Prune(clock.UtcNow));
    }

    [Fact]
    public void A_failure_never_expires()
    {
        var (log, clock) = Build();
        log.Post(NotificationSeverity.Failure, "Push failed.", "rejected — non-fast-forward");

        // A failed push that quietly vanished is how somebody goes home believing their work is
        // on the remote. A day of elapsed time must not remove it.
        clock.Advance(TimeSpan.FromDays(1));

        Assert.Empty(log.Prune(clock.UtcNow));
        Assert.Single(log.Live);
        Assert.True(log.Live[0].IsSticky);
    }

    [Fact]
    public void The_same_headline_refreshes_rather_than_stacking()
    {
        var (log, clock) = Build();
        log.Post(NotificationSeverity.Failure, "Push failed.");

        clock.Advance(TimeSpan.FromSeconds(1));
        log.Post(NotificationSeverity.Failure, "Push failed.");
        log.Post(NotificationSeverity.Failure, "Push failed.");

        // Three retries against an unreachable remote is one problem. Three identical cards
        // describe it no better and push everything else off the screen.
        Assert.Single(log.Live);
    }

    [Fact]
    public void Reposting_restarts_the_lifetime()
    {
        var (log, clock) = Build();
        log.Post(NotificationSeverity.Success, "Fetched.");

        clock.Advance(TimeSpan.FromSeconds(3));
        log.Post(NotificationSeverity.Success, "Fetched.");

        // Would have expired at +4s from the first post; the second post moved it.
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(log.Prune(clock.UtcNow));
        Assert.Single(log.Live);
    }

    [Fact]
    public void The_same_text_at_a_different_severity_is_a_different_notification()
    {
        var (log, _) = Build();
        log.Post(NotificationSeverity.Success, "Pulled.");
        log.Post(NotificationSeverity.Failure, "Pulled.");

        // Contrived text, real rule: de-duplication must not let a success swallow a failure
        // that happens to be worded the same way.
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void Newest_is_first()
    {
        var (log, clock) = Build();
        log.Post(NotificationSeverity.Success, "first");
        clock.Advance(TimeSpan.FromSeconds(1));
        log.Post(NotificationSeverity.Success, "second");

        Assert.Equal("second", log.Live[0].Headline);
        Assert.Equal("first", log.Live[1].Headline);
    }

    [Fact]
    public void The_oldest_is_dropped_past_capacity()
    {
        var (log, clock) = Build(capacity: 3);

        foreach (var n in Enumerable.Range(1, 5))
        {
            log.Post(NotificationSeverity.Failure, $"failure {n}");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(3, log.Count);
        Assert.Equal("failure 5", log.Live[0].Headline);
        Assert.Equal("failure 3", log.Live[2].Headline);
    }

    [Fact]
    public void Prune_returns_what_it_removed()
    {
        var (log, clock) = Build();
        log.Post(NotificationSeverity.Success, "Fetched.");
        log.Post(NotificationSeverity.Failure, "Push failed.");

        clock.Advance(TimeSpan.FromSeconds(5));
        var gone = log.Prune(clock.UtcNow);

        // Returned rather than merely removed, so a bound collection can mirror the removal
        // instead of being rebuilt — a rebuild drops whatever the user was reading.
        Assert.Single(gone);
        Assert.Equal("Fetched.", gone[0].Headline);
        Assert.Single(log.Live);
    }

    [Fact]
    public void Clearing_for_a_repository_change_takes_the_failures_too()
    {
        var (log, _) = Build();
        log.Post(NotificationSeverity.Failure, "Push failed.");

        log.Clear();

        // These notifications are ABOUT a repository that is no longer open. Keeping them
        // would attach them to whatever is opened next.
        Assert.Empty(log.Live);
    }

    [Fact]
    public void Dismissing_one_removes_only_that_one()
    {
        var (log, _) = Build();
        var first = log.Post(NotificationSeverity.Failure, "Push failed.");
        log.Post(NotificationSeverity.Failure, "Pull failed.");

        Assert.True(log.Dismiss(first));
        Assert.Single(log.Live);
        Assert.Equal("Pull failed.", log.Live[0].Headline);
    }

    [Fact]
    public void A_failure_carries_gits_own_words_to_the_clipboard()
    {
        var (log, _) = Build();
        var entry = log.Post(NotificationSeverity.Failure, "Push failed.",
                             "rejected — non-fast-forward");

        var text = entry.ToClipboardText();

        // What gets pasted into a message asking for help is the whole thing: the headline
        // alone loses git's reason, the detail alone loses what was being attempted.
        Assert.Contains("Push failed.", text);
        Assert.Contains("non-fast-forward", text);
    }

    [Fact]
    public void A_notification_with_no_detail_copies_as_one_line()
    {
        var (log, _) = Build();
        var entry = log.Post(NotificationSeverity.Success, "Pushed.");

        Assert.Equal("Pushed.", entry.ToClipboardText());
    }

    [Fact]
    public void Capacity_is_never_zero_however_it_is_asked_for()
    {
        // A log that keeps nothing is a log that silently swallows failures.
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var log = new NotificationLog(clock, capacity: 0);

        log.Post(NotificationSeverity.Failure, "Push failed.");

        Assert.Single(log.Live);
    }
}
