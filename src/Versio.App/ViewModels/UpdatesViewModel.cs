using System.Diagnostics;
using Versio.App.Mvvm;
using Versio.Core.Logging;
using Versio.Core.Updates;
using Versio.Infrastructure.Logging;

namespace Versio.App.ViewModels;

/// <summary>
/// Whether a newer Versio is published, and getting the user onto it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked twice, answered differently.</b> At launch the check is the application's idea, so
/// it is quiet: a failure is one warning in the log, "up to date" says nothing, and a version
/// the user has already answered Later to is not asked about again. From the gear menu it is
/// the user's idea, so every outcome is answered - including "you are on the latest", and
/// including the version they said Later to, because they are now asking for it.
/// </para>
/// <para>
/// <b>It never closes the window itself.</b> After the download it raises
/// <see cref="RestartRequested"/>, and the window closes through the same guard as the X
/// button. An import halfway through a project file is exactly as unsafe to interrupt for an
/// update as for anything else, and the close guard is the one place that already knows how to
/// ask Stop, Wait or Don't close. If the user picks Don't close, the update is not lost: it is
/// downloaded, and it installs whenever Versio next exits (<see cref="RestartDeclined"/>).
/// </para>
/// <para>
/// <b>Once the user says Update, the window is theirs no longer, and it says so.</b> The first
/// installed update (0.4.9, 2026-10-27) downloaded behind a one-line status under the gear
/// menu, which had closed: the user clicked Update and restart and saw nothing at all until
/// the window vanished. The update now runs under <see cref="Stage"/>, which the window draws
/// as a dimmed overlay with the progress, what happens next, and Cancel - blocking, because
/// there is nothing to do in a Versio that is about to close, and a click on a panel that is
/// about to disappear is a click that will be lost.
/// </para>
/// <para>
/// <b>Running work is asked about before the download, not after it.</b> The close guard
/// would ask anyway when the window closes, but by then the user has watched a minute of
/// progress bar over an export they did not know would hold the restart. Asking first means
/// the overlay never locks the screen over work that still has to finish; it shows "waiting
/// for the current operation" instead. The close guard still runs at the close, for whatever
/// started in between.
/// </para>
/// <para>
/// Constructed by the shell, which owns the notification list, the prompts and the settings
/// file, and hands this class delegates rather than itself - the same arrangement as
/// <see cref="GitAuthViewModel"/>.
/// </para>
/// </remarks>
public sealed class UpdatesViewModel : ObservableObject
{
    private readonly VersioLogger _logger;
    private readonly IUpdateSource _source;
    private readonly NotificationCenter _notifications;
    private readonly Func<IUserPrompts> _prompts;
    private readonly Func<string> _dismissedVersion;
    private readonly Func<string, Task> _rememberDismissed;
    private readonly Func<IDisposable> _askingSomething;
    private readonly Func<Action, CancellationToken, Task<bool>> _clearRunningWork;

    private bool _checking;
    private string _status = string.Empty;
    private string? _downloadedVersion;
    private bool _restartWanted;

    private UpdateStage _stage;
    private UpdateOffer? _pending;
    private CancellationTokenSource? _downloadCancel;
    private int _percent;
    private string _failure = string.Empty;

