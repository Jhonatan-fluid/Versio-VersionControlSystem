using System.Collections.ObjectModel;
using System.Windows.Threading;
using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Core.Notifications;

namespace Versio.App.Mvvm;

/// <summary>
/// One notification, dressed for the screen.
/// </summary>
/// <remarks>
/// A view model per card rather than binding the record directly, because the card needs two
/// things the record has no business knowing: which theme brush it wears, and how to remove
/// itself. Named as a brush <i>key</i> rather than a brush for the reason every other row in
/// this application is — the view model decides the meaning and the theme decides the paint.
/// </remarks>
public sealed class NotificationRow
{
    private readonly NotificationCenter _owner;

    public NotificationRow(Notification entry, NotificationCenter owner)
    {
        Entry = entry;
        _owner = owner;

        // Constructed here rather than in an initialiser because it closes over `this`, which
        // is not available until the constructor body. An initialiser would have to capture
        // something else and hand the wrong row back.
        DismissCommand = new RelayCommand(() => _owner.Dismiss(this));
    }

    public Notification Entry { get; }

    public string Headline => Entry.Headline;

    public string Detail => Entry.Detail;

    public bool HasDetail => Entry.Detail.Length > 0;

    /// <summary>Failures alone are dismissed by hand, so they alone show the control.</summary>
    /// <remarks>
    /// A close button on a card that is about to vanish anyway is a target that moves out from
    /// under the cursor — worse than no button.
    /// </remarks>
    public bool IsSticky => Entry.IsSticky;

    public string BrushKey => Entry.Severity switch
    {
        NotificationSeverity.Success => "Brush.Status.Added",
        NotificationSeverity.Warning => "Brush.Status.Modified",
        _ => "Brush.Status.Deleted",
    };

    public string Glyph => Entry.Severity switch
    {
        NotificationSeverity.Success => "Icon.Check",
        NotificationSeverity.Warning => "Icon.Warning",
        _ => "Icon.Error",
    };

    public RelayCommand DismissCommand { get; }

    /// <summary>The whole message, for the clipboard.</summary>
    public string ClipboardText => Entry.ToClipboardText();
}

/// <summary>
/// What Versio is currently telling the user, and the timer that takes it away again.
/// </summary>
/// <remarks>
/// <para>
/// <b>A thin shell over <see cref="NotificationLog"/>.</b> Every rule about lifetimes,
/// de-duplication and what survives a sweep lives in Core and is tested there against a fake
/// clock. What is here is the part that cannot be: an <see cref="ObservableCollection{T}"/> for
/// WPF to bind to, and a <see cref="DispatcherTimer"/> to call <see cref="NotificationLog.Prune"/>
/// on the UI thread.
/// </para>
/// <para>
/// <b>Why this exists at all.</b> <c>StatusSummary</c> was carrying two different things on one
/// property: durable state ("3 changed") and events ("Pushed."). They have different lifetimes,
/// and the collision was not theoretical — <c>PushAsync</c> set the message and then called
/// <c>RunRefreshAsync</c>, which overwrote it with the file count. A successful push produced no
/// visible confirmation at all, for as long as that code existed. Separating the channels means
/// the refresh cannot clobber the result, by construction rather than by ordering awaits
/// carefully and hoping the next caller does the same.
/// </para>
/// <para>
/// <b>The timer only runs while something can expire.</b> A sticky failure on screen with no
/// transient cards behind it needs no ticking, and neither does an idle application — so the
/// timer is started when a perishable notification arrives and stopped when the last one goes.
/// </para>
/// </remarks>
public sealed class NotificationCenter : ObservableObject, IDisposable
{
    /// <summary>
    /// How many cards are on screen before the rest become a count.
    /// </summary>
    /// <remarks>
    /// Three. The panel has to stay usable behind them, and a stack that can grow without
    /// limit turns a network outage into a wall of identical text — which is the state in
    /// which somebody most needs to reach the buttons underneath.
    /// </remarks>
    public const int VisibleLimit = 3;

    private readonly NotificationLog _log;
    private readonly IClock _clock;
    private readonly IVersioLogger _logger;
    private readonly DispatcherTimer? _timer;

