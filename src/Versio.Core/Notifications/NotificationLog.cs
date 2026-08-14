using Versio.Core.Abstractions;

namespace Versio.Core.Notifications;

/// <summary>
/// What is currently worth telling the user, and for how long.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure and clocked by the caller</b>, like every other policy in this assembly. Expiry is a
/// rule about time, not about timers: <see cref="Prune"/> is given the moment and says what has
/// gone. The App layer owns the <c>DispatcherTimer</c> that calls it, and holds nothing else —
/// so every lifetime rule below is testable without waiting four seconds for a card to vanish.
/// </para>
/// <para>
/// <b>Lifetimes are per severity, not one timer for all.</b> A success is confirmation of
/// something the user just watched happen and needs to be seen rather than read twice. A
/// failure is the opposite: it is the thing they must not miss, so it stays until dismissed.
/// Giving both the same four seconds would either nag about successes or lose failures, and
/// losing failures is how work goes unpushed overnight.
/// </para>
/// <para>
/// <b>Newest first, and capped.</b> A run of failures during a network outage would otherwise
/// fill the screen with cards nobody can act on. The cap is on what is <i>kept</i>, not only on
/// what is shown, so the oldest genuinely goes rather than lurking behind a roll-up forever.
/// </para>
/// </remarks>
public sealed class NotificationLog(IClock clock, int capacity = 24)
{
    /// <summary>A success is confirmation, not news. Long enough to be seen, short enough not
    /// to become furniture.</summary>
    public static readonly TimeSpan SuccessLifetime = TimeSpan.FromSeconds(4);

    /// <summary>Twice a success: it worked, but there is a sentence to read.</summary>
    public static readonly TimeSpan WarningLifetime = TimeSpan.FromSeconds(8);

    private readonly List<Notification> _live = [];

    /// <summary>How many are held before the oldest is dropped.</summary>
    public int Capacity { get; } = Math.Max(1, capacity);

    /// <summary>Everything currently live, newest first.</summary>
    public IReadOnlyList<Notification> Live => _live;

    public int Count => _live.Count;

    /// <summary>
    /// Say something, or refresh what is already being said.
    /// </summary>
    /// <remarks>
    /// <b>An identical headline refreshes rather than stacks.</b> A push retried three times
    /// against an unreachable remote is one problem, and three identical cards describe it no
    /// better than one — they only push everything else off the screen. The refresh restarts
    /// the lifetime, because the event genuinely just happened again.
    /// </remarks>
    public Notification Post(NotificationSeverity severity, string headline, string detail = "")
    {
        var now = clock.UtcNow;

        var entry = new Notification
        {
            Severity = severity,
            Headline = headline,
            Detail = detail,
            PostedUtc = now,
            ExpiresUtc = severity switch
            {
                NotificationSeverity.Success => now + SuccessLifetime,
                NotificationSeverity.Warning => now + WarningLifetime,

                // Failures never expire. This is the whole reason the lifetime is a function of
                // severity rather than a constant.
                _ => null,
            },
        };

        var existing = _live.FindIndex(
            n => n.Severity == severity && string.Equals(n.Headline, headline, StringComparison.Ordinal));

        if (existing >= 0)
        {
            _live.RemoveAt(existing);
        }

        _live.Insert(0, entry);

        while (_live.Count > Capacity)
        {
            _live.RemoveAt(_live.Count - 1);
        }

        return entry;
    }

    /// <summary>Remove everything that has expired by <paramref name="now"/>.</summary>
    /// <returns>What was removed, so a caller can mirror the removal into a bound collection
    /// without rebuilding it — which would drop the user's scroll position and any card they
    /// were part-way through reading.</returns>
    public IReadOnlyList<Notification> Prune(DateTimeOffset now)
    {
        var gone = _live.Where(n => n.HasExpired(now)).ToList();

        foreach (var entry in gone)
        {
            _live.Remove(entry);
        }

        return gone;
    }

    /// <summary>Take one away because the user said so.</summary>
    public bool Dismiss(Notification entry) => _live.Remove(entry);

    /// <summary>Everything goes, including failures. For closing a repository.</summary>
    /// <remarks>
    /// These notifications are <i>about</i> a repository that is no longer open, so keeping the
    /// failures would attach them to whatever is opened next.
    /// <para>
    /// A <c>DismissTransient</c> sat beside this, sparing the sticky cards so a "dismiss all"
    /// could not sweep away a failed push unread. It was a good rule for a button nobody
    /// built: the panel dismisses one card at a time. Removed rather than kept warm - if a
    /// dismiss-all ever appears, that reasoning is in this comment and in the history.
    /// </para>
    /// </remarks>
    public void Clear() => _live.Clear();
}
