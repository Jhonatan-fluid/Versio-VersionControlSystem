using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Versio.App.Mvvm;
using Versio.Core.Abstractions;
using Versio.Core.Diff;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Core.Sync;
using Versio.Core.Watching;
using Versio.Infrastructure.Git;
using Versio.Infrastructure.Logging;
using Versio.Infrastructure.Platform;
using Versio.Infrastructure.Watching;
// RepositoryLocator lives in Core; FileSystemRepositoryProbe is its Infrastructure half.

namespace Versio.App.ViewModels;

/// <summary>Which panel the left rail is showing.</summary>
/// <remarks>
/// <para>
/// The PLC panel was folded into the project view: it had become a row of buttons for
/// operations that now run on their own, and its object tree was answering the same question
/// as the changed-files list one pane to the left.
/// </para>
/// <para>
/// History went the other way, out of the git panel and into its own. The two were never one
/// task — staging and committing is about what is <i>about</i> to happen, and history is about
/// what already did — and sharing a 380 px column meant history got 150 pixels of it, which is
/// four rows. Splitting them is also what makes the history affordable to load lazily: a panel
/// nobody has opened can read nothing at all.
/// </para>
/// </remarks>
public enum ShellPanel
{
    /// <summary>The repository as CODESYS structures it, plus every other changed file.</summary>
    Project,

    /// <summary>Staging, commit, and the remote operations.</summary>
    Git,

    /// <summary>The commit log, with each commit's files underneath it.</summary>
    History,
}

/// <summary>An entry in the repository dropdown.</summary>
public sealed class RepoEntry(string path)
{
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));

    /// <summary>Parent directory, shown beneath the name so two repos with the same folder
    /// name are still distinguishable.</summary>
    public string Location { get; } = System.IO.Path.GetDirectoryName(path) ?? path;
}

/// <summary>An entry in the branch dropdown.</summary>
public sealed class BranchEntry(BranchInfo branch)
{
    public string Name { get; } = branch.Name;

    public bool IsCurrent { get; } = branch.IsCurrent;

    public string Tracking { get; } = branch.Upstream ?? "no upstream";

    public string AheadBehind { get; } =
        branch is { Ahead: 0, Behind: 0 } ? string.Empty : $"↑{branch.Ahead} ↓{branch.Behind}";
}

/// <summary>
/// One row in the changed-files list.
/// </summary>
/// <remarks>
/// Observable, and updated in place rather than replaced, because the list refreshes itself
/// now. Rebuilding the collection on every refresh would drop the user's selection and reset
/// the diff pane roughly once a second while they typed in their editor — which is a worse
/// experience than the stale panel this replaced.
/// </remarks>
public sealed class ChangeRow(FileChange change) : ObservableObject
{
    private FileChange _change = change;

    /// <summary>
    /// The row's identity. A path can change status but a row never changes path — a
    /// renamed file is a different row.
    /// </summary>
    public string Path => _change.Path;

    /// <summary>
    /// Adopt fresh git state. Returns true when anything visible moved, so the caller knows
    /// whether the open diff is now stale.
    /// </summary>
    public bool Update(FileChange fresh)
    {
        if (_change.Effective == fresh.Effective &&
            _change.IsStaged == fresh.IsStaged &&
            _change.OriginalPath == fresh.OriginalPath)
        {
            return false;
        }

        _change = fresh;
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(BrushKey));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(IsStaged));
        return true;
    }

    public string Glyph => ChangeGlyph.For(_change.Effective);

    public string BrushKey => ChangeGlyph.BrushFor(_change.Effective);

    public string Detail => _change.OriginalPath is null
        ? string.Empty
        : $"← {_change.OriginalPath}";

    public bool IsStaged => _change.IsStaged;

    /// <summary>
    /// What git says happened to this file, unglyphed.
    /// </summary>
    /// <remarks>
    /// The row renders itself through <see cref="Glyph"/> and <see cref="BrushKey"/>, so this
    /// exists for the one caller that has to <i>decide</i> rather than draw: discard, which
    /// must refuse an untracked file because git has no copy to restore it from. Reading the
    /// decision off the glyph — comparing against "?" — would tie behaviour to a string chosen
    /// for how it looks.
    /// </remarks>
    public ChangeKind Kind => _change.Effective;
}