    /// <param name="clearRunningWork">
    /// Asks about running work before the download - Stop, Wait or Don't close, the close
    /// guard's own question - and returns false for Don't close. Calls its action when it
    /// starts waiting, so the overlay can say what it is waiting for.
    /// </param>
    public UpdatesViewModel(
        VersioLogger logger,
        IUpdateSource source,
        NotificationCenter notifications,
        Func<IUserPrompts> prompts,
        Func<string> dismissedVersion,
        Func<string, Task> rememberDismissed,
        Func<IDisposable> askingSomething,
        Func<Action, CancellationToken, Task<bool>>? clearRunningWork = null)
    {
        _logger = logger;
        _source = source;
        _notifications = notifications;
        _prompts = prompts;
        _dismissedVersion = dismissedVersion;
        _rememberDismissed = rememberDismissed;
        _askingSomething = askingSomething;
        _clearRunningWork = clearRunningWork ?? ((_, _) => Task.FromResult(true));

        // An error handler of its own, because the outcomes the check expects are all turned
        // into a status and a notice already; anything that still escapes is a defect, and
        // it should say so in the log and on screen rather than reach the dispatcher.
        CheckNowCommand = new AsyncCommand(CheckNowAsync, () => !_checking, ex =>
        {
            _logger.Error(LogCategory.App, "the update check failed unexpectedly", ex);
            _notifications.Failure("Could not check for updates", ex.GetBaseException().Message);
        });

        CancelCommand = new RelayCommand(Cancel,
            () => _stage is UpdateStage.WaitingForWork or UpdateStage.Downloading);
        TryAgainCommand = new AsyncCommand(TryAgainAsync, () => _stage == UpdateStage.Failed, ex =>
        {
            _logger.Error(LogCategory.App, "the update retry failed unexpectedly", ex);
            SetStage(UpdateStage.Failed, ex.GetBaseException().Message);
        });
        CloseOverlayCommand = new RelayCommand(CloseOverlay, () => _stage == UpdateStage.Failed);
    }

    /// <summary>The gear menu's Check for updates.</summary>
    public AsyncCommand CheckNowCommand { get; }

    /// <summary>The overlay's Cancel, while waiting or downloading. Nothing is installed yet.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>The overlay's Try again, after a failed download.</summary>
    public AsyncCommand TryAgainCommand { get; }

    /// <summary>The overlay's Close, after a failed download.</summary>
    public RelayCommand CloseOverlayCommand { get; }

    /// <summary>Where the update the user accepted has got to; None when no overlay shows.</summary>
    public UpdateStage Stage => _stage;

    /// <summary>The overlay is up, and the window behind it takes no input.</summary>
    public bool IsOverlayOpen => _stage != UpdateStage.None;

    public bool IsDownloading => _stage == UpdateStage.Downloading;

    public bool IsRestarting => _stage == UpdateStage.Restarting;

    public bool IsFailed => _stage == UpdateStage.Failed;

    /// <summary>Waiting has no percentage to show; the bar runs instead.</summary>
    public bool IsIndeterminate => _stage == UpdateStage.WaitingForWork;

    public bool CanCancel => _stage is UpdateStage.WaitingForWork or UpdateStage.Downloading;

    /// <summary>The step strip: Download is current until it is done.</summary>
    public bool DownloadStepCurrent => _stage is UpdateStage.WaitingForWork or UpdateStage.Downloading
        or UpdateStage.Failed;

    public bool DownloadStepDone => _stage == UpdateStage.Restarting;

    public bool CloseStepCurrent => _stage == UpdateStage.Restarting;

    /// <summary>0-100, as the download reports it.</summary>
    public int Percent => _percent;

    /// <summary>The bar's fill, 0-1. A scale rather than a width, so the XAML needs no converter.</summary>
    public double ProgressFraction => _stage == UpdateStage.Restarting ? 1.0 : _percent / 100.0;

    public string PercentText => _stage == UpdateStage.Downloading ? $"{_percent}%" : string.Empty;

    /// <summary>
    /// "About 12 MB" - the size of what will be fetched, which is the delta when there is one.
    /// </summary>
    /// <remarks>
    /// Not "5 of 12 MB": Velopack reports one percentage across downloading and applying the
    /// deltas, so a byte count worked back from it would be a number that looks measured and
    /// is not.
    /// </remarks>
    public string SizeText => _pending is { DownloadBytes: > 0 } offer && _stage == UpdateStage.Downloading
        ? $"about {offer.DownloadBytes / 1048576.0:0.#} MB"
        : string.Empty;

    public string Headline => _stage switch
    {
        UpdateStage.Restarting => "Update downloaded",
        UpdateStage.Failed => "Couldn't download the update",
        _ => $"Updating to Versio {_pending?.Version}",
    };

    public string Detail => _stage switch
    {
        UpdateStage.WaitingForWork =>
            "Waiting for the current operation to finish. The download starts as soon as it does.",
        UpdateStage.Downloading =>
            "Downloading the update. When it finishes, Versio closes, installs it and reopens on its own.",
        UpdateStage.Restarting =>
            $"Versio is closing to install {_pending?.Version}. It reopens in a few seconds.",
        UpdateStage.Failed =>
            $"{_failure.TrimEnd('.')}. Nothing was installed; you're still on {_source.CurrentVersion}.",
        _ => string.Empty,
    };