    /// <summary>
    /// How often expiry is checked.
    /// </summary>
    /// <remarks>
    /// 250 ms. Fine enough that a four-second card does not visibly outstay it, coarse enough
    /// that an idle application is not waking sixty times a second to ask whether anything has
    /// happened.
    /// </remarks>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    public NotificationCenter(IClock clock, IVersioLogger logger, bool useTimer = true)
    {
        _clock = clock;
        _logger = logger;
        _log = new NotificationLog(clock);

        // No dispatcher in a plain test host, and none wanted: a test moves the clock and calls
        // Prune itself, which is the whole reason the rule lives in Core.
        if (useTimer && System.Windows.Application.Current is not null)
        {
            _timer = new DispatcherTimer { Interval = TickInterval };
            _timer.Tick += (_, _) => Prune();
        }
    }

    /// <summary>The cards on screen, newest first.</summary>
    public ObservableCollection<NotificationRow> Visible { get; } = [];

    /// <summary>How many are held but not shown.</summary>
    public int Overflow => Math.Max(0, _log.Count - VisibleLimit);

    public bool HasOverflow => Overflow > 0;

    public string OverflowText => Overflow == 1 ? "+1 more" : $"+{Overflow} more";

    public bool Any => _log.Count > 0;

    /// <summary>Something worked.</summary>
    public void Success(string headline, string detail = "") =>
        Post(NotificationSeverity.Success, headline, detail);

    /// <summary>It worked, with something worth reading once.</summary>
    public void Warning(string headline, string detail = "") =>
        Post(NotificationSeverity.Warning, headline, detail);

    /// <summary>It did not work. Stays until dismissed.</summary>
    public void Failure(string headline, string detail = "") =>
        Post(NotificationSeverity.Failure, headline, detail);

    /// <summary>
    /// Post, and mirror it into the log file at the matching level.
    /// </summary>
    /// <remarks>
    /// <b>Both, always.</b> The log panel and the notification must never disagree about what
    /// happened — a card the user dismissed and a log with no record of it is how a bug report
    /// arrives with nothing in it.
    /// </remarks>
    private void Post(NotificationSeverity severity, string headline, string detail)
    {
        _log.Post(severity, headline, detail);

        var line = detail.Length == 0 ? headline : $"{headline} — {detail}";
        _logger.Log(
            severity switch
            {
                NotificationSeverity.Success => LogLevel.Information,
                NotificationSeverity.Warning => LogLevel.Warning,
                _ => LogLevel.Error,
            },
            LogCategory.Git,
            line);

        Rebuild();
        StartTicking();
    }

    /// <summary>Drop whatever has expired. Called by the timer, and by tests directly.</summary>
    public void Prune()
    {
        if (_log.Prune(_clock.UtcNow).Count == 0)
        {
            StopIfNothingPerishable();
            return;
        }

        Rebuild();
        StopIfNothingPerishable();
    }

    /// <summary>Take one card away because the user pressed its close button.</summary>
    public void Dismiss(NotificationRow row)
    {
        if (_log.Dismiss(row.Entry))
        {
            Rebuild();
        }
    }

    /// <summary>Everything goes — the notifications belong to a repository that is closing.</summary>
    public void Clear()
    {
        _log.Clear();
        Rebuild();
        StopIfNothingPerishable();
    }

    private void StartTicking()
    {
        if (_timer is { IsEnabled: false } && _log.Live.Any(n => !n.IsSticky))
        {
            _timer.Start();
        }
    }

    private void StopIfNothingPerishable()
    {
        if (_timer is { IsEnabled: true } && !_log.Live.Any(n => !n.IsSticky))
        {
            _timer.Stop();
        }
    }

    /// <summary>
    /// Rebuild the bound collection from the log.
    /// </summary>
    /// <remarks>
    /// Rebuilt rather than reconciled, unlike the changed-files list, and the difference is that
    /// these rows carry no selection and no expansion state — nothing is lost by replacing them.
    /// At three visible items the cost is not worth a reconcile loop that could get the ordering
    /// subtly wrong.
    /// </remarks>
    private void Rebuild()
    {
        Visible.Clear();

        foreach (var entry in _log.Live.Take(VisibleLimit))
        {
            Visible.Add(new NotificationRow(entry, this));
        }

        OnPropertyChanged(nameof(Overflow));
        OnPropertyChanged(nameof(HasOverflow));
        OnPropertyChanged(nameof(OverflowText));
        OnPropertyChanged(nameof(Any));
    }

    public void Dispose() => _timer?.Stop();
}