/// <summary>
/// The shell. Opens a real repository and shows real git state.
/// </summary>
/// <remarks>
/// Deliberately wired to the real <see cref="GitCliClient"/> rather than sample data. A
/// mock shell proves nothing about whether the pieces fit; this one has already surfaced
/// the encoding and quoting bugs that sample data would have hidden.
/// </remarks>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly VersioLogger _logger;
    private readonly Func<string, GitCliClient> _gitFactory;
    private readonly RepositoryLocator _locator;
    private readonly IClock _clock;

    /// <summary>
    /// The UI thread, captured at construction.
    /// </summary>
    /// <remarks>
    /// Watcher callbacks arrive on threadpool threads, and touching an
    /// <see cref="ObservableCollection{T}"/> bound to a WPF list from one of those throws.
    /// Captured rather than taking a Dispatcher dependency so the view model still runs in a
    /// plain test host, where the context is null and everything runs inline.
    /// </remarks>
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    private readonly StatusRefreshPolicy _policy;

    /// <summary>
    /// Collapses the runs of refreshes that find nothing into one periodic line.
    /// </summary>
    /// <remarks>
    /// Owned here rather than by the logger because only this class knows what a check is:
    /// the sink sees text, and a generic repeat-collapse would lose the per-trigger counts
    /// that make the summary worth reading at all.
    /// </remarks>
    private readonly QuietRunTracker _quietRefreshes = new();

    private GitCliClient? _git;
    private RepositoryWatcher? _watcher;
    private string? _repositoryPath;
    private string _branchName = "—";
    private string _statusSummary = "No repository open";
    private string _aheadBehind = string.Empty;
    private string _selectedDiff = string.Empty;
    private bool _isBusy;
    private bool _showFullFile;
    private string? _fileLogDirectory;
    private string _watchState = string.Empty;
    private DateTimeOffset? _lastRefreshUtc;
    private string _lastRefreshText = string.Empty;
    private ShellPanel _activePanel = ShellPanel.Project;

    public ShellViewModel(VersioLogger logger, LogViewModel log,
                          Func<string, GitCliClient>? gitFactory = null,
                          IRepositoryProbe? probe = null,
                          IClock? clock = null)
    {
        _logger = logger;
        Log = log;
        _clock = clock ?? new SystemClock();
        Sync = new SyncViewModel(logger, _clock);
        History = new HistoryViewModel(logger);
        _gitFactory = gitFactory ?? (path => new GitCliClient(path, logger: logger));
        _locator = new RepositoryLocator(probe ?? new FileSystemRepositoryProbe());
        _policy = new StatusRefreshPolicy(_clock);

        OpenRepositoryCommand = new AsyncCommand(OpenRepositoryAsync, onError: ReportError);
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => Ready,
                                          onError: ReportError);

        BuildGitCommands();

        // Picking a row in the project tree shows it in the diff pane. One pane, whichever
        // way the row was reached.
        Sync.NodeSelected += ShowNode;

        // And the same for the history: the panel decides what was picked, this decides how
        // it is rendered. Keeping the render here is what stops a second diff pane growing
        // in the history panel the first time it needs something the shared one already does.
        History.CommitSelected += row => _ = ShowCommitAsync(row.Entry);
        History.FileSelected += row => _ = ShowCommitFileAsync(row);

        logger.Info(LogCategory.App, "Versio started");
    }

    public LogViewModel Log { get; }

    /// <summary>
    /// Whether anything is running, anywhere in the app.
    /// </summary>
    /// <remarks>
    /// Shared with the sync panel rather than duplicated, so one progress bar covers a git
    /// refresh and a 20-second CODESYS export alike. Two independent indicators would
    /// disagree the moment the two overlapped, which is exactly when the user is looking.
    /// </remarks>
    public BusyTracker Busy { get; } = new();

    /// <summary>The PLC sync panel — what Versio is for, as distinct from the git client.</summary>
    public SyncViewModel Sync { get; }

    /// <summary>The commit history, in its own panel and loaded only when it is shown.</summary>
    public HistoryViewModel History { get; }

    public ObservableCollection<ChangeRow> Changes { get; } = [];

    /// <summary>Most-recently-opened repositories, newest first. Backs the top-bar dropdown.</summary>
    public ObservableCollection<RepoEntry> RecentRepositories { get; } = [];

    public ObservableCollection<BranchEntry> Branches { get; } = [];

    public AsyncCommand OpenRepositoryCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    /// <summary>Reveal the log folder in Explorer, from the settings menu.</summary>
    /// <remarks>
    /// Always enabled, including when no repository is open and when the file sink failed to
    /// attach: the folder is where the user was told the logs are, and opening it to find it
    /// empty is a far better answer than a greyed-out button that explains nothing.
    /// </remarks>
    public RelayCommand OpenLogsFolderCommand { get; }

    private RepoEntry? _selectedRepository;
    private BranchEntry? _selectedBranch;
    private bool _suppressSelectionSideEffects;

    /// <summary>Selecting a different entry switches repository. Guarded, because
    /// repopulating the list also fires this setter and would recurse.</summary>
    public RepoEntry? SelectedRepository
    {
        get => _selectedRepository;
        set
        {
            if (!SetProperty(ref _selectedRepository, value) || _suppressSelectionSideEffects)
            {
                return;
            }

            if (value is not null && value.Path != _repositoryPath)
            {
                _ = OpenAsync(value.Path);
            }
        }
    }

    public BranchEntry? SelectedBranch
    {
        get => _selectedBranch;
        set
        {
            if (!SetProperty(ref _selectedBranch, value) || _suppressSelectionSideEffects)
            {
                return;
            }

            if (value is not null && !value.IsCurrent)
            {
                _ = CheckoutAsync(value.Name);
            }
        }
    }

    /// <summary>Panel currently selected in the rail.</summary>
    public ShellPanel ActivePanel
    {
        get => _activePanel;
        set
        {
            if (!SetProperty(ref _activePanel, value))
            {
                return;
            }

            _logger.Click($"Rail.{value}");
            OnPropertyChanged(nameof(PanelTitle));

            // The history reads nothing until it is looked at, so it has to be told when
            // that happens. Everything else here is already loaded by the time the rail
            // can switch to it.
            History.IsVisible = value == ShellPanel.History;
        }
    }

    public string PanelTitle => _activePanel switch
    {
        ShellPanel.Project => "PROJECT",
        ShellPanel.Git => "GIT",
        ShellPanel.History => "HISTORY",
        _ => string.Empty,
    };

    public string? RepositoryPath
    {
        get => _repositoryPath;
        private set
        {
            if (SetProperty(ref _repositoryPath, value))
            {
                OnPropertyChanged(nameof(RepositoryName));
            }
        }
    }

    public string RepositoryName =>
        _repositoryPath is null ? "Open a repository…" : Path.GetFileName(_repositoryPath);

    public string BranchName
    {
        get => _branchName;
        private set => SetProperty(ref _branchName, value);
    }

    public string AheadBehind
    {
        get => _aheadBehind;
        private set => SetProperty(ref _aheadBehind, value);
    }

    public string StatusSummary
    {
        get => _statusSummary;
        private set => SetProperty(ref _statusSummary, value);
    }

    public string SelectedDiff
    {
        get => _selectedDiff;
        private set => SetProperty(ref _selectedDiff, value);
    }

    /// <summary>
    /// Show the whole file around the changes, rather than a few lines either side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three lines of context is plenty to review a typo and nowhere near enough to review a
    /// change to a POU, where whether an edit is safe depends on declarations far above it
    /// and on the rest of the routine below.
    /// </para>
    /// <para>
    /// Implemented by asking git for an enormous <c>-U</c> rather than by loading the file
    /// from disk. The result is still a diff, so added and removed lines stay marked and
    /// coloured; reading the raw file would show every line and lose the only thing the
    /// reviewer opened it for.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Where the file log is actually being written, once a sink is attached.
    /// </summary>
    /// <remarks>
    /// Set by the composition root rather than assumed, because the sink can end up
    /// somewhere other than the default — a temp folder under test, or nowhere at all if the
    /// default directory was unwritable. "Open logs folder" opening a folder the logs are not
    /// in would be worse than the button not existing.
    /// </remarks>
    public string? FileLogDirectory
    {
        get => _fileLogDirectory;
        set => SetProperty(ref _fileLogDirectory, value);
    }

    public bool ShowFullFile
    {
        get => _showFullFile;
        set
        {
            if (!SetProperty(ref _showFullFile, value))
            {
                return;
            }

            _logger.Click($"Diff.ShowFullFile={value}");

            // Re-read rather than re-render: the extra context has to come from git, and the
            // currently displayed diff simply does not contain it. Back to the same source it
            // came from - re-reading a commit's diff off the working tree would quietly swap
            // what the pane is answering.
            switch (_shownCommit)
            {
                case { Path: { } path } commit:
                    _ = ShowCommitFileAsync(commit.Id, commit.ShortId, path);
                    break;

                case { } commit:
                    _ = ShowCommitPatchAsync(commit.Id, commit.ShortId, commit.Caption);
                    break;

                default:
                    if (!string.IsNullOrEmpty(SelectedDiffPath))
                    {
                        _ = ShowPathAsync(SelectedDiffPath, _selectedDiffStaged);
                    }

                    break;
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>
    /// Whether the panel is following the repository by itself, in words the user can act on.
    /// </summary>
    /// <remarks>
    /// Worth showing rather than assuming: on a network share or some cloud-synced folders
    /// the OS refuses to host a watcher, and the panel then updates on the backstop tick
    /// instead of instantly. That is a real difference in behaviour and silently degrading
    /// would look like the bug this replaced.
    /// </remarks>
    public string WatchState
    {
        get => _watchState;
        private set => SetProperty(ref _watchState, value);
    }

    /// <summary>How long ago the file list was last read from git.</summary>
    public string LastRefreshText
    {
        get => _lastRefreshText;
        private set => SetProperty(ref _lastRefreshText, value);
    }

    private ChangeRow? _selectedStaged;
    private ChangeRow? _selectedUnstaged;

    /// <summary>
    /// The row picked in the git panel's staged list.
    /// </summary>
    /// <remarks>
    /// Two properties rather than one shared selection, because they are two list boxes and
    /// WPF will not let them agree. The pair exists at all because the git panel used to be
    /// the one place in the app where clicking a file did nothing — the changed-files list it
    /// duplicates has always opened a diff, and the difference read as a dead control.
    /// </remarks>
    public ChangeRow? SelectedStaged
    {
        get => _selectedStaged;
        set
        {
            if (SetProperty(ref _selectedStaged, value) && value is not null)
            {
                _ = ShowPathAsync(value.Path, staged: true);
            }
        }
    }

    public ChangeRow? SelectedUnstaged
    {
        get => _selectedUnstaged;
        set
        {
            if (!SetProperty(ref _selectedUnstaged, value))
            {
                return;
            }

            // Discard acts on this selection, so its enabled state moves with it — including
            // back to disabled when the selection is cleared, which is why this sits outside
            // the null check.
            DiscardCommand.RaiseCanExecuteChanged();

            if (value is not null)
            {
                _ = ShowPathAsync(value.Path, staged: false);
            }
        }
    }

    /// <summary>
    /// Raised when a chosen folder cannot be used, so the view can show a dialog.
    /// </summary>
    /// <remarks>
    /// An event rather than a direct message box: putting a modal here would make
    /// <see cref="OpenAsync"/> untestable and would block a background caller.
    /// </remarks>
    public event Action<RepositoryLocation>? RepositoryRejected;

    /// <summary>
    /// Open a repository by path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chosen folder is resolved to its working tree root first. <b>Versio runs
    /// entirely on Git</b> — branches, history, the diff, the direction lock releasing on
    /// push — so a folder outside a repository gives it nothing to work with, and every
    /// control would be present but dead. Refusing with an explanation is the better
    /// failure.
    /// </para>
    /// <para>
    /// A subdirectory is accepted and promoted to its root, because people select the
    /// folder they were looking at rather than the root — which is what <c>git status</c>
    /// does too.
    /// </para>
    /// </remarks>
    public async Task OpenAsync(string path)
    {
        if (!await OpenRepositoryAsync(path).ConfigureAwait(true))
        {
            return;
        }

        // ONLY once the repository is fully open: git state read, projects scanned, the
        // orchestrator rebuilt and its locks restored from disk, and staleness evaluated.
        // Ordering is the whole point — exporting before the locks are restored would export
        // against a direction the previous session had already decided, and exporting before
        // the scan would not know which projects exist.
        //
        // Deliberately outside the "Opening…" busy scope above, which has ended by now: this
        // starts its own, with a caption saying what is being exported and why, because a
        // CODESYS launch is 17–31 s and the window is already on screen and interactive.
        await Sync.CatchUpAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>
    /// Everything about this machine worth putting in a bug report.
    /// </summary>
    /// <remarks>
    /// Assembled here rather than in the window, so what goes in it is decided somewhere
    /// testable and the window stays a list of rows it was handed. The git version is asked of
    /// git itself: the one on PATH is the one Versio will use, and "which git" is a question
    /// that has caused real confusion on machines with several.
    /// </remarks>
    public async Task<IReadOnlyList<Views.SystemInfoRow>> BuildSystemInfoAsync(CancellationToken ct)
    {
        var rows = new List<Views.SystemInfoRow>
        {
            Views.SystemInfoRow.Section("VERSIO"),
            new("Version", typeof(ShellViewModel).Assembly.GetName().Version?.ToString()
                           ?? "unknown"),
            new("Repository", RepositoryPath ?? "none open"),
            new("Logs", FileLogDirectory ?? "not writing to disk"),
        };

        rows.AddRange(await Sync.DescribeToolchainsAsync(ct).ConfigureAwait(true));

        rows.Add(Views.SystemInfoRow.Section("GIT"));
        rows.Add(new Views.SystemInfoRow("Version", await GitVersionAsync(ct).ConfigureAwait(true)));

        rows.Add(Views.SystemInfoRow.Section("RUNTIME"));
        rows.Add(new Views.SystemInfoRow(".NET",
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription));
        rows.Add(new Views.SystemInfoRow("OS",
            System.Runtime.InteropServices.RuntimeInformation.OSDescription));
        rows.Add(new Views.SystemInfoRow("Machine", Environment.MachineName));

        return rows;
    }

    /// <summary>Ask git for its version, or say why it could not be asked.</summary>
    private async Task<string> GitVersionAsync(CancellationToken ct)
    {
        if (_git is null)
        {
            // No repository open, so there is no client. Still worth answering: a machine
            // with no git at all is the most likely reason somebody is reading this window.
            return "not checked — open a repository";
        }

        try
        {
            return await _git.VersionAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return $"could not be asked: {ex.Message}";
        }
    }

    /// <summary>Open the repository and load everything that depends on it.</summary>
    /// <returns>False when the folder was rejected, so the caller does no follow-up work.</returns>
    private async Task<bool> OpenRepositoryAsync(string path)
    {
        _logger.Info(LogCategory.App, $"Opening {path}");

        using var busy = Busy.Begin($"Opening {System.IO.Path.GetFileName(path.TrimEnd('\\', '/'))}…");

        var location = _locator.Locate(path);
        if (!location.IsUsable)
        {
            _logger.Warn(LogCategory.Git, $"{path}: {location.Explanation}");
            StatusSummary = location.Explanation;
            RepositoryRejected?.Invoke(location);
            return false;
        }

        // Always operate on the working tree root, never the folder that was picked.
        path = location.WorkingTreeRoot!;
        if (location.Kind == RepositoryKind.InsideWorkingTree)
        {
            _logger.Info(LogCategory.Git, $"resolved to repository root {path}");
        }

        var client = _gitFactory(path);

        // Second, authoritative check. The locator reads the filesystem; only git itself
        // can say whether it will actually operate here, since a corrupt or partially
        // cloned .git looks perfectly fine from the outside.
        if (!await client.IsRepositoryAsync(CancellationToken.None).ConfigureAwait(true))
        {
            var rejected = location with { Kind = RepositoryKind.NotARepository };
            _logger.Warn(LogCategory.Git, $"git refused to open {path}");
            StatusSummary = rejected.Explanation;
            RepositoryRejected?.Invoke(rejected);
            return false;
        }

        _git = client;
        RepositoryPath = path;

        _suppressSelectionSideEffects = true;
        try
        {
            var existing = RecentRepositories.FirstOrDefault(r => r.Path == path);
            if (existing is null)
            {
                existing = new RepoEntry(path);
                RecentRepositories.Insert(0, existing);
            }

            SelectedRepository = existing;
        }
        finally
        {
            _suppressSelectionSideEffects = false;
        }

        RefreshCommand.RaiseCanExecuteChanged();

        // Watch before the first refresh, so a change landing during it is not missed.
        StartWatching(path);

        await RefreshCoreAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);

        // The history is handed the client but reads nothing yet. Whether this repository's
        // log is ever walked is now the user's decision, made by clicking the rail.
        History.Attach(client);

        // Scan for PLC projects after the git state is up, so the sync panel is populated
        // by the time the user reaches for it.
        await Sync.AttachAsync(path, client, CancellationToken.None).ConfigureAwait(true);
        return true;
    }

    /// <summary>Why a refresh is happening. Only affects logging and how much is reloaded.</summary>
    private enum RefreshTrigger
    {
        /// <summary>The toolbar button, or opening a repository.</summary>
        Manual,

        /// <summary>A file in the working tree changed.</summary>
        WorkingTree,

        /// <summary>Git's own state moved — staged, committed, checked out elsewhere.</summary>
        GitMetadata,

        /// <summary>The periodic re-read that covers dropped watcher events.</summary>
        Backstop,
    }

    /// <summary>
    /// Begin following <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// Any previous watcher is disposed first. Leaving one attached to the repository the
    /// user just switched away from would refresh this panel from the wrong tree — and the
    /// symptom would look exactly like the stale panel this feature fixes.
    /// </remarks>
    private void StartWatching(string path)
    {
        StopWatching();
        _policy.Reset();

        // A quiet run belongs to the repository it was quiet about. Carrying one across a
        // switch would report the previous tree's idle polls against the new one.
        _quietRefreshes.Reset();

        var watcher = new RepositoryWatcher(path, _clock, logger: _logger);

        watcher.Settled += OnRepositoryChanged;
        watcher.Tick += OnWatcherTick;
        _watcher = watcher;

        WatchState = watcher.IsDegraded ? "polling" : "live";
        _logger.Info(LogCategory.Git,
            watcher.IsDegraded
                ? "filesystem watching unavailable; the file list will refresh periodically"
                : "watching the working tree for changes");
    }

    private void StopWatching()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.Settled -= OnRepositoryChanged;
        _watcher.Tick -= OnWatcherTick;
        _watcher.Dispose();
        _watcher = null;
        WatchState = string.Empty;
    }

    /// <summary>A burst of filesystem changes settled. Runs on a threadpool thread.</summary>
    private void OnRepositoryChanged(IReadOnlyList<RepositoryChange> changes)
    {
        // A metadata change moves the branch and the ahead/behind counts too, so it is the
        // stronger of the two and wins when a burst contains both.
        var relevance = changes.Any(c => c.Relevance == RepositoryChangeRelevance.GitMetadata)
            ? RepositoryChangeRelevance.GitMetadata
            : RepositoryChangeRelevance.WorkingTree;

        if (_policy.OnChange(relevance) != RefreshDecision.RunNow)
        {
            // Queued or ignored. OnWatcherTick starts queued work once the throttle expires.
            return;
        }

        var trigger = relevance == RepositoryChangeRelevance.GitMetadata
            ? RefreshTrigger.GitMetadata
            : RefreshTrigger.WorkingTree;

        var paths = changes.Select(c => c.FullPath).ToArray();
        OnUi(() => _ = RunRefreshAsync(trigger, paths));
    }

    /// <summary>Timer tick. Starts throttled work and the periodic backstop.</summary>
    private void OnWatcherTick()
    {
        UpdateLastRefreshText();

        if (_policy.TryStartPending())
        {
            OnUi(() => _ = RunRefreshAsync(RefreshTrigger.WorkingTree, null));
            return;
        }

        if (_policy.ShouldRunBackstop())
        {
            OnUi(() => _ = RunRefreshAsync(RefreshTrigger.Backstop, null));
        }
    }

    /// <summary>Run a policy-driven refresh, surfacing failures instead of losing them.</summary>
    /// <remarks>
    /// The policy must be told the refresh finished even when it throws, or
    /// <c>IsRunning</c> stays true for the rest of the session and automatic refreshing
    /// stops dead — a failure that would present as the panel going stale again.
    /// </remarks>
    private async Task RunRefreshAsync(RefreshTrigger trigger, IReadOnlyList<string>? paths)
    {
        try
        {
            await RefreshCoreAsync(trigger, paths).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            if (_policy.OnRefreshCompleted())
            {
                OnUi(() => _ = RunRefreshAsync(trigger, null));
            }
        }
    }

    /// <summary>Marshal to the UI thread, or run inline when there is no UI (tests).</summary>
    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
            return;
        }

        _ui.Post(_ => action(), null);
    }

    // ---------------------------------------------------------------- git panel

    /// <summary>Files git will include in the next commit.</summary>
    public ObservableCollection<ChangeRow> Staged { get; } = [];

    /// <summary>Files changed in the working tree but not staged.</summary>
    public ObservableCollection<ChangeRow> Unstaged { get; } = [];

    public AsyncCommand StageCommand { get; private set; } = null!;
    public AsyncCommand UnstageCommand { get; private set; } = null!;
    public AsyncCommand CommitCommand { get; private set; } = null!;
    public AsyncCommand CommitAndPushCommand { get; private set; } = null!;
    public AsyncCommand PullCommand { get; private set; } = null!;
    public AsyncCommand PushCommand { get; private set; } = null!;
    public AsyncCommand FetchCommand { get; private set; } = null!;

    private string _commitMessage = string.Empty;

    /// <summary>
    /// The commit message, subject on the first line.
    /// </summary>
    /// <remarks>
    /// Deliberately not cleared when a commit fails. A rejected commit that also threw away
    /// what the user typed is two losses for one mistake, and the message is usually the part
    /// they cared about.
    /// </remarks>
    public string CommitMessage
    {
        get => _commitMessage;
        set
        {
            if (SetProperty(ref _commitMessage, value))
            {
                OnPropertyChanged(nameof(CommitSubjectLength));
                CommitCommand.RaiseCanExecuteChanged();
                CommitAndPushCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Length of the subject line, for the 50/72 guide.</summary>
    public int CommitSubjectLength =>
        (_commitMessage.Split('\n').FirstOrDefault() ?? string.Empty).TrimEnd('\r').Length;

    private void BuildGitCommands()
    {
        StageCommand = new AsyncCommand(
            () => StageAsync(Unstaged, stage: true),
            () => Ready && Unstaged.Count > 0, ReportError);

        UnstageCommand = new AsyncCommand(
            () => StageAsync(Staged, stage: false),
            () => Ready && Staged.Count > 0, ReportError);

        CommitCommand = new AsyncCommand(
            () => CommitAsync(push: false), CanCommit, ReportError);

        CommitAndPushCommand = new AsyncCommand(
            () => CommitAsync(push: true), CanCommit, ReportError);

        BuildHunkCommand();

        PullCommand = new AsyncCommand(PullAsync, () => Ready, ReportError);
        PushCommand = new AsyncCommand(() => PushAsync(afterCommit: false),
                                       () => Ready, ReportError);
        FetchCommand = new AsyncCommand(FetchAsync, () => Ready, ReportError);

        // Enabled only with a row selected. Discard is per-file on purpose: a "discard all"
        // beside "stage all" would put an irreversible button one pixel away from a routine
        // one, and the two are not comparable operations.
        DiscardCommand = new AsyncCommand(
            DiscardAsync, () => Ready && SelectedUnstaged is not null, ReportError);

        CreateBranchCommand = new AsyncCommand(CreateBranchAsync, () => Ready, ReportError);

        // Cloning needs no open repository — it is how you get one — so it is enabled
        // whenever nothing else is running, unlike every other command here.
        CloneCommand = new AsyncCommand(CloneRepositoryAsync, () => !Busy.IsBusy, ReportError);

        // Every git button consults the same shared flag, so the moment one operation starts
        // they all go dead together. AsyncCommand.IsRunning only disables the button that was
        // pressed, which would leave Push live during a Pull.
        Busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BusyTracker.IsBusy))
            {
                OnUi(RaiseGitCommandStates);
            }
        };
    }

    /// <summary>
    /// A git command may run: there is a repository, and nothing else is running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One operation at a time, across the whole application.</b> Git serialises some of
    /// this itself with index and ref locks, but not all of it, and the failures it does not
    /// prevent are the expensive ones: a push racing a commit, or a commit landing while the
    /// gate's export is still writing the mirror it is supposed to be committing.
    /// </para>
    /// <para>
    /// Deliberately the shared <see cref="BusyTracker"/> rather than a git-only flag, so a
    /// 17-31 s CODESYS export also holds the git buttons. Those two overlapping is exactly
    /// the case worth preventing - the export writes the files the commit is about to stage.
    /// </para>
    /// <para>
    /// Automatic status refreshes do not raise the tracker, so the buttons do not flicker
    /// every time the user saves a file in their editor.
    /// </para>
    /// </remarks>
    private bool Ready => _git is not null && !Busy.IsBusy;

    private bool CanCommit() =>
        Ready && Staged.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);

    private void RaiseGitCommandStates()
    {
        StageCommand.RaiseCanExecuteChanged();
        UnstageCommand.RaiseCanExecuteChanged();
        CommitCommand.RaiseCanExecuteChanged();
        CommitAndPushCommand.RaiseCanExecuteChanged();
        PullCommand.RaiseCanExecuteChanged();
        PushCommand.RaiseCanExecuteChanged();
        FetchCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        HunkCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
        CreateBranchCommand.RaiseCanExecuteChanged();
        CloneCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Stage or unstage every row in a list.</summary>
    /// <remarks>
    /// Whole-list rather than per-file selection for now. Partial staging is a real feature
    /// and belongs with per-hunk staging, which needs the diff view to own a selection first;
    /// half-offering it would be worse than not offering it.
    /// </remarks>
    private async Task StageAsync(IReadOnlyList<ChangeRow> rows, bool stage)
    {
        if (_git is null || rows.Count == 0)
        {
            return;
        }

        var paths = rows.Select(r => r.Path).ToList();
        _logger.Click(stage ? "Git.Stage" : "Git.Unstage");

        using var busy = Busy.Begin(stage ? "Staging…" : "Unstaging…");

        var result = stage
            ? await _git.StageAsync(paths, CancellationToken.None).ConfigureAwait(true)
            : await _git.UnstageAsync(paths, CancellationToken.None).ConfigureAwait(true);

        if (!result.Success)
        {
            StatusSummary = result.FailureReason ?? "git refused that";
            return;
        }

        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    /// <summary>
    /// Commit, running the export gate first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gate runs before the commit, and exports rather than merely refusing.</b> With
    /// <c>On git action</c> as the default sync mode there is no export on save, so this is
    /// the primary path by which the mirror gets written at all. Refusing here and leaving the
    /// user to find the export button would make the default mode unusable.
    /// </para>
    /// <para>
    /// The export writes files, so those files are staged afterwards and before the commit.
    /// A commit recording the project without the mirror it exports to is the exact state the
    /// gate exists to prevent, and exporting and then committing only what happened to be
    /// staged beforehand would produce it silently.
    /// </para>
    /// </remarks>
    private async Task CommitAsync(bool push)
    {
        if (_git is null)
        {
            return;
        }

        _logger.Click(push ? "Git.CommitAndPush" : "Git.Commit");
        using var scope = _logger.BeginOperation(Guid.NewGuid().ToString("N")[..8]);

        var gate = await Sync.EvaluateGateAsync(CancellationToken.None).ConfigureAwait(true);
        if (gate.Decision == GateDecision.Blocked)
        {
            // Something an export will not fix, such as a failed binding awaiting
            // acknowledgement. Say so rather than running an export that cannot help.
            _logger.Warn(LogCategory.Sync, $"commit blocked: {gate.Explanation}");
            StatusSummary = gate.Explanation;
            return;
        }

        if (gate.Decision == GateDecision.ExportRequired)
        {
            StatusSummary = "Exporting before the commit…";
            var outcomes = await Sync.SatisfyGateAsync(CancellationToken.None).ConfigureAwait(true);

            if (outcomes.Any(o => o.IsFailure))
            {
                var failure = outcomes.First(o => o.IsFailure);
                StatusSummary = $"Export failed, so nothing was committed: {failure.Explanation}";
                _logger.Error(LogCategory.Sync, StatusSummary);
                return;
            }

            var staged = await StageExportedTreesAsync().ConfigureAwait(true);
            _logger.Info(LogCategory.Git,
                $"staged {staged} export path(s) before committing");
        }

        using var busy = Busy.Begin("Committing…");
        var result = await _git.CommitAsync(CommitMessage, CancellationToken.None)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            StatusSummary = result.FailureReason ?? "the commit failed";
            _logger.Error(LogCategory.Git, StatusSummary);
            return;
        }

        _logger.Info(LogCategory.Git, "commit created");
        CommitMessage = string.Empty;
        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);

        if (push)
        {
            await PushAsync(afterCommit: true).ConfigureAwait(true);
            return;
        }

        // Said plainly, because it is the most surprising rule in the tool: a commit does not
        // release the direction lock. Until the work is pushed nobody else can see it.
        StatusSummary = "Committed. Not pushed, so the direction lock is still held.";
    }

    /// <summary>Stage every tracked project's export directory.</summary>
    /// <remarks>
    /// By directory rather than by the file list the export reported. An export can delete an
    /// object as well as write one, and staging only what was written would leave the deletion
    /// out of the commit.
    /// </remarks>
    private async Task<int> StageExportedTreesAsync()
    {
        var paths = Sync.ExportPaths;
        if (_git is null || paths.Count == 0)
        {
            return 0;
        }

        var result = await _git.StageAsync(paths, CancellationToken.None).ConfigureAwait(true);
        if (!result.Success)
        {
            _logger.Warn(LogCategory.Git,
                $"could not stage the export tree: {result.FailureReason}");
        }

        return paths.Count;
    }

    private async Task PullAsync()
    {
        if (_git is null)
        {
            return;
        }

        _logger.Click("Git.Pull");
        using var busy = Busy.Begin("Pulling…");

        var result = await _git.PullAsync(null, CancellationToken.None).ConfigureAwait(true);
        StatusSummary = result.Success ? "Pulled." : result.FailureReason ?? "the pull failed";

        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    /// <summary>
    /// Push, and release the direction locks if it worked.
    /// </summary>
    /// <remarks>
    /// Push is what releases the lock, not commit: until work is pushed nobody else can see
    /// it, so the direction is still meaningfully owned (plan section 6.5). Push is repo-wide,
    /// so it releases every binding rather than the one last looked at.
    /// </remarks>
    private async Task PushAsync(bool afterCommit)
    {
        if (_git is null)
        {
            return;
        }

        if (!afterCommit)
        {
            _logger.Click("Git.Push");
        }

        using var busy = Busy.Begin("Pushing…");
        var result = await _git.PushAsync(null, CancellationToken.None).ConfigureAwait(true);

        if (!result.Success)
        {
            StatusSummary = result.FailureReason ?? "the push failed";
            _logger.Error(LogCategory.Git, StatusSummary);
            return;
        }

        var head = await _git.HeadCommitAsync(CancellationToken.None).ConfigureAwait(true);
        await Sync.ReleaseAfterPushAsync(head, CancellationToken.None).ConfigureAwait(true);

        StatusSummary = "Pushed. Every direction lock is released.";
        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    private async Task FetchAsync()
    {
        if (_git is null)
        {
            return;
        }

        _logger.Click("Git.Fetch");
        using var busy = Busy.Begin("Fetching…");

        var result = await _git.FetchAsync(null, CancellationToken.None).ConfigureAwait(true);
        StatusSummary = result.Success ? "Fetched." : result.FailureReason ?? "the fetch failed";

        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    // -------------------------------------------------------------- hunks, diffs

    /// <summary>Stage or unstage one hunk of the open diff, by its index.</summary>
    public AsyncCommand<int> HunkCommand { get; private set; } = null!;

    /// <summary>Throw away the working-tree changes to the selected unstaged file.</summary>
    public AsyncCommand DiscardCommand { get; private set; } = null!;

    /// <summary>Create a branch and switch to it.</summary>
    public AsyncCommand CreateBranchCommand { get; private set; } = null!;

    /// <summary>Clone a repository, then open it.</summary>
    public AsyncCommand CloneCommand { get; private set; } = null!;

    /// <summary>
    /// Asks the view to run the clone dialog, returning where the clone landed or null.
    /// </summary>
    /// <remarks>
    /// The dialog drives verify-then-clone itself, because "Clone stays disabled until Verify
    /// passes" is a rule about a conversation rather than about one operation. What comes back
    /// is a path on disk, which is all the shell needs to open it.
    /// </remarks>
    public Func<Task<string?>>? PromptForClone { get; set; }

    /// <summary>Check a remote is reachable, with no repository open.</summary>
    /// <remarks>Handed to the clone dialog. Lives here rather than in the view so that the
    /// view never constructs a git client of its own.</remarks>
    public Task<GitResult> VerifyRemoteAsync(string url, CancellationToken ct) =>
        GitCliClient.VerifyRemoteAsync(url, "git", _logger, ct);

    /// <summary>Clone a repository into a folder that does not exist yet.</summary>
    public Task<GitResult> CloneAsync(
        string url, string destination, IProgress<GitProgress>? progress, CancellationToken ct) =>
        GitCliClient.CloneAsync(url, destination, "git", progress, _logger, ct);

    /// <summary>
    /// Asks the view for a new branch name, or null when the user backs out.
    /// </summary>
    /// <remarks>
    /// A delegate for the same reason as <see cref="ConfirmDestructive"/>: a modal opened from
    /// here would block whichever thread called it and would make the command untestable. The
    /// dialog validates while typing with <see cref="Versio.Core.Git.BranchName"/>; this side validates the
    /// answer again, because a view model that trusts its view has no way to state what it
    /// requires.
    /// </remarks>
    public Func<string?>? PromptForBranchName { get; set; }

    /// <summary>
    /// Asked before anything destroys work git cannot give back.
    /// </summary>
    /// <remarks>
    /// A delegate set by the view rather than a message box here, for the same reason
    /// <see cref="RepositoryRejected"/> is an event: a modal in the view model makes this
    /// untestable and blocks whichever thread happened to call it. Unset means "no", so a
    /// host that forgets to wire it destroys nothing.
    /// </remarks>
    public Func<string, string, bool>? ConfirmDestructive { get; set; }

    private void BuildHunkCommand() =>
        HunkCommand = new AsyncCommand<int>(ApplyHunkAsync, _ => Ready && CanStageHunks,
                                            ReportError);

    /// <summary>
    /// Show one whole commit's patch in the same pane everything else uses.
    /// </summary>
    /// <remarks>
    /// Reached by clicking the commit itself rather than a file under it. Every file at once
    /// is the right answer to "what did this commit do?", and the per-file rows underneath are
    /// the right answer to "what did it do to this file?" — the panel offers both because they
    /// are different questions, and the expensive one is not the one asked by default.
    /// </remarks>
    private Task ShowCommitAsync(CommitEntry commit) =>
        ShowCommitPatchAsync(commit.Id, commit.ShortId, $"{commit.ShortId} {commit.Subject}");

    private async Task ShowCommitPatchAsync(string commitId, string shortId, string caption)
    {
        if (_git is null)
        {
            return;
        }

        SelectedDiffPath = caption;
        _shownCommit = (commitId, shortId, caption, null);
        _selectedDiffStaged = false;

        // A commit is history: it is already staged, already committed, and there is nothing
        // to move in or out of the index. Offering to stage a hunk of it would be offering
        // something git cannot do.
        CanStageHunks = false;

        var patch = await _git
            .CommitPatchAsync(commitId, CancellationToken.None, DiffContext)
            .ConfigureAwait(true);

        SelectedDiff = string.IsNullOrWhiteSpace(patch)
            ? caption + Environment.NewLine + Environment.NewLine +
              "No patch to show. This is usually a merge commit, whose changes are a diff " +
              "against several parents rather than one patch."
            : patch;
    }

    /// <summary>Show what one commit did to one file.</summary>
    private Task ShowCommitFileAsync(CommitFileRow row) =>
        ShowCommitFileAsync(row.Commit.Id, row.Commit.ShortId, row.Path);

    private async Task ShowCommitFileAsync(string commitId, string shortId, string path)
    {
        if (_git is null)
        {
            return;
        }

        // The commit is named in the header as well as the path. Without it this reads
        // identically to the working-tree diff of the same file, which is the one thing a
        // reviewer must not confuse it with.
        SelectedDiffPath = $"{path} @ {shortId}";
        _shownCommit = (commitId, shortId, SelectedDiffPath, path);
        _selectedDiffStaged = false;
        CanStageHunks = false;

        var patch = await _git
            .CommitFileDiffAsync(commitId, path, CancellationToken.None, DiffContext)
            .ConfigureAwait(true);

        SelectedDiff = string.IsNullOrWhiteSpace(patch)
            ? SelectedDiffPath + Environment.NewLine + Environment.NewLine +
              "Nothing to compare: git reports this file as binary, which for a .project " +
              "file is the whole reason the exported tree beside it exists."
            : patch;
    }

    /// <summary>
    /// Stage or unstage one hunk of the open diff.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The patch is cut from the diff currently on screen, so what is staged is exactly what
    /// the user is looking at. Re-reading the file here would open a window in which the two
    /// disagreed, and the visible symptom would be a button that staged something else.
    /// </para>
    /// <para>
    /// Direction follows the side the diff came from: a working-tree hunk goes into the index,
    /// and an index hunk is reversed back out of it. <see cref="_selectedDiffStaged"/> is
    /// remembered for exactly this, because guessing it would silently do the opposite of
    /// what the button said.
    /// </para>
    /// </remarks>
    private async Task ApplyHunkAsync(int hunkIndex)
    {
        if (_git is null)
        {
            return;
        }

        var patch = HunkPatchBuilder.Build(SelectedDiff, hunkIndex);
        if (patch is null)
        {
            StatusSummary = "That hunk is no longer in the diff on screen.";
            return;
        }

        var unstaging = _selectedDiffStaged;
        _logger.Click(unstaging ? "Diff.UnstageHunk" : "Diff.StageHunk");

        using var busy = Busy.Begin(unstaging ? "Unstaging a hunk…" : "Staging a hunk…");

        var result = await _git
            .ApplyToIndexAsync(patch, reverse: unstaging, CancellationToken.None)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            // Almost always a context mismatch: the file moved on under a diff that was read
            // a moment ago. Git's own wording is the diagnosis, so it is passed through.
            StatusSummary = result.FailureReason ?? "git would not apply that hunk";
            _logger.Warn(LogCategory.Git, StatusSummary);
            return;
        }

        StatusSummary = unstaging ? "Hunk unstaged." : "Hunk staged.";
        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    /// <summary>Keep the two staging lists in step with the status just read.</summary>
    /// <remarks>
    /// Rebuilt rather than reconciled, unlike the changed-files list. These are short, and
    /// they carry no selection worth preserving: the buttons act on the whole list.
    /// </remarks>
    private void UpdateStagingLists(GitStatus status)
    {
        Staged.Clear();
        foreach (var change in status.Staged.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            Staged.Add(new ChangeRow(change));
        }

        Unstaged.Clear();
        foreach (var change in status.Unstaged.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            Unstaged.Add(new ChangeRow(change));
        }

        RaiseGitCommandStates();
    }

    /// <summary>
    /// Open the log folder in Explorer, with today's file selected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Explorer is launched by name with the path as an argument, rather than handing the
    /// folder to <c>ShellExecute</c>. Both work; this form also allows <c>/select,</c>, which
    /// highlights today's file instead of dropping the user into a folder of dated logs to
    /// work out which one is current. It falls back to the plain folder when nothing has been
    /// written yet - <c>/select,</c> on a missing file opens Documents, which is a confusing
    /// place to land.
    /// </para>
    /// <para>
    /// The argument is deliberately one string: <c>/select,</c> immediately followed by the
    /// path, with no space after the comma. Splitting it across two arguments makes Explorer
    /// ignore the selection and open Documents instead.
    /// </para>
    /// <para>
    /// <b>If this ever reports that the location is unavailable for a folder that plainly
    /// exists, suspect filesystem virtualization rather than the code.</b> A process launched
    /// inside an MSIX/AppContainer sees writes to <c>%LOCALAPPDATA%</c> redirected into that
    /// package's <c>LocalCache</c>, so Versio logs to a real folder, and Explorer - which runs
    /// outside the container - is handed a path that does not exist from where it is standing.
    /// Diagnosed once, at the cost of a fix aimed at the wrong thing; the log line below
    /// prints the directory actually in use, which is what settles it next time.
    /// </para>
    /// </remarks>
    private void OpenLogsFolder()
    {
        _logger.Click("Settings.OpenLogsFolder");

        var folder = _fileLogDirectory ?? RollingFileSink.DefaultDirectory;
        try
        {
            // Created if missing, so the button cannot fail merely because file logging was
            // never attached or nothing has been written yet.
            Directory.CreateDirectory(folder);

            var current = Directory
                .EnumerateFiles(folder, "*.log")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            var argument = current is null ? folder : "/select," + current;

            var started = Process.Start(new ProcessStartInfo("explorer.exe", argument)
            {
                UseShellExecute = true,
            });

            _logger.Info(LogCategory.App,
                started is null
                    ? $"asked Explorer to open {folder}"
                    : $"opened {(current ?? folder)} in Explorer (pid {started.Id})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
            // Explorer replaced or policy-blocked - both real on a locked-down industrial
            // machine. Put the path in the log and the status bar so it can still be reached
            // by hand, because the log is what somebody is reaching for at this point.
            _logger.Error(LogCategory.App, $"could not open the log folder {folder}", ex);
            StatusSummary = $"Logs are in {folder}";
        }
    }

    /// <summary>
    /// Throw away the working-tree changes to the selected unstaged file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one button in this application that destroys work nothing else has a copy of.</b>
    /// Not the index, not a stash, not a commit — the content is simply gone. So it is per-file
    /// rather than per-list, it names the file in the confirmation, and it is unreachable
    /// without a selection.
    /// </para>
    /// <para>
    /// An <b>untracked</b> file is refused rather than attempted. <c>git restore --worktree</c>
    /// has nothing to restore it from and fails with "pathspec did not match any file(s) known
    /// to git", which reads like a bug in Versio rather than like the answer to the question
    /// that was asked. Deleting it instead would be a different operation, more destructive
    /// than the one on the button, and not one to perform because the wording happened to fit.
    /// </para>
    /// <para>
    /// Discarding inside an export tree also <b>releases that binding's direction lock</b>
    /// (plan §6.5). The lock exists because one side holds work the other has not seen; once
    /// that work is thrown away there is nothing left to guard, and a lock left held would
    /// block the next export for a reason nobody could find from the UI.
    /// </para>
    /// </remarks>
    private async Task DiscardAsync()
    {
        if (_git is null || SelectedUnstaged is not { } row)
        {
            return;
        }

        if (row.Kind == ChangeKind.Untracked)
        {
            // Said in the status bar rather than done. The file is not in git at all, so
            // there is no version to go back to.
            _logger.Warn(LogCategory.Git,
                $"discard refused for the untracked file {row.Path}: git has no copy to restore");
            StatusSummary = $"{row.Path} is not tracked by git yet — there is nothing to restore. " +
                            "Delete it in Explorer if that is what you meant.";
            return;
        }

        var confirmed = ConfirmDestructive?.Invoke(
            "Discard changes to this file?",
            $"{row.Path}\n\n" +
            "The changes are thrown away and cannot be recovered — they are not in a commit " +
            "or in the index.\n\n" +
            "Commit them on a scratch branch if you might want them back.") ?? false;

        if (!confirmed)
        {
            _logger.Info(LogCategory.Git, $"discard cancelled for {row.Path}");
            return;
        }

        _logger.Click("Git.Discard");
        using var busy = Busy.Begin($"Discarding {row.Path}…");

        var result = await _git.DiscardAsync([row.Path], CancellationToken.None)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            _logger.Warn(LogCategory.Git, $"discard failed: {result.FailureReason}");
            StatusSummary = result.FailureReason ?? "the changes could not be discarded";
            await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
            return;
        }

        StatusSummary = $"Discarded the changes to {row.Path}.";

        // Only when the file was part of a mirror. A discarded README has nothing to do with
        // the direction lock, and releasing every lock because one unrelated file was
        // restored would be the more damaging kind of over-reach.
        if (Sync.BindingOwning(row.Path) is { } bindingId)
        {
            await Sync.ReleaseAfterDiscardAsync(bindingId, CancellationToken.None)
                .ConfigureAwait(true);
        }

        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    /// <summary>
    /// Create a branch and switch to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Create-and-switch, not create-and-stay.</b> <c>git checkout -b</c> carries the
    /// working tree across, so it is safe with unsynced CODESYS work in progress — which is
    /// the state somebody is usually in when they realise this should have been a branch.
    /// Switching to an <i>existing</i> branch is the dangerous one, and git refuses that
    /// itself on a dirty tree.
    /// </para>
    /// <para>
    /// The name is validated here as well as in the dialog. Git remains the authority: a name
    /// that passes both checks can still be refused because it already exists, or because it
    /// collides with an existing ref path — <c>feature</c> and <c>feature/x</c> cannot both be
    /// branches — so the failure is reported rather than assumed away.
    /// </para>
    /// </remarks>
    private async Task CreateBranchAsync()
    {
        if (_git is null)
        {
            return;
        }

        _logger.Click("TopBar.CreateBranch");

        if (PromptForBranchName?.Invoke() is not { } name)
        {
            return;
        }

        var check = Versio.Core.Git.BranchName.Validate(name);
        if (!check.IsValid)
        {
            // Reaching this means the dialog let through something it should not have, so it
            // is logged rather than only shown: it is a wiring bug, not a user error.
            _logger.Warn(LogCategory.Git, $"refused branch name '{name}': {check.Reason}");
            StatusSummary = check.Reason;
            return;
        }

        using var busy = Busy.Begin($"Creating {name}…");

        var result = await _git.CreateBranchAsync(name, checkout: true, CancellationToken.None)
            .ConfigureAwait(true);

        StatusSummary = result.Success
            ? $"On {name}."
            : result.FailureReason ?? $"{name} could not be created";

        if (!result.Success)
        {
            _logger.Warn(LogCategory.Git, $"create branch failed: {result.FailureReason}");
        }

        // Refreshed either way: a failed create leaves the branch list as it was, and a
        // successful one has moved HEAD.
        await RunRefreshAsync(RefreshTrigger.GitMetadata, null).ConfigureAwait(true);
    }

    /// <summary>
    /// Run the clone dialog, and open what it produced.
    /// </summary>
    /// <remarks>
    /// Opening it afterwards is the point: a clone that leaves the user looking at the same
    /// repository they started in, with a new folder somewhere on disk they have to go and
    /// find, has done the work and withheld the result.
    /// </remarks>
    private async Task CloneRepositoryAsync()
    {
        _logger.Click("TopBar.Clone");

        if (PromptForClone is null)
        {
            return;
        }

        if (await PromptForClone().ConfigureAwait(true) is not { } cloned)
        {
            return;
        }

        _logger.Info(LogCategory.Git, $"cloned into {cloned}; opening it");
        await OpenAsync(cloned).ConfigureAwait(true);
    }

    private async Task CheckoutAsync(string branch)
    {
        if (_git is null)
        {
            return;
        }

        _logger.Info(LogCategory.Git, $"checkout {branch}");
        var result = await _git.CheckoutAsync(branch, CancellationToken.None).ConfigureAwait(true);

        if (!result.Success)
        {
            // Almost always a dirty working tree. Say so rather than failing silently and
            // leaving the dropdown showing a branch we are not on.
            _logger.Warn(LogCategory.Git, $"checkout failed: {result.FailureReason}");
            StatusSummary = result.FailureReason ?? "checkout failed";
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private Task OpenRepositoryAsync()
    {
        // The view supplies the path via OpenAsync; this exists so the button has a command
        // and its click is logged like every other.
        _logger.Click("TopBar.OpenRepository");
        return Task.CompletedTask;
    }

    /// <summary>The toolbar button.</summary>
    private Task RefreshAsync()
    {
        _logger.Click("TopBar.Refresh");

        // Tell the policy, so a manual click is subject to the same throttle and echo
        // suppression as everything else. Without it the click is followed a moment later by
        // an automatic refresh triggered by the index write the click itself caused.
        _policy.NoteExternalStart();

        return RunRefreshAsync(RefreshTrigger.Manual, null);
    }

    /// <summary>
    /// Re-read git and bring the panel into line with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reconciles the existing rows rather than rebuilding the collection. Clearing and
    /// refilling is simpler and was fine when this only ran on a button press, but it drops
    /// the selection and resets the diff pane — which, several times a minute, would be
    /// worse than the staleness it fixes.
    /// </para>
    /// <para>
    /// The spinner only turns for a manual refresh. An automatic one is meant to be
    /// unobtrusive; a status bar flickering every time a file is saved reads as instability.
    /// </para>
    /// </remarks>
    private async Task RefreshCoreAsync(RefreshTrigger trigger, IReadOnlyList<string>? paths)
    {
        if (_git is null)
        {
            return;
        }

        var manual = trigger == RefreshTrigger.Manual;
        IsBusy = manual;

        // Only a manual refresh earns the progress bar. The automatic ones fire whenever the
        // working tree moves, and a bar blinking on every keystroke in the user's editor is
        // noise that would teach them to ignore the one signal that matters.
        using var busy = manual ? Busy.Begin("Reading git status…") : null;

        var operationId = Guid.NewGuid().ToString("N")[..8];
        using var scope = _logger.BeginOperation(operationId);

        try
        {
            var status = await _git.StatusAsync(CancellationToken.None).ConfigureAwait(true);

            var previousBranch = BranchName;
            BranchName = status.Branch.IsDetached ? "(detached)" : status.Branch.Name;
            AheadBehind = status.Branch is { Ahead: 0, Behind: 0 }
                ? string.Empty
                : $"↑{status.Branch.Ahead} ↓{status.Branch.Behind}";

            var changed = Reconcile(status.Changes);

            UpdateStagingLists(status);

            // The project tree is now the primary view, so it follows git the way the file
            // list does. Handed the status just read rather than reading its own: a second
            // `git status` per refresh is a process spawn for an answer we already have, and
            // two reads a moment apart can disagree.
            await Sync.RefreshTreeAsync(status).ConfigureAwait(true);

            StatusSummary = status.IsClean
                ? "No changes"
                : $"{status.Changes.Count} changed";

            _lastRefreshUtc = _clock.UtcNow;
            UpdateLastRefreshText();

            // Branches cost a git process and almost never move, so they are reloaded only
            // when something could plausibly have changed them - which a working-tree save,
            // the common automatic refresh, cannot.
            //
            // The history is marked rather than reloaded. It lives in a panel of its own now,
            // and re-reading a log for a panel nobody is looking at was most of what this
            // branch used to cost; when it is in front, Invalidate reloads it at once.
            if (manual || trigger == RefreshTrigger.GitMetadata || BranchName != previousBranch)
            {
                await LoadBranchesAsync().ConfigureAwait(true);
                History.Invalidate();
            }

            // The open diff is as stale as the list was. Reload it when the shown path's
            // status moved, or when the refresh was triggered by a write to that very file.
            //
            // A diff read out of a commit is exempt: it is a patch that has already happened,
            // and nothing in the working tree can change what it says.
            if (_shownCommit is null &&
                SelectedDiffPath is { Length: > 0 } shown &&
                (changed.Contains(shown) || Touches(paths, shown)))
            {
                await ShowPathAsync(shown, _selectedDiffStaged).ConfigureAwait(true);
            }

            // Automatic refreshes are the common case and mostly find nothing. Demoting
            // those to Debug was the first attempt and was not enough: the backstop runs
            // every 20 s forever, so an idle app still wrote a line every 20 s saying
            // nothing had happened, three times over once the git command's own pair is
            // counted. They are now counted and summarised - see QuietRunTracker.
            if (manual || changed.Count > 0)
            {
                if (_quietRefreshes.RecordChange(_clock.UtcNow) is { } ended)
                {
                    _logger.Debug(LogCategory.Git, ended);
                }

                _logger.Info(LogCategory.Git,
                    $"status: {status.Changes.Count} changed on {BranchName}" +
                    (manual ? string.Empty : $" ({trigger})"));
            }
            else if (_quietRefreshes.RecordQuiet(trigger.ToString(), _clock.UtcNow) is { } due)
            {
                _logger.Debug(LogCategory.Git, due);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Bring <see cref="Changes"/> into line with git, touching only what actually moved.
    /// </summary>
    /// <remarks>
    /// Both sequences are ordinal-sorted by path, so a single forward pass is enough: once
    /// rows git no longer reports are removed, any position where the paths disagree is an
    /// insertion.
    /// </remarks>
    /// <returns>Paths whose displayed state changed, added or removed.</returns>
    private HashSet<string> Reconcile(IReadOnlyList<FileChange> fresh)
    {
        var ordered = fresh.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
        var live = ordered.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        var moved = new HashSet<string>(StringComparer.Ordinal);

        for (var i = Changes.Count - 1; i >= 0; i--)
        {
            if (!live.Contains(Changes[i].Path))
            {
                moved.Add(Changes[i].Path);
                Changes.RemoveAt(i);
            }
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            if (i >= Changes.Count)
            {
                Changes.Add(new ChangeRow(ordered[i]));
                moved.Add(ordered[i].Path);
            }
            else if (!string.Equals(Changes[i].Path, ordered[i].Path, StringComparison.Ordinal))
            {
                Changes.Insert(i, new ChangeRow(ordered[i]));
                moved.Add(ordered[i].Path);
            }
            else if (Changes[i].Update(ordered[i]))
            {
                moved.Add(ordered[i].Path);
            }
        }

        return moved;
    }

    /// <summary>Did this burst of filesystem events include the given repo-relative path?</summary>
    private bool Touches(IReadOnlyList<string>? absolutePaths, string relativePath)
    {
        if (absolutePaths is null || _repositoryPath is null)
        {
            return false;
        }

        return absolutePaths.Any(p =>
            string.Equals(RepositoryChangeFilter.ToRelative(_repositoryPath, p),
                          relativePath,
                          StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Keep the "updated N ago" caption honest.
    /// </summary>
    /// <remarks>
    /// Runs on every watcher tick, four times a second, but only raises a change
    /// notification when the rendered text actually differs — otherwise it would invalidate
    /// a binding continuously for no visible effect.
    /// </remarks>
    private void UpdateLastRefreshText()
    {
        if (_lastRefreshUtc is not { } last)
        {
            LastRefreshText = string.Empty;
            return;
        }

        var age = _clock.UtcNow - last;
        LastRefreshText = age switch
        {
            { TotalSeconds: < 5 } => "updated just now",
            { TotalSeconds: < 60 } => $"updated {(int)age.TotalSeconds}s ago",
            { TotalMinutes: < 60 } => $"updated {(int)age.TotalMinutes}m ago",
            _ => "updated over an hour ago",
        };
    }

    private async Task LoadBranchesAsync()
    {
        if (_git is null)
        {
            return;
        }

        var branches = await _git.BranchesAsync(CancellationToken.None).ConfigureAwait(true);

        _suppressSelectionSideEffects = true;
        try
        {
            Branches.Clear();
            foreach (var branch in branches.OrderByDescending(b => b.IsCurrent)
                                           .ThenBy(b => b.Name, StringComparer.Ordinal))
            {
                Branches.Add(new BranchEntry(branch));
            }

            SelectedBranch = Branches.FirstOrDefault(b => b.IsCurrent);
        }
        finally
        {
            _suppressSelectionSideEffects = false;
        }
    }

    private string _selectedDiffPath = string.Empty;

    /// <summary>
    /// Path the diff pane is showing, whichever list it was chosen from.
    /// </summary>
    /// <remarks>
    /// The header used to read the selected row's path, which only the changed-files list
    /// ever set - so an object picked in the project tree showed its diff under the previous
    /// file's name.
    /// </remarks>
    public string SelectedDiffPath
    {
        get => _selectedDiffPath;
        private set => SetProperty(ref _selectedDiffPath, value);
    }

    private bool _canStageHunks;

    /// <summary>
    /// Whether the open diff is one git could apply a hunk from.
    /// </summary>
    /// <remarks>
    /// False for an untracked file, whose diff came from <c>--no-index</c> against
    /// <c>/dev/null</c>. <c>git apply --cached</c> refuses that patch, and a button that
    /// produced an error every time would be worse than no button - <c>git add</c> is how a
    /// new file is staged, and there are no partial additions to choose between anyway.
    /// </remarks>
    public bool CanStageHunks
    {
        get => _canStageHunks;
        private set
        {
            if (SetProperty(ref _canStageHunks, value))
            {
                HunkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _hunkAction = "Stage hunk";

    /// <summary>
    /// What the per-hunk button says, which is also what it will do.
    /// </summary>
    /// <remarks>
    /// One button rather than two, labelled by the side the diff came from. Showing both
    /// would mean showing one that cannot apply — there is nothing to unstage in a
    /// working-tree hunk — and a disabled button beside a live one on every hunk of a long
    /// diff is a lot of furniture for no information.
    /// </remarks>
    public string HunkAction
    {
        get => _hunkAction;
        private set => SetProperty(ref _hunkAction, value);
    }

    /// <summary>
    /// The commit the open diff was read out of, when it was read out of one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null whenever the pane is showing the working tree or the index. Set, it carries the
    /// commit and — for a single file rather than a whole patch — the path, which is what
    /// re-reading needs: <see cref="SelectedDiffPath"/> is a caption by then, not a path, and
    /// asking git for a diff of "PLC_PRG.st @ 1a2b3c4" would find nothing.
    /// </para>
    /// <para>
    /// It is also what keeps a refresh from replacing a historical diff with the working-tree
    /// diff of the same file, which would silently answer a different question than the one
    /// the user clicked on.
    /// </para>
    /// </remarks>
    private (string Id, string ShortId, string Caption, string? Path)? _shownCommit;

    /// <summary>How much context git is asked for, following the pane's own checkbox.</summary>
    private int DiffContext => ShowFullFile ? GitDiff.FullFileContext : GitDiff.DefaultContext;

    /// <summary>
    /// Which side of the index the open diff came from.
    /// </summary>
    /// <remarks>
    /// Remembered rather than re-derived, because it decides where a staged hunk goes: a
    /// working-tree hunk is applied to the index, and an index hunk is reversed out of it.
    /// Guessing wrong would silently do the opposite of what the button said.
    /// </remarks>
    private bool _selectedDiffStaged;

    /// <summary>Show whatever a tree row has to show.</summary>
    /// <remarks>
    /// A <c>.project</c> row reports its export instead of a diff. That is the whole premise
    /// of the tool stated in the one place somebody is most likely to test it: the binary
    /// file has nothing to compare, and the text beside it does.
    /// </remarks>
    private void ShowNode(TreeRow row)
    {
        if (row.IsProject)
        {
            SelectedDiffPath = row.Node.Path;
            _shownCommit = null;
            _selectedDiffStaged = false;
            CanStageHunks = false;

            var coverage = Sync.CoverageFor(row.Node.Path);

            SelectedDiff = string.Join(Environment.NewLine,
            [
                row.Node.Path,
                string.Empty,
                "This is a binary project file, so there is no diff to show — which is the",
                "reason Versio exists. Its reviewable text is the tree underneath this row.",
                string.Empty,
                $"Export state: {(string.IsNullOrEmpty(row.Detail) ? "unknown" : row.Detail)}",
                row.Node.ChangedDescendants switch
                {
                    0 => "Nothing under it has changed.",
                    1 => "1 exported file under it has changed.",
                    var n => $"{n} exported files under it have changed.",
                },

                // How far the tree below can be trusted. Selecting the project is the moment
                // somebody asks "is this everything?", and it is the only place in the app
                // where the honest answer is sometimes no.
                .. coverage is null
                    ? Array.Empty<string>()
                    : new[] { string.Empty, "Coverage: " + coverage.Describe() },
            ]);

            return;
        }

        if (row.PrimaryFile is { } path)
        {
            _ = ShowPathAsync(path, staged: null);
        }
    }

    /// <summary>
    /// Load a diff for one path.
    /// </summary>
    /// <param name="staged">
    /// Which side to read, or null to work it out. Unstaged wins when both differ: a file
    /// staged and then edited again differs on both sides, and the working-tree change is the
    /// one the engineer just made and is looking for.
    /// </param>
    private async Task ShowPathAsync(string path, bool? staged)
    {
        if (_git is null)
        {
            return;
        }

        SelectedDiffPath = path;
        _shownCommit = null;

        try
        {
            var context = DiffContext;

            var fromIndex = staged ?? false;
            var diff = await _git.DiffAsync(path, fromIndex, CancellationToken.None, context)
                .ConfigureAwait(true);

            if (staged is null && string.IsNullOrWhiteSpace(diff))
            {
                fromIndex = true;
                diff = await _git.DiffAsync(path, staged: true, CancellationToken.None, context)
                    .ConfigureAwait(true);
            }

            _selectedDiffStaged = fromIndex;
            HunkAction = fromIndex ? "Unstage hunk" : "Stage hunk";

            // Untracked files arrive here as a real all-additions diff, so the only things
            // left with nothing to show are genuinely binary or genuinely unchanged. Saying
            // which beats a catch-all that blames untracked files for a gap that was ours.
            SelectedDiff = string.IsNullOrWhiteSpace(diff)
                ? path + Environment.NewLine + Environment.NewLine +
                  "Nothing to compare: this file is unchanged, or git reports it as binary."
                : diff;

            // A hunk can only be applied to a patch git produced against the index. An
            // untracked file's diff came from --no-index against /dev/null, and git apply
            // --cached would refuse it; `git add` is how a new file is staged.
            CanStageHunks = !string.IsNullOrWhiteSpace(diff) &&
                            !diff.Contains("--- /dev/null", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void ReportError(Exception ex)
    {
        _logger.Error(LogCategory.App, ex.Message, ex);
        StatusSummary = ex.Message;
    }

    /// <summary>
    /// Release the watcher.
    /// </summary>
    /// <remarks>
    /// A <see cref="FileSystemWatcher"/> holds an OS handle on the directory. Leaking one
    /// keeps the folder locked against rename or delete for the life of the process, which
    /// on a repository the user is trying to move is a visible bug rather than a slow leak.
    /// </remarks>
    public void Dispose()
    {
        StopWatching();

        // The sync panel holds its own watcher over every tracked project and its export
        // tree. Releasing only the shell's would leave those handles open and the repository
        // folder locked against rename or delete after the window closes.
        Sync.Detach();
        History.Detach();
    }
}
