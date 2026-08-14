using Versio.App.Mvvm;
using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Core.Notifications;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The bound half of the notification surface: what is on screen, what overflows, and what the
/// log file records alongside it.
/// </summary>
/// <remarks>
/// <para>
/// The lifetime and de-duplication rules are proved in <c>NotificationLogTests</c> against a
/// fake clock, and are not re-proved here. What this covers is the part that only exists
/// because WPF does: the observable collection, the three-card limit, the overflow count, and
/// the mirroring into the logger.
/// </para>
/// <para>
/// Constructed with <c>useTimer: false</c>. A <see cref="System.Windows.Threading.DispatcherTimer"/>
/// in a test would make expiry a race against the scheduler; here the clock is moved and
/// <see cref="NotificationCenter.Prune"/> is called directly, which is exactly what the timer
/// does and is the reason the rule was put in Core in the first place.
/// </para>
/// </remarks>
public sealed class NotificationCenterTests
{
    private static (NotificationCenter Center, FakeClock Clock, InMemoryLogSink Sink) Build()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero));
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(clock).AddSink(sink);
        return (new NotificationCenter(clock, logger, useTimer: false), clock, sink);
    }

    [Fact]
    public void A_success_appears_and_then_goes()
    {
        var (center, clock, _) = Build();

        center.Success("Pushed.");
        Assert.Single(center.Visible);
        Assert.True(center.Any);

        clock.Advance(TimeSpan.FromSeconds(5));
        center.Prune();

        Assert.Empty(center.Visible);
        Assert.False(center.Any);
    }

    [Fact]
    public void A_failure_stays()
    {
        var (center, clock, _) = Build();

        center.Failure("Push failed.", "rejected — non-fast-forward");

        clock.Advance(TimeSpan.FromHours(8));
        center.Prune();

        // Eight hours is a working day. This is the card that must survive one.
        Assert.Single(center.Visible);
    }

    [Fact]
    public void A_success_survives_the_refresh_that_used_to_overwrite_it()
    {
        // THE REGRESSION TEST for the bug this phase exists to fix. PushAsync set
        // StatusSummary and then called RunRefreshAsync, which overwrote it with the file
        // count — so a successful push produced no visible confirmation at all. The channels
        // are separate now, so a status refresh cannot reach this one.
        var (center, _, _) = Build();

        center.Success("Pushed.", "Every direction lock is released.");

        // Whatever else the shell does to StatusSummary afterwards, this is untouched.
        Assert.Single(center.Visible);
        Assert.Equal("Pushed.", center.Visible[0].Headline);
    }

    [Fact]
    public void Only_three_cards_are_shown_and_the_rest_are_counted()
    {
        var (center, clock, _) = Build();

        foreach (var n in Enumerable.Range(1, 5))
        {
            center.Failure($"failure {n}");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // The panel underneath has to stay usable, which is exactly what somebody needs during
        // the network outage that produced five failures.
        Assert.Equal(NotificationCenter.VisibleLimit, center.Visible.Count);
        Assert.True(center.HasOverflow);
        Assert.Equal(2, center.Overflow);
        Assert.Equal("+2 more", center.OverflowText);
    }

    [Fact]
    public void One_held_back_reads_as_one()
    {
        var (center, clock, _) = Build();

        foreach (var n in Enumerable.Range(1, 4))
        {
            center.Failure($"failure {n}");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal("+1 more", center.OverflowText);
    }

    [Fact]
    public void Nothing_overflows_below_the_limit()
    {
        var (center, _, _) = Build();
        center.Success("Fetched.");

        Assert.False(center.HasOverflow);
        Assert.Equal(0, center.Overflow);
    }

    [Fact]
    public void Newest_is_on_top()
    {
        var (center, clock, _) = Build();

        center.Success("first");
        clock.Advance(TimeSpan.FromSeconds(1));
        center.Success("second");

        Assert.Equal("second", center.Visible[0].Headline);
    }

    [Fact]
    public void Every_notification_is_mirrored_into_the_log()
    {
        var (center, _, sink) = Build();

        center.Success("Pushed.");
        center.Warning("Nothing to merge.");
        center.Failure("Push failed.", "rejected — non-fast-forward");

        // A card the user dismissed and a log with no record of it is how a bug report arrives
        // with nothing in it. Both, always.
        var lines = sink.Snapshot().Select(e => e.Message).ToList();

        Assert.Contains(lines, l => l.Contains("Pushed.", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Nothing to merge.", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("non-fast-forward", StringComparison.Ordinal));
    }

    [Fact]
    public void The_log_level_matches_the_severity()
    {
        var (center, _, sink) = Build();

        center.Success("Pushed.");
        center.Failure("Push failed.");

        Assert.Equal(LogLevel.Information,
                     sink.Snapshot().First(e => e.Message.Contains("Pushed.")).Level);
        Assert.Equal(LogLevel.Error,
                     sink.Snapshot().First(e => e.Message.Contains("Push failed.")).Level);
    }

    [Fact]
    public void Only_the_sticky_cards_offer_a_dismiss_control()
    {
        var (center, _, _) = Build();

        center.Success("Pushed.");
        Assert.False(center.Visible[0].IsSticky);

        center.Failure("Push failed.");

        // A close button on something about to vanish is a target that moves out from under
        // the cursor.
        Assert.True(center.Visible[0].IsSticky);
    }

    [Fact]
    public void Dismissing_a_card_removes_it()
    {
        var (center, _, _) = Build();
        center.Failure("Push failed.");

        center.Visible[0].DismissCommand.Execute(null);

        Assert.Empty(center.Visible);
    }

    [Fact]
    public void Clearing_takes_everything_including_failures()
    {
        var (center, _, _) = Build();
        center.Failure("Push failed.");
        center.Success("Fetched.");

        center.Clear();

        // These belong to a repository that is closing. Keeping the failure would attach it to
        // whatever is opened next.
        Assert.Empty(center.Visible);
        Assert.False(center.Any);
    }

    [Fact]
    public void A_failure_carries_the_whole_message_to_the_clipboard()
    {
        var (center, _, _) = Build();
        center.Failure("Push failed.", "rejected — non-fast-forward");

        var text = center.Visible[0].ClipboardText;

        Assert.Contains("Push failed.", text);
        Assert.Contains("non-fast-forward", text);
    }

    [Fact]
    public void Severity_chooses_the_brush_and_the_glyph()
    {
        var (center, _, _) = Build();

        center.Success("ok");
        Assert.Equal("Brush.Status.Added", center.Visible[0].BrushKey);
        Assert.Equal("Icon.Check", center.Visible[0].Glyph);

        center.Failure("bad");
        Assert.Equal("Brush.Status.Deleted", center.Visible[0].BrushKey);
        Assert.Equal("Icon.Error", center.Visible[0].Glyph);
    }

    [Fact]
    public void A_card_with_no_detail_says_so()
    {
        var (center, _, _) = Build();
        center.Success("Pushed.");

        Assert.False(center.Visible[0].HasDetail);
    }
}
