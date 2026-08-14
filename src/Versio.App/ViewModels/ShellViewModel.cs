using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Versio.App.Mvvm;
using Versio.Core.Abstractions;
using Versio.Core.Diff;
using Versio.Core.Git;
using Versio.Core.Guarding;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Core.Settings;
using Versio.Core.Sync;
using Versio.Core.Watching;
using Versio.Infrastructure.FileSystem;
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
/// <remarks>
/// Carries the remote and the last-opened time because the dropdown is a memory now rather
/// than a session: the list survives a restart, and an entry whose folder has since gone can
/// only be offered back if its URL was recorded while it was still there.
/// </remarks>
public sealed class RepoEntry(string path, string remoteUrl = "", DateTimeOffset? lastOpened = null)
{
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));

    /// <summary>Parent directory, shown beneath the name so two repos with the same folder
    /// name are still distinguishable.</summary>
    public string Location { get; } = System.IO.Path.GetDirectoryName(path) ?? path;

    /// <summary><c>origin</c>'s URL when this repository was last opened, or empty.</summary>
    public string RemoteUrl { get; } = remoteUrl;

    /// <summary>When it was last opened, or null for one opened in this session only.</summary>
    public DateTimeOffset? LastOpenedUtc { get; } = lastOpened;

    /// <summary>This entry as the settings file records it.</summary>
    public RecentRepository ToRecord(DateTimeOffset when) => new()
    {
        Path = Path,
        RemoteUrl = RemoteUrl,
        LastOpenedUtc = when,
    };
}

/// <summary>An entry in the branch dropdown.</summary>
public sealed class BranchEntry(BranchInfo branch)
{
    public string Name { get; } = branch.Name;

    public bool IsCurrent { get; } = branch.IsCurrent;

    public string Tracking { get; } = branch.Upstream ?? "no upstream";

    public string AheadBehind { get; } =
        branch is { Ahead: 0, Behind: 0 } ? string.Empty : $"↑{branch.Ahead} ↓{branch.Behind}";

    /// <summary>
    /// Which semantic colour the divergence deserves.
    /// </summary>
    /// <remarks>
    /// Behind is the one that matters: it means somebody else has moved and this branch has not
    /// caught up, which is the state a pull answers. Ahead alone is normal - work that has not
    /// been pushed yet - so it is stated without being coloured like a problem. Named as a key
    /// rather than a brush for the reason <see cref="ChangeRow.BrushKey"/> is: the view model
    /// decides the meaning and the theme decides the paint.
    /// </remarks>
    public string BrushKey { get; } = branch.Behind > 0
        ? "Brush.Status.Modified"
        : "Brush.Text.Secondary";
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

    private readonly IAppSettingsStore _settings;

    /// <summary>
    /// Where this clone's auto-stashes are recorded. Rebuilt for each repository opened.
    /// </summary>
    /// <remarks>
    /// Constructed here rather than injected, unlike <see cref="_settings"/>, and the
    /// difference is where the file lives: this one is inside the repository being opened, so
    /// a test that opens a temp repository writes into that temp repository and cleans up with
    /// it. The settings store writes to the user's own machine, which is why the host owns it.
    /// </remarks>
    private IAutoStashStore? _autoStash;

    /// <summary>
    /// The recent list as it is on disk, which is not what the dropdown holds.
    /// </summary>
    /// <remarks>
    /// The <see cref="RecentRepositories"/> collection is for WPF and holds view objects; this
    /// is the record written back. Kept apart so the ordering and capping rules stay in
    /// <see cref="Core.Settings.RecentRepositories"/>, where they are tested without a window.
    /// </remarks>
    private IReadOnlyList<RecentRepository> _recent = [];

    private GitCliClient? _git;
    private readonly RemotePoller _poller;

    /// <summary>True while a question is on screen waiting for an answer.</summary>
    /// <remarks>
    /// Suspends the poller. A background fetch landing while somebody is deciding whether to
    /// stash their work would change the thing they are deciding about.
    /// </remarks>
    private bool _modalOpen;

    /// <summary>
    /// Mark a modal question as being on screen for as long as this is held.
    /// </summary>
    /// <remarks>
    /// A scope rather than a flag set at each call site, because there are six of them and one
    /// that forgot to clear the flag on an early return would silence the remote watch for the
    /// rest of the session with nothing to show why.
    /// </remarks>
    private ModalScope AskingSomething() => new(this);

