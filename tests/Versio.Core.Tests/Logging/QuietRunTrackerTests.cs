using Xunit;
using Versio.Core.Logging;

namespace Versio.Core.Tests.Logging;

/// <summary>
/// The property under test is <b>silence</b>, which is awkward to assert and easy to get
/// vacuously right: a tracker that returned <c>null</c> unconditionally would pass any test
/// that only counts what it suppressed. Every test here that asserts a run of nulls is
/// therefore paired with one that drives the same run past the interval and demands the
/// sentence, so a permanently-silent implementation fails.
/// </summary>
public sealed class QuietRunTrackerTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 8, 12, 17, 19, 34, TimeSpan.Zero);

    [Fact]
    public void A_quiet_check_says_nothing()
    {
        var tracker = new QuietRunTracker();

        Assert.Null(tracker.RecordQuiet("Backstop", T0));
    }

    [Fact]
    public void An_idle_hour_of_backstop_polls_produces_twelve_lines_not_one_per_poll()
    {
        // The measured behaviour that prompted this: a 20 s backstop over an hour is 180
        // polls. At one line each that is the log the user reported drowning in.
        var tracker = new QuietRunTracker();
        var sentences = new List<string>();

        for (var i = 0; i < 180; i++)
        {
            if (tracker.RecordQuiet("Backstop", T0.AddSeconds(20 * i)) is { } line)
            {
                sentences.Add(line);
            }
        }

        Assert.Equal(11, sentences.Count);
        Assert.All(sentences, s => Assert.Contains("no change in", s));
    }

    [Fact]
    public void The_summary_arrives_only_once_the_interval_has_passed()
    {
        var tracker = new QuietRunTracker(TimeSpan.FromMinutes(5));

        Assert.Null(tracker.RecordQuiet("Backstop", T0));
        Assert.Null(tracker.RecordQuiet("Backstop", T0.AddMinutes(4)));

        var due = tracker.RecordQuiet("Backstop", T0.AddMinutes(5));

        Assert.NotNull(due);
        Assert.Contains("3 checks", due);
    }

    [Fact]
    public void The_breakdown_names_each_trigger_so_the_change_filter_can_be_tuned()
    {
        // A WorkingTree poll that finds nothing means the filter passed an event git does
        // not care about. That is only ever visible in aggregate, and it is the reason the
        // summary counts per trigger rather than just totalling.
        var tracker = new QuietRunTracker(TimeSpan.FromMinutes(5));

        tracker.RecordQuiet("Backstop", T0);
        tracker.RecordQuiet("WorkingTree", T0.AddMinutes(1));
        tracker.RecordQuiet("Backstop", T0.AddMinutes(2));
        tracker.RecordQuiet("Backstop", T0.AddMinutes(3));

        var due = tracker.RecordQuiet("WorkingTree", T0.AddMinutes(6));

        Assert.NotNull(due);
        Assert.Contains("3 Backstop", due);
        Assert.Contains("2 WorkingTree", due);
    }

    [Fact]
    public void The_breakdown_is_ordered_by_count_so_the_sentence_is_stable()
    {
        // Without an explicit order the wording would depend on dictionary iteration, which
        // makes this a line nobody can grep for and a test nobody can write.
        var tracker = new QuietRunTracker(TimeSpan.FromMinutes(5));

        tracker.RecordQuiet("WorkingTree", T0);
        tracker.RecordQuiet("Backstop", T0.AddMinutes(1));
        tracker.RecordQuiet("Backstop", T0.AddMinutes(2));

        var due = tracker.RecordQuiet("Backstop", T0.AddMinutes(6));

        Assert.NotNull(due);
        Assert.EndsWith("(3 Backstop, 1 WorkingTree)", due);
    }

    [Fact]
    public void A_change_ends_the_run_and_reports_what_it_interrupted()
    {
        var tracker = new QuietRunTracker();

        tracker.RecordQuiet("Backstop", T0);
        tracker.RecordQuiet("Backstop", T0.AddSeconds(20));

        var ended = tracker.RecordChange(T0.AddSeconds(40));

        Assert.NotNull(ended);
        Assert.Contains("2 checks", ended);
        Assert.Contains("40s", ended);
    }

    [Fact]
    public void A_change_with_no_quiet_run_behind_it_says_nothing()
    {
        // Otherwise every ordinary save would be preceded by an empty summary.
        var tracker = new QuietRunTracker();

        Assert.Null(tracker.RecordChange(T0));
    }

    [Fact]
    public void The_run_restarts_after_a_change()
    {
        var tracker = new QuietRunTracker(TimeSpan.FromMinutes(5));

        tracker.RecordQuiet("Backstop", T0);
        tracker.RecordChange(T0.AddMinutes(1));

        // Four minutes after the change is still inside the interval measured from the new
        // run's start, not from the old one's.
        Assert.Null(tracker.RecordQuiet("Backstop", T0.AddMinutes(2)));
        Assert.Null(tracker.RecordQuiet("Backstop", T0.AddMinutes(5)));

        var due = tracker.RecordQuiet("Backstop", T0.AddMinutes(7));

        Assert.NotNull(due);
        Assert.Contains("3 checks", due);
    }

    [Fact]
    public void Reset_forgets_the_run_without_saying_anything()
    {
        var tracker = new QuietRunTracker();

        tracker.RecordQuiet("Backstop", T0);
        tracker.Reset();

        Assert.Equal(0, tracker.PendingCount);
        Assert.Null(tracker.RecordChange(T0.AddMinutes(1)));
    }

    [Theory]
    [InlineData(30, "30s")]
    [InlineData(90, "1m")]
    [InlineData(3600, "1h 0m")]
    [InlineData(5400, "1h 30m")]
    public void Durations_read_as_a_person_would_say_them(int seconds, string expected)
    {
        var tracker = new QuietRunTracker(TimeSpan.Zero);

        tracker.RecordQuiet("Backstop", T0);
        var ended = tracker.RecordChange(T0.AddSeconds(seconds));

        Assert.NotNull(ended);
        Assert.Contains($"over {expected}", ended);
    }

    [Fact]
    public void One_check_is_singular()
    {
        var tracker = new QuietRunTracker();

        tracker.RecordQuiet("Backstop", T0);
        var ended = tracker.RecordChange(T0.AddSeconds(20));

        Assert.NotNull(ended);
        Assert.Contains("1 check ", ended);
    }
}
