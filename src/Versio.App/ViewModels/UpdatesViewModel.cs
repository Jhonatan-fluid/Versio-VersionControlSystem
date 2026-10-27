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

    private bool _checking;
    private string _status = string.Empty;
    private string? _downloadedVersion;
    private bool _restartWanted;

    public UpdatesViewModel(
        VersioLogger logger,
        IUpdateSource source,
        NotificationCenter notifications,
        Func<IUserPrompts> prompts,
        Func<string> dismissedVersion,
        Func<string, Task> rememberDismissed,
        Func<IDisposable> askingSomething)
    {
        _logger = logger;
        _source = source;
        _notifications = notifications;
        _prompts = prompts;
        _dismissedVersion = dismissedVersion;
        _rememberDismissed = rememberDismissed;
        _askingSomething = askingSomething;

        // An error handler of its own, because the outcomes the check expects are all turned
        // into a status and a notice already; anything that still escapes is a defect, and
        // it should say so in the log and on screen rather than reach the dispatcher.
        CheckNowCommand = new AsyncCommand(CheckNowAsync, () => !_checking, ex =>
        {
            _logger.Error(LogCategory.App, "the update check failed unexpectedly", ex);
            _notifications.Failure("Could not check for updates", ex.GetBaseException().Message);
        });
    }

    /// <summary>The gear menu's Check for updates.</summary>
    public AsyncCommand CheckNowCommand { get; }

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

        if (_downloadedVersion != offer.Version)
        {
            if (!await DownloadAsync(offer, ct).ConfigureAwait(true))
            {
                return;
            }
        }

        _restartWanted = true;
        Status = $"Versio {offer.Version} is ready - closing to install it";
        _logger.Info(LogCategory.App, $"update {offer.Version}: asking the window to close and install");
        RestartRequested?.Invoke();
    }

    private async Task<bool> DownloadAsync(UpdateOffer offer, CancellationToken ct)
    {
        _logger.Info(LogCategory.App, $"update {offer.Version}: downloading");
        Status = $"Downloading Versio {offer.Version}…";
        var stopwatch = Stopwatch.StartNew();

        // A heartbeat at each quarter: a download is long enough on a site connection that a
        // log with nothing between "downloading" and the next line would read as a hang.
        var lastQuarter = 0;
        var progress = new Progress<int>(percent =>
        {
            Status = $"Downloading Versio {offer.Version}… {percent}%";
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
            _notifications.Failure($"Could not download Versio {offer.Version}", ex.GetBaseException().Message);
            return false;
        }

        _downloadedVersion = offer.Version;
        _logger.Info(LogCategory.App,
            $"update {offer.Version}: downloaded in {stopwatch.Elapsed.TotalSeconds:0.0} s");
        return true;
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