    /// <summary>The line under the bar.</summary>
    public string Caption => _stage switch
    {
        UpdateStage.WaitingForWork => "Waiting…",
        UpdateStage.Restarting => "Closing Versio…",
        _ => string.Empty,
    };

    /// <summary>
    /// The window should close so the downloaded update can install.
    /// </summary>
    /// <remarks>
    /// An event rather than a call into the view, like <c>HandedOverToOtherWindow</c>: the
    /// window owns closing, and closing goes through its guard.
    /// </remarks>
    public event Action? RestartRequested;

    /// <summary>One line under the menu item: what the last check found, or the download's progress.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => _status.Length > 0;

    /// <summary>
    /// Whether the user asked to restart into the update and the close has not been refused.
    /// </summary>
    public bool RestartWanted => _restartWanted;

    /// <summary>The check at launch: quiet unless there is something to offer.</summary>
    /// <remarks>
    /// Called once the startup open has settled, never alongside it - opening a repository
    /// clears the notification list, and a modal over a half-drawn tree is a question asked
    /// over something still moving.
    /// </remarks>
    public Task CheckOnLaunchAsync(CancellationToken ct) => CheckAsync(manual: false, ct);

    /// <summary>The check from the gear menu: every outcome is answered.</summary>
    public Task CheckNowAsync()
    {
        _logger.Click("Settings.CheckForUpdates");
        return CheckAsync(manual: true, CancellationToken.None);
    }

