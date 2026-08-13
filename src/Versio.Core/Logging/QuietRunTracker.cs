namespace Versio.Core.Logging;

/// <summary>
/// Turns a run of "nothing happened" checks into one periodic sentence.
/// </summary>
/// <remarks>
/// <para>
/// The git status backstop runs every 20 s whether or not anything moved, and each run used
/// to write three Debug lines — the command, its duration, and a verdict of
/// <c>status unchanged</c>. An idle app therefore produced about <b>540 lines an hour</b>,
/// none of which answered a question anybody asks, and the entries that matter scrolled out
/// of the panel behind them.
/// </para>
/// <para>
/// <b>The information in those lines is real, but it is not per-line.</b> What a reader
/// actually wants from them is "the watcher is alive and has seen nothing", which is one
/// sentence however many polls it covers. So the polls are counted and summarised on an
/// interval, and the counts are kept <i>per trigger</i>: a backstop poll finding nothing is
/// the definition of routine, while a working-tree poll finding nothing means the change
/// filter passed an event git does not care about — which is how
/// <c>RepositoryChangeFilter</c> would be tuned, and it is only visible in aggregate.
/// </para>
/// <para>
/// Deliberately not a log sink. A generic "collapse repeats" rule in the sink would also
/// have worked, and would have hidden the trigger breakdown that makes the summary worth
/// reading — the two lines differ in the one word that matters.
/// </para>
/// <para>
/// Pure, and clocked by the caller, so the interval behaviour is testable without waiting.
/// </para>
/// </remarks>
public sealed class QuietRunTracker(TimeSpan? summaryInterval = null)
{
    /// <summary>
    /// How long a quiet run goes unmentioned. Five minutes is chosen against the reader
    /// rather than against the poll rate: somebody scanning the panel wants enough
    /// punctuation to see the app is still running, and 12 lines an hour is punctuation
    /// where 540 is wallpaper.
    /// </summary>
    private readonly TimeSpan _interval = summaryInterval ?? TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, int> _byTrigger = new(StringComparer.Ordinal);

    private DateTimeOffset? _runStarted;
    private DateTimeOffset? _lastSummary;
    private int _count;

    /// <summary>How many checks the current quiet run covers. Zero when nothing is pending.</summary>
    public int PendingCount => _count;

    /// <summary>
    /// Record a check that found nothing.
    /// </summary>
    /// <returns>
    /// A sentence when one is due, or <c>null</c> — which is the answer on the overwhelming
    /// majority of calls, and the whole point.
    /// </returns>
    public string? RecordQuiet(string trigger, DateTimeOffset now)
    {
        _runStarted ??= now;
        _lastSummary ??= now;
        _count++;
        _byTrigger[trigger] = _byTrigger.GetValueOrDefault(trigger) + 1;

        if (now - _lastSummary < _interval)
        {
            return null;
        }

        var sentence = Describe(now);
        _lastSummary = now;
        return sentence;
    }

    /// <summary>
    /// Record a check that found something, ending the quiet run.
    /// </summary>
    /// <returns>
    /// A trailing summary of the run that just ended, or <c>null</c> when there was none.
    /// Worth emitting because it is the one moment the reader is looking: it says how long
    /// the app had been quiet before the thing they are reading about happened.
    /// </returns>
    public string? RecordChange(DateTimeOffset now)
    {
        if (_count == 0)
        {
            return null;
        }

        var sentence = Describe(now);
        Reset();
        return sentence;
    }

    /// <summary>Forget the current run without saying anything, e.g. on repository close.</summary>
    public void Reset()
    {
        _byTrigger.Clear();
        _runStarted = null;
        _lastSummary = null;
        _count = 0;
    }

    private string Describe(DateTimeOffset now)
    {
        var span = _runStarted is { } started ? now - started : TimeSpan.Zero;

        // Ordered by count, then by name, so the sentence is stable for a given set of
        // counts. A summary whose wording depends on dictionary iteration order would be
        // untestable by string comparison, and this is the kind of line somebody greps.
        var breakdown = string.Join(", ", _byTrigger
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Value} {p.Key}"));

        var checks = _count == 1 ? "check" : "checks";
        return $"no change in {_count} {checks} over {Humanize(span)} ({breakdown})";
    }

    /// <summary>
    /// A duration a person reads at a glance. Never fractional seconds: the reader is
    /// asking "roughly how long has this been quiet", not measuring anything.
    /// </summary>
    private static string Humanize(TimeSpan span) => span switch
    {
        { TotalSeconds: < 60 } => $"{(int)span.TotalSeconds}s",
        { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes}m",
        _ => $"{(int)span.TotalHours}h {span.Minutes}m",
    };
}