    private readonly struct ModalScope : IDisposable
    {
        private readonly ShellViewModel _shell;

        public ModalScope(ShellViewModel shell)
        {
            _shell = shell;
            shell._modalOpen = true;
        }

        public void Dispose() => _shell._modalOpen = false;
    }
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
                          IClock? clock = null,
                          Func<PlcVendor, IVersioLogger, IPlcToolchain?>? toolchains = null,
                          IAppSettingsStore? settings = null)
    {
        _logger = logger;
        Log = log;
        _clock = clock ?? new SystemClock();
        // Null by default, real only when the host passes one. A view model that reached
        // for %LOCALAPPDATA% on its own rewrote the developer's recent list every time a
        // test opened a temp repository.
        _settings = settings ?? NullAppSettingsStore.Instance;

        // Built here rather than in an initialiser because it needs the clock: every scope
        // records when it started, so a dialog can say how long the user has been waiting
        // rather than only that something is happening.
        Busy = new BusyTracker(_clock, logger);

        // Events, as against the state StatusSummary holds. The two were one property until
        // now, and the collision was not theoretical - see NotificationCenter's remarks.
        Notifications = new NotificationCenter(_clock, logger);

        // Keeps the ahead/behind counts honest without anybody asking. It probes with
        // ls-remote and fetches only when the remote has actually moved - and it NEVER pulls,
        // because a pull rewrites the project and its mirror under the watcher, which is the
        // one scenario SAFEGUARDS §5.A calls able to corrupt a project through no user error.
        _poller = new RemotePoller(
            logger,
            _clock,
            client: () => _git,

            // One shared flag, so a probe cannot compete with a push for the network or the
            // credential helper, and a modal question is never answered by a background fetch.
            suspended: () => Busy.IsBusy || _modalOpen,
            target: PollTarget,
            onMoved: FetchBecauseTheRemoteMovedAsync,
            onHalted: reason => OnUi(() => Notifications.Warning(
                "Not watching the remote any more.", reason)));

        // The SHELL's tracker, not one of its own.
        //
        // This defaulted, so the sync view model built a private BusyTracker while the
        // progress bar binds to this one — meaning the app-wide bar never appeared for any
        // toolchain work at all, including the catch-up and commit-gate exports that both
        // carefully open a scope for it. Two objects, both correct, wired to nothing.
        Sync = new SyncViewModel(logger, _clock, Busy, toolchains);
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

        // A project adopted while the app is running writes new .gitignore rules, and its
        // toolchain starts rewriting a lock marker a second later. Without this the watcher
        // would keep spending a git status on it until the next repository switch.
        Sync.IgnoredPathsChanged += globs => _watcher?.KnownIgnored(globs);

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
    public BusyTracker Busy { get; }

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
                _ = SwitchRepositoryAsync(value);
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
                _ = SwitchBranchAsync(value);
            }
        }
    }

    /// <summary>
    /// Whether the repository and branch dropdowns accept a selection at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bound to <c>IsEnabled</c> on both, so the half of the problem with no safe answer is
    /// <b>unreachable</b> rather than refused after the click. A dropdown that accepts a
    /// selection, opens a dialog and then snaps back is a worse experience than one that was
    /// visibly unavailable — and it briefly shows a branch the user is not on, which is the one
    /// bug this area already had.
    /// </para>
    /// <para>
    /// Only uninterruptible work closes them. An export is 17-31 s and there is a real choice
    /// worth offering during it, so the dropdown stays live and the guard does the asking.
    /// </para>
    /// </remarks>
    public bool CanChangeContext => !Busy.HasUninterruptible;

    /// <summary>
    /// Switch repository, once anything in flight has been dealt with.
    /// </summary>
    /// <remarks>
    /// Nothing about the working tree matters here: the folder being left stays on disk, still
    /// dirty, still holding its locks, and is still there on the way back. Only a run in
    /// progress is a reason to pause, because that one is pointed at the repository we are
    /// about to stop looking at.
    /// </remarks>
    /// <remarks>
    /// Internal rather than private so a test can await it. The setter is a fire-and-forget
    /// dispatcher — it has to be, since a property setter cannot await — and a test driving it
    /// through the property would be racing the work it wants to assert on.
    /// </remarks>
    internal async Task SwitchRepositoryAsync(RepoEntry target)
    {
        if (!await ClearForContextChangeAsync(GuardIntent.SwitchRepository, target.Name)
                .ConfigureAwait(true))
        {
            ShowOpenRepository();
            return;
        }

        // Checked before the guard's work is undone but after it has agreed, because a
        // repository that is not there is a different conversation from one that is: the
        // generic refusal says "not a Git repository" and offers a folder picker, which is
        // an odd answer to a row the user was told they could come back to.
        if (await MissingAsync(target).ConfigureAwait(true))
        {
            ShowOpenRepository();
            return;
        }

        await OpenAsync(target.Path).ConfigureAwait(true);

        // OpenAsync refuses a folder git will not work in and says why through
        // RepositoryRejected. The dropdown must not be left pointing at it.
        if (target.Path != _repositoryPath)
        {
            ShowOpenRepository();
        }
    }

    /// <summary>
    /// Ask the view to decide what to do about a remembered repository that has gone.
    /// </summary>
    /// <remarks>
    /// <b>Unset means cancel</b>, the same way <see cref="AskGuard"/> is unset: a view model
    /// with no view attached must never take the destructive-looking branch by default.
    /// </remarks>
    public Func<MissingRepositoryPrompt, MissingRepositoryChoice?>? AskAboutMissingRepository
    {
        get;
        set;
    }

    /// <summary>
    /// Deal with a recent repository that is no longer where it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns true when the switch must not proceed — which is every outcome except a
    /// repository that is still there. Cloning re-enters through the ordinary clone path, so
    /// what opens afterwards is a repository that has just been verified rather than a path
    /// this method assumed would appear.
    /// </para>
    /// <para>
    /// A folder that exists but is no longer a repository is included, because it is the same
    /// question with the same three answers. Whether git will work there is asked of git
    /// itself, not inferred from a <c>.git</c> directory being present.
    /// </para>
    /// </remarks>
    private async Task<bool> MissingAsync(RepoEntry target)
    {
        var reason = !Directory.Exists(target.Path)
            ? MissingRepositoryReason.Gone
            : await _gitFactory(target.Path).IsRepositoryAsync(CancellationToken.None)
                .ConfigureAwait(true)
                ? (MissingRepositoryReason?)null
                : MissingRepositoryReason.NotARepository;

        if (reason is not { } why)
        {
            return false;
        }

        _logger.Warn(LogCategory.App,
            $"recent repository {target.Path} is {(why == MissingRepositoryReason.Gone
                ? "no longer on disk" : "no longer a git repository")}");

        var record = target.ToRecord(target.LastOpenedUtc ?? _clock.UtcNow);
        using var asking = AskingSomething();
        var choice = AskAboutMissingRepository?.Invoke(MissingRepository.Prompt(record, why))
                     ?? MissingRepositoryChoice.Cancel;

        switch (choice)
        {
            case MissingRepositoryChoice.Forget:
                await ForgetAsync(target.Path).ConfigureAwait(true);
                break;

            case MissingRepositoryChoice.CloneAgain:
                // The path, never the URL. A remote can carry a token in its userinfo, and
                // the redaction that handles that is inside the git client where every
                // argument passes through it - not something a view model reimplements.
                _logger.Info(LogCategory.Git, $"re-cloning the repository that was at {target.Path}");
                await CloneRepositoryAsync(target.RemoteUrl).ConfigureAwait(true);
                break;

            default:
                // Left in the list on purpose. A drive that is not mounted today is mounted
                // tomorrow, and the entry is the only record of where the repository was.
                break;
        }

        return true;
    }

    /// <summary>
    /// Switch branch, once anything in flight has been dealt with.
    /// </summary>
    /// <remarks>
    /// <b>The work half of the guard only.</b> A dirty tree and a held direction lock are also
    /// reasons to stop somebody switching branch — safeguards §3.1 — but the answer to both is
    /// the auto-stash, which is not built yet. Until it is, a dirty checkout stays git's to
    /// allow or refuse, which it does correctly and which <see cref="CheckoutAsync"/> now
    /// reports properly. Asking about a tree nothing can yet act on would mean offering a
    /// "stash my changes" button that does nothing.
    /// </remarks>
    /// <remarks>Internal for the same reason as <see cref="SwitchRepositoryAsync"/>.</remarks>
    internal async Task SwitchBranchAsync(BranchEntry target)
    {
        if (!await ClearForContextChangeAsync(GuardIntent.SwitchBranch, target.Name)
                .ConfigureAwait(true))
        {
            ShowCurrentBranch();
            return;
        }

        await CheckoutAsync(target.Name).ConfigureAwait(true);
    }

    /// <summary>
    /// Put the guard's question about work in flight, and carry out the answer.
    /// </summary>
    /// <remarks>
    /// Shared by both dropdowns so they cannot come to differ about what "something is running"
    /// means. Waiting and cancelling both end by waiting: cancelling is a request, and the
    /// operation is over when its own <c>await</c> unwinds, not when the token is tripped.
    /// </remarks>
    /// <returns>True when the switch may go ahead.</returns>
    private async Task<bool> ClearForContextChangeAsync(GuardIntent intent, string target)
    {
        // Ask, with the real working tree in it. This used to go through a wrapper that
        // passed WorkingTreeSnapshot.Clean deliberately - offering to stash before anything
        // could stash would be a button that lies about what it does - and the wrapper became
        // a synonym for Ask the moment the auto-stash existed.
        switch (Ask(intent, target))
        {
            case GuardChoice.Proceed:
                return true;

            case GuardChoice.CancelWorkAndProceed:
                Busy.CancelCancellable();
                await Busy.WhenIdleAsync().ConfigureAwait(true);
                return true;

            case GuardChoice.WaitThenProceed:
                StatusSummary =
                    $"Waiting for the current work to finish, then switching to {target}…";
                await Busy.WhenIdleAsync().ConfigureAwait(true);
                return true;

            case GuardChoice.StashAndProceed:
                // False here means the stash did not happen, and the switch must not either.
                return await TakeAutoStashAsync(target).ConfigureAwait(true);

            default:
                _logger.Info(LogCategory.Ui, $"switch to {target} cancelled");
                return false;
        }
    }

    /// <summary>
    /// Commit messages typed but not yet committed, per repository.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not in <c>repo.json</c>.</b> That file is committed and shared, so a half-written
    /// commit message would be pushed to everybody else on the project — a draft is exactly the
    /// kind of thing that must not leave the machine it was typed on.
    /// </para>
    /// <para>
    /// In memory, so it lasts the session rather than for ever. Switching to another repository
    /// and back is the case this is for, and it happens within a session by definition. Surviving
    /// a restart would need a store under <c>.versio/local/</c>, which is a bigger thing than the
    /// problem asks for.
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, string> _commitDrafts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Forget everything that belonged to the repository being left.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One method, so a field added later has an obvious home.</b> Before this, switching
    /// repository called <c>Sync.AttachAsync</c> and never <c>Sync.Detach</c>, so the panel kept
    /// the previous repository's expanded folders, coverage, staleness and search — and the
    /// shell kept its diff, its commit message and its branch list. Some of it was corrected a
    /// moment later by the refresh, which made the rest look like a glitch rather than a bug.
    /// </para>
    /// <para>
    /// <b>Called only once the switch is certain.</b> A folder git will not work in is refused
    /// before this runs, so picking the wrong directory costs nothing — the repository already
    /// open is still open, still showing its diff, still holding the message being typed. Doing
    /// this first and asking questions afterwards would punish a misclick with the loss of
    /// whatever was on screen.
    /// </para>
    /// <para>
    /// The collections are cleared inside <see cref="WithoutSideEffects"/>: they are bound to
    /// controls with a selection, and emptying one raises a selection change that would
    /// otherwise be read as the user asking to switch branch.
    /// </para>
    /// </remarks>
    /// <param name="leaving">The repository being closed, or null on the first open.</param>
    /// <param name="arrivingAt">The repository about to be opened.</param>
    internal void ResetForRepositoryChange(string? leaving, string arrivingAt)
    {
        if (leaving is not null)
        {
            _logger.Info(LogCategory.App, $"leaving {leaving} for {arrivingAt}");
            RememberCommitDraft(leaving);
        }

        WithoutSideEffects(() =>
        {
            Changes.Clear();
            Staged.Clear();
            Unstaged.Clear();
            Branches.Clear();

            SelectedStaged = null;
            SelectedUnstaged = null;
            SelectedBranch = null;
        });

        BranchName = "—";
        AheadBehind = string.Empty;
        Standing = RemoteStanding.None;
        _hasRemote = false;
        _remote = RemoteUrl.None;
        AuthState = GitAuthState.Fine;

        // The poller is about a repository that is no longer open. Resume clears the halt as
        // well, so a credential problem on the last one does not silence the next.
        _poller.Stop();
        _poller.Resume();

        // These notifications are about a repository that is no longer open. Keeping them
        // would attach a failed push to whatever is opened next.
        Notifications.Clear();
        StatusSummary = "Opening…";
        _lastRefreshUtc = null;
        LastRefreshText = string.Empty;
        WatchState = string.Empty;

        // The diff pane, including which commit it was reading out of. A patch from the old
        // repository's history is not merely stale here — that commit does not exist.
        SelectedDiff = string.Empty;
        SelectedDiffPath = string.Empty;
        _shownCommit = null;
        _selectedDiffStaged = false;
        ShowFullFile = false;

        // Handed back if this repository has one waiting. Empty otherwise, which is the point:
        // a message typed for one project must not be sitting in another project's box next to
        // a live Commit button.
        CommitMessage = RecallCommitDraft(arrivingAt);

        // The two panels that own their own state clear it themselves. Detach is what was
        // never called, and it already knew what to forget.
        Sync.Detach();
        History.Detach();

        RaiseGitCommandStates();
    }

    /// <summary>Keep the message being typed, if there is one.</summary>
    private void RememberCommitDraft(string repository)
    {
        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            _commitDrafts.Remove(repository);
            return;
        }

        _commitDrafts[repository] = CommitMessage;

        _logger.Debug(LogCategory.App,
            $"kept the unsent commit message for {repository} ({CommitMessage.Length} chars)");
    }

    /// <summary>The message that was being typed here last time, or empty.</summary>
    private string RecallCommitDraft(string repository)
    {
        if (!_commitDrafts.TryGetValue(repository, out var draft))
        {
            return string.Empty;
        }

        _logger.Info(LogCategory.App,
            $"restored the unsent commit message for {repository}");

        return draft;
    }

    /// <summary>Put the branch dropdown back on the branch we are actually on.</summary>
    /// <remarks>
    /// A dropdown showing a branch you are not on is worse than no dropdown: every other panel
    /// is describing HEAD, and the one control claiming otherwise is the one the user believes.
    /// This was a real bug before the guard existed — a refused checkout left the selection
    /// wherever the click had put it, and nothing ever moved it back.
    /// </remarks>
    private void ShowCurrentBranch() =>
        WithoutSideEffects(() =>
            SelectedBranch = Branches.FirstOrDefault(b => b.IsCurrent));

    /// <summary>
    /// Fill the repository dropdown from what this machine remembers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Awaited by the host before the first repository is opened, so the list is already
    /// there when <see cref="OpenAsync"/> promotes one to the top of it. Called after, the
    /// load would overwrite the entry that had just been opened.
    /// </para>
    /// <para>
    /// The dropdown held only what the current session had opened until now — which on a
    /// fresh start is one repository, the one the app opened by itself, so a control whose
    /// entire purpose is jumping between repositories could never do it.
    /// </para>
    /// </remarks>
    public async Task LoadRecentAsync(CancellationToken ct = default)
    {
        _recent = (await _settings.ReadAsync(ct).ConfigureAwait(true)).Recent;

        WithoutSideEffects(() =>
        {
            RecentRepositories.Clear();
            foreach (var entry in _recent)
            {
                RecentRepositories.Add(
                    new RepoEntry(entry.Path, entry.RemoteUrl, entry.LastOpenedUtc));
            }

            SelectedRepository = RecentRepositories.FirstOrDefault(r => r.Path == _repositoryPath);
        });

        _logger.Debug(LogCategory.App,
            $"{_recent.Count} recent repositor(ies) restored");
    }

    /// <summary>
    /// The working tree root for a folder, or null when it is not inside a repository.
    /// </summary>
    /// <remarks>
    /// Exposed for the startup choice, which has to know whether the working directory is
    /// worth opening <i>before</i> it falls back to the recent list. Asking the locator here
    /// rather than testing for a <c>.git</c> folder keeps one answer to "is this a
    /// repository" — the one that already handles subdirectories, bare repos and worktrees.
    /// </remarks>
    public string? WorkingTreeRootOf(string path) =>
        _locator.Locate(path) is { Kind: RepositoryKind.WorkingTreeRoot
                                        or RepositoryKind.InsideWorkingTree } found
            ? found.WorkingTreeRoot
            : null;

    /// <summary>The most recently opened repository that is still on disk, or null.</summary>
    /// <remarks>
    /// The startup fallback, for the ordinary case of launching from a shortcut rather than
    /// from a repository's own folder. Checked for existence here so the host does not open
    /// something that will only be refused — and left in the list either way, because a
    /// disconnected drive is not a deleted repository.
    /// </remarks>
    public string? MostRecentExisting() =>
        _recent.FirstOrDefault(r => Directory.Exists(r.Path))?.Path;

    /// <summary>Move a repository to the top of the list, and write the list down.</summary>
    private async Task RememberAsync(string path, string remoteUrl)
    {
        _recent = Core.Settings.RecentRepositories.Promote(
            _recent,
            new RecentRepository
            {
                Path = path,
                RemoteUrl = remoteUrl,
                LastOpenedUtc = _clock.UtcNow,
            });

        SyncRecentToDropdown(path);
        await _settings.WriteAsync(new AppSettings { Recent = _recent }, CancellationToken.None)
            .ConfigureAwait(true);
    }

    /// <summary>Take a repository out of the list, and write the list down.</summary>
    private async Task ForgetAsync(string path)
    {
        _recent = Core.Settings.RecentRepositories.Remove(_recent, path);

        SyncRecentToDropdown(_repositoryPath);
        await _settings.WriteAsync(new AppSettings { Recent = _recent }, CancellationToken.None)
            .ConfigureAwait(true);

        _logger.Info(LogCategory.App, $"removed {path} from the recent repositories");
    }

    /// <summary>Rebuild the dropdown from the record, keeping the selection meaningful.</summary>
    /// <remarks>
    /// Rebuilt wholesale rather than reconciled, unlike the project tree: this list is at most
    /// ten rows of two strings, nothing about it is expensive, and nothing in it is expanded
    /// or scrolled. <see cref="WithoutSideEffects"/> is what keeps the reselection from
    /// reading as the user choosing a repository.
    /// </remarks>
    private void SyncRecentToDropdown(string? select) =>
        WithoutSideEffects(() =>
        {
            RecentRepositories.Clear();
            foreach (var entry in _recent)
            {
                RecentRepositories.Add(
                    new RepoEntry(entry.Path, entry.RemoteUrl, entry.LastOpenedUtc));
            }

            SelectedRepository = RecentRepositories.FirstOrDefault(r => r.Path == select);
        });

    /// <summary>Put the repository dropdown back on the repository that is actually open.</summary>
    private void ShowOpenRepository() =>
        WithoutSideEffects(() =>
            SelectedRepository = RecentRepositories.FirstOrDefault(r => r.Path == _repositoryPath));

    /// <summary>
    /// Move a dropdown's selection without that counting as the user picking something.
    /// </summary>
    /// <remarks>
    /// The setters act on a change, and a revert is a change. Without this the revert would be
    /// read as a fresh request to switch — back to where we already are, which is merely
    /// wasteful, or into two dropdowns correcting each other, which is not.
    /// </remarks>
    private void WithoutSideEffects(Action change)
    {
        _suppressSelectionSideEffects = true;
        try
        {
            change();
        }
        finally
        {
            _suppressSelectionSideEffects = false;
        }
    }

    /// <summary>Panel currently selected in the rail.</summary>
    /// <summary>
    /// Wake the remote watch: somebody is here.
    /// </summary>
    /// <remarks>
    /// The backoff exists to stop polling for nobody. Any deliberate git action, or opening the
    /// panel, means there is a somebody — and the counts should be as live as they were at the
    /// start rather than on a sixty-second cadence inherited from an idle hour.
    /// </remarks>
    private void WakeThePoller() => _poller.Wake();

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

    private RemoteStanding _standing = RemoteStanding.None;
    private bool _hasRemote;

    /// <summary>
    /// Where this branch stands in relation to the remote, in words.
    /// </summary>
    /// <remarks>
    /// Replaces reading the two arrows. <see cref="AheadBehind"/> survives for the branch
    /// dropdown, where a compact glyph beside a name is the right shape; the panel itself now
    /// says what the numbers mean, because that is the question people open it to answer.
    /// </remarks>
    public RemoteStanding Standing
    {
        get => _standing;
        private set
        {
            if (SetProperty(ref _standing, value))
            {
                OnPropertyChanged(nameof(PushLabel));
                OnPropertyChanged(nameof(PullLabel));
                OnPropertyChanged(nameof(ShowPublish));
                OnPropertyChanged(nameof(ShowPush));
                RaiseGitCommandStates();
            }
        }
    }

    /// <summary>Push and Publish swap places: they are different operations, so only one is
    /// offered at a time rather than one button meaning two things.</summary>
    public bool ShowPublish => _standing.CanPublish;

    public bool ShowPush => !_standing.CanPublish;

    /// <summary>The count on the label, where it informs without gating.</summary>
    public string PushLabel => "Push" + _standing.PushCountSuffix;

    public string PullLabel => "Pull" + _standing.PullCountSuffix;

    // ----------------------------------------------------------- credentials (plan §12c)

    private RemoteUrl _remote = RemoteUrl.None;
    private GitAuthState _authState = GitAuthState.Fine;

    /// <summary>
    /// What stands between this machine and the remote, as far as identity is concerned.
    /// </summary>
    /// <remarks>
    /// Set from a proactive check when a repository opens, and from the classification of any
    /// network operation that fails. Cleared the moment one succeeds — a stale "sign in needed"
    /// banner above a working remote is worse than none.
    /// </remarks>
    public GitAuthState AuthState
    {
        get => _authState;
        private set
        {
            if (SetProperty(ref _authState, value))
            {
                OnPropertyChanged(nameof(AuthExplanation));
                OnPropertyChanged(nameof(NeedsAttentionForAuth));
                OnPropertyChanged(nameof(CanSignIn));
                SignInCommand.RaiseCanExecuteChanged();
                SignOutCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The host the credential belongs to, for the sentence and the lookup.</summary>
    public string RemoteHost => _remote.Host;

    public string AuthExplanation => GitAuthClassifier.Explain(_authState, _remote.Host);

    /// <summary>True whenever there is something to say about credentials.</summary>
    public bool NeedsAttentionForAuth => _authState is not GitAuthState.Fine;

    /// <summary>
    /// True only when signing in could plausibly help.
    /// </summary>
    /// <remarks>
    /// <b>False for a 403 and for an SSH key refusal.</b> Both read like authentication
    /// failures and neither is one: a 403 means the credential worked and the account lacks
    /// access, and SSH has no credential helper at all. Offering the button on either is
    /// offering something that cannot work — and in the 403 case it is a loop that cannot
    /// terminate.
    /// </remarks>
    public bool CanSignIn =>
        _remote.UsesCredentialHelper && GitAuthClassifier.SignInWouldHelp(_authState);

    public AsyncCommand SignInCommand { get; private set; } = null!;

    public AsyncCommand SignOutCommand { get; private set; } = null!;

    /// <summary>
    /// What just happened, as against what is currently true.
    /// </summary>
    /// <remarks>
    /// <see cref="StatusSummary"/> keeps only durable state now - "3 changed", "No changes".
    /// Every result of an operation goes here instead, which is why a refresh can no longer
    /// overwrite the answer to what the user just pressed.
    /// </remarks>
    public NotificationCenter Notifications { get; }

    public string StatusSummary
    {
        get => _statusSummary;
        private set => SetProperty(ref _statusSummary, value);
    }

    // ------------------------------------------------------- git's own health (plan §12a/§12b)

    private GitPresence _gitPresence = GitPresence.Unknown;
    private GitIdentity _identity = GitIdentity.None;

    /// <summary>
    /// Whether the git everything runs through is actually there.
    /// </summary>
    /// <remarks>
    /// Probed once at launch by <see cref="ProbeGitAsync"/> rather than on first use, so a
    /// machine without git is told in a sentence instead of discovering it as an exception from
    /// somewhere deep. Starts <see cref="GitPresence.Unknown"/>, which is not
    /// <c>Missing</c> — see that type.
    /// </remarks>
    public GitPresence GitPresence
    {
        get => _gitPresence;
        private set
        {
            if (SetProperty(ref _gitPresence, value))
            {
                OnPropertyChanged(nameof(GitIsMissing));
                OnPropertyChanged(nameof(GitPresenceExplanation));
            }
        }
    }

    /// <summary>True only once the probe has come back and said no.</summary>
    public bool GitIsMissing => _gitPresence.Availability == GitAvailability.Missing;

    public string GitPresenceExplanation => _gitPresence.Explanation;

    /// <summary>
    /// Who git will attribute a commit to in the open repository.
    /// </summary>
    /// <remarks>
    /// Read at open, and again after the user sets it. <see cref="CanCommit"/> gates on it, so
    /// the export gate never runs a 17–31 second CODESYS export for a commit that git was
    /// always going to refuse.
    /// </remarks>
    public GitIdentity Identity
    {
        get => _identity;
        private set
        {
            if (SetProperty(ref _identity, value))
            {
                OnPropertyChanged(nameof(IdentityIsMissing));
                OnPropertyChanged(nameof(IdentityExplanation));
                IdentityName = value.Name;
                IdentityEmail = value.Email;
                RaiseGitCommandStates();
            }
        }
    }

    /// <summary>True when a repository is open and git could not name a committer.</summary>
    /// <remarks>
    /// Gated on having a repository, because with none open there is nothing to be missing an
    /// identity <i>for</i>, and a prompt on an empty shell is noise.
    /// </remarks>
    public bool IdentityIsMissing => _git is not null && !_identity.IsComplete;

    public string IdentityExplanation => _identity.Explanation;

    private string _identityName = string.Empty;
    private string _identityEmail = string.Empty;
    private bool _identityGlobal;

    /// <summary>Edit buffer for the name field. Separate from <see cref="Identity"/> so typing
    /// does not claim to have configured anything until Save is pressed.</summary>
    public string IdentityName
    {
        get => _identityName;
        set
        {
            if (SetProperty(ref _identityName, value))
            {
                SaveIdentityCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string IdentityEmail
    {
        get => _identityEmail;
        set
        {
            if (SetProperty(ref _identityEmail, value))
            {
                OnPropertyChanged(nameof(IdentityEmailLooksWrong));
                SaveIdentityCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Write to the global config as well as this repository.
    /// </summary>
    /// <remarks>
    /// Off by default. A work machine carrying a personal global identity is a common and
    /// annoying way to attribute commits to the wrong person, and the repository in front of
    /// the user is the one they are answering about.
    /// </remarks>
    public bool IdentityGlobal
    {
        get => _identityGlobal;
        set => SetProperty(ref _identityGlobal, value);
    }

    /// <summary>Advisory: the address does not have the shape of one. Never blocks Save.</summary>
    public bool IdentityEmailLooksWrong =>
        _identityEmail.Length > 0 && !GitIdentity.From(_identityName, _identityEmail).LooksLikeEmail;

    public AsyncCommand SaveIdentityCommand { get; private set; } = null!;

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

        // Which git, not just which version. A machine can carry a Git for Windows install, one
        // inside a vendor toolchain and one on a build agent's PATH; they all answer --version
        // and only one of them has the credentials. Blank until the launch probe has resolved
        // it, and blank is not a failure - see GitPresence.ExecutablePath.
        rows.Add(new Views.SystemInfoRow("Path",
            GitPresence.ExecutablePath.Length > 0 ? GitPresence.ExecutablePath : "not resolved"));

        rows.Add(new Views.SystemInfoRow("Commits as",
            _identity.IsComplete
                ? $"{_identity.Name} <{_identity.Email}>"
                : _identity.Explanation));

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

        using var busy = Busy.Begin(WorkKind.Scan, $"Opening {System.IO.Path.GetFileName(path.TrimEnd('\\', '/'))}…");

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
        if (!await client.IsRepositoryAsync(busy.Token).ConfigureAwait(true))
        {
            var rejected = location with { Kind = RepositoryKind.NotARepository };
            _logger.Warn(LogCategory.Git, $"git refused to open {path}");
            StatusSummary = rejected.Explanation;
            RepositoryRejected?.Invoke(rejected);
            return false;
        }

        // Past every refusal, so the switch is certain. Everything on screen belongs to the
        // repository being left, and this is the last moment at which that is still true.
        ResetForRepositoryChange(_repositoryPath, path);

        _git = client;
        RepositoryPath = path;
        _autoStash = new JsonAutoStashStore(path, _logger);

        // Before anything is written into .versio/local/, and per clone rather than per
        // project: an auto-stash record that git can see is a record the NEXT auto-stash
        // swallows, leaving real work in the stash with nothing pointing at it.
        await LocalExclude.EnsureAsync(
            await client.GitDirectoryAsync(busy.Token).ConfigureAwait(true),
            _logger,
            busy.Token).ConfigureAwait(true);

        // Read while the repository is open, because this is the only time it can be read.
        // When it is wanted - offering to clone a vanished repository back - there is no
        // .git left to ask. Empty for a repository with no remote, which is ordinary.
        var remoteUrl = await client.RemoteUrlAsync("origin", busy.Token).ConfigureAwait(true);

        await RememberAsync(path, remoteUrl).ConfigureAwait(true);

        // Before the first refresh, and long before anything can be committed, so the panel
        // can say "git needs a name" while the user is still looking at an empty commit box
        // rather than after a 17-31 s export has run for a commit git will refuse.
        await LoadIdentityAsync(busy.Token).ConfigureAwait(true);

        // Whether there is anywhere to push at all. Read here rather than in the refresh for
        // the reason above, and before the first refresh so the very first Standing computed
        // is already right - otherwise a repository with a remote would flash "no remote
        // configured" for one frame on every open.
        _hasRemote = (await client.RemotesAsync(busy.Token).ConfigureAwait(true)).Count > 0;

        // The remote's shape decides what a credential failure will mean, so it is parsed once
        // here rather than re-derived at each failure. remoteUrl was already read above for
        // the recent-repositories list.
        _remote = RemoteUrl.Parse(remoteUrl);
        OnPropertyChanged(nameof(RemoteHost));

        await CheckCredentialAsync(busy.Token).ConfigureAwait(true);

        RefreshCommand.RaiseCanExecuteChanged();

        // Watch before the first refresh, so a change landing during it is not missed.
        StartWatching(path);

        // Only once there is something to watch. Start is idempotent, and PollTarget returns
        // nothing until a Standing with an upstream has been computed.
        _poller.Start();

        await RefreshCoreAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);

        // The history is handed the client but reads nothing yet. Whether this repository's
        // log is ever walked is now the user's decision, made by clicking the rail.
        History.Attach(client);

        // Scan for PLC projects after the git state is up, so the sync panel is populated
        // by the time the user reaches for it.
        await Sync.AttachAsync(path, client, busy.Token).ConfigureAwait(true);
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

        // The bindings are not loaded yet - Sync attaches after this - so the list starts
        // with what is true of every repository and is replaced the moment the scan knows
        // better. Starting empty is the safe direction: it filters nothing.
        var watcher = new RepositoryWatcher(
            path, _clock, logger: _logger, knownIgnored: Sync.IgnoredPaths());

        watcher.Settled += OnRepositoryChanged;
        watcher.Tick += OnWatcherTick;
        _watcher = watcher;

        WatchState = watcher.IsDegraded ? "polling" : "live";
        _logger.Info(LogCategory.Git,
            watcher.IsDegraded
                ? "filesystem watching unavailable; the file list will refresh periodically"
                : "watching the working tree for changes");
    }

    /// <summary>What the repository watcher is ignoring by name, for a test.</summary>
    /// <remarks>
    /// Internal rather than public: nothing in the app needs it, and the reason it exists is
    /// that the alternative - trusting that a subscription written in this file is wired to a
    /// watcher created in this file - is exactly the assumption that has been wrong three
    /// times here.
    /// </remarks>
    internal IReadOnlyList<string> WatcherIgnoredGlobs => _watcher?.IgnoredGlobs ?? [];

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

    /// <summary>Put a branch on the remote for the first time.</summary>
    /// <remarks>
    /// Separate from <see cref="PushCommand"/> because it is a separate operation: a plain
    /// push on a branch with no upstream fails outright, so folding the two together would
    /// give one button two meanings and a failure that only appears sometimes.
    /// </remarks>
    public AsyncCommand PublishCommand { get; private set; } = null!;

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

        // Pull is enabled whenever an upstream exists, NOT only when behind. Behind-ness is
        // as fresh as the last fetch, and disabling a safe fast-forward on stale knowledge
        // would leave somebody unable to act on a remote that moved a second ago.
        PullCommand = new AsyncCommand(
            PullAsync, () => Ready && Standing.CanPull, ReportError);
        // Push stays live while diverged, where git will reject it. Disabling it would hide
        // the state the user has to resolve; git's own rejection, in a notification that does
        // not expire, is the clearest possible explanation of what is wrong.
        PushCommand = new AsyncCommand(() => PushAsync(afterCommit: false),
                                       () => Ready && Standing.CanPush, ReportError);
        FetchCommand = new AsyncCommand(FetchAsync, () => Ready, ReportError);

        PublishCommand = new AsyncCommand(
            PublishAsync, () => Ready && Standing.CanPublish, ReportError);

        SignInCommand = new AsyncCommand(SignInAsync, () => Ready && CanSignIn, ReportError);

        // Available whenever a credential could be stored, not only when one has failed:
        // "sign out of this host" is a thing somebody may want to do deliberately, and a
        // button that only appears after a failure cannot be found before one.
        SignOutCommand = new AsyncCommand(
            SignOutAsync, () => Ready && _remote.UsesCredentialHelper, ReportError);

        // Enabled only with a row selected. Discard is per-file on purpose: a "discard all"
        // beside "stage all" would put an irreversible button one pixel away from a routine
        // one, and the two are not comparable operations.
        DiscardCommand = new AsyncCommand(
            DiscardAsync, () => Ready && SelectedUnstaged is not null, ReportError);

        CreateBranchCommand = new AsyncCommand(CreateBranchAsync, () => Ready, ReportError);

        // Cloning needs no open repository — it is how you get one — so it is enabled
        // whenever nothing else is running, unlike every other command here.
        CloneCommand = new AsyncCommand(() => CloneRepositoryAsync(), () => !Busy.IsBusy, ReportError);

        SaveIdentityCommand = new AsyncCommand(SaveIdentityAsync, CanSaveIdentity, ReportError);

        // Every git button consults the same shared flag, so the moment one operation starts
        // they all go dead together. AsyncCommand.IsRunning only disables the button that was
        // pressed, which would leave Push live during a Pull.
        //
        // The dropdowns follow HasUninterruptible rather than IsBusy, and it moves
        // independently — an export raises one and not the other — so both are listened for.
        Busy.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(BusyTracker.IsBusy):
                    OnUi(RaiseGitCommandStates);

                    // Any deliberate git action means somebody is here, so the remote is worth
                    // watching closely again. The backoff exists to stop polling for nobody.
                    WakeThePoller();
                    break;

                case nameof(BusyTracker.HasUninterruptible):
                    OnUi(() => OnPropertyChanged(nameof(CanChangeContext)));
                    break;
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

    /// <summary>
    /// Whether a commit may be attempted at all.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Identity"/> is part of the gate, and that is the point of it.</b> The PLC
    /// export gate runs before the commit, so without this check a machine with no
    /// <c>user.email</c> spends 17–31 seconds exporting a CODESYS project and is then handed
    /// git's nine-line lecture about configuring an identity. The wait is what makes that
    /// failure expensive, and it is entirely avoidable from two <c>git config --get</c> calls
    /// made when the repository opened.
    /// </remarks>
    private bool CanCommit() =>
        Ready && Staged.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage)
        && _identity.IsComplete;

    /// <summary>
    /// Ask whether git can be run at all. Once, at launch.
    /// </summary>
    /// <remarks>
    /// Called by the host before any repository is opened, because the machine that needs the
    /// answer is the one where opening a repository is what fails. Never throws: a probe that
    /// could take the application down with it would be worse than the absence it is reporting.
    /// </remarks>
    public async Task ProbeGitAsync(CancellationToken ct)
    {
        GitPresence = await GitCliClient.ProbeAsync("git", _logger, ct).ConfigureAwait(true);

        if (!GitPresence.IsUsable)
        {
            StatusSummary = "Git is not available";
        }
    }

    /// <summary>Read who git will attribute a commit to, and let the panel say so.</summary>
    private async Task LoadIdentityAsync(CancellationToken ct)
    {
        if (_git is null)
        {
            Identity = GitIdentity.None;
            return;
        }

        Identity = await _git.IdentityAsync(ct).ConfigureAwait(true);
        OnPropertyChanged(nameof(IdentityIsMissing));

        if (!_identity.IsComplete)
        {
            // Info, not Warn: an unconfigured machine is an ordinary state on a first run, and
            // the panel is already saying so where the user can act on it.
            _logger.Info(LogCategory.Git, $"commit identity incomplete — {_identity.Explanation}");
        }
    }

    /// <summary>Write the name and email the user typed, then re-read what git now reports.</summary>
    /// <remarks>
    /// Re-read rather than assumed. Writing <c>--local</c> into a repository whose global config
    /// already holds a different identity is a case where what git will use and what Versio just
    /// wrote are not obviously the same thing, and the panel must report git's answer.
    /// </remarks>
    private async Task SaveIdentityAsync()
    {
        if (_git is null)
        {
            return;
        }

        _logger.Click("Git.SaveIdentity");
        using var busy = Busy.Begin(WorkKind.GitWrite, "Setting the git identity…");

        var result = await _git.SetIdentityAsync(
            IdentityName.Trim(), IdentityEmail.Trim(), IdentityGlobal, busy.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            Notifications.Failure("Could not save the identity.",
                                  result.FailureReason ?? string.Empty);
            return;
        }

        await LoadIdentityAsync(busy.Token).ConfigureAwait(true);
        if (_identity.IsComplete)
        {
            Notifications.Success($"Committing as {_identity.Name} <{_identity.Email}>.");
        }
        else
        {
            Notifications.Warning("The identity is still incomplete.", _identity.Explanation);
        }
    }

    private bool CanSaveIdentity() =>
        _git is not null
        && !Busy.IsBusy
        && !string.IsNullOrWhiteSpace(IdentityName)
        && !string.IsNullOrWhiteSpace(IdentityEmail);

    private void RaiseGitCommandStates()
    {
        StageCommand.RaiseCanExecuteChanged();
        UnstageCommand.RaiseCanExecuteChanged();
        CommitCommand.RaiseCanExecuteChanged();
        CommitAndPushCommand.RaiseCanExecuteChanged();
        PullCommand.RaiseCanExecuteChanged();
        PushCommand.RaiseCanExecuteChanged();
        FetchCommand.RaiseCanExecuteChanged();
        PublishCommand.RaiseCanExecuteChanged();
        SignInCommand.RaiseCanExecuteChanged();
        SignOutCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        HunkCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
        CreateBranchCommand.RaiseCanExecuteChanged();
        CloneCommand.RaiseCanExecuteChanged();
        SaveIdentityCommand.RaiseCanExecuteChanged();
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

        using var busy = Busy.Begin(WorkKind.GitWrite, stage ? "Staging…" : "Unstaging…");

        var result = stage
            ? await _git.StageAsync(paths, busy.Token).ConfigureAwait(true)
            : await _git.UnstageAsync(paths, busy.Token).ConfigureAwait(true);

        if (!result.Success)
        {
            Notifications.Failure(stage ? "Could not stage." : "Could not unstage.",
                                  result.FailureReason ?? string.Empty);
            return;
        }

        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    /// <summary>
    /// Commit, running the export gate first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gate runs before the commit, and exports rather than merely refusing.</b> A
    /// backstop, not the trigger: exports run when a change is detected, so by the time
    /// somebody commits the mirror is normally already current. This covers what the watcher
    /// missed — a dropped event, a save during a throttle window, a project adopted while the
    /// toolchain was unavailable. Refusing here and leaving the user to find an export button
    /// would turn a rare gap into a dead end.
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
            Notifications.Failure("Nothing was committed.", gate.Explanation);
            return;
        }

        if (gate.Decision == GateDecision.ExportRequired)
        {
            StatusSummary = "Exporting before the commit…";
            var outcomes = await Sync.SatisfyGateAsync(CancellationToken.None).ConfigureAwait(true);

            if (outcomes.Any(o => o.IsFailure))
            {
                var failure = outcomes.First(o => o.IsFailure);
                Notifications.Failure("Export failed, so nothing was committed.",
                                      failure.Explanation);
                return;
            }

            var staged = await StageExportedTreesAsync().ConfigureAwait(true);
            _logger.Info(LogCategory.Git,
                $"staged {staged} export path(s) before committing");
        }

        using var busy = Busy.Begin(WorkKind.GitWrite, "Committing…");
        var result = await _git.CommitAsync(CommitMessage, busy.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            Notifications.Failure("Commit failed.", result.FailureReason ?? string.Empty);
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
        // The most surprising rule in the tool, said where the decision was just made: a
        // commit does not release the direction lock. Until the work is pushed nobody else can
        // see it, so the direction is still meaningfully owned.
        Notifications.Success("Committed.",
                              "Not pushed, so the direction lock is still held.");
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

    /// <summary>Pull, for a test that cannot click the button.</summary>
    internal Task PullForTestAsync() => PullAsync();

    private async Task PullAsync()
    {
        if (_git is null)
        {
            return;
        }

        _logger.Click("Git.Pull");

        // Guarded at last. GuardIntent.Pull has existed, been tested and been deliberately
        // unwired since the guard was written, because every verdict it produces resolves to
        // the auto-stash - and offering to stash before anything could would have been a
        // button that lies. A pull is the last path that can meet a dirty tree, and it is the
        // worst one to meet it on: it rewrites the project and the mirror together.
        if (!await ClearForContextChangeAsync(GuardIntent.Pull, "pulling").ConfigureAwait(true))
        {
            return;
        }

        using var busy = Busy.Begin(WorkKind.GitWrite, "Pulling…");

        // Same as a checkout: a pull can land a whole new mirror, and an unheld watcher would
        // report it as an edit to the exported text and import it into the project.
        GitResult result = default!;

        await Sync.WhileGitRewritesTheTreeAsync(
            "pull",
            async () => result = await _git.PullAsync(null, busy.Token).ConfigureAwait(true))
            .ConfigureAwait(true);

        if (result.Success)
        {
            NoteAuthSuccess();
            Notifications.Success("Pulled.");
        }
        else
        {
            NoteAuthFailure(result);
            Notifications.Failure("Pull failed.", result.FailureReason ?? string.Empty);
        }

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

        using var busy = Busy.Begin(WorkKind.GitPush, "Pushing…");
        var result = await _git.PushAsync(null, busy.Token).ConfigureAwait(true);

        if (!result.Success)
        {
            // Sticky, and that is the single most important lifetime rule in the panel. A
            // failed push that quietly vanished after four seconds is how somebody goes home
            // believing their work is on the remote.
            NoteAuthFailure(result);
            Notifications.Failure("Push failed.", result.FailureReason ?? string.Empty);
            return;
        }

        var head = await _git.HeadCommitAsync(busy.Token).ConfigureAwait(true);
        await Sync.ReleaseAfterPushAsync(head, busy.Token).ConfigureAwait(true);

        NoteAuthSuccess();

        // Posted BEFORE the refresh, and it survives it. That ordering used to be the bug:
        // StatusSummary was set here and RunRefreshAsync then overwrote it with the file
        // count, so a successful push said nothing at all.
        Notifications.Success("Pushed.", "Every direction lock is released.");
        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    /// <summary>
    /// Put this branch on the remote for the first time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The remote is chosen, never guessed silently.</b> <c>origin</c> when it exists — which
    /// it does for anything that was cloned — then the sole remote if there is exactly one.
    /// With several and no <c>origin</c>, this refuses and says so rather than picking: pushing
    /// a branch to the wrong remote is not something to do on a coin toss, and a repository
    /// with three remotes is one where somebody has already thought about which is which.
    /// </para>
    /// <para>
    /// Releases the direction locks on success exactly as <see cref="PushAsync"/> does. The
    /// lock rule is about work becoming visible to other people, and a publish makes it visible
    /// for the first time — if anything it is the more complete case.
    /// </para>
    /// </remarks>
    private async Task PublishAsync()
    {
        if (_git is null)
        {
            return;
        }

        _logger.Click("Git.Publish");

        var remotes = await _git.RemotesAsync(CancellationToken.None).ConfigureAwait(true);

        var remote = remotes.FirstOrDefault(r => r == "origin")
                     ?? (remotes.Count == 1 ? remotes[0] : null);

        if (remote is null)
        {
            Notifications.Failure(
                "Versio does not know which remote to publish to.",
                remotes.Count == 0
                    ? "This repository has no remote."
                    : $"It has {remotes.Count} remotes and none is called origin: " +
                      string.Join(", ", remotes));
            return;
        }

        using var busy = Busy.Begin(WorkKind.GitPush, $"Publishing {BranchName} to {remote}…");
        var result = await _git.PublishBranchAsync(remote, BranchName, null, busy.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            NoteAuthFailure(result);
            Notifications.Failure($"Could not publish {BranchName}.",
                                  result.FailureReason ?? string.Empty);
            return;
        }

        var head = await _git.HeadCommitAsync(busy.Token).ConfigureAwait(true);
        await Sync.ReleaseAfterPushAsync(head, busy.Token).ConfigureAwait(true);

        NoteAuthSuccess();
        Notifications.Success($"{BranchName} is on {remote} now.",
                              "Every direction lock is released.");

        await RunRefreshAsync(RefreshTrigger.GitMetadata, null).ConfigureAwait(true);
    }

    /// <summary>
    /// Ask, before anything fails, whether this machine can talk to the remote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One <c>git credential fill</c> with prompts suppressed. It makes no network call and
    /// cannot prompt, so it costs a process and tells the panel to say "sign in needed" while
    /// the user is still looking at the window — rather than after their first push fails.
    /// </para>
    /// <para>
    /// Only for a remote with a credential helper. An SSH remote's readiness is a question
    /// about keys and an agent, which this cannot answer and must not pretend to.
    /// </para>
    /// </remarks>
    private async Task CheckCredentialAsync(CancellationToken ct)
    {
        if (_git is null || !_remote.UsesCredentialHelper)
        {
            AuthState = GitAuthState.Fine;
            return;
        }

        var stored = await _git.HasStoredCredentialAsync(_remote.Protocol, _remote.Host, ct)
            .ConfigureAwait(true);

        AuthState = stored ? GitAuthState.Fine : GitAuthState.NoCredential;

        if (!stored)
        {
            _logger.Info(LogCategory.Git,
                $"no credential stored for {_remote.Host} — sign-in will be needed");
        }
    }

    /// <summary>
    /// Read a failed network operation and record what it was actually complaining about.
    /// </summary>
    /// <remarks>
    /// Called from every remote operation's failure path, so the banner and the Sign in button
    /// follow the most recent real answer rather than the state at open.
    /// </remarks>
    private void NoteAuthFailure(GitResult result)
    {
        var state = GitAuthClassifier.Classify(result.StdErr, _remote.Transport);

        if (state is not GitAuthState.Fine)
        {
            AuthState = state;
        }
    }

    /// <summary>Clear the banner: something reached the remote and worked.</summary>
    private void NoteAuthSuccess()
    {
        if (_authState is not GitAuthState.Fine)
        {
            _logger.Info(LogCategory.Git, $"the remote accepted us — clearing {_authState}");
            AuthState = GitAuthState.Fine;
        }
    }

    /// <summary>
    /// Hand off to the credential helper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Versio never sees the credential.</b> git invokes whatever helper is configured and
    /// that helper does its own thing — a window, a browser for OAuth, a token prompt. What
    /// comes back here is whether it worked.
    /// </para>
    /// <para>
    /// A rejected credential is <b>erased first</b>. The maddening case is not a missing
    /// credential but a stale one: an expired token the helper keeps cheerfully returning, so
    /// every attempt fails identically. Without the erase, asking again just replays it.
    /// </para>
    /// </remarks>
    private async Task SignInAsync()
    {
        if (_git is null || !_remote.UsesCredentialHelper)
        {
            return;
        }

        _logger.Click("Git.SignIn");

        if (GitAuthClassifier.ShouldEraseFirst(_authState))
        {
            await _git.SignOutAsync(_remote.Protocol, _remote.Host, CancellationToken.None)
                .ConfigureAwait(true);
        }

        // The caption is loud on purpose. The helper's window is a separate process and can
        // come up BEHIND Versio, at which point the application looks frozen while something
        // off-screen waits for a password.
        using var busy = Busy.Begin(
            WorkKind.GitRead,
            $"Signing in to {_remote.Host} — finish in the window that opened (check behind Versio)…");

        var result = await _git.SignInAsync(_remote.Protocol, _remote.Host, busy.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            Notifications.Failure($"Not signed in to {_remote.Host}.",
                                  result.FailureReason ?? string.Empty);
            return;
        }

        AuthState = GitAuthState.Fine;
        Notifications.Success($"Signed in to {_remote.Host}.");
    }

    /// <summary>
    /// Erase the stored credential for this host.
    /// </summary>
    /// <remarks>
    /// The button no git GUI offers, and the one that fixes the failure people actually hit.
    /// Destructive only in the sense that the next network operation will ask again, so it is
    /// not confirmed — nothing is lost that a sign-in cannot restore.
    /// </remarks>
    private async Task SignOutAsync()
    {
        if (_git is null || !_remote.UsesCredentialHelper)
        {
            return;
        }

        _logger.Click("Git.SignOut");
        using var busy = Busy.Begin(WorkKind.GitRead, $"Forgetting the credential for {_remote.Host}…");

        var result = await _git.SignOutAsync(_remote.Protocol, _remote.Host, busy.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            Notifications.Failure("Could not erase the stored credential.",
                                  result.FailureReason ?? string.Empty);
            return;
        }

        AuthState = GitAuthState.NoCredential;
        Notifications.Success($"Forgot the credential for {_remote.Host}.",
                              "The next fetch or push will ask for a new one.");
    }

    /// <summary>Which remote and branch the poller should watch, or nothing.</summary>
    /// <remarks>
    /// Nothing unless there is an upstream: a branch that has never been published has no
    /// remote counterpart to compare against, and asking about one would fail on every probe.
    /// </remarks>
    private (string Remote, string Branch)? PollTarget()
    {
        if (_git is null || !_standing.HasUpstream)
        {
            return null;
        }

        // "origin/main" -> ("origin", "main"). The upstream is stored in exactly that shape by
        // porcelain v2, and a branch name may itself contain slashes, so only the first is a
        // separator.
        var slash = _standing.Upstream.IndexOf('/');
        return slash <= 0
            ? null
            : (_standing.Upstream[..slash], _standing.Upstream[(slash + 1)..]);
    }

    /// <summary>
    /// The remote moved, so learn what it moved to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fetch, never a pull. This is the only expensive call the poller makes and it runs only
    /// when a probe has already established there is something new.
    /// </para>
    /// <para>
    /// <b>It does not raise the <see cref="BusyTracker"/>.</b> Every git button consults that
    /// flag, so raising it here would make the panel flicker dead whenever a colleague pushed —
    /// the same reasoning that keeps automatic status refreshes off the progress bar.
    /// </para>
    /// </remarks>
    private async Task FetchBecauseTheRemoteMovedAsync(CancellationToken ct)
    {
        if (_git is null)
        {
            return;
        }

        var result = await _git.FetchAsync(null, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            var state = GitAuthClassifier.Classify(result.StdErr, _remote.Transport);

            if (GitAuthClassifier.IsTerminal(state))
            {
                // Stops on the first refusal rather than after the usual run: retrying a
                // rejected credential every few seconds cannot succeed and may lock an account.
                _poller.NoteRefused(GitAuthClassifier.Explain(state, _remote.Host));
                OnUi(() => AuthState = state);
            }

            return;
        }

        // Back onto the UI thread before anything observable moves. Watcher and timer callbacks
        // arrive on threadpool threads, and touching a bound collection from one throws.
        OnUi(() => _ = RunRefreshAsync(RefreshTrigger.GitMetadata, null));
    }

    private async Task FetchAsync()
    {
        if (_git is null)
        {
            return;
        }

        _logger.Click("Git.Fetch");
        using var busy = Busy.Begin(WorkKind.GitRead, "Fetching…");

        var result = await _git.FetchAsync(null, busy.Token).ConfigureAwait(true);

        if (result.Success)
        {
            NoteAuthSuccess();
            Notifications.Success("Fetched.");
        }
        else
        {
            NoteAuthFailure(result);
            Notifications.Failure("Fetch failed.", result.FailureReason ?? string.Empty);
        }

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
    /// <param name="initialUrl">
    /// Prefilled when the clone is a repository being fetched back rather than a new one.
    /// The dialog still requires Verify to pass before it will clone: a remembered URL is a
    /// convenience, not a claim that the remote is still there.
    /// </param>
    public Func<string?, Task<string?>>? PromptForClone { get; set; }

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

    /// <summary>
    /// Puts a guard's question to the user and returns what they chose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Beside <see cref="ConfirmDestructive"/> rather than replacing it. That one asks a
    /// yes-or-no about a single named file and is answered by a message box; this one offers
    /// between two and three named courses of action and needs a real dialog. Collapsing them
    /// would mean the discard confirmation grew a choice list it has no use for.
    /// </para>
    /// <para>
    /// <b>Unset means Cancel</b>, exactly as unset means "no" for the destructive confirm — the
    /// right way round for a wiring mistake to fail, and what lets <see cref="Ask"/> be called
    /// from a view model with no view attached at all.
    /// </para>
    /// </remarks>
    public Func<GuardPrompt, GuardChoice?>? AskGuard { get; set; }

    /// <summary>
    /// Puts the offer of stashed work to the user on returning to a branch.
    /// </summary>
    /// <remarks>
    /// <b>Unset means Leave</b>, not Restore and certainly not Discard. A host that forgets to
    /// wire this leaves every stash exactly where it is and offers it again next time, which is
    /// the only one of the three answers that cannot lose anything.
    /// </remarks>
    public Func<ChoicePrompt, StashedWorkChoice?>? AskAboutStashedWork { get; set; }

    /// <summary>
    /// Evaluate the guard for an intent, and ask the user if it wants asking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one entry point every guarded action goes through, so that "what is running" and
    /// "what state is the repository in" are gathered in one place and cannot be assembled
    /// differently by two callers. Phases 3 and 4 hang the close guard and the context-switch
    /// guard off this; nothing calls it yet.
    /// </para>
    /// <para>
    /// Returns the choice to act on: <see cref="GuardChoice.Proceed"/> when nothing was in the
    /// way, whatever the user picked when they were asked, and <see cref="GuardChoice.Cancel"/>
    /// for a refusal — a refusal has no choices, so there is nothing else it could be.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Ask about work in flight, and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>A deliberate seam, not a shortcut.</b> The guard's verdicts about a dirty tree and a
    /// held direction lock both resolve to the auto-stash, which is not built yet (safeguards
    /// §4). Passing the real tree state now would produce a "stash my changes" button that does
    /// nothing — worse than not asking, because a button that lies about what it will do is how
    /// somebody loses work believing it was saved.
    /// <para>
    /// Phase 7 deletes this method and its callers switch to <see cref="Ask"/>.
    /// </para>
    /// </remarks>
    private GuardChoice Ask(
        GuardIntent intent, string target = "", WorkingTreeSnapshot? tree = null)
    {
        var verdict = InterruptionGuard.Evaluate(
            intent, Busy.Snapshot(), tree ?? TreeState(), target);

        // Refusals and questions at Info, a clear road at Debug. Phase 4 puts this on every
        // repository and branch selection, and a line saying nothing was in the way each time
        // is how the log panel got drowned before. What matters in a log is the decision that
        // stopped somebody.
        _logger.Log(
            verdict.Decision == GuardDecision.Proceed ? LogLevel.Debug : LogLevel.Information,
            LogCategory.App,
            GuardNarrative.Describe(verdict));

        if (verdict.Decision == GuardDecision.Proceed)
        {
            return GuardChoice.Proceed;
        }

        if (GuardNarrative.Prompt(verdict) is not { } prompt)
        {
            return GuardChoice.Cancel;
        }

        GuardChoice answer;
        using (AskingSomething())
        {
            answer = AskGuard?.Invoke(prompt) ?? GuardChoice.Cancel;
        }

        // A REFUSAL IS NOT A QUESTION.
        //
        // It is still shown — the user has to be told why nothing happened — but whatever
        // comes back is discarded. The dialog returns null for the acknowledgement it puts on
        // a refusal, so in the shipped path this changes nothing; it matters because the hook
        // is a delegate anybody can set, and without this a handler that answered `Proceed`
        // out of habit would close the window over a running import. The refusal has to be the
        // decision, not a strong suggestion that the caller is trusted to respect.
        if (verdict.Decision == GuardDecision.Refuse)
        {
            return GuardChoice.Cancel;
        }

        return answer;
    }

    /// <summary>
    /// May Versio close, and if not yet, when?
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole close decision, in the view model rather than in the window, so it can be
    /// driven by a test without anything being shown. The window's job is reduced to vetoing
    /// the close, awaiting this, and closing again — which is all a synchronous
    /// <c>Window.Closing</c> can do with an asynchronous answer.
    /// </para>
    /// <para>
    /// <b>An import in flight refuses outright.</b> The guard produces that verdict, not this
    /// method, and it produces a refusal rather than a question because no answer would make it
    /// safe: it is the one write that can leave a project the IDE will not open, and "are you
    /// sure" is the wrong question when the honest answer is "twenty more seconds". The
    /// toolchain job carries its own timeout, so the wait is bounded whatever happens.
    /// </para>
    /// <para>
    /// Once closing is agreed, the watchers are released <b>before</b> waiting. Otherwise a
    /// save settling during the wait would start a fresh export and the app would sit there
    /// working after being asked to stop.
    /// </para>
    /// </remarks>
    /// <returns>True when the window may close now.</returns>
    public async Task<bool> RequestCloseAsync()
    {
        var choice = Ask(GuardIntent.CloseApp);

        if (choice == GuardChoice.Proceed)
        {
            return true;
        }

        if (choice is not (GuardChoice.WaitThenProceed or GuardChoice.CancelWorkAndProceed))
        {
            _logger.Info(LogCategory.App, "close cancelled; Versio stays open");
            return false;
        }

        _logger.Info(LogCategory.App,
            choice == GuardChoice.CancelWorkAndProceed
                ? "closing: stopping what is running first"
                : "closing: waiting for what is running to finish");

        // Committed to closing. Nothing new may start from here on.
        StopWatching();
        Sync.Quiesce();

        if (choice == GuardChoice.CancelWorkAndProceed)
        {
            var stopped = Busy.CancelCancellable();
            _logger.Info(LogCategory.App,
                $"closing: asked {stopped} operation(s) to stop");
        }

        StatusSummary = choice == GuardChoice.CancelWorkAndProceed
            ? "Stopping, then closing…"
            : "Closing when the current work finishes…";

        await Busy.WhenIdleAsync().ConfigureAwait(true);

        _logger.Info(LogCategory.App, "nothing left running; closing");
        return true;
    }

    /// <summary>
    /// Windows is ending the session. Give up whatever is running, deliberately.
    /// </summary>
    /// <remarks>
    /// Not a close the user can be asked about: the process has a few seconds and no dialog
    /// will be read. See <see cref="BusyTracker.CancelEverythingForShutdown"/> for why even an
    /// import is stopped here — the alternative is an orphaned CODESYS holding the project
    /// after its parent has gone.
    /// </remarks>
    public void AbandonForShutdown()
    {
        StopWatching();
        Sync.Quiesce();

        var stopped = Busy.CancelEverythingForShutdown();

        _logger.Warn(LogCategory.App,
            stopped == 0
                ? "the session is ending; nothing was running"
                : $"the session is ending; {stopped} operation(s) stopped mid-flight. The " +
                  "direction locks are on disk and will be recovered on the next open");
    }

    /// <summary>
    /// The repository as the guard needs to see it.
    /// </summary>
    /// <remarks>
    /// Built from what the shell has already read rather than by asking git again: a second
    /// <c>status</c> here would be a process spawn for an answer on screen, and two reads a
    /// moment apart can disagree — which for a guard would mean refusing on the strength of a
    /// conflict that had just been resolved.
    /// <para>
    /// The held locks come from the sync panel, which owns the orchestrator and asks it
    /// directly — see <see cref="SyncViewModel.HeldLocks"/>. Reading them off the project rows
    /// instead would be reading a sentence written for a human at the time of the last scan,
    /// rather than the lock as it stands now.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Stash the work in the tree so a switch can go ahead, and record that we did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The set difference is the correctness check, not bookkeeping.</b> <c>git stash
    /// push</c> on a clean tree prints <i>No local changes to save</i> and exits 0 <i>without
    /// creating an entry</i> — measured against real git — so the exit code cannot say whether
    /// the work is safe. If no new stash id appears this returns false and the switch is
    /// abandoned: a stash that did not happen followed by a checkout that did is the worst
    /// outcome available here.
    /// </para>
    /// <para>
    /// The locks are captured <i>before</i> the push and released <i>after</i> the id is in
    /// hand, in that order deliberately. Releasing first would leave a lock cleared against a
    /// stash that was never created, which is work nobody can find.
    /// </para>
    /// </remarks>
    /// <remarks>Internal so a test can reach the refusal path directly: with a clean tree the
    /// guard answers Proceed and never asks, so the only way to exercise "the stash created
    /// nothing" through the front door is a race nothing can schedule.</remarks>
    internal async Task<bool> TakeAutoStashAsync(string target)
    {
        if (_git is null || _autoStash is null)
        {
            return false;
        }

        using var busy = Busy.Begin(WorkKind.GitWrite, $"Saving your work before {target}…");

        var head = await _git.HeadCommitAsync(busy.Token).ConfigureAwait(true);
        var branch = AutoStash.KeyFor(BranchName, head);
        var before = await _git.StashListAsync(busy.Token).ConfigureAwait(true);

        // Captured before anything moves: the push is what makes the tree clean, and a lock
        // read afterwards would describe the world the stash created rather than the one the
        // user is leaving.
        var captured = Sync.CaptureLocksForStash();
        var files = Changes.Count;
        var marker = AutoStash.Marker(branch, Short(head));

        GitResult push = default!;
        await Sync.WhileGitRewritesTheTreeAsync(
            "auto-stash",
            async () => push = await _git
                .StashPushAsync(marker, includeUntracked: true, busy.Token)
                .ConfigureAwait(true))
            .ConfigureAwait(true);

        var after = await _git.StashListAsync(busy.Token).ConfigureAwait(true);

        if (AutoStash.NewStash(before, after) is not { } stashId)
        {
            // Either the push failed or it found nothing to save, and both mean the same thing
            // here: the work is not somewhere it can be recovered from, so nothing else may
            // happen. Never fall through to the checkout, and never to a discard.
            StatusSummary = push.Success
                ? "Nothing was stashed, so nothing was switched."
                : push.FailureReason ?? "the stash failed, so nothing was switched";

            _logger.Warn(LogCategory.Git,
                "auto-stash created no entry " +
                $"({(push.Success ? "clean tree" : push.FailureReason)}); {target} abandoned");

            return false;
        }

        var state = await _autoStash.ReadAsync(CancellationToken.None).ConfigureAwait(true);

        await _autoStash.WriteAsync(
            AutoStash.With(state, new AutoStashRecord
            {
                Branch = branch,
                StashCommitId = stashId,
                Message = marker,
                CreatedUtc = _clock.UtcNow,
                HeadCommit = head,
                FileCount = files,
                Locks = captured,
            }),
            CancellationToken.None).ConfigureAwait(true);

        await Sync.ReleaseStashedLocksAsync(captured, stashId).ConfigureAwait(true);

        _logger.Info(LogCategory.Git,
            $"auto-stashed {files} changed file(s) on {branch} as {Short(stashId)}" +
            (captured.Count == 0
                ? string.Empty
                : $", releasing {captured.Count} direction lock(s)"));

        StatusSummary = $"Saved {files} changed file(s). They will be offered back on {branch}.";
        return true;
    }

    /// <summary>
    /// Offer back whatever was stashed for the branch just arrived at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Offered, never automatic.</b> The same principle as the direction lock, which is
    /// never auto-cleared: the work a stash holds is still work, and silently rewriting
    /// somebody's working tree as a side effect of picking a branch from a dropdown is that
    /// mistake wearing a friendlier hat.
    /// </para>
    /// <para>
    /// Records whose stash has gone — popped or dropped from a terminal, which is an ordinary
    /// thing for a git user to do — are cleared here and nothing is said about it. It must not
    /// read as an error.
    /// </para>
    /// </remarks>
    private async Task OfferStashedWorkAsync(string branch)
    {
        if (_git is null || _autoStash is null)
        {
            return;
        }

        var state = await _autoStash.ReadAsync(CancellationToken.None).ConfigureAwait(true);
        if (state.Entries.Count == 0)
        {
            return;
        }

        var live = await _git.StashListAsync(CancellationToken.None).ConfigureAwait(true);
        var arrival = AutoStash.OnArrival(branch, state, live);

        if (arrival.Stale.Count > 0)
        {
            state = AutoStash.Without(state, [.. arrival.Stale.Select(e => e.StashCommitId)]);
            await _autoStash.WriteAsync(state, CancellationToken.None).ConfigureAwait(true);

            _logger.Info(LogCategory.Git,
                $"{arrival.Stale.Count} auto-stash record(s) for {branch} name stashes that are " +
                "no longer in git; forgotten");
        }

        if (!arrival.HasWork)
        {
            return;
        }

        // Each in turn, newest first, rather than only the newest.
        //
        // Switching away from a branch twice without coming back is ordinary, and the records
        // are a list for exactly that reason - but offering one per arrival meant the older
        // stashes were reachable only by switching away and back again, once per stash, with
        // nothing on screen saying so. A stash somebody cannot reach is a stash they will
        // eventually stop believing in.
        //
        // The list is re-read from the arrival snapshot rather than re-queried between
        // answers: restoring one pops it, which renumbers every stash@{n} below it, and the
        // refs held here would then name the wrong entries. RestoreStashAsync and the drop
        // below both act on THIS binding's captured ref immediately, before anything else
        // moves, which is what makes that safe.
        for (var i = 0; i < arrival.Restorable.Count; i++)
        {
            var offer = arrival.Restorable[i];
            var remaining = arrival.Restorable.Count - i;
            using var asking = AskingSomething();
            var choice = AskAboutStashedWork?.Invoke(
                             AutoStashNarrative.Arrival(arrival, offer, remaining, _clock.UtcNow))
                         ?? StashedWorkChoice.Leave;

            if (choice == StashedWorkChoice.Restore)
            {
                await RestoreStashAsync(offer).ConfigureAwait(true);

                // One restore per arrival. A second pop on top of the first would land two
                // sets of edits in one working tree with no way to tell them apart, and the
                // remaining records are still there to be offered on the next arrival.
                return;
            }

            if (choice == StashedWorkChoice.Discard)
            {
                var warning = AutoStashNarrative.DiscardWarning(offer);
                if (ConfirmDestructive?.Invoke(warning.Title, warning.Body) != true)
                {
                    return;
                }

                await _git.StashDropAsync(offer.Ref, CancellationToken.None).ConfigureAwait(true);

                state = AutoStash.Without(state, offer.Record.StashCommitId);
                await _autoStash.WriteAsync(state, CancellationToken.None).ConfigureAwait(true);

                _logger.Warn(LogCategory.Git,
                    $"auto-stash {Short(offer.Record.StashCommitId)} discarded at the user's request");

                StatusSummary = "The stashed work was discarded.";

                // Dropping renumbers the refs below it, so nothing else may be acted on with
                // the refs captured before it. The rest are offered on the next arrival.
                return;
            }

            // Left where it is, and the next one is offered. Doing nothing has to be a real
            // answer, or the dialog is a demand rather than an offer.
            StatusSummary = $"Your stashed work is still saved for {branch}.";
        }
    }

    /// <summary>Restore one stash, and the sync state that went with it.</summary>
    /// <remarks>
    /// <c>pop</c> rather than apply-then-drop, because git drops only when the restore
    /// succeeded. A conflicted restore keeps both the stash and its record: the work still
    /// exists and is still ours, and the conflict gate refuses both sync directions until
    /// somebody resolves it.
    /// </remarks>
    private async Task RestoreStashAsync(LiveStash offer)
    {
        if (_git is null || _autoStash is null)
        {
            return;
        }

        using var busy = Busy.Begin(WorkKind.GitWrite, "Restoring your stashed work…");

        GitResult result = default!;
        await Sync.WhileGitRewritesTheTreeAsync(
            "restoring an auto-stash",
            async () => result = await _git
                .StashApplyAsync(offer.Ref, drop: true, busy.Token)
                .ConfigureAwait(true))
            .ConfigureAwait(true);

        if (!result.Success)
        {
            StatusSummary = result.FailureReason ?? "the stashed work could not be restored";
            _logger.Error(LogCategory.Git,
                $"restoring auto-stash {Short(offer.Record.StashCommitId)} failed; the stash is " +
                $"kept: {result.FailureReason}");
            return;
        }

        var refused = await Sync.RestoreStashedLocksAsync(offer.Record.Locks).ConfigureAwait(true);

        var state = await _autoStash.ReadAsync(CancellationToken.None).ConfigureAwait(true);
        await _autoStash.WriteAsync(
            AutoStash.Without(state, offer.Record.StashCommitId),
            CancellationToken.None).ConfigureAwait(true);

        _logger.Info(LogCategory.Git,
            $"restored auto-stash {Short(offer.Record.StashCommitId)}" +
            (refused.Count == 0
                ? string.Empty
                : $"; {refused.Count} direction lock(s) left alone because newer work holds them"));

        StatusSummary = refused.Count == 0
            ? "Your stashed work is back."
            : "Your stashed work is back. Some projects have changed since, so their sync " +
              "state was left as it is.";

        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    private static string Short(string commit) => commit.Length >= 7 ? commit[..7] : commit;

    private WorkingTreeSnapshot TreeState() => new()
    {
        DirtyPaths = [.. Changes.Select(c => c.Path)],
        ConflictedPaths = [.. Changes.Where(c => c.Kind == ChangeKind.Conflicted).Select(c => c.Path)],
        HeldLocks = Sync.HeldLocks,
    };

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
            Notifications.Warning("That hunk is no longer in the diff on screen.");
            return;
        }

        var unstaging = _selectedDiffStaged;
        _logger.Click(unstaging ? "Diff.UnstageHunk" : "Diff.StageHunk");

        using var busy = Busy.Begin(
            WorkKind.GitWrite, unstaging ? "Unstaging a hunk…" : "Staging a hunk…");

        var result = await _git
            .ApplyToIndexAsync(patch, reverse: unstaging, busy.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            // Almost always a context mismatch: the file moved on under a diff that was read
            // a moment ago. Git's own wording is the diagnosis, so it is passed through.
            Notifications.Failure("git would not apply that hunk.",
                                  result.FailureReason ?? string.Empty);
            _logger.Warn(LogCategory.Git, StatusSummary);
            return;
        }

        Notifications.Success(unstaging ? "Hunk unstaged." : "Hunk staged.");
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

        using var asking = AskingSomething();
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
        using var busy = Busy.Begin(WorkKind.GitWrite, $"Discarding {row.Path}…");

        var result = await _git.DiscardAsync([row.Path], busy.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            _logger.Warn(LogCategory.Git, $"discard failed: {result.FailureReason}");
            Notifications.Failure("Could not discard.", result.FailureReason ?? string.Empty);
            await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
            return;
        }

        Notifications.Success($"Discarded the changes to {row.Path}.");

        // Only when the file was part of a mirror. A discarded README has nothing to do with
        // the direction lock, and releasing every lock because one unrelated file was
        // restored would be the more damaging kind of over-reach.
        if (Sync.BindingOwning(row.Path) is { } bindingId)
        {
            await Sync.ReleaseAfterDiscardAsync(bindingId, busy.Token)
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

        using var asking = AskingSomething();
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

        using var busy = Busy.Begin(WorkKind.GitWrite, $"Creating {name}…");

        var result = await _git.CreateBranchAsync(name, checkout: true, busy.Token)
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
    private async Task CloneRepositoryAsync(string? initialUrl = null)
    {
        _logger.Click("TopBar.Clone");

        if (PromptForClone is null)
        {
            return;
        }

        if (await PromptForClone(initialUrl).ConfigureAwait(true) is not { } cloned)
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

        // A checkout had no scope at all until now, which meant the one operation that
        // rewrites every file in the working tree was invisible both to the progress bar and
        // to anything asking whether Versio was busy. Two quick selections in the dropdown
        // could therefore overlap, and the second would be reasoning about the tree the first
        // was still replacing.
        using var busy = Busy.Begin(WorkKind.GitWrite, $"Switching to {branch}…");

        // A checkout rewrites the project and its mirror together, and the watcher cannot tell
        // that from somebody editing them. Held off for the duration, and what git leaves is
        // adopted rather than synced.
        GitResult result = default!;

        await Sync.WhileGitRewritesTheTreeAsync(
            $"checkout {branch}",
            async () => result = await _git.CheckoutAsync(branch, busy.Token).ConfigureAwait(true))
            .ConfigureAwait(true);

        if (!result.Success)
        {
            // Almost always a dirty working tree whose changes collide with the target.
            _logger.Warn(LogCategory.Git, $"checkout failed: {result.FailureReason}");

            Notifications.Failure("Checkout failed.", result.FailureReason ?? string.Empty);

            // Explicitly, and not left to the refresh below. That reloads the branch list only
            // on some of its paths, so the selection sitting on a branch we are not on would
            // be correct by coincidence rather than by decision — and the comment here used to
            // claim a revert that nothing actually did.
            ShowCurrentBranch();
            await RefreshAsync().ConfigureAwait(true);
            return;
        }

        await RefreshAsync().ConfigureAwait(true);

        // After the refresh, so the panel already describes the branch we have arrived on
        // before a dialog appears over it. Offered on EVERY successful checkout - the
        // dropdown, a new branch, or a pull that moved HEAD - because a stash the user is
        // never offered is a stash they will not know to look for.
        await OfferStashedWorkAsync(branch).ConfigureAwait(true);
    }

    private Task OpenRepositoryAsync()
    {
        // The view supplies the path via OpenAsync; this exists so the button has a command
        // and its click is logged like every other.
        _logger.Click("TopBar.OpenRepository");
        return Task.CompletedTask;
    }

    /// <summary>The toolbar button.</summary>
    /// <remarks>Internal so a test can await it; the command cannot.</remarks>
    internal Task RefreshAsync()
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
        using var busy = manual ? Busy.Begin(WorkKind.GitRead, "Reading git status…") : null;

        var operationId = Guid.NewGuid().ToString("N")[..8];
        using var scope = _logger.BeginOperation(operationId);

        try
        {
            var status = await _git.StatusAsync(busy?.Token ?? CancellationToken.None).ConfigureAwait(true);

            var previousBranch = BranchName;
            BranchName = status.Branch.IsDetached ? "(detached)" : status.Branch.Name;
            AheadBehind = status.Branch is { Ahead: 0, Behind: 0 }
                ? string.Empty
                : $"↑{status.Branch.Ahead} ↓{status.Branch.Behind}";

            // The remote list is read once per repository rather than per refresh: remotes are
            // configuration, they change when somebody edits them, and a process spawn on
            // every working-tree save to re-learn that origin still exists is waste.
            Standing = RemoteStanding.For(status.Branch, _hasRemote);

            var changed = Reconcile(status.Changes);

            UpdateStagingLists(status);

            // The project tree is now the primary view, so it follows git the way the file
            // list does. Handed the status just read rather than reading its own: a second
            // `git status` per refresh is a process spawn for an answer we already have, and
            // two reads a moment apart can disagree.
            await Sync.RefreshTreeAsync(status).ConfigureAwait(true);

            // A project dropped into the repository while Versio is running is git's news
            // first: it appears here as an untracked file and nothing else happens, because
            // the project scan only ran when a repository was opened. Handed the paths that
            // actually moved, so this costs a set lookup unless something project-shaped
            // turned up.
            await Sync.NoticeWorkingTreeAsync(changed).ConfigureAwait(true);

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

        WithoutSideEffects(() =>
        {
            Branches.Clear();
            foreach (var branch in branches.OrderByDescending(b => b.IsCurrent)
                                           .ThenBy(b => b.Name, StringComparer.Ordinal))
            {
                Branches.Add(new BranchEntry(branch));
            }

            SelectedBranch = Branches.FirstOrDefault(b => b.IsCurrent);
        });
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

    /// <summary>
    /// Surface a failed command.
    /// </summary>
    /// <remarks>
    /// <b>Cancellation is not a failure.</b> Every operation now runs under a token that a
    /// user can trip deliberately — by answering "stop it and continue" to a guard — and the
    /// exception that produces is the mechanism working, not breaking. Reported at Info and
    /// worded as an outcome, because a red error line for something the user just asked for
    /// teaches them that the button is broken.
    /// </remarks>
    private void ReportError(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            _logger.Info(LogCategory.App, "the operation was cancelled");
            StatusSummary = "Cancelled.";
            return;
        }

        _logger.Error(LogCategory.App, ex.Message, ex);
        Notifications.Failure("Something went wrong.", ex.Message);
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

        // A background loop outliving the window would keep spawning git processes against a
        // repository nobody is looking at.
        _poller.Dispose();

        // The sync panel holds its own watcher over every tracked project and its export
        // tree. Releasing only the shell's would leave those handles open and the repository
        // folder locked against rename or delete after the window closes.
        Sync.Detach();
        History.Detach();
    }
}