    private async Task CheckAsync(bool manual, CancellationToken ct)
    {
        if (_checking)
        {
            return;
        }

        SetChecking(true);
        using var scope = _logger.BeginOperation(LoggerExtensions.NewOperationId());
        try
        {
            // Downloaded already, this session: there is nothing new to fetch, and asking
            // GitHub again would only find the same version. Offer the restart instead.
            if (manual && _downloadedVersion is { } ready)
            {
                await OfferAsync(new UpdateOffer(ready, string.Empty), ct).ConfigureAwait(true);
                return;
            }

            if (_source.Unavailable is { } why)
            {
                _logger.Info(LogCategory.App, $"update check skipped: {why}");
                Status = "Updates are off for this copy";
                if (manual)
                {
                    _notifications.Warning("Updates are off", Capitalise(why) + ".");
                }

                return;
            }

            _logger.Info(LogCategory.App,
                $"checking for updates ({(manual ? "asked" : "at launch")}; running {_source.CurrentVersion})");
            Status = "Checking for updates…";
            var stopwatch = Stopwatch.StartNew();

            var check = await _source.CheckAsync(ct).ConfigureAwait(true);
            var took = $"{stopwatch.ElapsedMilliseconds} ms";

            switch (check.Result)
            {
                case UpdateCheckResult.UpToDate:
                    _logger.Info(LogCategory.App, $"update check: {_source.CurrentVersion} is the latest ({took})");
                    Status = $"Up to date ({_source.CurrentVersion})";
                    if (manual)
                    {
                        _notifications.Success("Versio is up to date",
                            $"{_source.CurrentVersion} is the latest published version.");
                    }

                    return;

                case UpdateCheckResult.Unavailable:
                    _logger.Info(LogCategory.App, $"update check skipped: {check.Reason}");
                    Status = "Updates are off for this copy";
                    if (manual)
                    {
                        _notifications.Warning("Updates are off", Capitalise(check.Reason) + ".");
                    }

                    return;

                case UpdateCheckResult.Failed:
                    // A warning, not an error: nothing the user is doing has failed, and at
                    // launch they were not told a check was happening at all.
                    _logger.Warn(LogCategory.App, $"update check failed ({took}): {check.Reason}");
                    Status = "The last update check failed";
                    if (manual)
                    {
                        _notifications.Failure("Could not check for updates", check.Reason);
                    }

                    return;
            }

            var offer = check.Offer!;
            _logger.Info(LogCategory.App,
                $"update available: {offer.Version} (running {_source.CurrentVersion}; {took})");
            Status = $"Versio {offer.Version} is available";

            if (!manual && !UpdatePolicy.ShouldAskAtLaunch(offer, _dismissedVersion()))
            {
                _logger.Info(LogCategory.App,
                    $"update {offer.Version}: answered Later before, so not asking at launch");
                return;
            }

            await OfferAsync(offer, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _logger.Info(LogCategory.App, "update check cancelled");
            Status = string.Empty;
            SetStage(UpdateStage.None);
        }
        finally
        {
            SetChecking(false);
        }
    }

    private async Task OfferAsync(UpdateOffer offer, CancellationToken ct)
    {
        UpdateChoice? choice;
        using (_askingSomething())
        {
            choice = _prompts().OfferUpdate(new UpdatePrompt(
                _source.CurrentVersion, offer.Version, UpdatePolicy.NotesForPrompt(offer.NotesMarkdown)));
        }

        if (choice != UpdateChoice.UpdateNow)
        {
            _logger.Info(LogCategory.App, $"update {offer.Version}: later");
            await _rememberDismissed(offer.Version).ConfigureAwait(true);
            Status = $"Versio {offer.Version} is available";
            return;
        }

        _pending = offer;
        await DownloadThenRestartAsync(offer, ct).ConfigureAwait(true);
    }

    private async Task DownloadThenRestartAsync(UpdateOffer offer, CancellationToken ct)
    {
        if (_downloadedVersion != offer.Version)
        {
            // Linked rather than separate: the overlay's Cancel trips this one, and the app
            // shutting down trips the caller's - and only the first is the user changing
            // their mind, which is why Cancel also sets the flag that tells them apart.
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _downloadCancel = cancel;
            try
            {
                if (!await _clearRunningWork(() => SetStage(UpdateStage.WaitingForWork), cancel.Token)
                        .ConfigureAwait(true))
                {
                    // Don't close: not a Later. The user still wants the version, just not
                    // over this export, so nothing is remembered and the gear offers it again.
                    _logger.Info(LogCategory.App,
                        $"update {offer.Version}: put off, because work is running and the user chose to keep it");
                    SetStage(UpdateStage.None);
                    Status = $"Versio {offer.Version} is available";
                    return;
                }

                if (!await DownloadAsync(offer, cancel.Token).ConfigureAwait(true))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.Info(LogCategory.App, $"update {offer.Version}: cancelled by the user; nothing was installed");
                SetStage(UpdateStage.None);
                Status = $"Versio {offer.Version} is available";
                return;
            }
            finally
            {
                _downloadCancel = null;
            }
        }

        _restartWanted = true;
        SetStage(UpdateStage.Restarting);
        Status = $"Versio {offer.Version} is ready - closing to install it";
        _logger.Info(LogCategory.App, $"update {offer.Version}: asking the window to close and install");
        RestartRequested?.Invoke();
    }

    private async Task<bool> DownloadAsync(UpdateOffer offer, CancellationToken ct)
    {
        _logger.Info(LogCategory.App, $"update {offer.Version}: downloading" +
                                      (offer.DownloadBytes > 0 ? $" ({offer.DownloadBytes:N0} bytes)" : ""));
        Status = $"Downloading Versio {offer.Version}…";
        SetPercent(0);
        SetStage(UpdateStage.Downloading);
        var stopwatch = Stopwatch.StartNew();

        // A heartbeat at each quarter: a download is long enough on a site connection that a
        // log with nothing between "downloading" and the next line would read as a hang.
        var lastQuarter = 0;
        var progress = new Progress<int>(percent =>
        {
            Status = $"Downloading Versio {offer.Version}… {percent}%";
            SetPercent(percent);
            var quarter = percent / 25;
            if (quarter > lastQuarter && quarter < 4)
            {
                lastQuarter = quarter;
                _logger.Log(LogLevel.Information, LogCategory.App,
                    $"update {offer.Version}: {quarter * 25}% downloaded", isHeartbeat: true);
            }
        });

        try
        {
            await _source.DownloadAsync(offer, progress, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Log(LogLevel.Warning, LogCategory.App, $"update {offer.Version}: download failed", ex);
            Status = "The update download failed";

            // The overlay says it now, and Close takes the overlay away; the notice is what
            // is left afterwards to say it happened.
            _notifications.Failure($"Could not download Versio {offer.Version}", ex.GetBaseException().Message);
            SetStage(UpdateStage.Failed, ex.GetBaseException().Message);
            return false;
        }

        SetPercent(100);
        _downloadedVersion = offer.Version;
        _logger.Info(LogCategory.App,
            $"update {offer.Version}: downloaded in {stopwatch.Elapsed.TotalSeconds:0.0} s");
        return true;
    }

    private void Cancel()
    {
        if (_downloadCancel is not { } cancel)
        {
            return;
        }

        _logger.Click("Updates.Cancel");
        cancel.Cancel();
    }

    private Task TryAgainAsync()
    {
        _logger.Click("Updates.TryAgain");
        return _pending is { } offer
            ? DownloadThenRestartAsync(offer, CancellationToken.None)
            : Task.CompletedTask;
    }

    private void CloseOverlay()
    {
        _logger.Click("Updates.CloseFailed");
        SetStage(UpdateStage.None);
    }

    private void SetPercent(int percent)
    {
        if (_percent == percent)
        {
            return;
        }

        _percent = percent;
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(PercentText));
    }

    private void SetStage(UpdateStage stage, string failure = "")
    {
        _failure = failure;
        _stage = stage;

        // Every overlay property reads the stage, so they all move together. One list, so a
        // property added above and forgotten here shows as the stale text it would draw.
        foreach (var name in new[]
                 {
                     nameof(Stage), nameof(IsOverlayOpen), nameof(IsDownloading), nameof(IsRestarting),
                     nameof(IsFailed), nameof(IsIndeterminate), nameof(CanCancel),
                     nameof(DownloadStepCurrent), nameof(DownloadStepDone), nameof(CloseStepCurrent),
                     nameof(ProgressFraction), nameof(PercentText), nameof(SizeText), nameof(Headline),
                     nameof(Detail), nameof(Caption),
                 })
        {
            OnPropertyChanged(name);
        }

        CancelCommand.RaiseCanExecuteChanged();
        TryAgainCommand.RaiseCanExecuteChanged();
        CloseOverlayCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// The close the update asked for was refused - the user chose to keep working.
    /// </summary>
    /// <remarks>
    /// The download is kept and installs when Versio next exits, without restarting it then:
    /// that exit is the user closing the app, and opening it again behind their back would be
    /// the opposite of what they asked for.
    /// </remarks>
    public void RestartDeclined()
    {
        if (!_restartWanted)
        {
            return;
        }

        _restartWanted = false;
        SetStage(UpdateStage.None);
        _logger.Info(LogCategory.App,
            $"update {_downloadedVersion}: the close was cancelled; it installs when Versio exits");
        Status = $"Versio {_downloadedVersion} installs when you close Versio";
        _notifications.Warning("Update ready", $"Versio {_downloadedVersion} installs when you close Versio.");
    }

    /// <summary>Hand a downloaded update to the installer. Called on the way out.</summary>
    public void ApplyOnExit()
    {
        if (!_source.HasDownloadedUpdate)
        {
            return;
        }

        try
        {
            _logger.Info(LogCategory.App,
                $"installing update {_downloadedVersion} after exit" +
                (_restartWanted ? ", then starting it" : ""));
            _source.ApplyAfterExit(_restartWanted);
        }
        catch (Exception ex)
        {
            // Nothing to roll back: the installed copy is untouched, and the next launch's
            // check finds the same version again.
            _logger.Error(LogCategory.App, $"could not start the updater for {_downloadedVersion}", ex);
        }
    }

    private void SetChecking(bool value)
    {
        _checking = value;
        CheckNowCommand.RaiseCanExecuteChanged();
    }

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>Where an accepted update has got to, as the window's overlay draws it.</summary>
public enum UpdateStage
{
    /// <summary>No update under way; no overlay.</summary>
    None,

    /// <summary>The user chose Wait or Stop over running work, and it has not finished yet.</summary>
    WaitingForWork,

    /// <summary>Fetching the package. Cancel is still free: nothing is installed yet.</summary>
    Downloading,

    /// <summary>Downloaded; the window has been asked to close so the updater can run.</summary>
    Restarting,

    /// <summary>The download failed; Try again or Close.</summary>
    Failed,
}
