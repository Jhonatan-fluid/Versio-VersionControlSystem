using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using Versio.App.Mvvm;
using Versio.Core.Abstractions;
using Versio.Core.Diff;
using Versio.Core.Git;
using Versio.Core.Guarding;
using Versio.Core.Locking;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Core.Projects;
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

/// <summary>Why a refresh is running.</summary>
/// <remarks>
/// <para>
/// <b>It was private, nested inside the shell, until the remote surface moved out.</b> A
/// collaborator that asks the shell to refresh has to be able to say which kind it is asking
/// for — a pressed button and a remote that moved are not the same event to the coalescer or to
/// the log. Flattening it into two callbacks would have hidden that distinction in the names of
/// two delegates instead of stating it in a type.
/// </para>
/// <para>
/// Public rather than internal only because it appears in a public constructor's signature and
/// C# will not allow that to be less accessible than the type. <c>Versio.App</c> is the
/// application, not a library — nothing outside it consumes this — so widening it costs a line
/// of explanation and nothing else.
/// </para>
/// </remarks>
public enum RefreshTrigger
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

    /// <summary>
    /// Parent directory, shown beside the name so two repositories with the same folder name
    /// are still distinguishable.
    /// </summary>
    /// <remarks>
    /// <b>Beside, not beneath.</b> This said "beneath" for a narrow list that never shipped,
    /// and the dropdown it is actually in is several hundred pixels wide — stacking the two
    /// doubled the height of the top bar to say what fits on one line. Corrected after the
    /// first binding followed the comment rather than the control.
    /// </remarks>
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

    /// <summary>
    /// Where this branch actually lives.
    /// </summary>
    /// <remarks>
    /// The enum, not just the label. The delete flow reconstructed it by comparing
    /// <see cref="Where"/> against the string <c>"local"</c> — so a change of wording would
    /// have silently reclassified every branch, and nothing would have failed until somebody
    /// deleted the wrong thing from a remote.
    /// </remarks>
    public BranchExistence Existence { get; } = branch.Existence;

    /// <summary>Where it lives, in a word, for the dropdown.</summary>
    /// <remarks>
    /// A word rather than a colour: colour alone is not a label, and "local" and "remote" are
    /// the difference between work nobody else can see and work not checked out here. Blank for
    /// the ordinary case of a branch on both sides, because a label on every row is a label
    /// nobody reads.
    /// </remarks>
    public string Where { get; } = branch.Existence switch
    {
        BranchExistence.LocalOnly => "local",
        BranchExistence.RemoteOnly => "remote",
        _ => string.Empty,
    };

    /// <summary>True when choosing this creates a local branch.</summary>
    public bool IsRemoteOnly { get; } = branch.IsRemoteOnly;

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

    private bool _isSelected;

    /// <summary>
    /// Ticked for the next staging action.
    /// </summary>
    /// <remarks>
    /// <b>Not the list selection.</b> Clicking a row opens its diff; ticking its box says what
    /// the next Stage, Unstage or Discard applies to. Two jobs, two pieces of state — a user
    /// who lost their diff every time they changed what they were about to stage would stop
    /// using one of the two.
    /// <para>
    /// Survives a refresh because <c>ReconcileInto</c> updates rows in place rather than
    /// replacing them. Before that, this would have been cleared every time the working tree
    /// moved.
    /// </para>
    /// </remarks>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                SelectionChanged?.Invoke();
            }
        }
    }

    /// <summary>Raised so the owning list can recount without watching every row.</summary>
    public event Action? SelectionChanged;

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

    private NamedObject? _object;

    /// <summary>
    /// Name this row as the object it was exported from, or as nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set after the row exists rather than in the constructor, because it has to be. The
    /// changed-files list is filled from <c>git status</c> and the index is built from the
    /// manifests, and those land in that order on the first refresh after a repository opens —
    /// so a row is created with a path and named a moment later. Handing the index to the
    /// constructor would have shown raw paths until the working tree next moved, which on a
    /// project nobody is editing is never.
    /// </para>
    /// <para>
    /// Null is the ordinary answer for most of the repository and is not a failure: a file the
    /// manifest does not describe is shown as its path, never as a guessed object.
    /// </para>
    /// </remarks>
    public void Describe(NamedObject? found)
    {
        if (ReferenceEquals(_object, found))
        {
            return;
        }

        _object = found;
        OnPropertyChanged(nameof(IsObject));
        OnPropertyChanged(nameof(Primary));
        OnPropertyChanged(nameof(Secondary));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(IsExportOnly));
    }

    /// <summary>This row is a PLC object rather than an ordinary file.</summary>
    public bool IsObject => _object is not null;

    /// <summary>
    /// The line the eye lands on: the object's name, or the file's.
    /// </summary>
    /// <remarks>
    /// <b>Never the whole path.</b> The panel is 380 px wide and an exported path is routinely
    /// sixty characters of shared prefix — <c>src/Project/PLC/POU/Applic…</c> on every row,
    /// trimmed at the tail, hiding the only part that differed. The name is what distinguishes
    /// one row from the next, so it is what gets the room; the directory is
    /// <see cref="Secondary"/> and may be elided freely.
    /// </remarks>
    public string Primary => _object?.Name ?? FileNameOf(Path);

    /// <summary>Where it lives: the directory, or the whole path when there is no name to lead with.</summary>
    public string Secondary
    {
        get
        {
            var slash = Path.LastIndexOf('/');
            var directory = slash > 0 ? Path[..slash] : string.Empty;

            // An object's file name is not implied by its object name — Glob_Var exports to
            // Glob_Var.gvl.st AND Glob_Var.gvl.xml, and two rows reading "Glob_Var" over the
            // same directory would be indistinguishable. The layer is what tells them apart.
            return _object is not null && directory.Length > 0
                ? $"{directory}/{FileNameOf(Path)}"
                : directory.Length > 0
                    ? directory
                    : string.Empty;
        }
    }

    /// <summary>The type chip beside the name, empty for an ordinary file.</summary>
    public string TypeLabel => _object?.TypeLabel ?? string.Empty;

    /// <summary>
    /// Editing this file changes nothing in the project.
    /// </summary>
    /// <remarks>
    /// Library references are exported for review and never applied back, so an edit to one
    /// sits in the working tree until the next export overwrites it from the project. The row
    /// says so because this is where somebody is about to stage it, which is why it went here
    /// rather than onto the tree row the plan originally suggested (§9.3): the tree says what
    /// an object <i>is</i>, and this list is where a decision is about to be made about it.
    /// </remarks>
    public bool IsExportOnly => _object?.IsExportOnly ?? false;

    private static string FileNameOf(string path) =>
        path.Split('/').LastOrDefault() is { Length: > 0 } name ? name : path;
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

    /// <summary>
    /// Everything else in settings.json, carried so a write does not throw it away.
    /// </summary>
    /// <remarks>
    /// Both writes below constructed <c>new AppSettings { Recent = _recent }</c>, which was
    /// correct exactly while the record had one field. The moment a second one existed - the
    /// theme, then the panel sizes - opening any repository would have silently reset it,
    /// because promoting a path rewrites the whole file. Held whole, replaced by field.
    /// </remarks>
    private AppSettings _stored = AppSettings.Empty;

    private GitCliClient? _git;

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
                          IAppSettingsStore? settings = null,
                          IProcessProbe? processes = null)
    {
        // Injected for the same reason IClock and IRepositoryProbe are. The real probe
        // requires the process NAME to start with "Versio", so no pid a test can reach ever
        // looks like a live instance — which would leave §5.G's refusal path the one branch
        // with no coverage, and it is the branch the whole guard exists for.
        _processes = processes ?? new ProcessProbe();

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

        // WATCHING THE LISTS THEMSELVES, RATHER THAN EVERY PLACE THAT WRITES TO THEM.
        //
        // Two things depend on a staging list changing: the commit button's caption and
        // enabled state, which are computed from the counts, and the per-row tick subscription
        // that keeps the header in step. Both used to be arranged by the one method that
        // happened to build rows - so a row added from anywhere else got neither, and the
        // caption went stale with nothing to show why.
        //
        // Subscribing to the collections instead means a row is tracked because it is IN the
        // list, not because whoever put it there remembered to say so.
        Staged.CollectionChanged += OnStagingListChanged;
        Unstaged.CollectionChanged += OnStagingListChanged;

        // The rail badge's count, on the same argument one line up: it is a computed property
        // over this collection, so it moves because the collection moved rather than because
        // Reconcile remembered to say so. Reconcile mutates row by row, and the badge is cheap
        // - a count, a comparison and a string.
        Changes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ChangeBadge));

        // Events, as against the state StatusSummary holds. The two were one property until
        // now, and the collision was not theoretical - see NotificationCenter's remarks.
        Notifications = new NotificationCenter(_clock, logger);

        // The credential and the git identity, in a class of their own. Constructed here
        // because it needs Busy and Notifications, and it is handed `ready` as a DELEGATE
        // rather than a copy of the predicate: Ready is `a client, and nothing running`, and a
        // second implementation of that would be a second thing to keep in step.
        //
        // The subscription is the seam. Everything in there used to call RaiseHealth and
        // RaiseGitCommandStates directly, and could not have moved while it did.
        Auth = new GitAuthViewModel(logger, Busy, Notifications, () => Ready, ReportError);
        Auth.Changed += () =>
        {
            RaiseHealth();
            RaiseGitCommandStates();
        };


        // The SHELL's tracker, not one of its own.
        //
        // This defaulted, so the sync view model built a private BusyTracker while the
        // progress bar binds to this one — meaning the app-wide bar never appeared for any
        // toolchain work at all, including the catch-up and commit-gate exports that both
        // carefully open a scope for it. Two objects, both correct, wired to nothing.
        Sync = new SyncViewModel(logger, _clock, Busy, toolchains);

        // Where this branch stands against the remote, and the background watch that keeps it
        // honest. After Sync, because it holds it: a pull and a push both have to tell the PLC
        // engine that git is about to move the tree under it.
        //
        // The callbacks are the shell's and stay the shell's. A background watch is defined by
        // them - what counts as busy, what to ask on each probe - and handing over the object
        // that owns them rather than the delegates would be passing the shell itself.
        Remote = new GitRemoteViewModel(
            logger,
            _clock,
            Busy,
            Notifications,
            Auth,
            Sync,
            client: () => _git,
            ready: () => Ready,

            // One shared flag, so a probe cannot compete with a push for the network or the
            // credential helper, and a modal question is never answered by a background fetch.
            suspended: () => Busy.IsBusy || _modalOpen,
            branchName: () => BranchName,
            clearForContextChange: ClearForContextChangeAsync,
            refresh: trigger => RunRefreshAsync(trigger, null),

            // It asks nothing at all unless the button is actually offering to create a request.
            onProbed: ProbeMergeRequestAsync,
            reportError: ReportError);

        Remote.Changed += () =>
        {
            RaiseHealth();
            RaiseGitCommandStates();
        };

        // A failed project is the one condition the status light reads that does not live on
        // this view model, so it is the one that has to be listened for rather than raised.
        Sync.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SyncViewModel.HasFailure)
                              or nameof(SyncViewModel.FailedProject))
            {
                RaiseHealth();
            }
        };

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

        // The changed-files list names its rows from the manifests, and the manifests are read
        // by the tree. Opening a repository refreshes git BEFORE the bindings are loaded, so
        // the first pass names nothing and the index turns up afterwards from inside
        // AttachAsync — a path this panel never hears about otherwise. Measured: without this
        // every row showed a raw path until the working tree next moved.
        Sync.ObjectsChanged += DescribeRows;

        // And the same for the history: the panel decides what was picked, this decides how
        // it is rendered. Keeping the render here is what stops a second diff pane growing
        // in the history panel the first time it needs something the shared one already does.
        History.CommitSelected += row => _ = ShowCommitAsync(row.Entry);
        History.FileSelected += row => _ = ShowCommitFileAsync(row);

        logger.Info(LogCategory.App, "Versio started");
    }

    /// <summary>The collapsible log console.</summary>
    /// <remarks>
    /// Handed in rather than built here, because the application needs one attached to the
    /// sink before the shell exists — a crash during startup has to be recorded somewhere.
    /// <b>The shell owns it from that point on and disposes it</b>; see <see cref="Dispose"/>
    /// for why, and do not hand the same instance to two shells.
    /// </remarks>
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

    /// <summary>
    /// The credential and the git identity — what stands between this machine and the remote.
    /// </summary>
    /// <remarks>
    /// Bound as <c>Auth.X</c> throughout <c>GitPanelView.xaml</c>, like <c>Sync</c>,
    /// <c>History</c> and <c>Log</c>. It owns the parsed <see cref="RemoteUrl"/> that forge
    /// detection and the merge-request button read, which is why those two ask it rather than
    /// the shell keeping a second copy.
    /// </remarks>
    public GitAuthViewModel Auth { get; }

    /// <summary>
    /// Where this branch stands against the remote, and the watch that keeps it honest.
    /// </summary>
    /// <remarks>
    /// Bound as <c>Remote.X</c> throughout <c>GitPanelView.xaml</c>. It owns the poller, so the
    /// shell disposes it — a background loop outliving the window would keep spawning git
    /// processes against a repository nobody is looking at.
    /// </remarks>
    public GitRemoteViewModel Remote { get; }

    /// <summary>The PLC sync panel — what Versio is for, as distinct from the git client.</summary>
    public SyncViewModel Sync { get; }

    /// <summary>The commit history, in its own panel and loaded only when it is shown.</summary>
    public HistoryViewModel History { get; }

    public ObservableCollection<ChangeRow> Changes { get; } = [];

    /// <summary>The count on the git rail, and what colour it is.</summary>
    /// <remarks>
    /// <b>On the rail rather than in a panel title, which is what makes it worth having.</b>
    /// The pill this replaces lived in the panel header and the shell drew it on the Project
    /// panel only — so it stated the working tree's size exactly where you could already see
    /// it, and said nothing from the two panels where you cannot. The rail is the one surface
    /// permanently on screen; a navigation control that cannot say <i>there is something over
    /// here</i> is doing half its job.
    /// <para>
    /// The rule is in <see cref="Core.Git.ChangeBadge"/> — suppression at zero, the cap, and
    /// conflicts outranking the count — because all three are decisions rather than
    /// arithmetic, and none of them needs a window to check.
    /// </para>
    /// </remarks>
    public ChangeBadge ChangeBadge => ChangeBadge.For(Changes.Count, HasConflicts);

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
    /// Every question this view model can put to the user.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Settable rather than injected because the host that owns the answers is the window,
    /// and the window is built from this view model rather than before it. Defaults to
    /// <see cref="NullPrompts"/>, so a view model with no view attached answers "no" to
    /// everything instead of needing a null check at each of the fifteen call sites.
    /// </para>
    /// <para>
    /// Assigning it hands the same prompts to <see cref="Sync"/>, which asks two of the nine.
    /// Two view models, one set of questions, one thing for a host to wire.
    /// </para>
    /// </remarks>
    public IUserPrompts Prompts
    {
        get => _prompts;
        set
        {
            _prompts = value;
            Sync.Prompts = value;
        }
    }

    private IUserPrompts _prompts = NullPrompts.Instance;

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
        var choice = Prompts.AskAboutMissingRepository(MissingRepository.Prompt(record, why))
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
            Stashes.Clear();

            SelectedStaged = null;
            SelectedUnstaged = null;
            SelectedBranch = null;
        });
        OnPropertyChanged(nameof(HasStashes));

        BranchName = "—";
        Auth.Reset();

        // Both are about a repository that is no longer open.
        Remote.Reset();
        RemoteDisplay = string.Empty;
        OnPropertyChanged(nameof(IsWorkingTreeClean));

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
        DiffLayers = [];
        ClearRendered();

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
        _stored = await _settings.ReadAsync(ct).ConfigureAwait(true);
        _recent = _stored.Recent;

        WithoutSideEffects(() =>
        {
            RecentRepositories.Clear();
            foreach (var entry in _recent)
            {
                RecentRepositories.Add(
                    new RepoEntry(entry.Path, entry.RemoteUrl, entry.LastOpenedUtc));
            }

            SelectedRepository = RowFor(_repositoryPath);
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
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Which palette the user chose, and how big they made the panels.
    /// </summary>
    /// <remarks>
    /// Read at startup before the first window is shown, and written back whenever one of
    /// them changes. The shell owns them because the shell owns the settings store — the
    /// alternative was a second store for two doubles and a string.
    /// </remarks>
    public string StoredTheme => _stored.Theme;

    public LayoutSettings StoredLayout => _stored.Layout;

    /// <summary>Remember the theme the user picked.</summary>
    /// <remarks>
    /// Fire-and-forget from a radio button's Checked handler, which cannot await. A failed
    /// write costs the preference and nothing else — the store never throws.
    /// </remarks>
    public async Task RememberThemeAsync(string theme)
    {
        if (string.Equals(_stored.Theme, theme, StringComparison.Ordinal))
        {
            return;
        }

        _stored = _stored with { Theme = theme };
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Remember a pane the user dragged.</summary>
    /// <remarks>
    /// Called when the window closes rather than on every drag: a GridSplitter raises its
    /// change continuously, and writing settings.json on each frame of a drag would be a file
    /// write per pixel.
    /// </remarks>
    public async Task RememberLayoutAsync(LayoutSettings layout)
    {
        _stored = _stored with { Layout = layout };
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Write the whole record, with the recent list as it currently stands.</summary>
    private Task SaveSettingsAsync()
    {
        _stored = _stored with { Recent = _recent };
        return _settings.WriteAsync(_stored, CancellationToken.None);
    }

    /// <summary>Take a repository out of the list, and write the list down.</summary>
    private async Task ForgetAsync(string path)
    {
        _recent = Core.Settings.RecentRepositories.Remove(_recent, path);

        SyncRecentToDropdown(_repositoryPath);
        await SaveSettingsAsync().ConfigureAwait(true);

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

            SelectedRepository = RowFor(select);
        });

    /// <summary>
    /// The dropdown row for a path, however that path happens to be spelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An ordinal <c>==</c> here left the dropdown blank with the list behind it perfectly
    /// populated.</b> The stored entry is canonical — backslashes, no trailing separator, the
    /// spelling <see cref="Core.Settings.RecentRepositories.Canonical"/> produces — and the path
    /// being matched against it is whatever opened the repository. Git answers
    /// <c>rev-parse --show-toplevel</c> with forward slashes, the folder picker hands back
    /// backslashes, and a command line carries whatever was typed. Any of those against the
    /// stored spelling is a miss, and a miss selects nothing.
    /// </para>
    /// <para>
    /// The same argument the recent list itself already makes, applied on the way out as well
    /// as on the way in: <c>Promote</c> canonicalises so the repository cannot appear twice,
    /// and this canonicalises so the one row it did produce can still be found. Fixing only
    /// half of it is what left a list of one with nothing selected.
    /// </para>
    /// </remarks>
    private RepoEntry? RowFor(string? path)
    {
        var wanted = Core.Settings.RecentRepositories.Canonical(path);

        return RecentRepositories.FirstOrDefault(
            r => Core.Settings.RecentRepositories.PathComparer.Equals(
                Core.Settings.RecentRepositories.Canonical(r.Path), wanted));
    }

    /// <summary>Put the repository dropdown back on the repository that is actually open.</summary>
    private void ShowOpenRepository() =>
        WithoutSideEffects(() => SelectedRepository = RowFor(_repositoryPath));

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

            // Somebody has come to look at the remote, so watch it closely again. The backoff
            // exists to stop polling for nobody; opening this panel is the clearest possible
            // evidence that there is a somebody, and stale counts are exactly what they came
            // to read.
            if (value == ShellPanel.Git)
            {
                Remote.Wake();
            }
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

    /// <summary>
    /// The status light, and the sentence behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dot in the corner of the status bar was <c>Brush.Status.Added</c> written straight
    /// into the XAML: hardcoded green, in the leftmost position of the status bar, shaped
    /// exactly like a health indicator, and green while git was missing, while the tree was
    /// mid-merge, while a sync had failed and while the branch had diverged. This shell
    /// computed all four and the status bar read none of them.
    /// </para>
    /// <para>
    /// Computed rather than stored, and the precedence lives in <see cref="ShellHealth"/>
    /// where it can be asserted without a repository. <see cref="RaiseHealth"/> is what makes
    /// it move — every condition it reads is on a property with its own setter, and each of
    /// those calls it.
    /// </para>
    /// </remarks>
    public ShellHealth Health => ShellHealth.Evaluate(new ShellConditions
    {
        RepositoryOpen = _git is not null,
        GitMissing = GitIsMissing,
        FailedProject = Sync.FailedProject?.Name ?? string.Empty,
        MidMerge = IsMidMerge,
        IdentityMissing = Auth.IdentityIsMissing,
        AuthNeedsAttention = Auth.NeedsAttentionForAuth,
        Diverged = Remote.Standing.IsDiverged,
    });

    /// <summary>Bound through <c>ResourceKeyConverter</c>, so the theme picks the paint.</summary>
    public string HealthBrushKey => Health.BrushKey;

    /// <summary>The tooltip. A coloured dot with no explanation is a riddle, not a status.</summary>
    public string HealthSummary => Health.Summary;

    /// <summary>Tell the status light one of its seven conditions moved.</summary>
    /// <remarks>
    /// Called from every setter that feeds <see cref="Health"/>, and from the sync panel's
    /// own notifications — a failed project is the one condition that does not live on this
    /// view model.
    /// </remarks>
    private void RaiseHealth()
    {
        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(HealthBrushKey));
        OnPropertyChanged(nameof(HealthSummary));
    }

    // ------------------------------------------------------- git's own health (plan §12a/§12b)

    private GitPresence _gitPresence = GitPresence.Unknown;

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
                RaiseHealth();
            }
        }
    }

    /// <summary>True only once the probe has come back and said no.</summary>
    public bool GitIsMissing => _gitPresence.Availability == GitAvailability.Missing;

    public string GitPresenceExplanation => _gitPresence.Explanation;

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

    /// <summary>Old file on the left, new on the right, instead of one interleaved column.</summary>
    /// <remarks>
    /// Render-only, unlike <see cref="ShowFullFile"/>: the split is a fold of the diff the
    /// pane already holds, so flipping it re-reads nothing — the view rebuilds its rows from
    /// the same text. Not persisted, deliberately, for now: which mode reads better depends
    /// on the file in front of you, not on the person.
    /// </remarks>
    public bool DiffSideBySide
    {
        get => _diffSideBySide;
        set
        {
            if (SetProperty(ref _diffSideBySide, value))
            {
                _logger.Click($"Diff.SideBySide={value}");
            }
        }
    }

    private bool _diffSideBySide;

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
    /// A repository another Versio already has open, with the sentence to show.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RepositoryRejected"/> because it is not a rejection of the
    /// folder: the repository is perfectly good and will open the moment the other window lets
    /// go. Reusing that event would have meant inventing a <c>RepositoryKind</c> for a state
    /// that is about this machine rather than about the folder.
    /// </remarks>
    public event Action<string>? RepositoryBusyElsewhere;

    /// <summary>The marker store for the repository this window has open, if any.</summary>
    private JsonSessionMarkerStore? _session;

    /// <summary>This machine and this process, for the §5.G presence marker.</summary>
    private readonly MachineIdentity _machine = new();

    /// <summary>How this window decides whether another Versio is really running.</summary>
    private readonly IProcessProbe _processes;

    /// <summary>
    /// Take the repository for this window, or refuse because another Versio has it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every failure mode here resolves to "open it".</b> An unreadable marker, an
    /// unwritable one, a crashed session, a marker from before the last restart — all of them
    /// mean nobody is really there. The guard exists to stop two live orchestrators, not to
    /// make a repository unusable because a file could not be written.
    /// </para>
    /// <para>
    /// The previous repository is released first. Switching from A to B and back must not find
    /// A still claimed by this same window.
    /// </para>
    /// </remarks>
    private bool ClaimRepository(string path)
    {
        var store = new JsonSessionMarkerStore(path, _logger);
        var verdict = RepositoryPresence.Evaluate(store.Read(), _machine, _processes);

        if (!verdict.CanOpen)
        {
            _logger.Warn(LogCategory.App, $"{path}: {verdict.Explanation}");
            StatusSummary = "Already open in another Versio window";
            RepositoryBusyElsewhere?.Invoke(verdict.Explanation);
            return false;
        }

        if (verdict.Availability is RepositoryAvailability.Free &&
            verdict.Explanation.Length > 0)
        {
            // "the session that left it is gone", "from before the last restart". Worth a line:
            // it is the difference between a clean open and one that stepped over somebody
            // else's leftovers, and it is the only place that is ever said.
            _logger.Info(LogCategory.App, $"{path}: {verdict.Explanation}");
        }

        ReleaseRepository();

        _session = store;
        store.Write(RepositoryPresence.MarkerFor(
            _machine, RepositoryPresence.SessionIdOf(_machine), _clock.UtcNow));

        return true;
    }

    /// <summary>Give up the claim on whatever this window had open.</summary>
    private void ReleaseRepository()
    {
        _session?.Release(RepositoryPresence.SessionIdOf(_machine));
        _session = null;
    }

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
            Auth.Identity.IsComplete
                ? $"{Auth.Identity.Name} <{Auth.Identity.Email}>"
                : Auth.Identity.Explanation));

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

        // ONE REPOSITORY, ONE VERSIO — safeguards §5.G.
        //
        // Two instances on one repository means two orchestrators writing repo.json and
        // state.json over each other, and until now nothing stopped it: the only signal was
        // LockRecovery.HeldByLiveSession, which promised "opening read-only" that nothing
        // implemented AND only fired for a binding that happened to hold a lock.
        //
        // Checked here, after git has agreed it will work in this folder and before anything
        // attaches: refusing later would mean tearing down a watcher and an orchestrator that
        // had already started writing.
        if (!ClaimRepository(path))
        {
            return false;
        }

        // Past every refusal, so the switch is certain. Everything on screen belongs to the
        // repository being left, and this is the last moment at which that is still true.
        ResetForRepositoryChange(_repositoryPath, path);

        _git = client;
        Auth.AttachClient(client);
        RepositoryPath = path;
        _autoStash = new JsonAutoStashStore(path, _logger);

        // The status light's "no repository open" is this field, and nothing else on the way
        // through here touches a property with a setter that would raise it.
        RaiseHealth();

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
        RemoteDisplay = DescribeRemote(remoteUrl);

        await RememberAsync(path, remoteUrl).ConfigureAwait(true);

        // Before the first refresh, and long before anything can be committed, so the panel
        // can say "git needs a name" while the user is still looking at an empty commit box
        // rather than after a 17-31 s export has run for a commit git will refuse.
        await Auth.LoadIdentityAsync(busy.Token).ConfigureAwait(true);

        // Whether there is anywhere to push at all. Read here rather than in the refresh for
        // the reason above, and before the first refresh so the very first Standing computed
        // is already right - otherwise a repository with a remote would flash "no remote
        // configured" for one frame on every open.
        Remote.NoteHasRemote(
            (await client.RemotesAsync(busy.Token).ConfigureAwait(true)).Count > 0);

        // The remote's shape decides what a credential failure will mean, so it is parsed once
        // here rather than re-derived at each failure. remoteUrl was already read above for
        // the recent-repositories list.
        Auth.SetRemote(RemoteUrl.Parse(remoteUrl));

        await Auth.CheckCredentialAsync(busy.Token).ConfigureAwait(true);

        RefreshCommand.RaiseCanExecuteChanged();

        // Watch before the first refresh, so a change landing during it is not missed.
        StartWatching(path);

        // Only once there is something to watch. Start is idempotent, and PollTarget returns
        // nothing until a Standing with an upstream has been computed.
        Remote.StartWatching();

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

    // ---------------------------------------------------------------- git panel

    /// <summary>Files git will include in the next commit.</summary>
    public ObservableCollection<ChangeRow> Staged { get; } = [];

    /// <summary>Files changed in the working tree but not staged.</summary>
    public ObservableCollection<ChangeRow> Unstaged { get; } = [];

    // ------------------------------------------------------- per-file selection (plan §4)

    /// <summary>How many staged rows are ticked.</summary>
    public int StagedSelectedCount => Staged.Count(r => r.IsSelected);

    public int UnstagedSelectedCount => Unstaged.Count(r => r.IsSelected);

    /// <summary>
    /// The staged list's master checkbox, three-state.
    /// </summary>
    /// <remarks>
    /// Nullable bool because that is what WPF's <c>IsThreeState</c> reads: null is the
    /// indeterminate middle. Setting it ticks or clears the whole list; the indeterminate value
    /// is only ever produced, never accepted, because "set this to partially ticked" has no
    /// meaning.
    /// </remarks>
    public bool? StagedAllSelected
    {
        get => ToCheck(StagingSelection.StateFor(Staged.Count, StagedSelectedCount));
        set => SetAll(Staged, value == true);
    }

    public bool? UnstagedAllSelected
    {
        get => ToCheck(StagingSelection.StateFor(Unstaged.Count, UnstagedSelectedCount));
        set => SetAll(Unstaged, value == true);
    }

    /// <summary>"3 of 12 selected", or nothing when nothing is ticked.</summary>
    public string StagedSelectionCaption =>
        StagingSelection.Caption(Staged.Count, StagedSelectedCount);

    public string UnstagedSelectionCaption =>
        StagingSelection.Caption(Unstaged.Count, UnstagedSelectedCount);

    /// <summary>"STAGED · 12", or "STAGED · 3 of 12 selected" while anything is ticked.</summary>
    /// <remarks>
    /// The label is passed in from here rather than living in <see cref="StagingSelection"/>,
    /// because the casing is this panel's typographic choice and Core has no business holding
    /// it. What Core owns is the rule about which of the two sentences applies.
    /// </remarks>
    public string StagedHeader =>
        StagingSelection.Header("STAGED", Staged.Count, StagedSelectedCount);

    public string UnstagedHeader =>
        StagingSelection.Header("NOT STAGED", Unstaged.Count, UnstagedSelectedCount);

    /// <summary>
    /// What the commit button will do right now.
    /// </summary>
    /// <remarks>
    /// Recomputed whenever the lists or the ticks move. The three behaviours it covers are why
    /// the panel no longer carries a <c>Commit</c> and a <c>Commit &amp; Push</c>: push is its
    /// own act in the remote row, and what is left is a commit that also does the step people
    /// forget.
    /// </remarks>
    public CommitPlan Commit =>
        CommitPlan.For(Staged.Count, Unstaged.Count, UnstagedSelectedCount);

    /// <summary>Why the commit button is dead, or empty when it is live.</summary>
    /// <remarks>
    /// Bound to the button's tooltip with <c>ToolTipService.ShowOnDisabled</c>, because WPF
    /// hides a tooltip on a disabled control by default — so without it the one moment the
    /// explanation is wanted is the one moment it cannot be seen.
    /// </remarks>
    public string CommitBlockedReason =>
        Commit.BlockedReason(
            hasMessage: !string.IsNullOrWhiteSpace(CommitMessage),
            hasIdentity: Auth.Identity.IsComplete,
            hasConflicts: HasConflicts);

    /// <summary>What the commit button will do, live or not.</summary>
    /// <remarks>
    /// The shortcut rides on the live explanation only (GP-L §10.6) — a key nobody can find
    /// is a key nobody has, and the tooltip is where the button already explains itself. It
    /// stays OFF the blocked reason, because "Nothing to commit. (Ctrl+Enter)" advertises a
    /// key at the one moment it does nothing — the chrome test that pins the blocked wording
    /// caught exactly that.
    /// </remarks>
    public string CommitExplanation =>
        CommitBlockedReason.Length > 0
            ? CommitBlockedReason
            : $"{Commit.Explanation} (Ctrl+Enter)";

    /// <summary>
    /// True while git reports an unmerged path.
    /// </summary>
    /// <remarks>
    /// Read from the last status. The panel's conflict <i>surface</i> is GP-G and is not built;
    /// what is here is the gate, because committing over a conflict is refused by git anyway
    /// and a button that says why beats one that fails.
    /// </remarks>
    public bool HasConflicts => Changes.Any(c => c.Kind == ChangeKind.Conflicted);

    // ------------------------------------------------------- conflicts (plan §6, §9b)

    private MergeState _mergeState = MergeState.None;

    /// <summary>Whether a merge or rebase is part-way through.</summary>
    /// <remarks>
    /// Read from git's own marker files, not inferred from unmerged paths. Resolving every
    /// conflict leaves the tree clean while the merge is still <i>in progress</i>, and a panel
    /// that read "no conflicts" as "nothing happening" would offer a plain commit for what is
    /// the conclusion of a merge.
    /// </remarks>
    public MergeState MergeState
    {
        get => _mergeState;
        private set
        {
            if (SetProperty(ref _mergeState, value))
            {
                OnPropertyChanged(nameof(IsMidMerge));
                OnPropertyChanged(nameof(ConflictHeadline));
                RaiseHealth();
                RaiseGitCommandStates();
            }
        }
    }

    public bool IsMidMerge => _mergeState is not MergeState.None;

    /// <summary>The conflicted paths, as their own section.</summary>
    /// <remarks>
    /// Not "staged" and not "not staged" — they are <i>stuck</i>, and putting them in either
    /// list would misrepresent what git will do with them. A third section is the honest shape.
    /// </remarks>
    public ObservableCollection<ChangeRow> Conflicted { get; } = [];

    /// <summary>What is wrong and what to do, in one line.</summary>
    public string ConflictHeadline
    {
        get
        {
            var count = Conflicted.Count;

            if (count > 0)
            {
                var what = _mergeState == MergeState.Rebasing ? "rebase" : "merge";
                return count == 1
                    ? $"1 file conflicts. Resolve it, then finish the {what}."
                    : $"{count} files conflict. Resolve them, then finish the {what}.";
            }

            return _mergeState switch
            {
                MergeState.Merging => "Every conflict is resolved. Commit to finish the merge.",
                MergeState.Rebasing => "Every conflict is resolved. Continue the rebase.",
                _ => string.Empty,
            };
        }
    }

    /// <summary>Take this side of the selected conflict.</summary>
    public AsyncCommand<bool> ResolveCommand { get; private set; } = null!;

    /// <summary>Undo the merge or rebase entirely.</summary>
    public AsyncCommand AbortMergeCommand { get; private set; } = null!;

    /// <summary>The way out of a diverged branch that a fast-forward pull cannot take.</summary>
    public AsyncCommand<bool> ReconcileCommand { get; private set; } = null!;

    private ChangeRow? _selectedConflict;

    public ChangeRow? SelectedConflict
    {
        get => _selectedConflict;
        set
        {
            if (SetProperty(ref _selectedConflict, value))
            {
                ResolveCommand.RaiseCanExecuteChanged();

                if (value is not null)
                {
                    _ = ShowPathAsync(value.Path, staged: false);
                }
            }
        }
    }

    // ------------------------------------------------------- branches (plan §7)

    private DefaultBranch _defaultBranch = DefaultBranch.None;

    /// <summary>
    /// Which branch this repository treats as its trunk.
    /// </summary>
    /// <remarks>
    /// Nothing in the codebase knew until now, and two things need it: deleting the branch you
    /// are standing on has to check something else out first, and a merge request has to name a
    /// target. Resolved down a ladder that ends in a refusal rather than a guess.
    /// </remarks>
    public DefaultBranch DefaultBranch
    {
        get => _defaultBranch;
        private set
        {
            if (SetProperty(ref _defaultBranch, value))
            {
                OnPropertyChanged(nameof(DefaultBranchName));
                RaiseGitCommandStates();
            }
        }
    }

    public string DefaultBranchName => _defaultBranch.Name;

    private int? _requestNumber;
    private BranchInfo? _branch;

    /// <summary>
    /// Which hosting product this repository's remote belongs to.
    /// </summary>
    /// <remarks>
    /// <b>Derived on each read rather than resolved once at open, and that is the point.</b> The
    /// obvious version caches it alongside <see cref="GitAuthViewModel.Remote"/> when the
    /// repository opens — but
    /// <c>repo.json</c> is loaded by <c>Sync.AttachAsync</c>, which runs <i>after</i> that, so
    /// a repository whose forge is only knowable from its config would have cached Unknown and
    /// shown no button until the next repository switch. That is the same ordering trap the
    /// object index hit; there it needed an event, and here it needs nothing at all, because
    /// this is an enum switch over two fields and costs less than the notification would.
    /// </remarks>
    private ForgeKind Forge => Core.Git.Forge.Detect(Auth.Remote, Sync.ConfiguredForge);

    /// <summary>
    /// The merge-request button: whether it is there, what it says, and where it goes.
    /// </summary>
    /// <remarks>
    /// Computed rather than stored, exactly like <see cref="Commit"/>: it is a function of the
    /// branch, the remote standing, the trunk and whether the tree is clean, and every one of
    /// those already raises when it moves. A stored copy would be a fifth thing to keep in step.
    /// </remarks>
    public MergeRequestPlan MergeRequest => MergeRequestPlan.For(
        Forge,
        Auth.Remote,
        _branch,
        Remote.Standing.State,
        DefaultBranch,
        _treeIsDirty,
        _requestNumber);

    /// <summary>
    /// Anything staged, unstaged or conflicted, as of the last refresh.
    /// </summary>
    /// <remarks>
    /// <b>A captured flag rather than three <c>Count</c> reads, because this is read off the UI
    /// thread.</b> The remote watch calls <see cref="ProbeMergeRequestAsync"/> on its own loop
    /// and that consults <see cref="MergeRequest"/> — so reading an
    /// <see cref="ObservableCollection{T}"/> there would be racing the UI thread's own
    /// <c>Insert</c> and <c>RemoveAt</c> as the lists reconcile. Set where the lists are set,
    /// which is the only place it can change.
    /// </remarks>
    private bool _treeIsDirty;

    /// <summary>
    /// What the merge-request button will do, or why it cannot.
    /// </summary>
    /// <remarks>
    /// One binding for both states, because a tooltip that went empty when the button went live
    /// would take the explanation away at the moment somebody is deciding whether to press it.
    /// Live, it says where the click goes — which is worth reading before opening a browser.
    /// </remarks>
    public string MergeRequestTooltip => MergeRequest.BlockedReason.Length > 0
        ? MergeRequest.BlockedReason
        : MergeRequest.Url;

    /// <summary>Open the delete-branches dialog.</summary>
    public AsyncCommand DeleteBranchesCommand { get; private set; } = null!;


    /// <summary>Button labels that say what will happen, not what the button is called.</summary>
    public string StageLabel =>
        StagingSelection.Label("Stage", Unstaged.Count, UnstagedSelectedCount);

    public string UnstageLabel =>
        StagingSelection.Label("Unstage", Staged.Count, StagedSelectedCount);

    public string DiscardLabel =>
        StagingSelection.Label("Discard", Unstaged.Count, UnstagedSelectedCount);

    private static bool? ToCheck(SelectionState state) => state switch
    {
        SelectionState.All => true,
        SelectionState.Some => null,
        _ => false,
    };

    private void SetAll(ObservableCollection<ChangeRow> rows, bool selected)
    {
        foreach (var row in rows)
        {
            row.IsSelected = selected;
        }

        RaiseSelectionState();
    }

    /// <summary>
    /// The rows an action applies to: the ticked ones, or all of them when none are.
    /// </summary>
    /// <remarks>
    /// What makes "Stage all" and "Stage selected" one button. The label carries the difference
    /// so nobody presses it expecting the other meaning.
    /// </remarks>
    private static IReadOnlyList<ChangeRow> Chosen(ObservableCollection<ChangeRow> rows)
    {
        var ticked = rows.Where(r => r.IsSelected).ToList();
        return ticked.Count > 0 ? ticked : [.. rows];
    }

    /// <summary>Recount after anything that could have changed a tick.</summary>
    private void RaiseSelectionState()
    {
        OnPropertyChanged(nameof(StagedSelectedCount));
        OnPropertyChanged(nameof(UnstagedSelectedCount));
        OnPropertyChanged(nameof(StagedAllSelected));
        OnPropertyChanged(nameof(UnstagedAllSelected));
        OnPropertyChanged(nameof(StagedSelectionCaption));
        OnPropertyChanged(nameof(UnstagedSelectionCaption));
        OnPropertyChanged(nameof(StagedHeader));
        OnPropertyChanged(nameof(UnstagedHeader));
        OnPropertyChanged(nameof(StageLabel));
        OnPropertyChanged(nameof(UnstageLabel));
        OnPropertyChanged(nameof(DiscardLabel));
        RaiseCommitState();
        RaiseGitCommandStates();
    }

    public AsyncCommand StageCommand { get; private set; } = null!;
    public AsyncCommand UnstageCommand { get; private set; } = null!;
    /// <summary>
    /// Stage what the panel says it will, then commit.
    /// </summary>
    /// <remarks>
    /// <b><c>CommitAndPushCommand</c> is gone.</b> It bundled two operations with different
    /// failure modes into one button, and its failure story was the worst in the panel: the
    /// commit succeeds, the push fails, and one message has to describe both. Push is a
    /// deliberate act of its own now, in the remote row where the ahead-count already lives.
    /// </remarks>
    public AsyncCommand CommitCommand { get; private set; } = null!;

    /// <summary>Put a branch on the remote for the first time.</summary>
    /// <remarks>
    /// Separate from <see cref="PushCommand"/> because it is a separate operation: a plain
    /// push on a branch with no upstream fails outright, so folding the two together would
    /// give one button two meanings and a failure that only appears sometimes.
    /// </remarks>

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
                OnPropertyChanged(nameof(CommitSubjectBrushKey));
                RaiseCommitState();
                CommitCommand.RaiseCanExecuteChanged();

                // The draft is offered only into an empty box, so typing the first character
                // withdraws it and clearing the box offers it again.
                DraftCommitMessageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Length of the subject line, for the 50/72 guide.</summary>
    public int CommitSubjectLength =>
        (_commitMessage.Split('\n').FirstOrDefault() ?? string.Empty).TrimEnd('\r').Length;

    /// <summary>
    /// The counter's colour, which is what makes the bare number decodable (GP-L §10.5).
    /// </summary>
    /// <remarks>
    /// The tooltip already explains the 50/72 convention to whoever hovers; the colour is
    /// for everyone who does not. Quiet within the convention, the modified amber over it,
    /// and the deleted red past git's own hard wrap — the same escalation the rest of the
    /// panel uses, so it reads without being learned.
    /// </remarks>
    public string CommitSubjectBrushKey =>
        CommitSubjectLength <= 50 ? "Brush.Text.Disabled"
        : CommitSubjectLength <= 72 ? "Brush.Status.Modified"
        : "Brush.Status.Deleted";

    /// <summary>
    /// Where a push goes — <c>origin · github.com/org/repo</c> — in the branch card
    /// (GP-L §10.3).
    /// </summary>
    /// <remarks>
    /// The word "push" is meaningless without it, and on a machine with several remotes it
    /// is a real question. Empty when the repository has no origin, and the card hides the
    /// line: "no remote" is the standing sentence's job, said once.
    /// </remarks>
    public string RemoteDisplay
    {
        get => _remoteDisplay;
        private set => SetProperty(ref _remoteDisplay, value);
    }

    private string _remoteDisplay = string.Empty;

    private static string DescribeRemote(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        // The parsed form rather than the raw URL, because the raw one answers a different
        // question: ssh://git@host:2222/org/repo.git says how to reach it, host/org/repo
        // says where it is — and the credential in an https URL must never reach a label.
        var parsed = RemoteUrl.Parse(url);
        var where =
            parsed is { Owner.Length: > 0, Repository.Length: > 0 }
                ? $"{parsed.Authority}/{parsed.Owner}/{parsed.Repository}"
                : parsed.Authority.Length > 0
                    ? parsed.Authority
                    : url;

        return $"origin · {where}";
    }

    /// <summary>One line instead of two empty boxes (GP-L §10.2).</summary>
    /// <remarks>
    /// Two boxes labelled STAGED and NOT STAGED with nothing in them say less than a
    /// sentence and take four times the height. Conflicts count as not-clean even though
    /// they sit in neither list, because "clean" over a merge in progress would be a lie.
    /// </remarks>
    public bool IsWorkingTreeClean =>
        RepositoryPath is not null
        && Staged.Count == 0 && Unstaged.Count == 0 && Conflicted.Count == 0;

    private string _changeSummary = string.Empty;

    /// <summary>
    /// What the commit button is about to record, in one sentence about objects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It describes what will be committed, not what changed.</b> Those differ the moment
    /// anything is staged, and a line above the button that counted the whole working tree
    /// while the button beside it said <c>Commit</c> — staged only — would be two controls
    /// disagreeing about the same press. The set comes from <see cref="Commit"/>, so there is
    /// one answer to "what is about to happen" and the caption and this sentence are both
    /// reading it.
    /// </para>
    /// <para>
    /// Empty when nothing about to be committed belongs to a PLC project, and the line is
    /// hidden rather than showing a count of files the list is already showing.
    /// </para>
    /// </remarks>
    public string ChangeSummaryLine
    {
        get => _changeSummary;
        private set
        {
            if (SetProperty(ref _changeSummary, value))
            {
                OnPropertyChanged(nameof(HasChangeSummary));
            }
        }
    }

    /// <summary>There is something to say about objects. Hides the line when there is not.</summary>
    public bool HasChangeSummary => _changeSummary.Length > 0;

    /// <summary>Fill the message box with a subject built from the objects.</summary>
    public RelayCommand DraftCommitMessageCommand { get; private set; } = null!;

    /// <summary>
    /// The changed files the commit button is about to record.
    /// </summary>
    /// <remarks>
    /// Reads <see cref="Commit"/> rather than reimplementing its three cases. The export gate
    /// may stage more than this — it exports and stages a mirror before any commit — and that
    /// is deliberately not modelled here for the reason <see cref="CommitPlan"/> gives: this
    /// answers what the user asked for.
    /// </remarks>
    private IReadOnlyList<FileChange> Committing()
    {
        IEnumerable<string> paths = Commit.Intent switch
        {
            CommitIntent.CommitStaged => Staged.Select(r => r.Path),
            CommitIntent.StageTickedThenCommit => Staged.Select(r => r.Path)
                .Concat(Unstaged.Where(r => r.IsSelected).Select(r => r.Path)),
            CommitIntent.StageAllThenCommit => Staged.Select(r => r.Path)
                .Concat(Unstaged.Select(r => r.Path)),
            _ => [],
        };

        var wanted = paths.ToHashSet(StringComparer.Ordinal);
        return [.. _lastChanges.Where(c => wanted.Contains(c.Path))];
    }

    /// <summary>
    /// Name every row from the current index, and restate what is about to be committed.
    /// </summary>
    /// <remarks>
    /// Called after the staging lists move and again after the tree refresh that rebuilds the
    /// index, because those land in that order: the lists come from <c>git status</c> and the
    /// index from the manifests behind it. On the first refresh after a repository opens the
    /// first pass names nothing and the second names everything; on every refresh after that
    /// the index is already current and the first pass does the work.
    /// </remarks>
    private void DescribeRows()
    {
        var index = Sync.Objects;

        foreach (var row in Conflicted.Concat(Staged).Concat(Unstaged))
        {
            row.Describe(index.Lookup(row.Path));
        }

        ChangeSummaryLine = ChangeSummary.Sentence(Committing(), index);
        DraftCommitMessageCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Fill the box with a drafted subject, without overwriting anything typed.
    /// </summary>
    /// <remarks>
    /// <b>Refuses rather than replaces.</b> The button is disabled once the box has anything in
    /// it, so there is no press that can lose a half-written message — an "are you sure" over
    /// somebody's own words is a worse answer than not offering the action. Clearing the box
    /// makes the draft available again.
    /// </remarks>
    private void DraftCommitMessage()
    {
        var subject = ChangeSummary.DraftSubject(Committing(), Sync.Objects);
        if (subject.Length == 0)
        {
            return;
        }

        _logger.Click("Git.DraftMessage");
        CommitMessage = subject;
        _logger.Info(LogCategory.Git, $"drafted commit subject: {subject}");
    }

    private bool CanDraftCommitMessage() =>
        CommitMessage.Trim().Length == 0 &&
        ChangeSummary.DraftSubject(Committing(), Sync.Objects).Length > 0;

    /// <summary>Open this branch's merge request, or the page for creating one.</summary>
    public RelayCommand OpenMergeRequestCommand { get; private set; } = null!;

    /// <summary>
    /// Open the forge page in the user's browser.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Opened, never submitted.</b> The URL carries the source and target branches already
    /// chosen; a human presses Create in the forge's own interface. Nothing outward-facing
    /// happens on Versio's say-so, and there is no code path here that posts anything.
    /// </para>
    /// <para>
    /// <c>UseShellExecute</c> so Windows picks the default browser. The URL is built by
    /// <see cref="Forge"/> from a parsed remote and a branch name — never from anything typed —
    /// and the plan hands back an empty string rather than a guess, which is why this can
    /// refuse on emptiness alone.
    /// </para>
    /// </remarks>
    private void OpenMergeRequest()
    {
        var plan = MergeRequest;
        if (!plan.CanOpen)
        {
            return;
        }

        _logger.Click("Git.MergeRequest");

        // Logged whole, and safe to be: this URL is assembled from a parsed RemoteUrl, and
        // RemoteUrl drops any embedded user:password before it records the host — precisely so
        // credentials cannot reach a log line, a browser URL or a credential lookup key. There
        // is no path by which one arrives here.
        _logger.Info(LogCategory.Git, $"opening {plan.Url}");

        try
        {
            Process.Start(new ProcessStartInfo(plan.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // A machine with no browser association, or a policy that blocks the launch. The
            // URL is the whole value here, so the failure card carries it: somebody can paste
            // it themselves and is no worse off than before the button existed.
            _logger.Error(LogCategory.Git, "could not open the browser", ex);
            Notifications.Failure("Could not open the browser.", plan.Url);
        }
    }

    /// <summary>
    /// Ask the remote whether a request already exists for this branch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only when the answer would change something.</b> The probe costs a round trip, so it
    /// is skipped unless the button is actually offering to create one on a forge that
    /// publishes request refs — which excludes the default branch, a dirty tree, unpushed work,
    /// every unidentified forge, and Azure and Bitbucket entirely. In the common case of
    /// sitting on the trunk it never runs at all.
    /// </para>
    /// <para>
    /// Called by the poller on its schedule rather than on one of its own, so it inherits the
    /// backoff, the suspension while other git work runs, and the halt on a rejected
    /// credential.
    /// </para>
    /// </remarks>
    private async Task ProbeMergeRequestAsync(CancellationToken ct)
    {
        if (_git is null || MergeRequest.Intent is not MergeRequestIntent.Create)
        {
            return;
        }

        var pattern = Core.Git.Forge.RequestRefPattern(Forge);
        if (pattern.Length == 0 || _branch is null)
        {
            return;
        }

        // Captured before the two round trips, and checked again after them. A probe takes as
        // long as the network does, and switching branch in the middle of one is an ordinary
        // thing to do — without this, the answer for the branch we were on would be stamped
        // onto the branch we are now on, and the button would offer to open somebody else's
        // pull request.
        var asked = _branch.Name;

        var tip = await _git.RevParseAsync(asked, ct).ConfigureAwait(false);
        if (tip.Length == 0)
        {
            return;
        }

        var refs = await _git.LsRemoteRefsAsync("origin", pattern, ct).ConfigureAwait(false);
        var found = Core.Git.Forge.RequestNumber(refs, tip);

        if (found == _requestNumber)
        {
            return;
        }

        // Back to the UI thread: this runs on the poller's loop, and the property it changes
        // is bound.
        OnUi(() =>
        {
            if (!string.Equals(_branch?.Name, asked, StringComparison.Ordinal))
            {
                return;
            }

            _requestNumber = found;

            // Only when the answer MOVED — the early return above is what keeps this off the
            // poller's cadence. A line per probe would be 360 an hour saying the same thing,
            // which is how the log panel got drowned before.
            _logger.Info(LogCategory.Git, found is { } n
                ? $"{Core.Git.Forge.Noun(Forge)} #{n} already covers {asked}"
                : $"no {Core.Git.Forge.Noun(Forge)} found for {asked}");

            OnPropertyChanged(nameof(MergeRequest));
            OnPropertyChanged(nameof(MergeRequestTooltip));
            OpenMergeRequestCommand.RaiseCanExecuteChanged();
        });
    }

    private void BuildGitCommands()
    {
        DraftCommitMessageCommand = new RelayCommand(DraftCommitMessage, CanDraftCommitMessage);
        OpenMergeRequestCommand = new RelayCommand(OpenMergeRequest, () => MergeRequest.CanOpen);

        // Chosen(), not the whole list: the ticked rows when any are, otherwise all of them.
        StageCommand = new AsyncCommand(
            () => StageAsync(Chosen(Unstaged), stage: true),
            () => Ready && Unstaged.Count > 0, ReportError);

        UnstageCommand = new AsyncCommand(
            () => StageAsync(Chosen(Staged), stage: false),
            () => Ready && Staged.Count > 0, ReportError);

        CommitCommand = new AsyncCommand(CommitAsync, CanCommit, ReportError);

        BuildHunkCommand();


        // Needs somewhere to go if the current branch is deleted, and something to delete.
        DeleteBranchesCommand = new AsyncCommand(
            DeleteBranchesAsync,
            () => Ready && Branches.Count > 1 && DefaultBranch.IsKnown, ReportError);

        ResolveCommand = new AsyncCommand<bool>(
            ResolveSelectedAsync,
            _ => Ready && SelectedConflict is not null, ReportError);

        AbortMergeCommand = new AsyncCommand(
            AbortMergeAsync, () => Ready && IsMidMerge, ReportError);

        // Enabled only while diverged. Before this there was no route forward at all from
        // inside the application: the badge said "pull first" and --ff-only refuses a diverged
        // branch, which is advice pointing at a button that cannot work.
        ReconcileCommand = new AsyncCommand<bool>(
            ReconcileAsync,
            _ => Ready && Remote.Standing.State == TrackingState.Diverged, ReportError);

        // Enabled only with a row selected. Discard is per-file on purpose: a "discard all"
        // beside "stage all" would put an irreversible button one pixel away from a routine
        // one, and the two are not comparable operations.
        DiscardCommand = new AsyncCommand(
            DiscardAsync, () => Ready && SelectedUnstaged is not null, ReportError);

        CreateBranchCommand = new AsyncCommand(CreateBranchAsync, () => Ready, ReportError);

        // Cloning needs no open repository — it is how you get one — so it is enabled
        // whenever nothing else is running, unlike every other command here.
        CloneCommand = new AsyncCommand(() => CloneRepositoryAsync(), () => !Busy.IsBusy, ReportError);


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
                    Remote.Wake();
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
    /// <summary>Everything the commit button's caption, tooltip and enabled state read.</summary>
    private void RaiseCommitState()
    {
        OnPropertyChanged(nameof(Commit));
        OnPropertyChanged(nameof(CommitBlockedReason));
        OnPropertyChanged(nameof(CommitExplanation));
        OnPropertyChanged(nameof(HasConflicts));

        // The rail's badge reads HasConflicts for its colour, so it moves whenever that does.
        // The other half - the count - comes from Changes, and is raised where that collection
        // changes rather than here.
        OnPropertyChanged(nameof(ChangeBadge));
    }

    private bool CanCommit() =>
        Ready
        && Commit.CanCommit
        && !string.IsNullOrWhiteSpace(CommitMessage)
        && Auth.Identity.IsComplete
        && !HasConflicts;

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

    private void RaiseGitCommandStates()
    {
        StageCommand.RaiseCanExecuteChanged();
        UnstageCommand.RaiseCanExecuteChanged();
        CommitCommand.RaiseCanExecuteChanged();
        Remote.RaiseCommandStates();
        Auth.RaiseCommandStates();
        RefreshCommand.RaiseCanExecuteChanged();
        HunkCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
        CreateBranchCommand.RaiseCanExecuteChanged();
        CloneCommand.RaiseCanExecuteChanged();

        // Every input the plan reads — branch, standing, trunk, dirtiness — has just moved or
        // may have. It is computed, so raising it is the whole update.
        OnPropertyChanged(nameof(MergeRequest));
        OnPropertyChanged(nameof(MergeRequestTooltip));
        OpenMergeRequestCommand.RaiseCanExecuteChanged();

        // THESE FOUR WERE BEING RAISED FROM THE SETTER OF AuthState, AND NOWHERE ELSE.
        //
        // Mis-indented inside it, which is how a paste ends up somewhere nothing about it
        // belongs: not one of them is gated on anything to do with credentials. Abandon
        // merge is Ready && IsMidMerge, the rebase-or-merge choice is Ready &&
        // Standing.State == Diverged, and delete-branches is Ready && Branches.Count > 1
        // && DefaultBranch.IsKnown.
        //
        // AsyncCommand raises a plain event and deliberately does not hook
        // CommandManager.RequerySuggested, so a command left out of this sweep is never
        // re-evaluated at all. A refresh that discovered a merge in progress raised
        // IsMidMerge, called this method, and left Abandon merge exactly as disabled as it
        // had been - until something unrelated happened to the credentials.
        DeleteBranchesCommand.RaiseCanExecuteChanged();
        ResolveCommand.RaiseCanExecuteChanged();
        AbortMergeCommand.RaiseCanExecuteChanged();
        ReconcileCommand.RaiseCanExecuteChanged();
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
    private async Task CommitAsync()
    {
        if (_git is null)
        {
            return;
        }

        // Captured before anything runs. The gate below stages export trees and the refresh
        // rebuilds the lists, so a plan read afterwards would describe a different panel than
        // the one whose button was pressed.
        var plan = Commit;

        _logger.Click("Git.Commit");
        using var scope = _logger.BeginOperation(Guid.NewGuid().ToString("N")[..8]);

        // WHAT THE USER ASKED FOR, BEFORE WHAT THE GATE INSISTS ON.
        //
        // Staging first means the export gate's own staging lands on top of a tree that
        // already holds the user's choice, rather than racing it. And the CommitStaged case
        // stages nothing at all - which is the whole safeguard: somebody who staged three
        // files deliberately does not get the fourth swept in because they pressed a button
        // whose caption said "Commit".
        if (plan.Intent is CommitIntent.StageTickedThenCommit or CommitIntent.StageAllThenCommit)
        {
            var rows = Chosen(Unstaged);
            _logger.Info(LogCategory.Git,
                $"{plan.Label}: staging {rows.Count} file(s) before the commit");

            if (!await StageForCommitAsync(rows).ConfigureAwait(true))
            {
                return;
            }
        }

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

        // The last cheap moment (GP-K §9h). A host rejects a large binary at the PUSH, by
        // which time the object is in history and removing it is a rewrite — so the
        // question is asked here, over what is actually staged after the gate's own
        // staging, not over the lists the button was pressed against.
        if (!await ConfirmLargeFilesAsync().ConfigureAwait(true))
        {
            _logger.Info(LogCategory.Git, "commit cancelled at the large-file preflight");
            Notifications.Warning("Nothing was committed.",
                "A large file without an LFS rule was declined.");
            return;
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

        // The most surprising rule in the tool, said where the decision was just made: a commit
        // does not release the direction lock. Until the work is pushed nobody else can see it,
        // so the direction is still meaningfully owned. Push is its own button now, which makes
        // that easier to act on than it was when the two were bundled.
        Notifications.Success("Committed.",
                              "Not pushed, so the direction lock is still held.");
    }

    /// <summary>Stage the rows the commit plan chose, reporting a refusal rather than
    /// committing without them.</summary>
    /// <remarks>
    /// Returns false when git refused. Carrying on would produce a commit missing exactly the
    /// files the user ticked, which is worse than no commit: it looks like it worked.
    /// </remarks>
    private async Task<bool> StageForCommitAsync(IReadOnlyList<ChangeRow> rows)
    {
        if (_git is null || rows.Count == 0)
        {
            return true;
        }

        var result = await _git
            .StageAsync([.. rows.Select(r => r.Path)], CancellationToken.None)
            .ConfigureAwait(true);

        if (result.Success)
        {
            return true;
        }

        Notifications.Failure("Nothing was committed — the files could not be staged.",
                              result.FailureReason ?? string.Empty);
        return false;
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
    internal Task PullForTestAsync() => Remote.PullForTestAsync();

    /// <summary>
    /// Take one side of the selected conflict.
    /// </summary>
    /// <remarks>
    /// <b>"Ours" and "theirs" swap meaning under a rebase</b> — git replays your commits onto
    /// the upstream, so during a rebase "ours" is the upstream's version. The labels are chosen
    /// in the view from <see cref="MergeState"/>; this passes through what the user picked, and
    /// the log records which side git was actually given.
    /// </remarks>
    private async Task ResolveSelectedAsync(bool ours)
    {
        if (_git is null || SelectedConflict is not { } row)
        {
            return;
        }

        _logger.Click(ours ? "Git.ResolveOurs" : "Git.ResolveTheirs");
        using var busy = Busy.Begin(WorkKind.GitWrite, $"Resolving {row.Path}…");

        var result = await _git.ResolveWithSideAsync(row.Path, ours, busy.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            Notifications.Failure($"Could not resolve {row.Path}.",
                                  result.FailureReason ?? string.Empty);
            return;
        }

        Notifications.Success($"Resolved {row.Path}.");
        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    /// <summary>
    /// Undo a merge or rebase that is part-way through.
    /// </summary>
    /// <remarks>
    /// Confirmed, because it throws away conflict resolutions the user may have spent real time
    /// on — and unlike a discard, what it destroys is not visible as a list of files. Offering
    /// the operation without offering the way back is how somebody ends up in a state they
    /// cannot describe, in a tool meant to keep them out of the terminal.
    /// </remarks>
    private async Task AbortMergeAsync()
    {
        if (_git is null || !IsMidMerge)
        {
            return;
        }

        var what = _mergeState == MergeState.Rebasing ? "rebase" : "merge";

        using (AskingSomething())
        {
            var confirmed = Prompts.ConfirmDestructive(new DestructivePrompt(
                $"Abandon this {what}?",
                $"The branch goes back to where it was before the {what} started.\n\n" +
                "Any conflicts you have already resolved are thrown away.",
                Confirm: "Abandon"));

            if (!confirmed)
            {
                _logger.Info(LogCategory.Git, $"{what} abort cancelled");
                return;
            }
        }

        _logger.Click("Git.AbortMerge");
        using var busy = Busy.Begin(WorkKind.GitWrite, $"Abandoning the {what}…");

        // Same as a checkout: this rewrites the working tree wholesale, and an unheld watcher
        // would report the result as an edit to the exported text and import it.
        GitResult result = default!;

        await Sync.WhileGitRewritesTheTreeAsync(
            $"{what} --abort",
            async () => result = await _git.AbortMergeOrRebaseAsync(busy.Token)
                .ConfigureAwait(true)).ConfigureAwait(true);

        if (!result.Success)
        {
            Notifications.Failure($"Could not abandon the {what}.",
                                  result.FailureReason ?? string.Empty);
            return;
        }

        Notifications.Success($"The {what} was abandoned.", "The branch is back where it was.");
        await RunRefreshAsync(RefreshTrigger.GitMetadata, null).ConfigureAwait(true);
    }

    /// <summary>
    /// Reconcile a diverged branch, by rebase or by merge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The panel's only remaining dead end, until now.</b> The badge says "diverged — pull
    /// first", and <c>pull --ff-only</c> refuses a diverged branch by design, so the advice
    /// pointed at a button that could not work and the way out was a terminal.
    /// </para>
    /// <para>
    /// <b><c>--ff-only</c> stays the default and this is not a relaxation of it.</b> That
    /// default exists because auto-merging native CODESYS XML would produce a file nobody
    /// reviewed; what changes here is that the user can ask for a merge <i>by name</i>, having
    /// been told what it means. The guard runs first, so a held direction lock or an open
    /// project refuses it exactly as it refuses a branch switch — both rewrite the working tree.
    /// </para>
    /// </remarks>
    private async Task ReconcileAsync(bool rebase)
    {
        if (_git is null)
        {
            return;
        }

        var what = rebase ? "rebase" : "merge";
        _logger.Click(rebase ? "Git.Rebase" : "Git.Merge");
        using var scope = _logger.BeginOperation(LoggerExtensions.NewOperationId());

        // The same intent a pull uses: both rewrite the project and its mirror together, which
        // is the scenario SAFEGUARDS §5.A calls able to corrupt a project through no user error.
        if (!await ClearForContextChangeAsync(GuardIntent.Pull, what).ConfigureAwait(true))
        {
            return;
        }

        using var busy = Busy.Begin(WorkKind.GitWrite, $"Running the {what}…");

        GitResult result = default!;

        await Sync.WhileGitRewritesTheTreeAsync(
            what,
            async () => result = rebase
                ? await _git.RebaseOntoUpstreamAsync(null, busy.Token).ConfigureAwait(true)
                : await _git.MergeUpstreamAsync(null, busy.Token).ConfigureAwait(true))
            .ConfigureAwait(true);

        await RunRefreshAsync(RefreshTrigger.GitMetadata, null).ConfigureAwait(true);

        if (result.Success)
        {
            Auth.NoteAuthSuccess();
            Notifications.Success($"The {what} finished.");
            return;
        }

        // A conflict is not a failure here - it is the expected outcome of reconciling two
        // branches that touched the same thing, and the panel is now equipped to show it. Only
        // say something went wrong when nothing is waiting to be resolved.
        if (Conflicted.Count > 0 || IsMidMerge)
        {
            Notifications.Warning(
                $"The {what} stopped at a conflict.",
                "Resolve the files below, or abandon it and nothing changes.");
            return;
        }

        Notifications.Failure($"The {what} did not run.", result.FailureReason ?? string.Empty);
    }

    /// <summary>
    /// Gather the branches, ask, and delete what came back.
    /// </summary>
    /// <remarks>
    /// The dialog decides <i>what</i>; every rule about whether it may happen lives here, where
    /// the repository is.
    /// </remarks>
    private async Task DeleteBranchesAsync()
    {
        if (_git is null || !DefaultBranch.IsKnown)
        {
            return;
        }

        _logger.Click("Git.DeleteBranches");

        // One id across the whole flow: the merged check, the prompt, the confirmation, the
        // checkout off a branch being deleted, one delete per branch, the prune and the
        // refresh. Filtering the log to it is how "why did that branch survive" is answered in
        // seconds rather than by reading around a dozen interleaved lines.
        using var scope = _logger.BeginOperation(LoggerExtensions.NewOperationId());

        var merged = await _git.MergedBranchesAsync(DefaultBranch.Name, CancellationToken.None)
            .ConfigureAwait(true);

        var mergedSet = merged.ToHashSet(StringComparer.Ordinal);

        var candidates = Branches
            .Select(b => new BranchCandidate
            {
                Name = b.Name,
                Existence = b.Existence,
                IsMerged = mergedSet.Contains(b.Name),
                IsCurrent = b.IsCurrent,
                IsDefault = string.Equals(b.Name, DefaultBranch.Name, StringComparison.Ordinal),
            })
            .ToList();

        DeletePlan? plan;
        using (AskingSomething())
        {
            plan = Prompts.PromptForBranchDeletion(candidates);
        }

        if (plan is null || !plan.CanProceed)
        {
            _logger.Info(LogCategory.Git, "branch deletion cancelled");
            return;
        }

        // Outward-facing, and effectively irreversible for anybody who has already fetched.
        // Confirmed with every branch named, because the consequences land on people who are
        // not in the room.
        if (plan.TouchesRemote)
        {
            using var asking = AskingSomething();
            var confirmed = Prompts.ConfirmDestructive(new DestructivePrompt(
                plan.Count == 1 ? "Delete this branch from the remote?"
                                : $"Delete {plan.Count} branches from the remote?",
                plan.RemoteConfirmation("origin"),
                Confirm: plan.Count == 1 ? "Delete it" : $"Delete {plan.Count}"));

            if (!confirmed)
            {
                _logger.Info(LogCategory.Git, "remote branch deletion cancelled");
                return;
            }
        }

        // Standing on one of them: git refuses outright, so this is handled explicitly rather
        // than surfacing the refusal. The switch goes through the ordinary branch-switch guard,
        // which means a dirty tree is offered the auto-stash exactly as it is anywhere else -
        // there is no second dirty-tree story for this dialog.
        if (plan.IncludesCurrent && !await LeaveTheBranchBeingDeletedAsync().ConfigureAwait(true))
        {
            return;
        }

        var outcomes = await DeleteEachAsync(plan).ConfigureAwait(true);
        await ReportDeletionAsync(plan, outcomes).ConfigureAwait(true);
    }

    /// <summary>Move off the branch about to be deleted, onto the trunk.</summary>
    /// <remarks>
    /// Returns false when the switch did not happen, and <b>nothing is deleted in that case</b>.
    /// A failed checkout followed by a successful delete would leave somebody on a branch that
    /// no longer exists.
    /// </remarks>
    private async Task<bool> LeaveTheBranchBeingDeletedAsync()
    {
        _logger.Info(LogCategory.Git,
            $"switching to {DefaultBranch.Name} before deleting the current branch");

        // Through the ordinary branch-switch path, entry and all, so this inherits every rule
        // that already applies: the guard, the auto-stash for a dirty tree, the direction-lock
        // refusal. A second checkout written here would be a second set of those rules.
        if (Branches.FirstOrDefault(
                b => string.Equals(b.Name, DefaultBranch.Name, StringComparison.Ordinal))
            is not { } trunk)
        {
            Notifications.Failure(
                "Nothing was deleted.",
                $"{DefaultBranch.Name} is not in this repository's branch list.");
            return false;
        }

        await SwitchBranchAsync(trunk).ConfigureAwait(true);

        if (string.Equals(BranchName, DefaultBranch.Name, StringComparison.Ordinal))
        {
            return true;
        }

        Notifications.Failure(
            "Nothing was deleted.",
            $"Versio could not switch to {DefaultBranch.Name} first, and git will not delete " +
            "the branch you are standing on.");

        return false;
    }

    /// <summary>
    /// Delete each branch, recording what happened to it.
    /// </summary>
    /// <remarks>
    /// <b><c>-d</c> is attempted first, always.</b> Git refuses an unmerged branch and that
    /// refusal is information; force is a separate, already-acknowledged decision rather than
    /// an escalation this code makes on its own.
    /// </remarks>
    private async Task<List<DeleteOutcome>> DeleteEachAsync(DeletePlan plan)
    {
        var outcomes = new List<DeleteOutcome>();

        using var busy = Busy.Begin(WorkKind.GitWrite, "Deleting branches…");

        foreach (var branch in plan.Selected)
        {
            var localOk = true;
            var failure = string.Empty;

            if (branch.HasLocal)
            {
                var gentle = await _git!.DeleteBranchAsync(branch.Name, force: false, busy.Token)
                    .ConfigureAwait(true);

                if (!gentle.Success && plan.ForceAcknowledged)
                {
                    var forced = await _git.DeleteBranchAsync(branch.Name, force: true, busy.Token)
                        .ConfigureAwait(true);

                    localOk = forced.Success;
                    failure = forced.Success ? string.Empty : forced.FailureReason ?? string.Empty;
                }
                else if (!gentle.Success)
                {
                    localOk = false;
                    failure = gentle.FailureReason ?? string.Empty;
                }
            }

            var remoteOk = false;

            // Only when the local half worked, or when there was no local half. Deleting the
            // remote copy of a branch that is still here would leave the two disagreeing in the
            // direction that loses the shared one.
            if (plan.Scope == DeleteScope.LocalAndRemote && branch.HasRemote && localOk)
            {
                var pushed = await _git!.DeleteRemoteBranchAsync("origin", branch.Name, busy.Token)
                    .ConfigureAwait(true);

                remoteOk = pushed.Success;

                if (!pushed.Success && failure.Length == 0)
                {
                    failure = pushed.FailureReason ?? string.Empty;
                }
            }

            outcomes.Add(new DeleteOutcome
            {
                Branch = branch.Name,
                LocalDeleted = branch.HasLocal && localOk,
                RemoteDeleted = remoteOk,
                Failure = failure,
            });
        }

        return outcomes;
    }

    /// <summary>
    /// Say what happened, per branch.
    /// </summary>
    /// <remarks>
    /// A single "some branches could not be deleted" is useless: it sends somebody to the
    /// terminal to find out which, which is the trip this panel exists to remove.
    /// </remarks>
    private async Task ReportDeletionAsync(DeletePlan plan, List<DeleteOutcome> outcomes)
    {
        var gone = outcomes.Where(o => o.Succeeded).ToList();
        var failed = outcomes.Where(o => !o.Succeeded).ToList();

        // Or the deleted branches linger as stale tracking refs and reappear in the dropdown
        // looking alive.
        if (gone.Any(o => o.RemoteDeleted))
        {
            await _git!.FetchPruneAsync(CancellationToken.None).ConfigureAwait(true);
        }

        if (failed.Count > 0)
        {
            Notifications.Failure(
                gone.Count == 0
                    ? "No branches were deleted."
                    : $"{gone.Count} deleted, {failed.Count} could not be.",
                string.Join(Environment.NewLine,
                            failed.Select(o => $"{o.Branch}: {o.Failure}")));
        }
        else
        {
            Notifications.Success(
                gone.Count == 1 ? $"Deleted {gone[0].Branch}." : $"Deleted {gone.Count} branches.",
                plan.TouchesRemote ? "Removed from origin as well." : string.Empty);
        }

        await RunRefreshAsync(RefreshTrigger.GitMetadata, null).ConfigureAwait(true);
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
            answer = Prompts.AskGuard(prompt) ?? GuardChoice.Cancel;
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
            var choice = Prompts.AskAboutStashedWork(
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
                if (!Prompts.ConfirmDestructive(
                        new DestructivePrompt(warning.Title, warning.Body, "Discard")))
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
        DiffLayers = [];
        ClearRendered();

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
        DiffLayers = [];
        ClearRendered();

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
        _lastChanges = status.Changes;

        // Conflicted paths come out of BOTH of git's columns - an unmerged file is reported
        // with a staged and an unstaged side - so they are taken first and excluded from the
        // other two. A file listed in all three sections would be three rows for one problem.
        var conflicted = status.Changes
            .Where(c => c.IsConflicted)
            .OrderBy(c => c.Path, StringComparer.Ordinal)
            .ToList();

        var stuck = conflicted.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);

        ReconcileInto(Conflicted, conflicted);
        ReconcileInto(Staged, [.. status.Staged
            .Where(c => !stuck.Contains(c.Path))
            .OrderBy(c => c.Path, StringComparer.Ordinal)]);
        ReconcileInto(Unstaged, [.. status.Unstaged
            .Where(c => !stuck.Contains(c.Path))
            .OrderBy(c => c.Path, StringComparer.Ordinal)]);

        OnPropertyChanged(nameof(ConflictHeadline));

        _treeIsDirty = Staged.Count > 0 || Unstaged.Count > 0 || Conflicted.Count > 0;
        OnPropertyChanged(nameof(IsWorkingTreeClean));

        // Named from the index the last tree refresh left behind, which is current except on
        // the first refresh after a repository opens. The pass after RefreshTreeAsync catches
        // that one.
        DescribeRows();

        RaiseSelectionState();
        RaiseGitCommandStates();
    }

    /// <summary>
    /// What git reported at the last refresh.
    /// </summary>
    /// <remarks>
    /// Kept because the summary line and the drafted subject describe a <i>subset</i> of it —
    /// whatever the commit button is about to record — and a <see cref="ChangeRow"/> carries
    /// its <see cref="FileChange"/> privately. Deriving the subset from paths here is cheaper
    /// than widening the row's surface for two readers.
    /// </remarks>
    private IReadOnlyList<FileChange> _lastChanges = [];

    /// <summary>
    /// Bring one staging list in step with git, in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reconciled rather than rebuilt, and that is a prerequisite rather than a
    /// refinement.</b> This used to <c>Clear()</c> both collections and refill them on every
    /// refresh — which is fine for a list whose rows carry nothing, and fatal for one whose
    /// rows carry a checkbox. A refresh fires whenever the working tree moves, so a user
    /// ticking three files out of forty would lose the ticks the next time they saved a file
    /// in their editor.
    /// </para>
    /// <para>
    /// Matched on <see cref="ChangeRow.Path"/>, which is a row's identity: a path can change
    /// status but a row never changes path, and a renamed file is a different row.
    /// </para>
    /// <para>
    /// It also fixes something nobody had complained about: rebuilding dropped the WPF list
    /// selection on every refresh, so the diff pane reset roughly once a second while somebody
    /// typed in their editor.
    /// </para>
    /// </remarks>
    private static void ReconcileInto(
        ObservableCollection<ChangeRow> rows, IReadOnlyList<FileChange> fresh)
    {
        var live = fresh.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);

        // Backwards, so removing does not shift the indices still to be examined. Nothing
        // subscribes or unsubscribes here - OnStagingListChanged does it, because a row is
        // tracked for being in the list rather than for having been added by this method.
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!live.Contains(rows[i].Path))
            {
                rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < fresh.Count; i++)
        {
            if (i >= rows.Count)
            {
                rows.Add(new ChangeRow(fresh[i]));
                continue;
            }

            if (string.Equals(rows[i].Path, fresh[i].Path, StringComparison.Ordinal))
            {
                // Same row, possibly a different status. Updated in place so whatever the user
                // has set on it survives.
                rows[i].Update(fresh[i]);
                continue;
            }

            rows.Insert(i, new ChangeRow(fresh[i]));
        }
    }

    /// <summary>
    /// Keep the tick subscriptions and the commit state in step with a list that just moved.
    /// </summary>
    /// <remarks>
    /// Rows are unsubscribed as they leave: the shell outlives every row, so a subscription
    /// left behind would keep a dead row alive for the rest of the session.
    /// </remarks>
    private void OnStagingListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.OldItems?.OfType<ChangeRow>() ?? [])
        {
            row.SelectionChanged -= RaiseSelectionState;
        }

        foreach (var row in e.NewItems?.OfType<ChangeRow>() ?? [])
        {
            row.SelectionChanged += RaiseSelectionState;
        }

        // THE WHOLE SELECTION STATE, NOT ONLY THE COMMIT BUTTON.
        //
        // This raised RaiseCommitState alone, which was enough while every count-bearing
        // caption depended only on the TICKS: with nothing ticked the captions were the empty
        // string before and after a list changed, so a missing notification looked like
        // agreement. The headers carry the list's own total now, so the same omission would
        // freeze "STAGED · 3" on a list that had grown to forty.
        //
        // It happened to work through UpdateStagingLists, which raises this itself once it has
        // reconciled both lists - but that made the CollectionChanged funnel and one of its
        // callers jointly responsible for a rule only one of them stated. This handler runs
        // for every mutation of either list by definition, which is the place the rule belongs.
        RaiseSelectionState();
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
    /// The git panel's entrance into <see cref="DiscardRowsAsync"/>, which carries the
    /// reasoning — per-file, named in the confirmation, untracked refused, direction lock
    /// released. Unreachable without a selection, which is part of that design.
    /// </remarks>
    private async Task DiscardAsync()
    {
        if (_git is null || SelectedUnstaged is not { } row)
        {
            return;
        }

        await DiscardRowsAsync([row]).ConfigureAwait(true);
    }

    /// <summary>
    /// Throw away the working-tree changes to the tree row's own files, after the same
    /// confirmation the git panel's button asks for.
    /// </summary>
    /// <remarks>
    /// The row's files are resolved against the unstaged list rather than trusted as
    /// changed: an object's two layers can differ one-and-not-the-other, and handing git a
    /// file with nothing to restore turns "discard the .st edit" into an error about the
    /// .xml beside it.
    /// </remarks>
    public async Task DiscardNodeAsync(TreeRow row)
    {
        if (_git is null)
        {
            return;
        }

        var files = row.Node.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = Unstaged.Where(r => files.Contains(r.Path)).ToList();

        if (rows.Count == 0)
        {
            StatusSummary = $"No working-tree changes to discard for {row.Name}.";
            return;
        }

        await DiscardRowsAsync(rows).ConfigureAwait(true);
    }

    /// <summary>
    /// The one flow in this application that destroys work nothing else has a copy of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not the index, not a stash, not a commit — the content is simply gone. So it acts on
    /// named files rather than a list's worth, it names every file in the confirmation, and
    /// both of its entrances require a deliberate selection.
    /// </para>
    /// <para>
    /// An <b>untracked</b> file is refused rather than attempted. <c>git restore --worktree</c>
    /// has nothing to restore it from and fails with "pathspec did not match any file(s) known
    /// to git", which reads like a bug in Versio rather than like the answer to the question
    /// that was asked. Deleting it instead would be a different operation, more destructive
    /// than the one on the button, and not one to perform because the wording happened to fit.
    /// A mixed selection discards the tracked files and says the untracked one was left.
    /// </para>
    /// <para>
    /// Discarding inside an export tree also <b>releases that binding's direction lock</b>
    /// (plan §6.5). The lock exists because one side holds work the other has not seen; once
    /// that work is thrown away there is nothing left to guard, and a lock left held would
    /// block the next export for a reason nobody could find from the UI. Only the bindings
    /// that owned a discarded file — a discarded README has nothing to do with the direction
    /// lock, and releasing every lock because one unrelated file was restored would be the
    /// more damaging kind of over-reach.
    /// </para>
    /// </remarks>
    private async Task DiscardRowsAsync(IReadOnlyList<ChangeRow> rows)
    {
        if (_git is null)
        {
            return;
        }

        var tracked = rows.Where(r => r.Kind != ChangeKind.Untracked).Select(r => r.Path).ToList();
        var untracked = rows.Where(r => r.Kind == ChangeKind.Untracked).Select(r => r.Path).ToList();

        if (tracked.Count == 0)
        {
            // Said in the status bar rather than done. The file is not in git at all, so
            // there is no version to go back to.
            var refused = untracked[0];
            _logger.Warn(LogCategory.Git,
                $"discard refused for the untracked file {refused}: git has no copy to restore");
            StatusSummary = $"{refused} is not tracked by git yet — there is nothing to restore. " +
                            "Delete it in Explorer if that is what you meant.";
            return;
        }

        var named = string.Join("\n", tracked);
        using var asking = AskingSomething();
        var confirmed = Prompts.ConfirmDestructive(new DestructivePrompt(
            tracked.Count == 1 ? "Discard changes to this file?"
                               : $"Discard changes to these {tracked.Count} files?",
            $"{named}\n\n" +
            "The changes are thrown away and cannot be recovered — they are not in a commit " +
            "or in the index.\n\n" +
            "Commit them on a scratch branch if you might want them back.",
            Confirm: "Discard"));

        if (!confirmed)
        {
            _logger.Info(LogCategory.Git, $"discard cancelled for {named.Replace('\n', ' ')}");
            return;
        }

        _logger.Click("Git.Discard");
        using var busy = Busy.Begin(WorkKind.GitWrite,
            tracked.Count == 1 ? $"Discarding {tracked[0]}…"
                               : $"Discarding {tracked.Count} files…");

        var result = await _git.DiscardAsync(tracked, busy.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            _logger.Warn(LogCategory.Git, $"discard failed: {result.FailureReason}");
            Notifications.Failure("Could not discard.", result.FailureReason ?? string.Empty);
            await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
            return;
        }

        Notifications.Success(
            (tracked.Count == 1 ? $"Discarded the changes to {tracked[0]}."
                                : $"Discarded the changes to {tracked.Count} files.")
            + (untracked.Count == 0 ? string.Empty
                : $" {untracked.Count} untracked file(s) were left — git has no copy to restore."));

        foreach (var bindingId in tracked
                     .Select(Sync.BindingOwning)
                     .OfType<string>()
                     .Distinct(StringComparer.Ordinal))
        {
            await Sync.ReleaseAfterDiscardAsync(bindingId, busy.Token)
                .ConfigureAwait(true);
        }

        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    /// <summary>
    /// True when the commit may proceed: nothing large and unprotected is staged, or the
    /// user saw the list and said so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fresh <c>git status</c> rather than the panel's lists, because the gate stages
    /// export trees between the button and here — the lists describe the panel as it was,
    /// and the commit records the index as it is. One extra process spawn on an operation
    /// that already runs several.
    /// </para>
    /// <para>
    /// The prompt is a question, not a refusal: LFS may be genuinely unavailable, the file
    /// may be a one-off, and a hard block would send somebody to the command line where no
    /// preflight exists at all. Sizes are shown per file, because "large" is an argument
    /// and a number is a fact.
    /// </para>
    /// </remarks>
    private async Task<bool> ConfirmLargeFilesAsync()
    {
        if (_git is null || RepositoryPath is not { } root)
        {
            return true;
        }

        var status = await _git.StatusAsync(CancellationToken.None).ConfigureAwait(true);

        var sized = new List<(string Path, long Bytes)>();
        foreach (var change in status.Changes.Where(c => c.IsStaged))
        {
            // A staged deletion has no file to weigh, and a file deleted between the
            // status and here weighs nothing either way.
            var info = new FileInfo(Path.Combine(
                root, change.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (info.Exists)
            {
                sized.Add((change.Path, info.Length));
            }
        }

        var attributesPath = Path.Combine(root, ".gitattributes");
        var offenders = LargeFilePreflight.Unprotected(
            sized,
            File.Exists(attributesPath) ? File.ReadAllText(attributesPath) : string.Empty);

        if (offenders.Count == 0)
        {
            return true;
        }

        var bySize = sized.ToDictionary(f => f.Path, f => f.Bytes, StringComparer.OrdinalIgnoreCase);
        var named = string.Join("\n", offenders.Select(p =>
            $"{p} ({bySize[p] / (1024.0 * 1024.0):0.#} MB)"));

        _logger.Warn(LogCategory.Git,
            $"large-file preflight: {offenders.Count} staged file(s) over " +
            $"{LargeFilePreflight.DefaultThreshold / (1024 * 1024)} MB with no LFS rule");

        using var asking = AskingSomething();
        return Prompts.ConfirmDestructive(new DestructivePrompt(
            "Commit a large file without LFS?",
            $"{named}\n\n" +
            "No LFS rule in .gitattributes matches " +
            (offenders.Count == 1 ? "this file" : "these files") + ". " +
            "Once committed it is in the repository's history for good — many hosts refuse " +
            "large pushes, and taking a file back out of history means rewriting it.\n\n" +
            "Adding an LFS rule for it first is usually the better answer.",
            Confirm: "Commit anyway"));
    }

    /// <summary>
    /// Stage the tree row's own files, from the context menu.
    /// </summary>
    /// <remarks>
    /// All of the row's files rather than only the changed ones: <c>git add</c> on an
    /// unchanged path is a no-op, and filtering here would be a second answer to a question
    /// git already answers. The commit gate still runs at commit time — staging early
    /// changes when the mirror is recorded, not whether the gate sees it.
    /// </remarks>
    /// <summary>
    /// Stage, unstage or discard exactly one row of a staging list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The clicked row, never the ticks.</b> This is the whole reason these exist rather
    /// than the menu reusing <c>StageCommand</c>: the buttons act on <c>Chosen</c> — the ticked
    /// rows, or all of them when nothing is ticked — and a right-click is a statement about the
    /// row under the cursor. Somebody who has ticked three files, then right-clicks a fourth to
    /// stage it, must not have their three swept in. The two interactions mean different things
    /// and they both have to keep meaning them.
    /// </para>
    /// <para>
    /// <b>Discard goes through <see cref="DiscardRowsAsync"/> unchanged</b>, so the one flow in
    /// this application that destroys work nothing else has a copy of still names every file it
    /// will touch and still asks first. The context menu is a new way to reach that flow, not a
    /// second implementation of it.
    /// </para>
    /// </remarks>
    public async Task StageRowAsync(ChangeRow row)
    {
        if (_git is null)
        {
            return;
        }

        _logger.Click("Row.Stage");
        using var busy = Busy.Begin(WorkKind.GitWrite, $"Staging {row.Primary}…");

        var result = await _git.StageAsync([row.Path], busy.Token).ConfigureAwait(true);

        if (!result.Success)
        {
            _logger.Warn(LogCategory.Git, $"stage failed for {row.Path}: {result.FailureReason}");
            Notifications.Failure("Could not stage.", result.FailureReason ?? string.Empty);
        }

        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    public async Task UnstageRowAsync(ChangeRow row)
    {
        if (_git is null)
        {
            return;
        }

        _logger.Click("Row.Unstage");
        using var busy = Busy.Begin(WorkKind.GitWrite, $"Unstaging {row.Primary}…");

        var result = await _git.UnstageAsync([row.Path], busy.Token).ConfigureAwait(true);

        if (!result.Success)
        {
            _logger.Warn(LogCategory.Git,
                         $"unstage failed for {row.Path}: {result.FailureReason}");
            Notifications.Failure("Could not unstage.", result.FailureReason ?? string.Empty);
        }

        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    public Task DiscardRowAsync(ChangeRow row) => DiscardRowsAsync([row]);

    /// <summary>Reveal one staging row's file in Explorer.</summary>
    public void RevealRow(ChangeRow row)
    {
        _logger.Click("Row.Reveal");
        RevealPath(row.Path);
    }

    /// <summary>Put one staging row's repo-relative path on the clipboard.</summary>
    public void CopyRowPath(ChangeRow row)
    {
        _logger.Click("Row.CopyPath");
        CopyPathToClipboard(row.Path);
    }

    public async Task StageNodeAsync(TreeRow row)
    {
        if (_git is null || row.Node.Files.Count == 0)
        {
            return;
        }

        _logger.Click("Tree.Stage");
        using var busy = Busy.Begin(WorkKind.GitWrite, $"Staging {row.Name}…");

        var result = await _git.StageAsync(row.Node.Files, busy.Token).ConfigureAwait(true);

        if (!result.Success)
        {
            _logger.Warn(LogCategory.Git, $"stage failed for {row.Name}: {result.FailureReason}");
            Notifications.Failure("Could not stage.", result.FailureReason ?? string.Empty);
        }

        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    /// <summary>
    /// Reveal the tree row's file in Explorer, from the context menu.
    /// </summary>
    /// <remarks>
    /// The same launch shape as <see cref="OpenLogsFolder"/>, for the same reasons: explorer
    /// by name so <c>/select,</c> works, the argument as one string because splitting it
    /// makes Explorer open Documents, and failure put in the log and the status bar rather
    /// than swallowed — a locked-down industrial machine really can refuse this.
    /// </remarks>
    public void RevealNode(TreeRow row)
    {
        _logger.Click("Tree.Reveal");
        RevealPath(row.Node.Files.FirstOrDefault() ?? row.Node.Path);
    }

    /// <summary>
    /// Reveal one repo-relative path in Explorer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted from <see cref="RevealNode"/> when the git panel's rows gained the same
    /// menu entry. <b>The whole of the reasoning came with it</b>, because none of it was
    /// about trees: explorer by name so <c>/select,</c> works, the argument as ONE string
    /// because splitting it makes Explorer open Documents instead, and failure logged and
    /// put in the status bar rather than swallowed — a locked-down industrial machine really
    /// can refuse this.
    /// </para>
    /// <para>
    /// The caller logs its own click, so the log still says which surface asked.
    /// </para>
    /// </remarks>
    public void RevealPath(string repoRelative)
    {
        if (RepositoryPath is not { } root)
        {
            return;
        }

        var relative = repoRelative.Replace('/', Path.DirectorySeparatorChar);
        var absolute = Path.Combine(root, relative);

        try
        {
            var argument = File.Exists(absolute)
                ? "/select," + absolute
                : Directory.Exists(absolute)
                    ? absolute

                    // A Deleted row's file is exactly the one that is not there; landing in
                    // its folder is the honest nearest place.
                    : Path.GetDirectoryName(absolute) ?? root;

            Process.Start(new ProcessStartInfo("explorer.exe", argument)
            {
                UseShellExecute = true,
            });

            _logger.Info(LogCategory.App, $"revealed {relative} in Explorer");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
            _logger.Error(LogCategory.App, $"could not reveal {absolute}", ex);
            StatusSummary = $"The file is at {absolute}";
        }
    }

    /// <summary>
    /// Put the tree row's repo-relative path on the clipboard, from the context menu.
    /// </summary>
    /// <remarks>
    /// The mirror path rather than an absolute one, because it is the path that means the
    /// same thing to every clone of the repository — a commit message, an issue, a chat
    /// message. Clipboard access can be refused when another process holds it open; that is
    /// worth a status-bar sentence, not a crash.
    /// </remarks>
    public void CopyNodePath(TreeRow row)
    {
        _logger.Click("Tree.CopyPath");
        CopyPathToClipboard(row.Node.Path);
    }

    /// <summary>
    /// Put one repo-relative path on the clipboard.
    /// </summary>
    /// <remarks>
    /// Extracted alongside <see cref="RevealPath"/>, with its reasoning intact: the
    /// repo-relative path rather than an absolute one, because that is the path that means
    /// the same thing to every clone — a commit message, an issue, a chat message. Clipboard
    /// access can be refused when another process holds it open, which is worth a status-bar
    /// sentence and not a crash, and the sentence still carries the path so the copy failing
    /// does not also lose the thing being copied.
    /// </remarks>
    public void CopyPathToClipboard(string repoRelative)
    {

        try
        {
            System.Windows.Clipboard.SetText(repoRelative);
            StatusSummary = $"Copied {repoRelative}";
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            _logger.Error(LogCategory.App, "could not reach the clipboard", ex);
            StatusSummary = $"Could not reach the clipboard — the path is {repoRelative}";
        }
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
        if (Prompts.PromptForBranchName() is not { } name)
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

        // No view attached answers null, the same as a user who backs out of the dialog, so
        // there is nothing to check for separately.
        if (await Prompts.PromptForClone(initialUrl).ConfigureAwait(true) is not { } cloned)
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

            // Kept whole rather than as the name alone, because the merge-request gate needs
            // the ahead count and the detached flag from the same reading. Two properties
            // derived separately from one status is how two of them come to disagree.
            _branch = status.Branch;

            // A merge request found for the branch we were on says nothing about the one we
            // are on now, and leaving it would send somebody to another branch's request. The
            // next probe answers for this branch.
            if (!string.Equals(previousBranch, BranchName, StringComparison.Ordinal))
            {
                _requestNumber = null;
            }
            Remote.NoteStatus(status.Branch);

            var changed = Reconcile(status.Changes);

            // Read whenever the tree has conflicts OR we thought it was mid-merge, so the
            // banner both appears and goes away. Skipped otherwise: it costs a rev-parse and
            // two file probes, and the overwhelmingly common answer is None.
            if (status.HasConflicts || IsMidMerge)
            {
                MergeState = await _git.MergeStateAsync(busy?.Token ?? CancellationToken.None)
                    .ConfigureAwait(true);
            }

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

        var local = await _git.BranchesAsync(CancellationToken.None).ConfigureAwait(true);
        var remote = await _git.RemoteBranchesAsync(CancellationToken.None).ConfigureAwait(true);

        // Both halves, merged by short name. refs/heads alone is local branches, so before this
        // a branch existing only on the remote was invisible to the application entirely.
        var branches = BranchInventory.Combine(local, remote);

        await ResolveDefaultBranchAsync(local).ConfigureAwait(true);

        WithoutSideEffects(() =>
        {
            // Already ordered by BranchInventory: current first, then local, then remote-only.
            // A busy remote carries hundreds nobody here has checked out and they must not bury
            // the three somebody works on.
            Branches.Clear();
            foreach (var branch in branches)
            {
                Branches.Add(new BranchEntry(branch));
            }

            SelectedBranch = Branches.FirstOrDefault(b => b.IsCurrent);
        });

        // Rides the branch cadence rather than the status refresh: a stash moves only on a
        // git write, and a `git stash list` per working-tree save would be a spawn to learn
        // nothing — the same argument as the branch list one paragraph up.
        await LoadStashesAsync().ConfigureAwait(true);
    }

    /// <summary>The stashes this repository holds. Empty is the ordinary state.</summary>
    /// <remarks>
    /// See <see cref="StashRow"/> for the design and its history — the section this fills
    /// exists only while there is something in it, which is the whole correction to the
    /// withdrawn six-permanent-controls version.
    /// </remarks>
    public System.Collections.ObjectModel.ObservableCollection<StashRow> Stashes { get; } = [];

    public bool HasStashes => Stashes.Count > 0;

    private async Task LoadStashesAsync()
    {
        if (_git is null)
        {
            return;
        }

        var entries = await _git.StashListAsync(CancellationToken.None).ConfigureAwait(true);

        Stashes.Clear();
        foreach (var entry in entries)
        {
            Stashes.Add(new StashRow(entry, ApplyStashAsync, DropStashAsync,
                                     () => Ready, ReportError));
        }

        OnPropertyChanged(nameof(HasStashes));
    }

    /// <summary>Apply one stash, keeping it.</summary>
    /// <remarks>
    /// Apply by SHA, which names the same stash however the list has shifted, and never
    /// <c>pop</c>: pop deletes on success and leaves a half-applied tree on conflict, so
    /// apply-then-drop is two decisions, each reversible until the second is taken.
    /// </remarks>
    private async Task ApplyStashAsync(StashEntry entry)
    {
        if (_git is null)
        {
            return;
        }

        _logger.Click("Git.StashApply");
        using var busy = Busy.Begin(WorkKind.GitWrite, "Applying the stash…");

        var result = await _git.StashApplyAsync(entry.CommitId, drop: false, busy.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            // Usually a conflict with the working tree, and git's own words say which file.
            Notifications.Failure("Could not apply the stash.",
                                  result.FailureReason ?? string.Empty);
        }
        else
        {
            Notifications.Success("Stash applied. It stays in the list until you drop it.");
        }

        await RunRefreshAsync(RefreshTrigger.Manual, null).ConfigureAwait(true);
    }

    /// <summary>Drop one stash, after a confirmation naming it.</summary>
    /// <remarks>
    /// The ref is re-resolved by SHA at the moment of the drop — <see cref="StashEntry.Ref"/>
    /// is positional and renumbers under deletions, so a ref held since the last refresh can
    /// name a different stash than the row the user pressed. The same rule the auto-stash's
    /// own drop follows, for the same measured reason.
    /// </remarks>
    private async Task DropStashAsync(StashEntry entry)
    {
        if (_git is null)
        {
            return;
        }

        using var asking = AskingSomething();
        var confirmed = Prompts.ConfirmDestructive(new DestructivePrompt(
            "Drop this stash?",
            $"{entry.Message}\n\n" +
            "The stashed changes are deleted. If they are applied to the working tree " +
            "already, nothing else is lost; if not, this is the only copy.",
            Confirm: "Drop"));

        if (!confirmed)
        {
            _logger.Info(LogCategory.Git, "stash drop cancelled");
            return;
        }

        _logger.Click("Git.StashDrop");
        using var busy = Busy.Begin(WorkKind.GitWrite, "Dropping the stash…");

        var fresh = await _git.StashListAsync(busy.Token).ConfigureAwait(true);
        var current = fresh.FirstOrDefault(s =>
            string.Equals(s.CommitId, entry.CommitId, StringComparison.OrdinalIgnoreCase));

        if (current is null)
        {
            // Dropped from a terminal since the list was drawn. The refresh below removes
            // the row; deleting whatever now sits at its old position would be the
            // positional-ref trap this method exists to avoid.
            Notifications.Warning("That stash is already gone.",
                                  "The list was refreshed.");
        }
        else
        {
            var result = await _git.StashDropAsync(current.Ref, busy.Token)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                Notifications.Failure("Could not drop the stash.",
                                      result.FailureReason ?? string.Empty);
            }
            else
            {
                Notifications.Success("Stash dropped.");
            }
        }

        await LoadStashesAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Work out which branch is the trunk, down the ladder in <see cref="Git.DefaultBranch"/>.
    /// </summary>
    /// <remarks>
    /// The configured value comes from <c>.versio/repo.json</c>, which the sync panel already
    /// loads for its bindings — so this reads what is in memory rather than the file, and one
    /// answer serves both.
    /// </remarks>
    private async Task ResolveDefaultBranchAsync(IReadOnlyList<BranchInfo> local)
    {
        if (_git is null)
        {
            DefaultBranch = DefaultBranch.None;
            return;
        }

        var remoteHead = Remote.HasRemote
            ? await _git.RemoteHeadAsync("origin", CancellationToken.None).ConfigureAwait(true)
            : string.Empty;

        var resolved = DefaultBranch.Resolve(
            Sync.ConfiguredDefaultBranch,
            remoteHead,
            [.. local.Select(b => b.Name)]);

        if (resolved.IsKnown && resolved.Name != _defaultBranch.Name)
        {
            _logger.Info(LogCategory.Git,
                $"default branch is {resolved.Name} (from {resolved.Source})");
        }

        DefaultBranch = resolved;
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

    private IReadOnlyList<DiffLayerChoice> _diffLayers = [];

    /// <summary>
    /// The layers of the object the pane is showing — <c>ST | native</c> — when it has more
    /// than one. Empty otherwise, and the strip hides itself.
    /// </summary>
    /// <remarks>
    /// Owned by the shown path, not by the tree selection: <see cref="ShowPathAsync"/>
    /// reconciles it on every diff it opens, keeping the strip when the new path is one of
    /// the current layers and clearing it for anything else. That one rule covers every
    /// route into the pane — a changed-files row, a history file, a refresh — without each
    /// of those callers having to remember the strip exists, which is exactly how the old
    /// header showed the wrong file's name.
    /// </remarks>
    public IReadOnlyList<DiffLayerChoice> DiffLayers
    {
        get => _diffLayers;
        private set
        {
            if (SetProperty(ref _diffLayers, value))
            {
                OnPropertyChanged(nameof(HasDiffLayers));
            }
        }
    }

    public bool HasDiffLayers => _diffLayers.Count > 1;

    /// <summary>Offer one chip per layer, with <paramref name="shown"/> marked active.</summary>
    private void OfferDiffLayers(IReadOnlyList<string> files, string shown) =>
        DiffLayers = files.Count > 1
            ? files
                .Select(f => new DiffLayerChoice(
                    f,
                    isActive: string.Equals(f, shown, StringComparison.OrdinalIgnoreCase),
                    show: () => _ = ShowPathAsync(f, staged: null)))
                .ToList()
            : [];

    // ------------------------------------------------------ the rendered pane (Rendered | Raw)

    private object? _renderedContent;
    private bool _showRendered;
    private string _paneNote = string.Empty;

    /// <summary>
    /// What the Rendered tab draws for the current selection, or null when there is only
    /// the raw view.
    /// </summary>
    /// <remarks>
    /// One object, dispatched by type through XAML data templates: a
    /// <see cref="Versio.Core.Projects.LibraryCatalog"/> renders as the libraries table, a
    /// <see cref="Versio.Core.Projects.TaskCatalog"/> as the task table,
    /// <see cref="TaskDetailRender"/> as one task's facts, <see cref="SourceRender"/> as the
    /// file's own highlighted content, and <see cref="ProjectSummaryRender"/> as the project
    /// card. The data is parsed on the click, from the same readers the tree already trusts
    /// — a second interpretation of a catalog would be a second answer to what it says.
    /// </remarks>
    public object? RenderedContent
    {
        get => _renderedContent;
        private set
        {
            if (SetProperty(ref _renderedContent, value))
            {
                OnPropertyChanged(nameof(HasRenderedView));
                OnPropertyChanged(nameof(ShowRenderedPane));
            }
        }
    }

    public bool HasRenderedView => _renderedContent is not null;

    /// <summary>Which tab is showing. Defaults per selection, not sticky.</summary>
    /// <remarks>
    /// A changed file opens on Raw, because the diff is what review is; anything clean opens
    /// on Rendered, because the diff of a clean file is nothing and the content is the
    /// answer. A sticky choice was considered and dropped: which view reads better is a fact
    /// about the file in front of you, not about the person.
    /// </remarks>
    public bool ShowRendered
    {
        get => _showRendered;
        set
        {
            if (SetProperty(ref _showRendered, value))
            {
                OnPropertyChanged(nameof(ShowRenderedPane));
                OnPropertyChanged(nameof(ShowRawTab));
            }
        }
    }

    /// <summary>The Raw chip's own state — WPF has no inverse TwoWay binding.</summary>
    public bool ShowRawTab
    {
        get => !_showRendered;
        set => ShowRendered = !value;
    }

    /// <summary>What the fill actually draws: the rendered host, or the diff under it.</summary>
    public bool ShowRenderedPane => HasRenderedView && ShowRendered;

    /// <summary>One sentence above the pane when it needs one — "no changes right now".</summary>
    public string PaneNote
    {
        get => _paneNote;
        private set => SetProperty(ref _paneNote, value);
    }

    private void ClearRendered()
    {
        RenderedContent = null;
        ShowRendered = false;
        PaneNote = string.Empty;
    }

    /// <summary>Build the rendered view for a tree row, or null when only raw makes sense.</summary>
    private object? BuildRenderedContent(TreeRow row)
    {
        if (RepositoryPath is not { } root)
        {
            return null;
        }

        var node = row.Node;

        string? CatalogFile(string suffix) => node.Files.FirstOrDefault(
            f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        string Absolute(string relative) =>
            Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        string? ReadOrNull(string relative)
        {
            try
            {
                return File.ReadAllText(Absolute(relative));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
        }

        switch (node.Kind)
        {
            case ProjectNodeKind.Project:
                return BuildProjectSummary(node);

            // A reference or task row renders its whole catalog: the row's own facts are in
            // the table, highlighted by being the thing the user just clicked at.
            case ProjectNodeKind.Library
                when CatalogFile(LibraryCatalog.FileSuffix) is { } yaml
                     && ReadOrNull(yaml) is { } text
                     && LibraryCatalog.Parse(text) is { IsValid: true } catalog:
                return catalog;

            case ProjectNodeKind.Task
                when CatalogFile(TaskCatalog.FileSuffix) is { } yaml
                     && ReadOrNull(yaml) is { } text
                     && TaskCatalog.Parse(text).Tasks.FirstOrDefault(
                            t => t.Name == node.Name) is { } task:
                return new TaskDetailRender { Task = task };

            case ProjectNodeKind.Object or ProjectNodeKind.File:
                if (CatalogFile(LibraryCatalog.FileSuffix) is { } libraries
                    && ReadOrNull(libraries) is { } libText
                    && LibraryCatalog.Parse(libText) is { IsValid: true } libCatalog)
                {
                    return libCatalog;
                }

                if (CatalogFile(TaskCatalog.FileSuffix) is { } tasks
                    && ReadOrNull(tasks) is { } taskText
                    && TaskCatalog.Parse(taskText) is { IsValid: true } taskCatalog)
                {
                    return taskCatalog;
                }

                if (row.PrimaryFile is { } primary
                    && SourceRender.Load(Absolute(primary), primary,
                           noChanges: node.Status == ChangeKind.Unmodified) is { } source

                    // A NUL says binary — an image or a compiled blob dropped into the
                    // repository — and a code renderer fed one draws a wall of boxes.
                    && !source.PseudoDiff.Contains('\0'))
                {
                    return source;
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>The project card: what a glance at the row should answer.</summary>
    /// <remarks>
    /// Everything here is already on this machine — the manifest, the catalogs, the
    /// coverage, the file itself. The one fact people will look for and not find is which
    /// CODESYS version last saved the project: the manifest does not record it, and saying
    /// nothing beats guessing from the installed toolchain, which is a different question.
    /// </remarks>
    private ProjectSummaryRender BuildProjectSummary(Versio.Core.Projects.ProjectTreeNode node)
    {
        var facts = new List<FactRow>();
        var manifest = Sync.ManifestFor(node.Path);

        if (RepositoryPath is { } root)
        {
            var file = new FileInfo(Path.Combine(
                root, node.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (file.Exists)
            {
                facts.Add(new FactRow("Project file",
                    $"{file.Length / (1024.0 * 1024.0):0.##} MB · saved {file.LastWriteTime:g}"));
            }
        }

        facts.Add(new FactRow("Vendor", (manifest?.Vendor ?? PlcVendor.Codesys).ToString()));
        facts.Add(new FactRow("Toolchain",
            Sync.ToolchainAvailable
                ? "installed — version under Settings → System information"
                : "not installed on this machine"));

        // WHICH TOOLCHAIN WROTE THIS MIRROR, which is a different question from whether one
        // is installed here — a colleague's export, a CI machine's, or an older CODESYS on
        // this one all read the same on the row above and differently on this one. That was
        // the gap: the card could say "Codesys" and never which CODESYS.
        //
        // Verbatim, because the manifest recorded it verbatim and shortening it here would be
        // this file inventing a parse of somebody else's version string. Absent on any mirror
        // exported before schema 10, and the row is simply not drawn — "not recorded" is what
        // an empty field means, and a row saying "unknown" would claim more than that.
        if (manifest?.ToolchainVersion is { Length: > 0 } producedBy)
        {
            facts.Add(new FactRow("Exported by", producedBy));
        }

        facts.Add(new FactRow("Sync state",
            node.Detail.Length > 0 ? node.Detail : "tracked"));

        if (manifest is null)
        {
            facts.Add(new FactRow("Mirror", "never exported"));
            return new ProjectSummaryRender { Name = node.Name, Facts = facts };
        }

        if (Sync.ManifestPathFor(node.Path) is { } manifestPath
            && File.Exists(manifestPath))
        {
            facts.Add(new FactRow("Last export",
                $"{File.GetLastWriteTime(manifestPath):g}"));
        }

        facts.Add(new FactRow("Mirror",
            $"{manifest.Objects.Count} exported file(s) · manifest schema {manifest.Schema}"));

        // One entry per OBJECT, not per file — a GVL is two files and one object, and a
        // summary that counted it twice would disagree with the panel beside it.
        var counts = manifest.Objects
            .Where(o => !string.IsNullOrEmpty(o.Path))
            .GroupBy(o => string.IsNullOrEmpty(o.Guid) ? o.Path : o.Guid, StringComparer.Ordinal)
            .GroupBy(g => g.First().Type, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => Versio.Core.Projects.ObjectTypeNames.Counted(
                manifest.Vendor, g.Key, g.Count()));
        facts.Add(new FactRow("Objects", string.Join(", ", counts)));

        var references = manifest.Objects
            .Where(o => o.Form == Versio.Core.Projects.ExportCoverage.LibrariesForm)
            .Select(o => Sync.ExportPathFor(node.Path) is { } export
                ? LibraryCatalog.Parse(
                      ReadRepoFileOrNull($"{export.TrimEnd('/')}/{o.Path}") ?? string.Empty)
                : LibraryCatalog.Empty)
            .Where(c => c.IsValid)
            .ToList();
        if (references.Count > 0)
        {
            facts.Add(new FactRow("Libraries",
                $"{references.Sum(c => c.References.Count)} reference(s) in " +
                $"{references.Count} manager(s)"));
        }

        if (manifest.Objects.FirstOrDefault(
                o => o.Form == Versio.Core.Projects.ExportCoverage.TaskBothForm) is { } taskRecord
            && Sync.ExportPathFor(node.Path) is { } exportRoot
            && ReadRepoFileOrNull($"{exportRoot.TrimEnd('/')}/{taskRecord.Path}") is { } taskYaml
            && TaskCatalog.Parse(taskYaml) is { IsValid: true } taskCatalog)
        {
            facts.Add(new FactRow("Tasks", string.Join(", ",
                taskCatalog.Tasks.Select(t => $"{t.Name} ({t.DisplaySchedule})"))));
        }

        facts.Add(new FactRow("Coverage",
            Sync.CoverageFor(node.Path) is { HasGaps: true } report
                ? report.Describe()
                : "complete — every object the exporter understands is in the mirror"));

        if (manifest.Warnings.Count > 0)
        {
            facts.Add(new FactRow("Warnings", $"{manifest.Warnings.Count} — see the log"));
        }

        return new ProjectSummaryRender { Name = node.Name, Facts = facts };
    }

    private string? ReadRepoFileOrNull(string repoRelative)
    {
        if (RepositoryPath is not { } root)
        {
            return null;
        }

        try
        {
            var absolute = Path.Combine(
                root, repoRelative.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(absolute) ? File.ReadAllText(absolute) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keep the strip when the new path is one of its layers; clear it otherwise.</summary>
    private void ReconcileDiffLayers(string path)
    {
        if (_diffLayers.Count == 0)
        {
            return;
        }

        if (!_diffLayers.Any(l => string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            DiffLayers = [];
            return;
        }

        OfferDiffLayers(_diffLayers.Select(l => l.Path).ToList(), path);
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
            DiffLayers = [];

            // The card is the project's rendered view, and it is the DEFAULT: the raw half
            // of a binary compound document is the explanation below, which is worth
            // reading once, not on every click.
            RenderedContent = BuildProjectSummary(row.Node);
            ShowRendered = true;
            PaneNote = string.Empty;

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

        // The rendered half, beside whatever the raw half loads below. Defaulted per
        // selection: a changed file opens on its diff, a clean one on its content —
        // see ShowRendered's remark.
        RenderedContent = BuildRenderedContent(row);
        ShowRendered = HasRenderedView && row.Node.Status == ChangeKind.Unmodified;

        if (row.PrimaryFile is { } path)
        {
            // Offered here because only the tree knows an object's sibling layers;
            // ShowPathAsync then keeps or clears the strip on every later navigation.
            OfferDiffLayers(row.Node.Files, path);
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

        ReconcileDiffLayers(path);
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
            // left with nothing to show are genuinely binary or genuinely unchanged. An
            // unchanged file now shows ITSELF — a clean object used to render as a shrug,
            // and "here is the file, nothing has changed in it" answers the question the
            // click asked. The note rides in the pane header, not the text, because the
            // header is where a fact about the whole view belongs. Binary stays a sentence:
            // a code renderer fed an image draws a wall of boxes.
            if (string.IsNullOrWhiteSpace(diff)
                && RepositoryPath is { } root
                && SourceRender.Load(
                       Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)),
                       path, noChanges: true) is { } content
                && !content.PseudoDiff.Contains('\0'))
            {
                SelectedDiff = content.PseudoDiff;
                PaneNote = "No changes right now — showing the whole file.";
                CanStageHunks = false;
                return;
            }

            PaneNote = string.Empty;
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
        Remote.Dispose();

        // The sync panel holds its own watcher over every tracked project and its export
        // tree. Releasing only the shell's would leave those handles open and the repository
        // folder locked against rename or delete after the window closes.
        Sync.Detach();
        History.Detach();

        // Let go of the repository, so the next Versio - or the next launch of this one - does
        // not have to work out that this process is gone before it can open it.
        ReleaseRepository();

        // LAST, because everything above it logs on its way out and those lines should still
        // reach the panel.
        //
        // The log is handed in rather than built here, so disposing it is this class taking
        // ownership of an object it did not create. That is deliberate. Nothing else keeps a
        // reference: App hands one over and forgets it, and TestShell builds one and returns
        // only the shell, so there is no second owner to disagree with. Leaving it out meant a
        // test that obeyed `using var shell = ...` to the letter still leaked a running
        // DispatcherTimer and a live subscription to the sink - the one leak that rule could
        // not prevent, and the named remaining suspect for the suite's residual flake.
        Log.Dispose();
    }
}
