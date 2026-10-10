using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.IO;
using Versio.App.Mvvm;
using Versio.Core.Abstractions;
using Versio.Core.Bindings;
using Versio.Core.Git;
using Versio.Core.Guarding;
using Versio.Core.Locking;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Versio.Core.Sync;
using Versio.Infrastructure.Bindings;
using Versio.Infrastructure.FileSystem;
using Versio.Infrastructure.Git;
using Versio.Infrastructure.Platform;
using Versio.Infrastructure.Plc;
using Versio.Infrastructure.Watching;

namespace Versio.App.ViewModels;

/// <summary>A PLC project Versio has found in the repository.</summary>
/// <remarks>
/// Observable rather than immutable because a row changes under the user during a run: it
/// says "exporting…" for 17–31 seconds and then reports what happened. Rebuilding the whole
/// list to move one row through that would lose the selection mid-export.
/// </remarks>
public sealed class ProjectRow(string path, string? bindingId, bool readFromFiles = false) : ObservableObject
{
    private string _state = "not tracked";
    private bool _busy;

    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileNameWithoutExtension(path);

    public string Folder { get; } = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";

    /// <summary>The binding that tracks this project, or null when it is untracked. The
    /// binding id is the identity: the path is an attribute of it, never the other way
    /// round.</summary>
    public string? BindingId { get; } = bindingId;

    public bool Tracked => BindingId is not null;

    /// <summary>
    /// True for a source project: a folder its vendor saved as readable files, shown and drawn
    /// and never synced. It has no binding and never will, so it is not "untracked" either.
    /// </summary>
    public bool ReadFromFiles { get; } = readFromFiles;

    /// <summary>One short phrase for the row: "not tracked", "in sync", "exporting…".</summary>
    public string State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(BrushKey));
            }
        }
    }

    /// <summary>True while a toolchain run is in flight for this row.</summary>
    public bool Busy
    {
        get => _busy;
        set => SetProperty(ref _busy, value);
    }

    public string BrushKey => (Tracked, _state) switch
    {
        // Not the untracked amber: nothing about a source project is waiting on anybody.
        (false, _) when ReadFromFiles => "Brush.Status.Renamed",
        (false, _) => "Brush.Status.Modified",
        (_, "failed") => "Brush.Status.Deleted",
        (_, "conflict") => "Brush.Status.Conflict",
        (_, "in sync") => "Brush.Status.Added",
        _ => "Brush.Status.Renamed",
    };
}

/// <summary>
/// What the last tree build read for one source project: the format that claimed the folder,
/// the objects it described, and how many files were there.
/// </summary>
public sealed record SourceProjectView(IPlcProjectFormat Format, ProjectManifest? Manifest, int FileCount);

/// <summary>
/// The PLC sync panel.
/// </summary>
/// <remarks>
/// <para>
/// This is the panel Versio exists for. A PLC project file is binary, so git can store it
/// but not diff it; the sync engine exports it to reviewable text beside the project and
/// imports edited text back. Everything shown here comes from the real scanner, the real
/// toolchain and the real orchestrator — there is no sample data.
/// </para>
/// <para>
/// The toolchain is probed once and its absence is <b>reported, not thrown</b>. A repository
/// full of CODESYS projects is perfectly usable as a git repository on a machine with no
/// CODESYS installed; the panel just has to say why it cannot export.
/// </para>
/// <para>
/// <b>This stopped being a panel and became state.</b> It used to carry a button for every
/// step — track, export, import, propose, confirm, rescan — from when each was something a
/// person decided. All of them now happen on their own: every project found is tracked, a
/// settled change exports, a settled mirror edit imports, and the commit gate catches
/// whatever the watcher missed. A control surface for something that no longer needs
/// controlling is worse than no surface at all, because every button on it implies the
/// automatic thing might not have happened.
/// </para>
/// <para>
/// <b>No buttons survive on the panel itself.</b> The last two were "Export now" and
/// "Acknowledge failure", and both were disabled almost always — which teaches people to stop
/// looking at that corner of the screen. Exporting what changed while Versio was closed is now
/// automatic (<see cref="CatchUpAsync"/>), because nothing about a stale mirror needs deciding.
/// The failure gate is unchanged and still deliberate, but it belongs to <i>one project</i>:
/// it is reported on that project's row, with the reason and <see cref="RetryCommand"/> in the
/// pane beside it.
/// </para>
/// </remarks>
public sealed class SyncViewModel : ObservableObject
{
    private readonly VersioLogger _logger;
    private readonly IClock _clock;
    private readonly HeartbeatService _heartbeat;

    /// <summary>Shared with the shell, so one progress bar covers every kind of work.</summary>
    private readonly BusyTracker _busy;
    private readonly OrphanScanner _orphanScanner = new();
    private readonly MachineIdentity _machine = new();

    /// <summary>
    /// What the last probe found, keyed by vendor.
    /// </summary>
    /// <remarks>
    /// A repository can hold more than one brand. One toolchain and one profile for the
    /// whole session was how a TIA project got driven by whoever registered first.
    /// </remarks>
    private readonly Dictionary<PlcVendor, ProbedToolchain> _probed = [];

    private string? _repositoryRoot;
    private IGitClient? _git;
    private SyncOrchestrator? _orchestrator;
    private RepoConfig _config = new();

    private string _toolchainState = "Not checked";
    private bool _toolchainAvailable;
    private string _summary = "No repository open";
    private string _lockState = string.Empty;
    private string _activity = string.Empty;
    private ProjectRow? _selectedProject;
    private FileChangeWatcher? _watcher;
    private DispatcherTimer? _deferredTick;

    private string _stalenessSummary = string.Empty;
    private int _unexportedCount;
    private string _treeSearch = string.Empty;
    private string _treeSummary = string.Empty;
    private string _coverageSummary = string.Empty;

    /// <summary>
    /// How a vendor's toolchain is obtained. The registry, unless a caller says otherwise.
    /// </summary>
    /// <remarks>
    /// <b>A seam, and it had to exist.</b> Adopting a project now exports it straight away —
    /// which is right for a user and wrong for a test suite, because a test that drops a
    /// <c>.project</c> into a temp repository was launching the real CODESYS and waiting for it
    /// to fail. One test went from one second to eight, and the suite from four to fourteen.
    /// A view model that can only ever reach the machine's actual toolchain is a view model
    /// whose behaviour cannot be exercised without it.
    /// </remarks>
    private readonly Func<PlcVendor, IVersioLogger, IPlcToolchain?> _toolchains;

    public SyncViewModel(
        VersioLogger logger,
        IClock clock,
        BusyTracker? busy = null,
        Func<PlcVendor, IVersioLogger, IPlcToolchain?>? toolchains = null)
    {
        _toolchains = toolchains ?? PlcToolchains.For;
        _logger = logger;
        _clock = clock;
        _busy = busy ?? new BusyTracker(clock, logger);
        _heartbeat = new HeartbeatService(clock, logger);

        // The beat fires on a timer thread. Assigning a string property from there is safe —
        // WPF marshals property notifications — but the collections are not, which is why
        // nothing else is touched here.
        _heartbeat.Beat += op => Activity = op.Describe(clock);

        RetryCommand = new AsyncCommand(RetryAsync, CanRetry, onError: Report);
        ConfirmImportCommand = new AsyncCommand(
            ConfirmImportAsync, CanConfirmImport, onError: Report);
        RestoreBackupCommand = new AsyncCommand(
            RestoreBackupAsync, CanRestoreBackup, onError: Report);
    }

    /// <summary>
    /// Every project found, with the state its row reports.
    /// </summary>
    /// <remarks>
    /// No longer bound to a list of its own — the projects are rows in the one tree now. It
    /// survives as the place a project's state is worked out and held between refreshes, so
    /// the tree builder can be handed a sentence rather than an orchestrator.
    /// </remarks>
    public ObservableCollection<ProjectRow> Projects { get; } = [];

    /// <summary>
    /// The tracker this panel opens its progress scopes on.
    /// </summary>
    /// <remarks>
    /// Exposed so a test can assert it is the <i>same instance</i> the shell binds its bar
    /// to. It was not: this class accepted one and defaulted to building its own, and
    /// nothing anywhere could tell the difference except a user watching for a bar that
    /// never came.
    /// </remarks>
    public BusyTracker Busy => _busy;

    /// <summary>
    /// What the scan found that needs a person: an export tree whose project is gone, a
    /// binding pointing at nothing, a manifest claiming a different binding.
    /// </summary>
    /// <remarks>
    /// Populated on every scan, and until now bound by nothing at all - the count reached the
    /// log and the sentences reached nobody. That is the shape of bug this repository keeps
    /// finding, so it is either shown or deleted, and these are worth showing: every one of
    /// them is a repository in a state Versio cannot resolve on its own.
    /// </remarks>
    /// <summary>
    /// The trunk this repository records, or empty when it records none.
    /// </summary>
    /// <remarks>
    /// From <c>.versio/repo.json</c>, which this panel already loads for its bindings — so the
    /// shell reads what is in memory rather than the file, and there is one answer rather than
    /// two that can disagree.
    /// </remarks>
    public string ConfiguredDefaultBranch => _config.DefaultBranch;

    /// <summary>
    /// The forge this repository records, or Unknown when it records none.
    /// </summary>
    /// <remarks>
    /// From the same in-memory <c>repo.json</c> as <see cref="ConfiguredDefaultBranch"/>, for
    /// the same reason: one answer rather than two that can disagree.
    /// </remarks>
    public ForgeKind ConfiguredForge => _config.Forge;

    public ObservableCollection<string> Findings { get; } = [];

    /// <summary>The findings as one sentence, or empty when the scan found nothing wrong.</summary>
    /// <remarks>
    /// A summary rather than a list in the panel, for the same reason the coverage warning is
    /// one line: these are rare, they are not the panel's subject, and the log carries the
    /// detail. Two is the most that has ever been seen at once; beyond that it counts rather
    /// than scrolls.
    /// </remarks>
    public string FindingsSummary => Findings.Count switch
    {
        0 => string.Empty,
        1 => Findings[0],
        _ => $"{Findings.Count} things need attention — {Findings[0]} (see the log for the rest)",
    };

    /// <summary>
    /// Clear the selected project's failure and sync it again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaces two buttons that stood permanently on the panel and were disabled almost
    /// always. "Export now" is gone because nothing about a stale mirror needs deciding — it
    /// is exported on open now, by <see cref="CatchUpAsync"/>. "Acknowledge failure" is gone
    /// as a panel-wide control, because a failure belongs to <i>one project</i> and the panel
    /// is the wrong place to say so.
    /// </para>
    /// <para>
    /// The gate itself is unchanged and deliberate: a failed binding syncs no further until a
    /// person has seen the failure. What moved is where they see it — on the project's own
    /// row, with the reason and this button in the pane beside it, instead of a greyed-out
    /// control at the top of an otherwise unrelated panel.
    /// </para>
    /// </remarks>
    public AsyncCommand RetryCommand { get; }


    /// <summary>Whether the toolchain is installed, and which profile will be used.</summary>
    public string ToolchainState
    {
        get => _toolchainState;
        private set => SetProperty(ref _toolchainState, value);
    }

    public bool ToolchainAvailable
    {
        get => _toolchainAvailable;
        private set => SetProperty(ref _toolchainAvailable, value);
    }

    /// <summary>One sentence describing what was found.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>
    /// The direction lock for the selected project, in plain English.
    /// </summary>
    /// <remarks>
    /// Without this the lock is invisible magic that blocks the user with no explanation —
    /// which is the single most likely way this tool gets abandoned (plan §9.5).
    /// </remarks>
    public string LockState
    {
        get => _lockState;
        private set => SetProperty(ref _lockState, value);
    }

    /// <summary>
    /// How many tracked projects have changes that were never exported.
    /// </summary>
    /// <remarks>
    /// Normally zero, because an export follows a detected change. It goes above zero while
    /// one is queued behind the minimum interval, when no toolchain is available, and when a
    /// gate refused — a merge in progress, a project open in the IDE. Counting it is what
    /// stops the panel from implying everything is in sync when it is not.
    /// </remarks>
    public int UnexportedCount
    {
        get => _unexportedCount;
        private set
        {
            if (SetProperty(ref _unexportedCount, value))
            {
                // "Export now" is enabled by this number and nothing else, so the button
                // would stay dead after the count first rose without the nudge.
                RaiseCommandStates();
            }
        }
    }

    /// <summary>
    /// Repo-relative export directory of every enabled binding.
    /// </summary>
    /// <remarks>
    /// Handed to the commit path so it can stage what an export just wrote. Directories, not
    /// the file list the export reported: an export can delete an object as well as write
    /// one, and staging only the writes would leave the deletion out of the commit.
    /// </remarks>
    /// <summary>Bindings whose mirror is behind their project, from the last gate evaluation.</summary>
    private readonly HashSet<string> _staleBindings = new(StringComparer.Ordinal);

    /// <summary>
    /// The folders the last scan found as source projects, repo-relative. Shown and drawn,
    /// never bound: see <see cref="IPlcProjectFormat.SourceProjectExtensions"/>.
    /// </summary>
    private IReadOnlyList<string> _sourceProjects = [];

    /// <summary>What the last applied tree build read for each source project, by folder.</summary>
    private readonly Dictionary<string, SourceProjectView> _sourceViews = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The state a source project's row and card show. There is no lock behind it to report,
    /// so it says what the project is rather than how it is doing.
    /// </summary>
    public const string SourceProjectState = "read from its files";

    /// <summary>Whether the once-per-repository catch-up export has already run.</summary>
    private bool _caughtUp;

    /// <summary>
    /// Every binding whose direction lock is currently held, for the guard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked of the orchestrator rather than of the rows, so the guard and the sync engine can
    /// never disagree about whether work is outstanding. A row's <c>State</c> is a sentence
    /// produced for a human at the time of the last scan; this is the lock itself, now.
    /// </para>
    /// <para>
    /// <see cref="SyncStateMachine.HoldsLock"/> decides what counts as held — the same
    /// predicate lock recovery uses, so "a lock worth refusing a branch switch over" and "a
    /// lock worth re-adopting after a reboot" cannot drift apart.
    /// </para>
    /// </remarks>
    /// <summary>The direction locks as an auto-stash needs to record them, and let them go.</summary>
    /// <remarks>
    /// Forwarded rather than exposing the orchestrator, so the shell cannot reach past this
    /// view model into the sync engine for anything else. Empty when no repository is open,
    /// which is the same answer as "nothing was held".
    /// </remarks>
    public IReadOnlyList<StashedLock> CaptureLocksForStash() =>
        _orchestrator?.CaptureLocks() ?? [];

    /// <summary>Let the captured locks go, now that the stash holding their work exists.</summary>
    public Task ReleaseStashedLocksAsync(
        IReadOnlyList<StashedLock> captured, string stashCommitId) =>
        _orchestrator?.ReleaseCapturedLocksAsync(captured, stashCommitId, CancellationToken.None)
        ?? Task.CompletedTask;

    /// <summary>Put them back, after the stash has been restored. Returns what it refused.</summary>
    public Task<IReadOnlyList<string>> RestoreStashedLocksAsync(
        IReadOnlyList<StashedLock> captured) =>
        _orchestrator?.RestoreCapturedLocksAsync(captured, CancellationToken.None)
        ?? Task.FromResult<IReadOnlyList<string>>([]);

    public IReadOnlyList<HeldLock> HeldLocks =>
        _orchestrator is null
            ? []
            : [.. _config.Bindings
                .Where(b => b.Enabled)
                .Select(b => (Binding: b, Direction: _orchestrator.StateOf(b.Id)))
                .Where(x => SyncStateMachine.HoldsLock(x.Direction))
                .Select(x => new HeldLock(
                    x.Binding.Id,
                    System.IO.Path.GetFileNameWithoutExtension(x.Binding.ProjectPath),
                    x.Direction))];

    public IReadOnlyList<string> ExportPaths =>
        [.. _config.Bindings.Where(b => b.Enabled).Select(b => b.ExportPath)];

    /// <summary>Export directories for a named set of bindings, in the order given.</summary>
    public IReadOnlyList<string> ExportPathsFor(IReadOnlyList<string> bindingIds)
    {
        var wanted = bindingIds.ToHashSet(StringComparer.Ordinal);
        return [.. _config.Bindings
            .Where(b => b.Enabled && wanted.Contains(b.Id))
            .Select(b => b.ExportPath)];
    }

    /// <summary>That count as a sentence, empty when nothing is outstanding.</summary>
    public string StalenessSummary
    {
        get => _stalenessSummary;
        private set => SetProperty(ref _stalenessSummary, value);
    }

    /// <summary>Live heartbeat text while CODESYS is running, empty otherwise.</summary>
    /// <remarks>
    /// A CODESYS launch costs 17–31 s with nothing on screen to show for it, and silence is
    /// indistinguishable from a hang.
    /// </remarks>
    public string Activity
    {
        get => _activity;
        private set => SetProperty(ref _activity, value);
    }

    /// <summary>
    /// The project the panel is currently talking about.
    /// </summary>
    /// <remarks>
    /// Driven by the tree rather than by a list of its own: whichever object is selected, the
    /// lock sentence beside it should be the lock on <i>that</i> object's project. With one
    /// project in the repository this never moves; with three it is the difference between a
    /// sentence and a guess.
    /// </remarks>
    public ProjectRow? SelectedProject
    {
        get => _selectedProject;
        private set
        {
            if (!SetProperty(ref _selectedProject, value))
            {
                return;
            }

            LockState = value is { Tracked: true } && _orchestrator is not null
                ? SyncNarrative.Describe(_orchestrator.StateOf(value.BindingId!))
                : string.Empty;

            // Read here rather than in CanExecute: the answer needs the disk, and CanExecute
            // is called on every command-state refresh in the panel. The selection moving is
            // the only thing that changes it.
            RefreshBackupAvailability();

            RaiseCommandStates();
        }
    }

    /// <summary>
    /// Point the panel at whichever project owns a selected path.
    /// </summary>
    /// <remarks>
    /// <see cref="Locate"/> is reused rather than matching the path here, so the panel and the
    /// sync engine can never disagree about which binding a file belongs to — including the
    /// awkward case it exists for, a project sitting beside its own export directory.
    /// </remarks>
    public void NoteSelection(string? repoRelativePath)
    {
        if (repoRelativePath is null)
        {
            return;
        }

        var (binding, _) = Locate(Normalize(repoRelativePath));
        if (binding is null)
        {
            // A file outside every export tree — a document, a script. It belongs to no
            // project, so the previous project's lock sentence would be about something else.
            SelectedProject = null;
            return;
        }

        SelectedProject = Projects.FirstOrDefault(p => p.BindingId == binding.Id);
    }

    /// <summary>Called when the shell opens a repository.</summary>
    public async Task AttachAsync(string repositoryRoot, IGitClient git, CancellationToken ct)
    {
        _repositoryRoot = repositoryRoot;
        _git = git;
        OnPropertyChanged(nameof(HasRepository));

        // Before the first tree build, because rows read their expansion at construction:
        // a state restored after the build would apply to nothing until the next refresh.
        LoadTreeExpansion();

        // Armed for this repository. Set before any of the loading below, so a second
        // AttachAsync - reopening the same repository, say - gets a fresh sweep.
        _caughtUp = false;

        await ProbeToolchainAsync(ct).ConfigureAwait(true);
        await BuildOrchestratorAsync(ct).ConfigureAwait(true);

        // Before the scan, and before anything is hashed or exported: if a previous run was
        // killed midway through swapping a mirror into place, the mirror is currently missing
        // and its last complete copy is sitting in a trash directory beside it. Everything
        // below would otherwise read that as "never exported" and cheerfully export over it.
        await SweepKilledSwapsAsync().ConfigureAwait(true);

        await RescanAsync().ConfigureAwait(true);

        // Again, because the scan is where a project that is not in repo.json yet becomes a
        // binding - and a repository being adopted for the first time is exactly where a
        // mirror in a trash directory would otherwise go unnoticed. The first pass is still
        // worth having: it runs before adoption can start an export over a mirror that is
        // sitting in the trash.
        await SweepKilledSwapsAsync().ConfigureAwait(true);

        StartWatching();
        await RefreshStalenessAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Export whatever changed while Versio was not watching.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The watcher only sees changes made while the app is running. A project edited with
    /// Versio closed — the normal case, since CODESYS is the tool people actually work in —
    /// is therefore behind the moment the repository opens, and used to sit there until
    /// somebody pressed a button or started a commit. Nothing about that state needs a human
    /// decision: the mirror is simply out of date, and bringing it up to date is what this
    /// application is for.
    /// </para>
    /// <para>
    /// <b>Ordering is the guard.</b> This runs only after <see cref="AttachAsync"/> has
    /// finished, which means the toolchain has been probed, the orchestrator has been rebuilt
    /// and its locks restored from the previous session, every project has been scanned, and
    /// staleness has been evaluated against all of it. Exporting earlier would run against a
    /// direction lock that had not been read yet.
    /// </para>
    /// <para>
    /// <b>Once per repository.</b> A refresh, a git operation or a second call must not start
    /// another sweep — the flag is set before the work, not after it, so a failure does not
    /// leave it armed to retry on every subsequent refresh.
    /// </para>
    /// <para>
    /// It goes through <see cref="SatisfyGateAsync"/>, the same path the commit gate uses, so
    /// the direction lock, hash gate, circuit breaker and open-project check all apply exactly
    /// as they do everywhere else. A project someone has open in the IDE is a wait, not a
    /// failure, and this leaves it alone.
    /// </para>
    /// </remarks>
    public async Task CatchUpAsync(CancellationToken ct)
    {
        if (_orchestrator is null || _caughtUp)
        {
            return;
        }

        _caughtUp = true;

        if (!ToolchainAvailable)
        {
            // Nothing to say to the user: a repository of PLC projects on a machine with no
            // toolchain is still a perfectly good repository, and the panel already reports
            // why it cannot export.
            _logger.Info(LogCategory.Sync,
                "catch-up skipped: no toolchain available to export with");
            return;
        }

        if (UnexportedCount == 0)
        {
            _logger.Info(LogCategory.Sync, "catch-up: every mirror is up to date");
            return;
        }

        var behind = UnexportedCount;
        _logger.Info(LogCategory.Sync,
            $"catch-up: {behind} project(s) changed while Versio was not running; exporting");

        using var busy = _busy.Begin(WorkKind.Export, behind == 1
            ? "Exporting 1 project that changed while Versio was closed…"
            : $"Exporting {behind} projects that changed while Versio was closed…");

        // Both tokens: the caller's, and the one belonging to the bar the user is looking at.
        // The catch-up runs unattended at open and is the longest thing Versio ever does
        // without being asked, so it has to answer to whoever wants to stop it.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, busy.Token);

        var outcomes = await SatisfyGateAsync(linked.Token).ConfigureAwait(true);

        var failure = outcomes.FirstOrDefault(o => o.IsFailure);
        Summary = failure is null
            ? outcomes.Count == 0 ? Summary : SyncNarrative.Describe(outcomes[^1])
            : $"Export failed: {failure.Explanation}";
    }

    /// <summary>
    /// Every registered vendor's toolchain, as it is on this machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks the <b>registry</b>, not CODESYS: a machine with TwinCAT and no CODESYS should
    /// say so, and the day a second vendor is implemented this needs no edit. Everything shown
    /// comes from <see cref="PlcToolchainStatus"/>, which is above the seam, so no brand name
    /// appears in this method.
    /// </para>
    /// <para>
    /// Each toolchain is probed fresh rather than reported from the last probe. This is opened
    /// precisely when somebody suspects the environment has changed — they have just installed
    /// something, or a profile has gone missing — and answering from a cached probe taken when
    /// the repository opened would be answering the wrong question.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Views.SystemInfoRow>> DescribeToolchainsAsync(
        CancellationToken ct)
    {
        var rows = new List<Views.SystemInfoRow>();

        foreach (var format in PlcFormats.All)
        {
            rows.Add(Views.SystemInfoRow.Section(format.DisplayName.ToUpperInvariant()));

            var toolchain = PlcToolchains.For(format.Vendor, _logger);
            if (toolchain is null)
            {
                // Recognised but not drivable: projects of this vendor can be tracked and
                // committed, and not exported. A real state, and one worth naming.
                rows.Add(new Views.SystemInfoRow("Status", "no runner implemented yet"));
                rows.Add(new Views.SystemInfoRow("Projects", string.Join(", ", format.ProjectExtensions)));
                continue;
            }

            PlcToolchainStatus status;
            try
            {
                status = await toolchain.ProbeAsync(ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A probe reads the filesystem, and a locked-down machine can refuse. Report
                // it here rather than letting it close the window somebody opened to find out
                // what was wrong.
                rows.Add(new Views.SystemInfoRow("Status", $"could not be probed: {ex.Message}"));
                continue;
            }

            rows.Add(new Views.SystemInfoRow("Status",
                status.Available ? "available" : status.Explanation ?? "unavailable"));

            if (status.Version is { Length: > 0 } version)
            {
                rows.Add(new Views.SystemInfoRow("Version", version));
            }

            if (status.Profile is { Length: > 0 } profile)
            {
                rows.Add(new Views.SystemInfoRow("Profile", profile));
            }

            if (status.ExecutablePath is { Length: > 0 } exe)
            {
                rows.Add(new Views.SystemInfoRow("Executable", exe));
            }

            // Several usable installations and no safe way to choose. Shown in full, because
            // the fix is for a person to pick one and the list is what they pick from.
            foreach (var candidate in status.Candidates)
            {
                rows.Add(new Views.SystemInfoRow("Also found", candidate));
            }

            rows.Add(new Views.SystemInfoRow("Projects", string.Join(", ", format.ProjectExtensions)));
        }

        return rows;
    }

    /// <summary>
    /// Stop noticing changes, without forgetting anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the moment a close has been agreed to but the work in flight has not finished yet.
    /// Releasing the watchers stops the five-second deferred tick and any settling save from
    /// starting a <i>new</i> export while we are waiting for the current one — without which
    /// "wait for it to finish" could wait for a queue it keeps refilling, and an app the user
    /// asked to close would sit there exporting.
    /// </para>
    /// <para>
    /// Deliberately not <see cref="Detach"/>. That nulls the orchestrator and clears the panel,
    /// which is right when a repository is being swapped and wrong here: a run is still going,
    /// its completion still has to be reported, and the user is still looking at the window.
    /// </para>
    /// </remarks>
    public void Quiesce()
    {
        StopWatching();
        _logger.Info(LogCategory.Watch, "stopped watching; no new sync will be started");
    }

    /// <summary>Clear state when the repository closes, so a stale scan is never shown.</summary>
    public void Detach()
    {
        // First, because it needs _repositoryRoot and this method's whole job is to null
        // that out. It sat lower once — beside the Clear() it protects against, which reads
        // better — and wrote nothing, because the root was already gone; the persistence
        // test caught the file not existing. Detach is the one funnel every way out of a
        // repository goes through — switching, closing, disposing — so saving here covers
        // all of them without any caller knowing the file exists.
        SaveTreeExpansion();

        // A FileSystemWatcher holds a handle on the directory, and leaking one keeps the
        // folder locked against rename or delete for the life of the process - on a
        // repository somebody is trying to move, a visible bug.
        StopWatching();

        // The orchestrator's events, before the reference to it is dropped. Without this the
        // old orchestrator keeps handlers pointing into this view model, and this view model
        // keeps `_attachedTo` pointing at an orchestrator for a repository nobody is looking
        // at any more — so a run finishing late would report itself into the new repository's
        // panel. BuildOrchestratorAsync detaches before it attaches, which hid this: the leak
        // only shows on a Detach that is not followed by a rebuild.
        DetachRunSignals();

        _repositoryRoot = null;
        _git = null;
        OnPropertyChanged(nameof(HasRepository));
        _orchestrator = null;
        _caughtUp = false;
        _staleBindings.Clear();
        _config = new RepoConfig();
        Projects.Clear();
        Findings.Clear();
        OnPropertyChanged(nameof(FindingsSummary));
        Tree.Clear();
        InSyncProjects.Clear();

        // The last git status this panel was built from. Left behind, the next tree rebuild
        // would be handed the previous repository's file list to mark changes against.
        _lastStatus = null;

        // A search typed about one repository's objects, applied to another's.
        TreeSearch = string.Empty;

        SelectedNode = null;

        // A failure belongs to the binding that failed, in the repository that held it. Carried
        // across, the panel would offer "Try again" for a project the new repository does not
        // have — and the retry would run against a binding id that no longer resolves.
        //
        // Through RefreshFailure rather than by assigning the two properties, so the
        // HasFailure notification and the command states are raised by the one method that
        // owns them. With Projects already cleared and the orchestrator gone, it can only
        // resolve to "nothing has failed".
        RefreshFailure(null);

        // The expanded set is keyed by repo-relative path, and two repositories share those
        // freely - "docs" is open in both. Carrying it across would open folders in the new
        // repository because of a click in the old one. Persisted at the top of this method,
        // while the root still said where.
        _expansion.Clear();
        _treeSignature = string.Empty;
        _coverage.Clear();
        CoverageSummary = string.Empty;

        // Object names belong to the repository that was open. Carrying them across would let
        // a path that happens to match in the new repository be labelled with the old one's
        // object — the same reasoning as the expanded set above.
        Objects = ObjectIndex.Empty;

        // And so does a waiting import. Binding ids are unique per repository, so a leftover
        // would offer to write into a project that is no longer open — the same defect the
        // failure banner was fixed for two lines below.
        _pendingImports.Clear();
        PendingImportDetail = string.Empty;
        OnPropertyChanged(nameof(PendingImportProject));
        OnPropertyChanged(nameof(HasPendingImport));

        SelectedProject = null;
        Summary = "No repository open";
        TreeSummary = string.Empty;
        StalenessSummary = string.Empty;
        UnexportedCount = 0;
        LockState = string.Empty;
        Activity = string.Empty;
        RaiseCommandStates();
    }

    /// <summary>
    /// Find every registered vendor's toolchain and settle on a profile for each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asks the toolchain, whichever vendor's it is. This used to construct a
    /// <c>CodesysLocator</c>, call <c>ProfileResolver</c>, read a <c>CodesysInstall</c> and
    /// build a <c>LocalCodesysRunner</c> — five brand names in a view model with no business
    /// knowing any of them. It then probed only the first registered format, so a repository
    /// of any other brand was reported as that first vendor's problem.
    /// </para>
    /// <para>
    /// The profile is opaque here. It is not optional and it cannot be guessed — one vendor
    /// refuses to start headless without one, and the name does not follow from the install
    /// folder — but that is the toolchain's problem, and a different vendor will have a
    /// different one.
    /// </para>
    /// </remarks>
    private async Task ProbeToolchainAsync(CancellationToken ct)
    {
        _probed.Clear();
        var vendors = PlcFormats.All.Select(f => f.Vendor).Distinct().ToList();
        if (vendors.Count == 0)
        {
            ToolchainAvailable = false;
            ToolchainState = "No PLC vendors are registered";
            _logger.Warn(LogCategory.Plc, ToolchainState);
            return;
        }

        var parts = new List<string>();
        foreach (var vendor in vendors)
        {
            var toolchain = _toolchains(vendor, _logger);
            if (toolchain is null)
            {
                _probed[vendor] = new ProbedToolchain(null, null);
                var missing = $"{vendor} projects can be tracked but not exported yet";
                parts.Add(missing);
                _logger.Warn(LogCategory.Plc, missing);
                continue;
            }

            var status = await toolchain.ProbeAsync(ct).ConfigureAwait(true);
            _probed[vendor] = new ProbedToolchain(toolchain, status);
            parts.Add(status.Explanation
                      ?? (status.Available
                          ? $"{toolchain.Format.DisplayName} ready"
                          : $"{toolchain.Format.DisplayName} unavailable"));
        }

        ToolchainAvailable = _probed.Values.Any(p => p.Status?.Available == true);
        ToolchainState = string.Join("; ", parts);
    }

    private readonly record struct ProbedToolchain(IPlcToolchain? Toolchain, PlcToolchainStatus? Status);

    /// <summary>
    /// Compose the sync engine for the bindings this repository currently has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rebuilt only when the set of bindings changes, never on a plain rescan: construction
    /// runs lock recovery, and re-running that on every refresh would report the same
    /// re-adopted lock over and over.
    /// </para>
    /// <para>
    /// Built even when the toolchain is missing. The lock, its persistence and its recovery
    /// are all still meaningful — only the export is impossible, and
    /// <see cref="UnavailableToolchainRunner"/> says so in the one place that would
    /// otherwise have to guess.
    /// </para>
    /// </remarks>
    private async Task BuildOrchestratorAsync(CancellationToken ct)
    {
        if (_repositoryRoot is null)
        {
            return;
        }

        var store = new RepoConfigStore(_repositoryRoot, _logger);
        _config = await store.LoadAsync(ct).ConfigureAwait(true);
        AnnounceIgnoredPaths();

        if (_config.Bindings.Count == 0)
        {
            _orchestrator = null;
            return;
        }

        // Each vendor builds its own runner. An unavailable toolchain yields a runner that
        // fails every job with the reason, so the locks, their persistence and their recovery
        // keep working on a machine that cannot export that brand.
        IPlcRunner RunnerFor(PlcVendor vendor)
        {
            if (_probed.TryGetValue(vendor, out var probed) && probed.Toolchain is not null)
            {
                return probed.Toolchain.CreateRunner(probed.Status ?? new PlcToolchainStatus
                {
                    Available = false,
                    Explanation = ToolchainState,
                });
            }

            return new UnavailableToolchainRunner(
                $"{vendor} projects can be tracked but not exported yet");
        }

        var fallback = RunnerFor(_config.Bindings[0].Vendor);

        // Constructed on the UI thread, so Progress<T> captures this context and every
        // report lands back here without an explicit dispatch.
        var progress = new Progress<PlcProgress>(p =>
            _heartbeat.Report(p.OperationId, p.Done, p.Total, p.Detail ?? p.Phase));

        // A previous orchestrator's notifications would otherwise keep arriving after the
        // repository was swapped, lighting up rows that belong to a different tree.
        DetachRunSignals();

        _orchestrator = new SyncOrchestrator(
            _repositoryRoot,
            _config.Bindings,
            new LockOwner
            {
                Machine = _machine.MachineName,
                User = _machine.UserName,
                SessionId = Guid.NewGuid().ToString("N"),
                ProcessId = _machine.CurrentProcessId,
                BootId = _machine.BootId,
            },
            _clock,
            new WindowsFileSystem(),
            fallback,
            new JsonLockStore(_repositoryRoot, _logger),
            progress: progress,
            logger: _logger,

            // Lets the engine find out that CODESYS has the project open before it spends
            // 17-31 s being told so - and, for an export, work around it by reading a copy.
            occupancy: new ProjectOccupancyProbe(
                new WindowsFileSystem(), _machine, new ProcessProbe()),

            // And whether the tree is mid-merge. Without this the engine cannot tell, and
            // an import would write conflict markers into a project that then stops
            // compiling - git's own status has reported them all along and nothing asked.
            //
            // Null when there is somehow no client, which leaves the engine on its previous
            // assumption that nothing is ever conflicted rather than crashing over a question
            // it only recently learned to ask.
            conflicts: _git is null ? null : new GitConflictProbe(_git),

            // What HEAD held for a file the mirror has lost. Without it a file that was renamed
            // AND edited matches no hash, and the import falls back to destroying the object
            // and rebuilding it under the new name — losing its GUID and every reference to it.
            history: _git is null ? null : new GitHistoricContent(_git),

            // Which vendor claims a project path. Handed in rather than left to the engine to
            // fetch from the static registry, so the composition root stays the one place that
            // decides which vendors exist — and so an orchestrator under test is independent of
            // whatever else has registered into a process-wide static.
            formats: PlcFormats.ForPath,
            runnerFor: RunnerFor);

        AttachRunSignals(_orchestrator);

        var decisions = await _orchestrator
            .RecoverAsync(new LockRecoveryPolicy(_machine, new ProcessProbe()), ct)
            .ConfigureAwait(true);

        foreach (var decision in decisions)
        {
            _logger.Info(LogCategory.Lock, $"{decision.BindingId}: {decision.Explanation}");
        }
    }

    /// <summary>
    /// Everything a running toolchain job is currently holding open on its behalf.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed by the run's <b>operation id</b>, not by its binding. Keying by binding is the
    /// obvious choice and it is wrong: two runs for one project can overlap — a synchronous
    /// commit-gate export skips the coalescer entirely and can start while a background one
    /// is still going — and the second would then overwrite the first's entry, leaving the
    /// first's progress-bar scope and heartbeat undisposed for the life of the session. A bar
    /// that never goes away is exactly the symptom this whole change set exists to remove.
    /// </para>
    /// <para>
    /// The row is keyed by binding, because a row is per project and "busy" is a fact about
    /// the project rather than about one run — hence the separate count below.
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, IDisposable[]> _runScopes =
        new(StringComparer.Ordinal);

    /// <summary>How many runs are in flight per binding, so the row clears on the last one.</summary>
    private readonly Dictionary<string, int> _runsPerBinding = new(StringComparer.Ordinal);

    /// <summary>
    /// The token source for the run currently under way on each binding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Owned by whoever <i>started</i> the run, and looked up by <see cref="OnRunStarted"/> so
    /// the progress scope adopts it rather than minting one connected to nothing. The two
    /// happen at different moments on purpose — a run announces itself only once it is past
    /// the last gate, so the caller is already several method frames away by then.
    /// </para>
    /// <para>
    /// Keyed by binding rather than by operation id because the caller does not know the
    /// operation id: it is assigned inside the orchestrator, after the call.
    /// </para>
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>
        _runCancellation = new(StringComparer.Ordinal);

    /// <summary>
    /// The token for a run nobody registered against a particular binding.
    /// </summary>
    /// <remarks>
    /// <see cref="RunDeferredAsync"/> releases whatever the throttle held back, and cannot say
    /// in advance which bindings that will turn out to be — so it registers here instead, and
    /// <see cref="OnRunStarted"/> falls back to it. Without the fallback the five-second tick
    /// would be the one path that produced a run with no reachable token.
    /// </remarks>
    private CancellationTokenSource? _deferredCancellation;

    private void AttachRunSignals(SyncOrchestrator orchestrator)
    {
        orchestrator.RunStarted += OnRunStarted;
        orchestrator.RunFinished += OnRunFinished;
        _attachedTo = orchestrator;
    }

    private void DetachRunSignals()
    {
        if (_attachedTo is null)
        {
            return;
        }

        _attachedTo.RunStarted -= OnRunStarted;
        _attachedTo.RunFinished -= OnRunFinished;
        _attachedTo = null;
    }

    private SyncOrchestrator? _attachedTo;

    /// <summary>
    /// A toolchain run has begun: say so everywhere somebody might be looking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fix for the complaint that started it: saving a project made it appear in
    /// the changed files immediately — that is git noticing the <c>.project</c> — and then
    /// nothing visible happened for the 17–31 seconds CODESYS took to produce the diff people
    /// were waiting to read. The app was working the entire time and had no way to say so.
    /// </para>
    /// <para>
    /// Three surfaces, because they answer different questions: the <b>bar</b> says the app
    /// is busy, the <b>row</b> says which project it is busy with, and the <b>heartbeat</b>
    /// says it is still alive and how far along. Only the first existed, and only for
    /// operations somebody had clicked.
    /// </para>
    /// <para>
    /// Raised on a watcher's thread, so everything here is marshalled first: <c>Projects</c>
    /// is bound to a list, and touching it off the UI thread throws.
    /// </para>
    /// </remarks>
    private void OnRunStarted(SyncRunNotice notice) => OnUi(() =>
    {
        var row = Projects.FirstOrDefault(p => p.BindingId == notice.BindingId);
        var name = row?.Name ?? notice.BindingId;

        var exporting = notice.Operation == PlcOperation.Export;
        var what = exporting
            ? $"Exporting {name}…"
            : $"Importing into {name}…";

        if (row is not null)
        {
            row.Busy = true;

            // The two words the row has always claimed it would say. Nothing assigned them:
            // `ProjectRow.Busy` and the "exporting…" state were both documented in the
            // class and written by nothing, so the row sat on its previous state for the
            // whole run.
            row.State = exporting ? "exporting…" : "importing…";
        }

        // Keyed by the run's own operation id, which the job now carries, so the phases and
        // counts the toolchain reports actually land on this tracker instead of being
        // dropped for want of a matching key.
        _runsPerBinding[notice.BindingId] =
            _runsPerBinding.GetValueOrDefault(notice.BindingId) + 1;

        _runScopes[notice.OperationId] =
        [
            _busy.Begin(exporting ? WorkKind.Export : WorkKind.Import, what,
                        _runCancellation.GetValueOrDefault(notice.BindingId)
                            ?? _deferredCancellation),
            _heartbeat.Track(notice.OperationId, what.TrimEnd('…')),
        ];

        // The row's text lives in the tree, which is derived from a signature that includes
        // the state just set — so this rebuilds, and rebuilds only because that state moved.
        if (_lastStatus is { } status)
        {
            _ = RefreshTreeAsync(status);
        }
    });

    private void OnRunFinished(SyncRunNotice notice) => OnUi(() =>
    {
        if (_runScopes.Remove(notice.OperationId, out var scopes))
        {
            foreach (var scope in scopes)
            {
                scope.Dispose();
            }
        }

        var left = _runsPerBinding.GetValueOrDefault(notice.BindingId) - 1;

        if (left > 0)
        {
            // Another run for this project is still going, so the row keeps saying so.
            _runsPerBinding[notice.BindingId] = left;
            return;
        }

        _runsPerBinding.Remove(notice.BindingId);

        if (Projects.FirstOrDefault(p => p.BindingId == notice.BindingId) is { } row)
        {
            row.Busy = false;
        }

        // The row's state is left to the rescan that follows, which reads it from the lock
        // rather than guessing. Clearing it here would blank the row for the moment between
        // the run ending and the scan finishing.
        Activity = string.Empty;
    });

    /// <summary>
    /// The working tree moved. Look for projects again if what moved could be one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A project added while Versio is running was invisible until the repository was
    /// reopened.</b> The scan ran on open, after a sync did work, and after a push, discard or
    /// retry — never because a file appeared. Git noticed it immediately, so it showed in the
    /// changed-file list as untracked and sat there, which reads as the tool having seen it and
    /// decided to do nothing.
    /// </para>
    /// <para>
    /// The watcher could not have caught it either: it follows the project file and mirror of
    /// each <i>existing</i> binding, so a file nobody tracks yet is watched by nothing — and in
    /// a repository with no bindings at all, <see cref="StartWatching"/> returns early and there
    /// is no watcher whatsoever. That is exactly the state somebody is in when they add their
    /// first project.
    /// </para>
    /// <para>
    /// Gated on a path that <b>looks like a project and is not already known</b>, so the ordinary
    /// case — saving a project that is already tracked — costs a set lookup rather than a
    /// filesystem walk and a git call on every refresh.
    /// </para>
    /// </remarks>
    /// <param name="changedPaths">Repo-relative paths whose git state moved in the last refresh.</param>
    public async Task NoticeWorkingTreeAsync(IReadOnlyCollection<string> changedPaths)
    {
        if (_repositoryRoot is null || changedPaths.Count == 0)
        {
            return;
        }

        var known = Projects.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var appeared = changedPaths
            .Where(p => LooksLikeAProject(p) && !known.Contains(p))
            .ToList();

        if (appeared.Count == 0)
        {
            return;
        }

        _logger.Info(LogCategory.Sync,
            $"a project appeared in the working tree ({string.Join(", ", appeared.Take(3))}" +
            (appeared.Count > 3 ? ", …" : string.Empty) + "); scanning again");

        await RescanAsync().ConfigureAwait(true);
        await RefreshStalenessAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Hold syncing off while git rewrites the working tree, and adopt what it leaves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wrapped around a checkout or a pull. Both rewrite the project <i>and</i> its mirror at
    /// once, and the watcher cannot tell those writes from an engineer's — the hash gate agrees
    /// something changed, because it did, and the self-write suppressor only recognises hashes
    /// Versio's own export produced. Left alone, a pull lands a new mirror and the engine
    /// imports it into the project: the one failure in this design that needs no user error.
    /// </para>
    /// <para>
    /// Adopting afterwards is the other half. The project and mirror git left behind agree with
    /// each other — the commit gate is what guarantees a commit never records one without the
    /// other — so the right answer is to believe them, not to sync. Without it the engine would
    /// keep measuring against the previous commit's hashes and export on the next real save for
    /// a change nobody made.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Every tracked project as the pair of repository-relative roots a merge preflight needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The export path comes from the binding, never from the project path.</b> Where a
    /// mirror lives is the vendor format's decision —
    /// <c>IPlcProjectFormat.ConventionalExportPath</c> — and deriving it here by trimming an
    /// extension would be exactly the vendor knowledge the seam exists to keep out of the
    /// shell. It happens to be "the project path without its extension" for CODESYS today, and
    /// that is not a rule this file is entitled to know.
    /// </para>
    /// <para>
    /// Enabled bindings only: a disabled one is not being kept in step with anything, so a
    /// merge that touches it is ordinary git.
    /// </para>
    /// </remarks>
    public IReadOnlyList<TrackedRoot> TrackedRoots() =>
        _config is null
            ? []
            : [.. _config.Bindings
                .Where(b => b.Enabled)
                .Select(b => new TrackedRoot
                {
                    BindingId = b.Id,
                    Name = System.IO.Path.GetFileNameWithoutExtension(b.ProjectPath),
                    ProjectPath = b.ProjectPath.Replace('\\', '/'),
                    MirrorPath = b.ExportPath.Replace('\\', '/').TrimEnd('/'),
                })];

    /// <summary>
    /// A merge has landed and left these projects' mirrors ahead of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Instead of adopting, not as well as — and the caller is what makes that true.</b>
    /// <c>AdoptExternalStateAsync</c> takes the project and the mirror as jointly true, which
    /// is right after a checkout or a pull and wrong after a merge, where the binary project
    /// keeps one side and the mirror keeps both. The merge flow therefore passes
    /// <c>adoptResult: false</c> to <see cref="WhileGitRewritesTheTreeAsync"/> before calling
    /// this. <b>The first version of this comment claimed the same thing while the merge was
    /// still going through the adopting path</b>, so the lock briefly held a <c>LastSynced</c>
    /// asserting an agreement that had never existed. See <c>MergePreflight</c> for the
    /// measurement behind all of it.
    /// </para>
    /// <para>
    /// Two things happen per project, and the order does not matter because neither can run the
    /// toolchain: the lock is moved so the export gate will be refused before it can touch the
    /// mirror, and the import is raised so the panel offers the way out.
    /// </para>
    /// </remarks>
    /// <summary>
    /// An undo has put the tree back; let go of any lock that now protects nothing.
    /// </summary>
    /// <remarks>
    /// <b>Abandoning a merge is the case this exists for.</b> The merge marked the mirror
    /// authoritative, and <c>AdoptExternalStateAsync</c> skips a binding holding a lock — so
    /// the abandon left the lock on, blocking exports over a mirror that no longer differed
    /// from anything, with the import banner offering to import a mirror identical to the
    /// project. The orchestrator compares both hashes against <c>LastSynced</c> and releases
    /// only when nothing is outstanding, so a lock that predates the merge and still owes an
    /// import is left exactly where it is.
    /// <para>
    /// The pending import goes with the lock. Leaving the banner up over a released binding
    /// would offer a button whose whole job is already done.
    /// </para>
    /// </remarks>
    public async Task ReleaseLocksWithNothingOutstandingAsync()
    {
        if (_orchestrator is null || _config is null)
        {
            return;
        }

        foreach (var binding in _config.Bindings.Where(b => b.Enabled))
        {
            if (await _orchestrator
                    .ReleaseIfNothingOutstandingAsync(binding.Id, CancellationToken.None)
                    .ConfigureAwait(true))
            {
                _pendingImports.Remove(binding.Id);
            }
        }

        RefreshPendingImport();
        await RescanAsync().ConfigureAwait(true);
        await RefreshStalenessAsync().ConfigureAwait(true);
    }

    /// <returns>The bindings whose mirror could NOT be put in charge, for the caller to
    /// report. Empty is the ordinary answer.</returns>
    public async Task<IReadOnlyList<string>> NoteMergedTextAsync(IReadOnlyList<string> bindingIds)
    {
        if (_orchestrator is null || bindingIds.Count == 0)
        {
            return [];
        }

        var unprotected = new List<string>();

        foreach (var id in bindingIds)
        {
            // The answer matters. A binding whose project already had unexported edits is
            // ProjectAuthoritative, and the state machine blocks TextChanged from there - so
            // the merged mirror would be left open to the commit gate's export with nothing
            // on screen saying so. Loud rather than logged: this is the one guarantee the
            // whole merge path rests on.
            if (await _orchestrator.NoteMergedTextAsync(id, CancellationToken.None)
                    .ConfigureAwait(true))
            {
                NotePendingImport(id);
            }
            else
            {
                unprotected.Add(id);
            }
        }

        await RescanAsync().ConfigureAwait(true);
        await RefreshStalenessAsync().ConfigureAwait(true);

        return unprotected;
    }

    /// <param name="adoptResult">
    /// Whether the state git leaves behind may be taken as the synced truth.
    /// </param>
    /// <remarks>
    /// <b><paramref name="adoptResult"/> is false for exactly one caller, and it is the reason
    /// this parameter exists.</b> Adopting is right after a checkout, a pull or a stash: all
    /// three arrive from a single commit the gate already vetted, so the project and its mirror
    /// agree. A <b>merge</b> is the one operation whose working tree came from no commit — the
    /// binary project keeps one side while the mirror three-way merges and keeps both — so
    /// adopting there writes a <c>LastSynced</c> that claims they agree when they do not. On a
    /// merge that stopped on a conflict it is worse still: the mirror on disk holds conflict
    /// markers at that moment, and adopt would record <i>those</i> as the synced state.
    /// <para>
    /// Nothing is lost either way, because the caller then makes the mirror authoritative and
    /// the export gate is refused. What this avoids is a false record living in the lock file
    /// until the import happens to overwrite it.
    /// </para>
    /// </remarks>
    public async Task WhileGitRewritesTheTreeAsync(
        string what, Func<Task> operation, bool adoptResult = true)
    {
        if (_orchestrator is null)
        {
            await operation().ConfigureAwait(true);
            return;
        }

        using (_orchestrator.BeginExternalWrite(what))
        {
            await operation().ConfigureAwait(true);
        }

        if (adoptResult)
        {
            var adopted = await _orchestrator
                .AdoptExternalStateAsync(CancellationToken.None)
                .ConfigureAwait(true);

            if (adopted.Count > 0)
            {
                _logger.Info(LogCategory.Sync,
                    $"{what}: adopted the state of {adopted.Count} binding(s) without syncing");
            }
        }
        else
        {
            _logger.Info(LogCategory.Sync,
                $"{what}: the result is NOT adopted — the caller is making the exported text " +
                "authoritative instead");
        }

        await RescanAsync().ConfigureAwait(true);
        await RefreshStalenessAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Give a project that has just been adopted its first mirror.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Versio does not export on save</b>, and that is deliberate: a CODESYS cold start is
    /// 17-31 s, so exporting after every settled change would run the toolchain most of the
    /// working day for diffs nobody looks at until they commit. Exports happen on a git action,
    /// and on open for whatever changed while Versio was closed.
    /// </para>
    /// <para>
    /// <b>Adoption is the case that fell between those two.</b> The catch-up on open is armed
    /// once per repository, so a project dropped in afterwards missed it, and the panel would
    /// sit reading "not exported · coverage unknown" until somebody committed or reopened —
    /// with nothing on screen suggesting either. A project that has never been exported has no
    /// mirror to diff, review or import from, which is the whole of what tracking it is for.
    /// </para>
    /// <para>
    /// Through <see cref="SatisfyGateAsync"/>, the same path the catch-up and the commit gate
    /// use, so the direction lock, the hash gate, the conflict gate and the open-project check
    /// all apply exactly as they do everywhere else. A project someone has open in the IDE is a
    /// wait, not a failure, and this leaves it alone.
    /// </para>
    /// </remarks>
    private async Task ExportNewlyAdoptedAsync(int adopted)
    {
        if (_orchestrator is null || !ToolchainAvailable)
        {
            // A repository of PLC projects on a machine with no toolchain is still a perfectly
            // good repository, and the panel already reports why it cannot export.
            _logger.Info(LogCategory.Sync,
                "newly tracked project(s) will be exported when a toolchain is available");
            return;
        }

        _logger.Info(LogCategory.Sync,
            $"{adopted} newly tracked project(s) have never been exported; exporting now");

        using var busy = _busy.Begin(
            WorkKind.Export,
            adopted == 1
                ? "Exporting a newly tracked project…"
                : $"Exporting {adopted} newly tracked projects…");

        var outcomes = await SatisfyGateAsync(busy.Token).ConfigureAwait(true);

        var failure = outcomes.FirstOrDefault(o => o.IsFailure);

        Summary = failure is null
            ? outcomes.Count == 0 ? Summary : SyncNarrative.Describe(outcomes[^1])
            : $"Export failed: {failure.Explanation}";

        await RefreshStalenessAsync().ConfigureAwait(true);
    }

    /// <summary>Does this path carry an extension some registered vendor claims?</summary>
    /// <remarks>
    /// Asked of the registry rather than hardcoded, so the day a second vendor is implemented
    /// this needs no edit — the same rule the scanner itself follows.
    /// </remarks>
    private static bool LooksLikeAProject(string repoRelativePath) =>
        PlcFormats.All
            .SelectMany(f => f.ProjectExtensions)
            .Any(extension =>
                repoRelativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>Scan the repository for PLC projects and classify what was found.</summary>
    /// <summary>
    /// Put right whatever a killed export left beside a mirror.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exporter clears its own leftovers on the <i>next</i> run, which is the common case
    /// and covers nothing until an export happens. A repository can be opened, read, committed
    /// from and closed again without one — so the state has to be corrected when it is seen,
    /// not when it is next written.
    /// </para>
    /// <para>
    /// <b>Not a delete loop.</b> One of the four combinations means the mirror is gone and the
    /// trash directory holds the only copy of it, and clearing that would destroy exactly what
    /// the three-rename swap exists to protect. <see cref="SwapRecovery"/> decides; this moves
    /// the directories and says what it did.
    /// </para>
    /// </remarks>
    private Task SweepKilledSwapsAsync()
    {
        // Synchronous on purpose, and awaited by its caller anyway. Three Directory.Exists
        // calls per binding cost nothing, and the moves are the kind of work that must be
        // finished before the scan reads the same paths - a Task.Run here would buy a thread
        // and a race.
        if (_repositoryRoot is null)
        {
            return Task.CompletedTask;
        }

        foreach (var binding in _config.Bindings.Where(b => b.Enabled))
        {
            var mirror = AbsolutePath(binding.ExportPath);
            var staging = mirror + BindingConventions.StagingSuffix;
            var trash = mirror + BindingConventions.TrashSuffix;

            var verdict = SwapRecovery.Decide(
                Directory.Exists(mirror), Directory.Exists(staging), Directory.Exists(trash));

            if (verdict.Action == SwapAction.Nothing)
            {
                continue;
            }

            // Warning, always. Every one of these means a previous run of this app was killed
            // while it was writing, and that is worth knowing even in the cases where nothing
            // was at risk.
            _logger.Warn(LogCategory.Sync, $"{binding.Id}: {verdict.Explanation}");

            try
            {
                switch (verdict.Action)
                {
                    case SwapAction.RestoreTrash:
                        Directory.Move(trash, mirror);
                        _logger.Info(LogCategory.Sync,
                            $"{binding.Id}: restored the export directory from {trash}");
                        break;

                    case SwapAction.DeleteTrash:
                        Directory.Delete(trash, recursive: true);
                        break;

                    case SwapAction.DeleteStaging:
                        Directory.Delete(staging, recursive: true);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Reported and carried on. A repository that cannot be tidied is still a
                // repository somebody can work in, and the next export tries again.
                _logger.Error(LogCategory.Sync,
                    $"{binding.Id}: could not put right what a killed export left behind", ex);
            }
        }

        return Task.CompletedTask;
    }

    private async Task RescanAsync()
    {
        if (_repositoryRoot is null)
        {
            return;
        }

        using var scope = _logger.BeginOperation(Guid.NewGuid().ToString("N")[..8]);
        using var busy = _busy.Begin(WorkKind.Scan, "Scanning for PLC projects…");
        _logger.Info(LogCategory.Sync, "scanning for PLC projects");

        var scanner = new RepoScanner(_repositoryRoot, PlcFormats.All, _logger);
        var snapshot = await scanner.ScanAsync(_git, busy.Token).ConfigureAwait(true);

        // Detection and tracking are one step now: anything found is tracked, and the locks
        // take it from there.
        var adopted = await AutoAdoptAsync(snapshot, busy.Token).ConfigureAwait(true);

        // After adoption, so a project tracked a moment ago is covered by the same pass, and
        // on every scan, because the rules Versio writes grow over time and a repository
        // adopted before a rule existed would otherwise never receive it.
        await TopUpRepositoryRulesAsync(busy.Token).ConfigureAwait(true);

        if (adopted > 0)
        {
            await BuildOrchestratorAsync(busy.Token).ConfigureAwait(true);
            StartWatching();
        }

        var findings = _orphanScanner.Scan(_config, snapshot);
        var selectedPath = SelectedProject?.Path;

        Projects.Clear();
        foreach (var project in snapshot.ProjectFiles.OrderBy(p => p, StringComparer.Ordinal))
        {
            // A template is a .project file too. Showing it as an untracked project would
            // invite adopting it, which creates a binding that exports a template into its
            // own tree - nonsense, and confusing to unpick (plan R28).
            if (IsTemplate(project))
            {
                continue;
            }

            var binding = _config.Bindings.FirstOrDefault(b =>
                string.Equals(b.ProjectPath, project, StringComparison.OrdinalIgnoreCase));

            var row = new ProjectRow(project, binding?.Id);
            row.State = DescribeRow(binding, snapshot);
            Projects.Add(row);
        }

        // After the tracked ones and never through adoption: nothing above this line, and
        // nothing in AutoAdoptAsync, ever sees a source project. Failing an export it could
        // never have run would block every commit in the repository.
        _sourceProjects = snapshot.SourceProjects;
        foreach (var source in snapshot.SourceProjects.OrderBy(p => p, StringComparer.Ordinal))
        {
            Projects.Add(new ProjectRow(source, null, readFromFiles: true) { State = SourceProjectState });
        }

        Findings.Clear();
        foreach (var finding in findings.Where(f => f.NeedsAttention))
        {
            Findings.Add($"{finding.Health}: {finding.Path} — {finding.Explanation}");
        }

        // Restore the selection by path, and fall back to something sensible rather than
        // nothing. A repository with one tracked project opening on an empty panel reads as a
        // broken scan, when all that happened is that nobody had clicked a row yet.
        SelectedProject =
            Projects.FirstOrDefault(p => p.Path == selectedPath)
            ?? Projects.FirstOrDefault(p => p.Tracked)
            ?? Projects.FirstOrDefault();

        var fromFiles = Projects.Count(p => p.ReadFromFiles);
        Summary = (Projects.Count, fromFiles) switch
        {
            (0, _) => "No PLC projects found in this repository.",
            (1, 1) => "1 project, shown from its own files.",
            (1, _) => Projects[0].Tracked
                ? "1 project, tracked."
                : "1 project found. It is not tracked yet.",
            (_, 0) => $"{Projects.Count} projects, {Projects.Count(p => p.Tracked)} tracked.",
            _ => $"{Projects.Count} projects, {Projects.Count(p => p.Tracked)} tracked, " +
                 $"{fromFiles} shown from their own files.",
        };

        RaiseCommandStates();
        await RefreshTreeAsync().ConfigureAwait(true);

        _logger.Info(LogCategory.Sync,
            $"scan: {Projects.Count} project(s), {Findings.Count} finding(s)");

        // The summary is derived, so it has to be told when the collection behind it moves.
        OnPropertyChanged(nameof(FindingsSummary));

        if (adopted > 0)
        {
            await ExportNewlyAdoptedAsync(adopted).ConfigureAwait(true);
        }
    }

    /// <summary>The short phrase for one row.</summary>
    /// <remarks>
    /// "tracked, never exported" is called out separately from the lock states because it is
    /// the one that has an obvious next action. A freshly adopted project is Clean, and
    /// reporting that as "in sync" would be a lie: there is no mirror yet to be in sync with.
    /// </remarks>
    private string DescribeRow(ProjectBinding? binding, RepoSnapshot snapshot)
    {
        if (binding is null)
        {
            return "not tracked";
        }

        if (!binding.Enabled)
        {
            return "disabled";
        }

        var exported = snapshot.ExportDirectories.Any(d =>
            string.Equals(d, binding.ExportPath, StringComparison.OrdinalIgnoreCase));

        return exported
            ? SyncNarrative.Label(_orchestrator?.StateOf(binding.Id) ?? SyncDirection.Clean)
            : "never exported";
    }

    /// <summary>
    /// Watch every tracked project and its mirror, so staleness is noticed rather than
    /// discovered at commit time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what triggers an export.</b> A settled change runs the sync pipeline, so the
    /// mirror is written while somebody is still deciding what to commit — which is the whole
    /// point of having one. Reviewing a diff that only appears once you have committed is
    /// reviewing it too late.
    /// </para>
    /// <para>
    /// A CODESYS cold start is 17-31 s, so the cost is managed rather than avoided: the
    /// coalescer holds the queue at one job per project and the minimum interval floors how
    /// often the toolchain starts. Saves inside the window are recorded as pending and run once
    /// things go quiet - none is lost, and N saves cost one export rather than N.
    /// </para>
    /// <para>
    /// Nothing depends on the watcher being reliable. It drops events - buffers overflow,
    /// network and cloud-synced paths misbehave, antivirus interferes - and the commit gate
    /// re-checks from scratch regardless. A completely dead watcher degrades this to "the
    /// panel tells me when I commit rather than when I saved": annoying, never wrong.
    /// </para>
    /// </remarks>
    private void StartWatching()
    {
        StopWatching();

        if (_repositoryRoot is null || _config.Bindings.Count == 0)
        {
            return;
        }

        // The idle tick the coalescer's throttle was written for and never got.
        //
        // A save inside the 30-second minimum interval is recorded as pending and refused,
        // and the pending flag is documented to "guarantee a final export once things go
        // quiet". Nothing was asking, so quiet is exactly when it did not happen: the change
        // waited for the next save, or for somebody to commit. Five seconds is well inside
        // the interval it is waiting on and costs nothing when there is nothing deferred.
        _deferredTick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _deferredTick.Tick += (_, _) => _ = RunDeferredAsync();
        _deferredTick.Start();

        _watcher = new FileChangeWatcher(_clock, logger: _logger);
        _watcher.Settled += OnFileSettled;

        foreach (var binding in _config.Bindings.Where(b => b.Enabled))
        {
            WatchProjectPayload(binding);

            // The export tree may not exist yet on a freshly adopted project; the watcher
            // says so and carries on, and the first export creates it.
            _watcher.WatchTree(AbsolutePath(binding.ExportPath));

            // The vendor's own open-project marker, so that CLOSING the IDE becomes the
            // retry (PHASE2 item 3). An import refused because the IDE held the project used
            // to wait for the next save or the next open; the marker vanishing is the same
            // fact arriving the moment it becomes true. Through the format so Sync/ never
            // learns a vendor's spelling, and through the same watcher as everything else —
            // the marker is rewritten every few seconds while the IDE holds the project, and
            // the settle handler tells that storm from a release by looking for the file.
            if (PlcFormats.ForVendor(binding.Vendor)
                    ?.OccupancyMarkerFor(AbsolutePath(binding.ProjectPath)) is { } marker)
            {
                _occupancyMarkers[marker] = binding;
                _watcher.WatchFile(marker);
            }
        }

        _logger.Info(LogCategory.Watch,
            $"watching {_config.Bindings.Count(b => b.Enabled)} tracked project(s) for changes");
    }

    /// <summary>
    /// Watch the bytes that are the project, not merely the pointer file.
    /// </summary>
    /// <remarks>
    /// A file payload is the project path. A directory payload is the folder that
    /// contains it — watching only the pointer would miss the program. Asked of the
    /// format so this file never learns which vendors are trees.
    /// </remarks>
    private void WatchProjectPayload(ProjectBinding binding)
    {
        var project = AbsolutePath(binding.ProjectPath);
        var format = PlcFormats.ForVendor(binding.Vendor) ?? PlcFormats.ForPath(binding.ProjectPath);
        if (format is null)
        {
            _watcher!.WatchFile(project);
            return;
        }

        var payload = format.PayloadPathFor(project);
        if (!string.Equals(payload, project, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(payload))
        {
            _watcher!.WatchTree(payload);
            return;
        }

        _watcher!.WatchFile(project);
    }

    private void StopWatching()
    {
        _deferredTick?.Stop();
        _deferredTick = null;
        _occupancyMarkers.Clear();
        _waitingOnIde.Clear();

        if (_watcher is null)
        {
            return;
        }

        _watcher.Settled -= OnFileSettled;
        _watcher.Dispose();
        _watcher = null;
    }

    /// <summary>Occupancy marker path, absolute, to the binding whose project it guards.</summary>
    private readonly Dictionary<string, ProjectBinding> _occupancyMarkers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bindings that last returned <see cref="SyncVerdict.BlockedByOpenProject"/>.
    /// </summary>
    /// <remarks>
    /// The occupancy marker vanishing is the retry for <b>that wait</b> (PHASE2 item 3).
    /// A toolchain that writes the same sidecar while it works — then deletes it on the
    /// way out — is not a wait: treating it as one re-exported a directory payload every
    /// thirty seconds on 2026-08-19, because each run's own close looked like the engineer
    /// shutting the IDE. Remember the refusal, not the file.
    /// </remarks>
    private readonly HashSet<string> _waitingOnIde = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The markers being watched, for the test that proves the watch is wired.</summary>
    /// <remarks>
    /// Internal for <c>IgnoredPaths()</c>'s reason: trusting that a dictionary populated in
    /// this file is consulted by a handler in this file is the assumption that has been
    /// wrong here before, and the end-to-end test that would catch it instead has to wait on
    /// real filesystem timers.
    /// </remarks>
    internal IReadOnlyDictionary<string, ProjectBinding> WatchedOccupancyMarkers =>
        _occupancyMarkers;

    /// <summary>
    /// Record that a direction waited on the IDE, so a later occupancy-marker
    /// vanishing retries. Tests drive this; production sets it from
    /// <see cref="SyncVerdict.BlockedByOpenProject"/>.
    /// </summary>
    internal void RememberWaitingOnIde(string bindingId) => _waitingOnIde.Add(bindingId);

    private void RememberIfWaitingOnIde(SyncOutcome outcome)
    {
        if (outcome.Verdict is SyncVerdict.BlockedByOpenProject
            && !string.IsNullOrEmpty(outcome.BindingId))
        {
            _waitingOnIde.Add(outcome.BindingId);
        }
    }

    /// <summary>
    /// Ask the engine whether a throttled change is now due, and run it if so.
    /// </summary>
    /// <remarks>
    /// Deliberately silent when there is nothing to do — it fires every five seconds for as
    /// long as a repository is open, and a line per tick is how the log panel got drowned
    /// the first time.
    /// </remarks>
    private async Task RunDeferredAsync()
    {
        if (_orchestrator is null)
        {
            return;
        }

        using var cancel = new CancellationTokenSource();
        _deferredCancellation = cancel;

        try
        {
            var outcomes = await _orchestrator.RunDeferredAsync(cancel.Token)
                .ConfigureAwait(true);

            foreach (var outcome in outcomes)
            {
                RememberIfWaitingOnIde(outcome);
            }

            if (outcomes.Any(o => o.DidWork || o.IsFailure))
            {
                await RescanAsync().ConfigureAwait(true);
                await RefreshStalenessAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Asked to stop, by somebody switching branch or closing the window. Not a
            // failure, and not worth an error line for something they just requested.
            _logger.Info(LogCategory.Sync, "the deferred sync was cancelled");
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory.Sync, "could not run the deferred sync", ex);
        }
        finally
        {
            if (ReferenceEquals(_deferredCancellation, cancel))
            {
                _deferredCancellation = null;
            }
        }
    }

    /// <summary>
    /// A watched file stopped changing. Re-read staleness; run nothing.
    /// </summary>
    /// <remarks>
    /// Raised on a timer thread, so the work is marshalled to the UI thread before it touches
    /// anything the view is bound to. The evaluation itself is a hash of each project file
    /// and its mirror - no toolchain, no process, and cheap enough to do on settle.
    /// </remarks>
    private void OnFileSettled(WatchedChange change)
    {
        // No line of its own. The watcher already logs each settle, and a tier other than
        // the watcher's as a warning, so this used to write every settle a second time -
        // two identical lines every five seconds while an IDE held a project open.

        // The vendor's open-project marker is not a side of any binding — Locate would
        // shrug at it — so it is routed before the ordinary flow. Most of these events are
        // the IDE rewriting the marker while it works; the handler is what tells that storm
        // from the release this watch exists for.
        if (_occupancyMarkers.TryGetValue(change.Path, out var released))
        {
            OnUi(() => _ = RetryAfterIdeReleaseAsync(released, change.Path));
            return;
        }

        OnUi(() => _ = SyncSettledChangeAsync(change));
    }

    /// <summary>
    /// The vendor's open-project marker settled. If it is gone <b>and a direction waited
    /// on it</b>, retry what waited.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Closing the IDE is the retry now</b> (PHASE2 item 3): an import refused with "close
    /// it and the import will run" used to sit until the next save or the next repository
    /// open, with the panel saying so and nothing listening. The marker vanishing is that
    /// same fact as a filesystem event.
    /// </para>
    /// <para>
    /// <b>Only a recorded wait.</b> The first vendor's occupancy is a sidecar the IDE
    /// writes while the engineer works and we never touch. A directory-payload host writes
    /// the same kind of marker for the length of its session, then deletes it — which is
    /// our run finishing, not the engineer closing the IDE. Retrying that unconditionally
    /// re-exported a 500 MB tree every thirty seconds on 2026-08-19. The wait is
    /// remembered when the orchestrator returns <see cref="SyncVerdict.BlockedByOpenProject"/>.
    /// </para>
    /// <para>
    /// <b>Both directions are submitted, through every gate a save goes through.</b> The
    /// retry must not become a second door around the direction lock, the hash gate or the
    /// import confirmation — so it does not decide what waited, it asks: each side runs
    /// <c>HandleChangeAsync</c> exactly as if the user had saved, and a side with nothing
    /// pending is refused quietly by the same gates that refuse an echoed export. The
    /// blocked import comes back as <c>AwaitingConfirmation</c>, which re-raises the offer
    /// on the project's row — a person still confirms it; the IDE closing merely stopped
    /// being the thing everybody was waiting on.
    /// </para>
    /// <para>
    /// <b>A marker that still exists is the rewrite storm, not a release.</b> CODESYS
    /// replaces the marker through a transient sibling every few seconds for as long as it
    /// holds the project, so most settles of this path mean "still open". Quiet on that
    /// path, deliberately: it fires all day while an engineer works, and nothing happened.
    /// </para>
    /// </remarks>
    internal async Task RetryAfterIdeReleaseAsync(ProjectBinding binding, string markerPath)
    {
        if (File.Exists(markerPath))
        {
            return;
        }

        if (!_waitingOnIde.Remove(binding.Id))
        {
            // Our own toolchain run wrote and deleted this marker. Nothing waited.
            return;
        }

        _logger.Info(LogCategory.Sync,
            $"{System.IO.Path.GetFileName(binding.ProjectPath)}: the IDE released the " +
            "project; retrying what waited for it");

        await RunSettledJobAsync(binding, ChangeSource.Project,
            "the IDE released the project").ConfigureAwait(true);
        await RunSettledJobAsync(binding, ChangeSource.ExportedText,
            "the IDE released the project").ConfigureAwait(true);
    }

    /// <summary>
    /// Ask the commit gate what is stale, and say so.
    /// </summary>
    /// <remarks>
    /// The gate is reused rather than reimplemented so the panel and the thing that will
    /// eventually block a commit can never disagree about which projects need exporting.
    /// </remarks>
    private async Task RefreshStalenessAsync()
    {
        if (_orchestrator is null)
        {
            return;
        }

        try
        {
            var gate = await _orchestrator.EvaluateCommitGateAsync(CancellationToken.None)
                .ConfigureAwait(true);

            var stale = new HashSet<string>(gate.StaleBindings, StringComparer.Ordinal);
            UnexportedCount = stale.Count;

            // Kept, not just counted: the tree needs to know WHICH bindings are behind, so a
            // project with unexported work stays on screen even when git has nothing to say
            // about it. Replaced wholesale rather than added to, so a binding that has since
            // been exported stops being stale.
            _staleBindings.Clear();
            foreach (var id in stale)
            {
                _staleBindings.Add(id);
            }

            foreach (var row in Projects.Where(p => p.Tracked && !p.Busy))
            {
                if (stale.Contains(row.BindingId!))
                {
                    row.State = "not exported";
                }
            }

            // Stated in the panel rather than only in a row, because the whole point is that
            // somebody who has not looked at this project all afternoon still finds out.
            StalenessSummary = stale.Count switch
            {
                0 => string.Empty,
                1 => "1 project has changes that have not been exported.",
                var n => $"{n} projects have changes that have not been exported.",
            };

            if (stale.Count > 0)
            {
                _logger.Info(LogCategory.Sync,
                    $"{stale.Count} binding(s) not yet exported: {gate.Explanation}");
            }
        }
        catch (Exception ex)
        {
            // Never let a background evaluation take the app down; it is advisory.
            _logger.Error(LogCategory.Sync, "could not evaluate export staleness", ex);
        }
    }

    private string AbsolutePath(string repoRelative) =>
        System.IO.Path.Combine(
            _repositoryRoot!,
            repoRelative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>
    /// The tracked projects as the explorer needs them: every binding, full object tree,
    /// no git status. The Project panel's own build still pairs catalogs with HEAD; this
    /// one does not, because the explorer does not mark changes.
    /// </summary>
    public IReadOnlyList<WorkspaceProject> ReadProjectsForExplorer()
    {
        if (_repositoryRoot is null)
        {
            return [];
        }

        var projects = new List<WorkspaceProject>();
        foreach (var binding in _config.Bindings.Where(b => b.Enabled))
        {
            var manifestPath = System.IO.Path.Combine(
                AbsolutePath(binding.ExportPath), BindingConventions.ManifestFileName);
            var manifest = File.Exists(manifestPath)
                ? ProjectManifest.Parse(File.ReadAllText(manifestPath), out _)
                : null;

            var catalogs = ReadCatalogs(manifest, binding.ExportPath,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            projects.Add(new WorkspaceProject
            {
                ProjectPath = binding.ProjectPath,
                ExportPath = binding.ExportPath,
                Manifest = manifest,
                Libraries = catalogs.Libraries,
                Tasks = catalogs.Tasks,
                Devices = catalogs.Devices,
                ExportState = Projects.FirstOrDefault(p => p.BindingId == binding.Id)?.State
                              ?? "tracked",
                Busy = Projects.FirstOrDefault(p => p.BindingId == binding.Id)?.Busy ?? false,
            });
        }

        // The same folders the Project panel draws, read the same way, so the two panels never
        // disagree about what a source project holds.
        var problems = new List<string>();
        foreach (var folder in _sourceProjects)
        {
            if (PlcFormats.ForSourceProject(folder) is not { } format)
            {
                continue;
            }

            projects.Add(new WorkspaceProject
            {
                ProjectPath = folder,
                ExportPath = folder,
                Manifest = format.DescribeSourceProject(folder, ListSourceFiles(folder, problems)),
                ExportState = SourceProjectState,
            });
        }

        foreach (var problem in problems)
        {
            _logger.Warn(LogCategory.Sync, problem);
        }

        return projects;
    }

    /// <summary>
    /// The whole working tree: every tracked project with its objects, and every other
    /// changed file beside them.
    /// </summary>
    public ObservableCollection<TreeRow> Tree { get; } = [];

    /// <summary>
    /// The tracked projects the tree leaves out because nothing about them needs a person.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tree hides an in-sync project on purpose: its job is "what is there to review",
    /// and a clean project has nothing. But hiding them left the panel blank on the calmest
    /// day the tool has, with the fact that everything was safe reduced to a grey footnote
    /// under an empty card. Seen in the running window on 2026-09-21: four projects in sync,
    /// and the middle of the panel said "Open a repository to see what is in it" — because
    /// the empty-state trigger read <c>Tree.Count == 0</c>, which is true both with no
    /// repository and with nothing changed.
    /// </para>
    /// <para>
    /// So the calm state gets rows of its own, one line each, and never enters the tree:
    /// expanding a clean project would put the whole Explorer back into a panel whose
    /// premise is changes.
    /// </para>
    /// </remarks>
    public ObservableCollection<InSyncProjectRow> InSyncProjects { get; } = [];

    /// <summary>A repository is open. The panel's "open one" empty state reads this, not the tree.</summary>
    public bool HasRepository => _repositoryRoot is not null;

    /// <summary>Which rows are open, kept across rebuilds.</summary>
    private readonly TreeExpansion _expansion = new();

    /// <summary>
    /// What the tree was last built from, so an unchanged refresh rebuilds nothing.
    /// </summary>
    /// <remarks>
    /// The tree is rebuilt whenever git's status is re-read, and most of those re-reads find
    /// nothing — they fire because somebody saved a file in their editor. Rebuilding anyway
    /// would clear the TreeView's selection several times a minute, which is precisely the
    /// regression the changed-files list was reconciled in place to avoid.
    /// </remarks>
    private string _treeSignature = string.Empty;

    /// <summary>Which call to <see cref="RefreshTreeAsync(GitStatus)"/> owns the tree.</summary>
    /// <remarks>
    /// Bumped at entry, checked after the awaits. UI-thread only, so a plain int — the
    /// interleaving is of continuations, not of threads.
    /// </remarks>
    private int _treeGeneration;
    private GitStatus? _lastStatus;

    /// <summary>What each project's export covers, keyed by its repo-relative path.</summary>
    private readonly Dictionary<string, CoverageReport> _coverage =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Filter the tree by object or file name.</summary>
    public string TreeSearch
    {
        get => _treeSearch;
        set
        {
            if (SetProperty(ref _treeSearch, value))
            {
                _ = RefreshTreeAsync();
            }
        }
    }

    /// <summary>
    /// What the export does not carry, when that is anything.
    /// </summary>
    /// <remarks>
    /// Empty for a clean export rather than a reassurance. The panel already tells the user
    /// what is in sync; a second line saying the export is also complete, on every project,
    /// every refresh, is the kind of standing green that gets read past — including the once
    /// it changes.
    /// </remarks>
    public string CoverageSummary
    {
        get => _coverageSummary;
        private set => SetProperty(ref _coverageSummary, value);
    }

    /// <summary>One sentence about the tree, or why there is not one.</summary>
    public string TreeSummary
    {
        get => _treeSummary;
        private set => SetProperty(ref _treeSummary, value);
    }

    /// <summary>
    /// Every exported file in the workspace, by the path git reports it under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rebuilt with the tree and from the same manifests, so the git panel names a changed
    /// file exactly as the tree names the object it belongs to. Read by the shell rather than
    /// pushed to it: the panel already relabels after every tree refresh, and an event here
    /// would be a second path to the same update.
    /// </para>
    /// <para>
    /// <b>Empty until the first tree refresh completes.</b> The changed-files list is filled
    /// before the manifests are parsed — deliberately, so the panel is not held behind file IO
    /// — so the first paint after opening a repository shows paths and the refresh that
    /// follows names them. Rows that arrive before the index are relabelled, not rebuilt.
    /// </para>
    /// </remarks>
    public ObjectIndex Objects
    {
        get => _objects;
        private set
        {
            _objects = value;
            ObjectsChanged?.Invoke();
        }
    }

    private ObjectIndex _objects = ObjectIndex.Empty;

    /// <summary>
    /// The index was rebuilt, and whatever is showing paths can be named again.
    /// </summary>
    /// <remarks>
    /// <b>An event rather than a call at each site, because the sites are not all in the git
    /// panel's control.</b> The index is filled by a tree refresh, and a tree refresh happens
    /// on a status refresh, on <c>AttachAsync</c>, after a rescan and after an adoption. The
    /// first of those is the one that catches people out: opening a repository refreshes git
    /// <i>before</i> the bindings are loaded, so the first pass names nothing and the index
    /// arrives afterwards, from a path the panel never hears about. It stayed wrong until the
    /// working tree next moved — which, on a project nobody is editing, is never.
    /// </remarks>
    public event Action? ObjectsChanged;

    private TreeRow? _selectedNode;

    /// <summary>
    /// Selecting a row opens whatever it has to show.
    /// </summary>
    /// <remarks>
    /// Folders are skipped — they stand for a grouping, not a thing. Projects are not: a
    /// <c>.project</c> row has no diff, and the pane says what its export did instead, which
    /// is the one answer somebody clicking a binary file actually wants.
    /// </remarks>
    public TreeRow? SelectedNode
    {
        get => _selectedNode;
        set
        {
            var previous = _selectedNode;

            if (!SetProperty(ref _selectedNode, value))
            {
                return;
            }

            if (value is null)
            {
                // Told to let go — the shell does this when the rail leaves the panel or the
                // shown file leaves the list. The TreeView deselects the old container itself
                // on a click; for a null it has to be asked, through the row's own flag.
                if (previous is not null)
                {
                    previous.IsSelected = false;
                }

                return;
            }

            NoteSelection(value.Node.Path);

            // A project row is where a failure is reported, so the pane beside it has to
            // follow the selection. Called for every node, not just project rows: selecting
            // something else has to CLEAR the pane, or a failure would follow the user
            // around the tree.
            RefreshFailure(value.Node.Kind == ProjectNodeKind.Project ? value.Node.Path : null);

            if (!value.IsFolder)
            {
                NodeSelected?.Invoke(value);
            }
        }
    }

    /// <summary>
    /// Raised when an object is picked, so the shell can show its diff.
    /// </summary>
    /// <remarks>
    /// An event rather than a reference to the shell. The sync panel has no business knowing
    /// what a diff pane is, and the alternative - passing the shell in - makes the two
    /// mutually dependent for one call.
    /// </remarks>
    public event Action<TreeRow>? NodeSelected;

    /// <summary>The set of paths git has been told to ignore has changed.</summary>
    /// <remarks>
    /// Raised when the bindings are loaded or a project is adopted, because that is when the
    /// answer moves. The shell forwards it to the repository watcher — the marker of a project
    /// adopted a moment ago is precisely the one that then beats on the status refresh.
    /// </remarks>
    public event Action<IReadOnlyList<string>>? IgnoredPathsChanged;

    /// <summary>
    /// Repo-relative globs Versio itself wrote into <c>.gitignore</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked of the vendor seam rather than assembled here: <c>TransientPatternsFor</c> is the
    /// same call adoption makes when it writes the rules, so what the watcher ignores and what
    /// git ignores cannot drift apart. Adding a vendor adds its markers here for free.
    /// </para>
    /// <para>
    /// <c>.versio/local/</c> is included for the same reason it is in the ignore file: locks,
    /// backups and scratch copies are written constantly by Versio itself, and none of them
    /// can ever appear in <c>git status</c>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> IgnoredPaths()
    {
        var globs = new List<string> { BindingConventions.LocalIgnorePattern + "**" };

        foreach (var binding in _config.Bindings.Where(b => b.Enabled))
        {
            if (PlcFormats.ForPath(binding.ProjectPath) is { } format)
            {
                globs.AddRange(format.TransientPatternsFor(binding.ProjectPath));
            }
        }

        return globs;
    }

    private void AnnounceIgnoredPaths() => IgnoredPathsChanged?.Invoke(IgnoredPaths());

    /// <summary>
    /// Parse every catalog the manifest names — library and task — each paired with its
    /// HEAD copy when one was fetched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The manifest decides what is a catalog — <c>form == LIBRARIES</c> or
    /// <c>TASK_BOTH</c> — rather than a directory listing deciding by suffix, because the
    /// manifest is the authority on what the export wrote and a stray YAML somebody dropped
    /// into the mirror must not grow rows.
    /// </para>
    /// <para>
    /// A file the manifest names but the disk lacks is skipped without a word here: the
    /// coverage report already owns the job of saying what the mirror is missing, and a
    /// second voice would say it twice. Neither parser throws on a torn or half-written
    /// file, for the same reason the manifest parser does not — this runs on every refresh,
    /// and an export mid-swap is a moment, not an error.
    /// </para>
    /// </remarks>
    private (IReadOnlyList<LibrarySnapshot> Libraries,
             IReadOnlyList<TaskSnapshot> Tasks,
             IReadOnlyList<DeviceSnapshot> Devices)
        ReadCatalogs(
            ProjectManifest? manifest,
            string exportPath,
            IReadOnlyDictionary<string, string> baselines)
    {
        if (manifest is null)
        {
            return ([], [], []);
        }

        List<LibrarySnapshot>? libraries = null;
        List<TaskSnapshot>? tasks = null;
        List<DeviceSnapshot>? devices = null;
        var root = exportPath.Replace('\\', '/').Trim('/');

        foreach (var record in manifest.Objects)
        {
            var isLibraries = string.Equals(
                record.Form, ExportCoverage.LibrariesForm, StringComparison.Ordinal);
            var isTasks = string.Equals(
                record.Form, ExportCoverage.TaskBothForm, StringComparison.Ordinal);
            var isDevices = string.Equals(
                record.Form, ExportCoverage.DevicesForm, StringComparison.Ordinal);

            if ((!isLibraries && !isTasks && !isDevices) || record.Path.Length == 0)
            {
                continue;
            }

            var absolute = System.IO.Path.Combine(AbsolutePath(exportPath), record.Path);
            if (!File.Exists(absolute))
            {
                continue;
            }

            var text = File.ReadAllText(absolute);
            var repoPath = $"{root}/{record.Path.Replace('\\', '/')}";
            var old = baselines.TryGetValue(repoPath, out var head) && head.Length > 0
                ? head
                : null;

            if (isLibraries)
            {
                var current = LibraryCatalog.Parse(text);
                if (current.IsValid)
                {
                    (libraries ??= []).Add(new LibrarySnapshot
                    {
                        Path = repoPath,
                        Current = current,
                        Baseline = old is null ? null : LibraryCatalog.Parse(old),
                    });
                }
            }
            else if (isTasks)
            {
                var current = TaskCatalog.Parse(text);
                if (current.IsValid)
                {
                    (tasks ??= []).Add(new TaskSnapshot
                    {
                        Path = repoPath,
                        Current = current,
                        Baseline = old is null ? null : TaskCatalog.Parse(old),
                    });
                }
            }
            else
            {
                var current = DeviceCatalog.Parse(text);
                if (current.IsValid)
                {
                    (devices ??= []).Add(new DeviceSnapshot
                    {
                        Path = repoPath,
                        Current = current,
                        Baseline = old is null ? null : DeviceCatalog.Parse(old),
                    });
                }
            }
        }

        return (libraries ?? [], tasks ?? [], devices ?? []);
    }

    /// <summary>Where this repository's expansion state lives: machine-local, gitignored.</summary>
    /// <remarks>
    /// Beside the locks and the auto-stash record, because it is the same kind of thing —
    /// true in this clone, on this machine, meaningless in any other.
    /// </remarks>
    private string? TreeExpansionPath =>
        _repositoryRoot is null
            ? null
            : System.IO.Path.Combine(_repositoryRoot, ".versio", "local", "tree.json");

    private static readonly System.Text.Json.JsonSerializerOptions TreeStateJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Restore which rows a previous session had open. A missing or unreadable file is the
    /// defaults, not an error — expansion state is the one kind of state that is always safe
    /// to lose.
    /// </summary>
    private void LoadTreeExpansion()
    {
        if (TreeExpansionPath is not { } path || !File.Exists(path))
        {
            return;
        }

        try
        {
            var state = System.Text.Json.JsonSerializer.Deserialize<TreeExpansion.State>(
                File.ReadAllText(path), TreeStateJson);

            if (state is not null)
            {
                _expansion.Restore(state);
                _logger.Debug(LogCategory.App,
                    $"restored tree expansion ({state.Opened.Count} open, " +
                    $"{state.Closed.Count} closed) from {path}");
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException
                                      or UnauthorizedAccessException)
        {
            _logger.Warn(LogCategory.App,
                $"the tree expansion state at {path} is unreadable and was ignored: {ex.Message}");
        }
    }

    /// <summary>Persist the expansion state for the repository that is closing.</summary>
    private void SaveTreeExpansion()
    {
        if (TreeExpansionPath is not { } path)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                System.Text.Json.JsonSerializer.Serialize(_expansion.Snapshot(), TreeStateJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Worth a line, not a dialog: the cost of losing this file is a tree that opens
            // to its defaults once.
            _logger.Warn(LogCategory.App,
                $"could not persist the tree expansion state to {path}: {ex.Message}");
        }
    }

    /// <summary>Rebuild the tree, reading git's status first.</summary>
    /// <remarks>
    /// For the callers inside this class, which have just changed the working tree and have no
    /// status to hand. The shell uses the overload — it has one already, and a second
    /// <c>git status</c> per refresh is a process spawn for an answer we were just given.
    /// </remarks>
    private async Task RefreshTreeAsync()
    {
        if (_git is null)
        {
            Tree.Clear();
            InSyncProjects.Clear();
            return;
        }

        var status = await _git.StatusAsync(CancellationToken.None).ConfigureAwait(true);
        await RefreshTreeAsync(status).ConfigureAwait(true);
    }

    /// <summary>
    /// Rebuild the one tree from the bindings, their manifests and git's status.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The manifests are read and the node graph assembled on a background thread, then handed
    /// over as a finished snapshot. Rebuilding a whole tree on the UI thread on every status
    /// refresh is the most likely source of jank in this app — the plan says so, and the
    /// 5,002-node spike is why it is believed. This now runs on every refresh rather than only
    /// when somebody picked a project, so that stopped being theoretical.
    /// </para>
    /// <para>
    /// <b>The rows are built on the UI thread and the nodes are not.</b> That split looks
    /// backwards until you notice <see cref="TreeExpansion"/> is written from the UI thread
    /// every time somebody opens a folder: constructing rows off-thread would read that set
    /// while a click writes it. There is one row per project and one per loose changed file,
    /// so the cost is trivial — and the thousands of object rows are not built here at all,
    /// because <see cref="TreeRow.Children"/> defers them until something asks.
    /// </para>
    /// <para>
    /// A missing manifest is a normal state, not an error: the project is tracked and has
    /// never been exported. Its row says so instead of vanishing.
    /// </para>
    /// </remarks>
    public async Task RefreshTreeAsync(GitStatus status)
    {
        // Kept so a toolchain run can refresh the tree on its own. A run starts about a
        // second after the save that caused it, by which time the refresh triggered by that
        // same save has already been and gone — so without this the row would not say
        // "exporting…" until the next backstop, up to twenty seconds into a run that only
        // lasts seventeen.
        _lastStatus = status;

        // The same guard DiffView.Rebuild carries, for the same race arrived at the same
        // way. This method captures the sources — including each row's "exporting…" state —
        // and then AWAITS twice before applying: the baseline fetch and the background
        // build. A refresh captured mid-export can therefore finish AFTER the refresh that
        // saw the export complete, and apply its stale snapshot last — observed as a project
        // row stuck on "exporting…" forever, because the signature gate below then refused
        // every later rebuild as already-shown. The last call to enter owns the tree;
        // everything older abandons at the next checkpoint.
        var generation = ++_treeGeneration;

        if (_repositoryRoot is null)
        {
            Tree.Clear();
            InSyncProjects.Clear();
            TreeSummary = string.Empty;
            return;
        }

        var search = TreeSearch;
        var sources = _config.Bindings
            .Where(b => b.Enabled)
            .Select(b => (
                b.ProjectPath,
                b.ExportPath,
                ManifestPath: System.IO.Path.Combine(
                    AbsolutePath(b.ExportPath), BindingConventions.ManifestFileName),
                State: Projects.FirstOrDefault(p => p.BindingId == b.Id)?.State ?? "tracked",
                Busy: Projects.FirstOrDefault(p => p.BindingId == b.Id)?.Busy ?? false,

                // Visible when there is something to DO, not merely something to report.
                //
                // This used to be "any state other than Clean", and the state that made that
                // wrong is the commonest one in the tool's life: after a successful export
                // the binding holds ProjectAuthoritative until the work is PUSHED, so a
                // project that had been exported, committed and left alone sat in a panel
                // whose whole premise is "everything with something to review", with no
                // changes under it and nothing to review. The lock is still reported - it is
                // in the header, in its own sentence, which is where a fact about state
                // belongs.
                //
                // Failed and Conflict stay: both need a person. Text-ahead stays: an import
                // is outstanding, and committing the mirror would otherwise hide it. Stale
                // is handled below, and a project with no manifest at the callsite.
                NeedsAttention: (_orchestrator?.StateOf(b.Id) ?? SyncDirection.Clean)
                                    is SyncDirection.Failed
                                    or SyncDirection.Conflict
                                    or SyncDirection.TextAuthoritative
                                || _staleBindings.Contains(b.Id)

                                // A project being exported RIGHT NOW is the one thing the
                                // user is waiting on, and the rule above would hide it: git
                                // reports nothing about a project whose file matches HEAD,
                                // so re-exporting an unchanged project made its row vanish
                                // for the twenty seconds it was working. Observed - the
                                // tree logged "1 project(s) hidden as in sync" while
                                // CODESYS was running against it.
                                || (Projects.FirstOrDefault(p => p.BindingId == b.Id)
                                        ?.Busy ?? false)))
            .ToList();

        // Source projects, beside the bindings and never among them. Captured here, on the UI
        // thread, like the bindings are: the scan that writes the list runs on this thread too.
        var folders = _sourceProjects;

        // Everything the tree is derived from, in one string. A manifest's write time stands
        // in for its contents: it is written by an export and by nothing else, so a changed
        // timestamp means a changed export, and hashing eleven files on every keystroke to
        // learn the same thing would be a poor trade.
        var signature = string.Join('',
        [
            search,
            .. sources.Select(x =>
                $"{x.ProjectPath}|{x.ExportPath}|{x.State}|{x.Busy}|{ManifestStamp(x.ManifestPath)}"),

            // A source project's objects come from its file list, and git's status (below)
            // already moves whenever a file is added, removed or edited inside it.
            .. folders.Select(f => $"source|{f}"),
            .. status.Changes
                .OrderBy(c => c.Path, StringComparer.Ordinal)
                .Select(c => $"{c.Path}|{c.Effective}|{c.IsStaged}"),
        ]);

        // Compared against the signature of what was APPLIED, and recorded only when a
        // build lands — never up front. Recording it before the awaits below was the second
        // half of the stuck-row race: the superseded refresh had already claimed its
        // signature as shown, so every later refresh with the same inputs returned early
        // while the tree displayed something older.
        if (signature == _treeSignature && Tree.Count > 0)
        {
            return;
        }

        // HEAD's copy of every changed catalog — library or task — fetched here because it
        // needs git and the build below deliberately does not. The pair is what lets the
        // tree mark WHICH library or task moved instead of only that the catalog's file
        // did. Added and untracked files are skipped — HEAD has no copy, and asking would
        // spawn a process to learn what the status already said. Sitting after the
        // signature gate, so a refresh that rebuilds nothing fetches nothing.
        var baselines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_git is not null)
        {
            foreach (var change in status.Changes)
            {
                var path = change.Path.Replace('\\', '/');
                var isCatalog =
                    path.EndsWith(LibraryCatalog.FileSuffix, StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(TaskCatalog.FileSuffix, StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(DeviceCatalog.FileSuffix, StringComparison.OrdinalIgnoreCase);
                if (isCatalog
                    && change.Effective is not (ChangeKind.Added or ChangeKind.Untracked))
                {
                    baselines[path] = await _git.ShowAtHeadAsync(path, CancellationToken.None)
                        .ConfigureAwait(true);
                }
            }
        }

        var built = await Task.Run(() =>
        {
            var coverage = new Dictionary<string, CoverageReport>(StringComparer.OrdinalIgnoreCase);
            var projects = new List<WorkspaceProject>();
            var unreadable = new List<string>();

            foreach (var source in sources)
            {
                IReadOnlyList<string> problems = [];
                var manifest = File.Exists(source.ManifestPath)
                    ? ProjectManifest.Parse(File.ReadAllText(source.ManifestPath), out problems)
                    : null;

                // Said out loud, because the tree cannot say it.
                //
                // A manifest that half-read draws a project with objects missing, and one
                // that did not read at all draws it as a flat list of raw paths - both of
                // which look like Versio's own tree rendering being wrong rather than like a
                // file it could not use. That is exactly how the warnings/strings mismatch
                // survived a whole feature. The signature above already gates this on the
                // manifest having changed, so a broken export logs once and not per refresh.
                if (manifest is not null && problems.Count > 0)
                {
                    unreadable.AddRange(problems.Select(p => $"{source.ProjectPath}: {p}"));
                }

                var report = ExportCoverage.Of(manifest);
                coverage[source.ProjectPath] = report;

                var catalogs = ReadCatalogs(manifest, source.ExportPath, baselines);

                projects.Add(new WorkspaceProject
                {
                    ProjectPath = source.ProjectPath,
                    ExportPath = source.ExportPath,
                    Manifest = manifest,
                    Libraries = catalogs.Libraries,
                    Tasks = catalogs.Tasks,
                    Devices = catalogs.Devices,

                    // A project with no manifest has never been exported. Git reports nothing
                    // about it, because there is nothing on disk to report on - and it is
                    // precisely the project somebody needs to be told about.
                    NeedsAttention = source.NeedsAttention || manifest is null,

                    // The gap rides along on the row itself. A project whose libraries are
                    // not exported reads as "in sync", which is true of what was exported
                    // and misleading about the project - so the row says both.
                    ExportState = report.Label.Length == 0
                        ? source.State
                        : $"{source.State} · {report.Label}",
                    Busy = source.Busy,
                });
            }

            var tracked = projects.Count;
            var views = new Dictionary<string, SourceProjectView>(StringComparer.OrdinalIgnoreCase);
            var unlisted = new List<string>();
            foreach (var folder in folders)
            {
                if (PlcFormats.ForSourceProject(folder) is not { } format)
                {
                    continue;
                }

                var files = ListSourceFiles(folder, unlisted);
                var manifest = format.DescribeSourceProject(folder, files);
                views[folder] = new SourceProjectView(format, manifest, files.Count);

                projects.Add(new WorkspaceProject
                {
                    // The folder is both: it is the project, and it is the readable tree the
                    // objects' paths are relative to. The tree claims every file under it.
                    ProjectPath = folder,
                    ExportPath = folder,
                    Manifest = manifest,
                    ExportState = SourceProjectState,
                });
            }

            return (Roots: WorkspaceTreeBuilder.Build(projects, status.Changes,
                        new ProjectTreeOptions { ChangedOnly = false, Search = search }),
                    Coverage: coverage,
                    Tracked: tracked,
                    Views: views,
                    Unlisted: unlisted,
                    Unreadable: unreadable,

                    // Built here rather than by the git panel, off the same parsed manifests
                    // the tree is assembled from. A second reader would be a second answer to
                    // "what is this file called", which is the disagreement WorkspaceTreeBuilder
                    // itself was written to end.
                    Objects: ObjectIndex.Of(projects));
        }).ConfigureAwait(true);

        // The checkpoint. A newer refresh entered while this one was fetching or building;
        // its snapshot is fresher than this one on every axis that matters — the busy flag
        // above all — so this one abandons rather than applying stale rows last.
        if (generation != _treeGeneration)
        {
            return;
        }

        _treeSignature = signature;

        foreach (var problem in built.Unreadable)
        {
            _logger.Warn(LogCategory.Sync, $"manifest not fully readable - {problem}");
        }

        foreach (var problem in built.Unlisted)
        {
            _logger.Warn(LogCategory.Sync, problem);
        }

        _sourceViews.Clear();
        foreach (var (folder, view) in built.Views)
        {
            _sourceViews[folder] = view;
        }

        var roots = built.Roots;

        _coverage.Clear();
        foreach (var (path, report) in built.Coverage)
        {
            _coverage[path] = report;
        }

        CoverageSummary = built.Coverage.Values.FirstOrDefault(c => c.HasGaps)?.Describe()
                          ?? string.Empty;

        // Reconciled, not rebuilt.
        //
        // Exporting on every change is what lets somebody review a diff before committing —
        // and it means an export lands while they are reading the tree. Clearing the
        // collection and refilling it dropped the selection, closed whatever WPF had realised,
        // and threw the reader back to the top on every save. The changed-files list was fixed
        // this way for the same reason; the tree is the primary view now and had not been.
        TreeRow.Reconcile(Tree, roots, 0, _expansion);

        var objects = roots.Sum(CountObjects);
        var changed = status.Changes.Count;

        // The hidden ones, by name, so the panel can list them beside the tree rather than
        // only counting them. Taken from the sources rather than re-derived: a project is
        // hidden exactly when the builder returned no root for its path.
        var shownPaths = roots
            .Where(r => r.Kind == ProjectNodeKind.Project)
            .Select(r => r.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Counted per kind. A source project with nothing changed is listed in the same quiet
        // note as an in-sync tracked one, but the sentences below talk about TRACKED projects,
        // and an unchanged folder of source files is not one.
        var hidden = sources.Count(s => !shownPaths.Contains(s.ProjectPath.Replace('\\', '/')));
        var hiddenFolders = built.Views.Keys.Count(f => !shownPaths.Contains(f));
        ReconcileInSync(sources
            .Where(s => !shownPaths.Contains(s.ProjectPath.Replace('\\', '/')))
            .Select(s => new InSyncProjectRow(
                System.IO.Path.GetFileName(s.ProjectPath), s.ProjectPath, s.State))
            .Concat(built.Views.Keys
                .Where(f => !shownPaths.Contains(f))
                .Select(f => new InSyncProjectRow(System.IO.Path.GetFileName(f), f, SourceProjectState)))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());

        // An empty panel has to say why it is empty. A tracked project that is simply in sync
        // is hidden from the tree, and a panel that went blank without explaining itself reads
        // as a broken scan - which is the failure this wording exists to prevent. The hidden
        // ones are listed by name in a quiet note at the foot of the panel, so the sentence
        // points there instead of saying they are not listed.
        TreeSummary = (Tree.Count, hidden) switch
        {
            (0, 0) when built.Tracked == 0 && hiddenFolders == 0 => "No PLC projects tracked in this repository.",
            (0, 1) => "Nothing has changed. The tracked project is in sync.",
            (0, var h) when h > 1 => $"Nothing has changed. All {h} tracked projects are in sync.",
            (0, _) => "Nothing has changed.",
            (_, 0) => $"{objects} object(s) · {changed} changed file(s).",
            var (_, h) => $"{objects} object(s) · {changed} changed file(s) · " +
                          $"{h} project(s) in sync.",
        };

        _logger.Debug(LogCategory.Sync,
            $"tree: {Tree.Count} root(s), {objects} object(s), {changed} changed file(s), " +
            $"{hidden} project(s) hidden as in sync, {built.Views.Count} source project(s), " +
            $"{hiddenFolders} unchanged");

        // Last, and it raises. Whatever is listening runs synchronously on the back of this
        // assignment, so it has to find the tree already settled rather than half-reconciled.
        Objects = built.Objects;
    }

    /// <summary>
    /// Replace the in-sync rows only when they differ, so a refresh that changes nothing does
    /// not make the list blink or drop the keyboard's place in it.
    /// </summary>
    private void ReconcileInSync(IReadOnlyList<InSyncProjectRow> rows)
    {
        if (rows.SequenceEqual(InSyncProjects))
        {
            return;
        }

        InSyncProjects.Clear();
        foreach (var row in rows)
        {
            InSyncProjects.Add(row);
        }
    }

    /// <summary>A manifest's identity for change detection: when it was written, and how big.</summary>
    /// <remarks>
    /// Missing is a value too — "never exported" is a state the tree renders, and it has to be
    /// distinguishable from an export that has since appeared.
    /// </remarks>
    private static string ManifestStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}"
                : "none";
        }
        catch (IOException)
        {
            // Mid-write, or on a share that just went away. Treat it as changed rather than
            // taking the panel down over a file that is only ever a convenience.
            return Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>
    /// What the last tree build read for a source project, or null when the path is not one.
    /// </summary>
    public SourceProjectView? SourceProjectFor(string projectPath) =>
        _sourceViews.GetValueOrDefault(Normalize(projectPath));

    /// <summary>
    /// Every file under a source project, relative to it with forward slashes, for the format to
    /// describe. A folder that cannot be listed is reported and drawn empty, never thrown: the
    /// build runs on a background thread, and an exception there would leave the panel blank.
    /// </summary>
    private List<string> ListSourceFiles(string folder, List<string> problems)
    {
        var root = AbsolutePath(folder);
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{folder}: could not list its files: {e.Message}");
            return [];
        }
    }

    /// <summary>What a project's export covers, or null when it is not a tracked project.</summary>
    /// <summary>The manifest of one tracked project, freshly read, or null without one.</summary>
    /// <remarks>
    /// Read on demand for the project summary rather than cached: it is one small file, the
    /// summary is built on a click, and a cached copy would be one more thing the export has
    /// to invalidate.
    /// </remarks>
    public ProjectManifest? ManifestFor(string projectPath)
    {
        if (ManifestPathFor(projectPath) is not { } path || !File.Exists(path))
        {
            return null;
        }

        return ProjectManifest.Parse(File.ReadAllText(path));
    }

    /// <summary>Absolute path of a tracked project's manifest, or null when untracked.</summary>
    public string? ManifestPathFor(string projectPath)
    {
        if (_repositoryRoot is null)
        {
            return null;
        }

        var normalized = Normalize(projectPath);
        var binding = _config.Bindings.FirstOrDefault(b =>
            string.Equals(Normalize(b.ProjectPath), normalized, StringComparison.OrdinalIgnoreCase));

        return binding is null
            ? null
            : System.IO.Path.Combine(
                AbsolutePath(binding.ExportPath), BindingConventions.ManifestFileName);
    }

    /// <summary>Repo-relative export root of one tracked project, or null when untracked.</summary>
    public string? ExportPathFor(string projectPath)
    {
        var normalized = Normalize(projectPath);
        return _config.Bindings.FirstOrDefault(b =>
                string.Equals(Normalize(b.ProjectPath), normalized,
                              StringComparison.OrdinalIgnoreCase))
            ?.ExportPath;
    }

    public CoverageReport? CoverageFor(string projectPath) =>
        _coverage.TryGetValue(Normalize(projectPath), out var report) ? report : null;

    private static int CountObjects(ProjectTreeNode node) =>
        (node.Kind == ProjectNodeKind.Object ? 1 : 0) + node.Children.Sum(CountObjects);

    /// <summary>
    /// Whether every tracked project's mirror is current enough to commit.
    /// </summary>
    /// <remarks>
    /// Exposed for the git panel. The gate belongs to the orchestrator, which lives here, and
    /// the alternative - letting the commit path build its own opinion of staleness - is how
    /// two answers to one question end up disagreeing at the moment somebody commits.
    /// </remarks>
    public Task<CommitGateResult> EvaluateGateAsync(CancellationToken ct) =>
        EvaluateGateAsync(includePaths: null, ct);

    public Task<CommitGateResult> EvaluateGateAsync(
        IReadOnlyList<string>? includePaths, CancellationToken ct) =>
        _orchestrator is null
            ? Task.FromResult(new CommitGateResult
            {
                Decision = GateDecision.Proceed,
                Explanation = "no tracked PLC projects in this repository",
            })
            : _orchestrator.EvaluateCommitGateAsync(includePaths, ct);

    /// <summary>
    /// Export every stale binding, so the commit records the project and its mirror together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A backstop rather than the primary path: exports follow a detected change, so the
    /// mirror is usually current well before anybody commits. This covers what the watcher
    /// missed - a dropped event, a save still inside the throttle window, a project adopted
    /// while no toolchain was available - which is why it exports rather than merely refusing,
    /// and why it is worth the 17-31 s on the occasions it is needed.
    /// </para>
    /// <para>
    /// Each job is synchronous, so it bypasses the coalescer and runs now rather than queueing
    /// behind a background sync the user cannot see. The circuit breaker, the hash gate and
    /// the direction lock still apply.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<SyncOutcome>> SatisfyGateAsync(CancellationToken ct) =>
        SatisfyGateAsync(bindingIds: null, ct);

    public async Task<IReadOnlyList<SyncOutcome>> SatisfyGateAsync(
        IReadOnlyList<string>? bindingIds, CancellationToken ct)
    {
        if (_orchestrator is null)
        {
            return [];
        }

        var operationId = Guid.NewGuid().ToString("N")[..8];
        using var scope = _logger.BeginOperation(operationId);
        using var busy = _busy.Begin(WorkKind.Export, "Exporting before commit…");
        using var beat = _heartbeat.Track(operationId, "commit gate export");

        // Says what it is doing, not why the caller asked. This same path now serves the
        // commit gate, the catch-up on open and a retry, and a line claiming a commit is
        // about to happen was wrong for two of the three - in a log that is read precisely
        // when somebody is trying to work out what the app did on its own.
        _logger.Info(LogCategory.Sync, bindingIds is { Count: > 0 }
            ? $"exporting {bindingIds.Count} project(s) whose mirror is behind"
            : "exporting every project whose mirror is behind");

        var outcomes = await _orchestrator
            .SatisfyCommitGateAsync(ct, bindingIds)
            .ConfigureAwait(true);

        foreach (var outcome in outcomes)
        {
            RememberIfWaitingOnIde(outcome);
            var sentence = SyncNarrative.Describe(outcome);
            if (outcome.IsFailure)
            {
                _logger.Error(LogCategory.Sync, sentence);
            }
            else
            {
                _logger.Info(LogCategory.Sync, sentence);
            }
        }

        await RescanAsync().ConfigureAwait(true);
        await RefreshStalenessAsync().ConfigureAwait(true);
        Activity = string.Empty;

        return outcomes;
    }

    /// <summary>Release every direction lock after a successful push (plan section 6.5).</summary>
    /// <remarks>
    /// Push, not commit: until work is pushed nobody else can see it, so the direction is
    /// still meaningfully owned. Push is repo-wide, so this releases every binding.
    /// </remarks>
    public async Task ReleaseAfterPushAsync(string headCommit, CancellationToken ct)
    {
        if (_orchestrator is null)
        {
            return;
        }

        await _orchestrator.ReleaseAfterPushAsync(headCommit, ct).ConfigureAwait(true);
        _logger.Info(LogCategory.Lock, $"push released every direction lock at {headCommit}");

        await RescanAsync().ConfigureAwait(true);
        await RefreshStalenessAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// The binding whose export tree contains a repo-relative path, if any.
    /// </summary>
    /// <remarks>
    /// The matching itself lives in <see cref="BindingConventions.OwnerOf"/> rather than here:
    /// it is a rule about paths and bindings, not about a panel, and Core is where it can be
    /// tested against the cases that have actually caused bugs — a sibling directory sharing a
    /// prefix, and a <c>.project</c> beside the export tree named after it.
    /// </remarks>
    public string? BindingOwning(string repoRelativePath) =>
        BindingConventions.OwnerOf(_config.Bindings, repoRelativePath)?.Id;

    /// <summary>
    /// Release one binding's direction lock after its mirror was discarded (plan section 6.5).
    /// </summary>
    /// <remarks>
    /// Discarding is the other way a direction stops being owned. The lock exists because one
    /// side holds work the other has not seen; throwing that work away means there is nothing
    /// left to protect, and leaving the lock held would block the next export for no reason
    /// anybody could discover from the UI.
    /// </remarks>
    public async Task ReleaseAfterDiscardAsync(string bindingId, CancellationToken ct)
    {
        if (_orchestrator is null)
        {
            return;
        }

        await _orchestrator.ReleaseAfterDiscardAsync(bindingId, ct).ConfigureAwait(true);

        await RescanAsync().ConfigureAwait(true);
        await RefreshStalenessAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Adopt every project in the repository that is not tracked yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Detection and tracking are one step: a <c>.project</c> that appears in the repository
    /// becomes a tracked project, and from then on the locks drive it. The plan originally had
    /// adoption offered rather than performed, on the grounds that auto-exporting an unknown
    /// project launches CODESYS for 17-31 s on a file that might be a vendor sample - that is
    /// a real cost, and the decision to pay it is deliberate.
    /// </para>
    /// <para>
    /// <b>Templates are still refused.</b> That guard is not about risk appetite: a binding
    /// whose project is a template exports the template into its own tree, which is incoherent
    /// rather than merely unwanted, and genuinely confusing to unpick afterwards (plan R28).
    /// </para>
    /// </remarks>
    /// <summary>
    /// Make sure the repository rules cover every binding, not only the ones just adopted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Adoption writes the <c>.gitignore</c> and <c>.gitattributes</c> rules once, when a
    /// project is first tracked, and <see cref="AutoAdoptAsync"/> skips a project that is
    /// already bound. So <b>a rule added to Versio after a repository was adopted never
    /// reaches it.</b> Four have been added in the time this repository has existed: the
    /// mirror's <c>text eol=lf</c>, the staging and trash directories an export swaps
    /// through, and a directory project's binary store beside its pointer — and the
    /// repository that found the first of them was one adopted last week.
    /// </para>
    /// <para>
    /// Both planners already leave an existing rule alone and report no change when
    /// everything is covered, so this is idempotent by construction: it writes on the first
    /// scan after an upgrade and never again.
    /// </para>
    /// </remarks>
    private async Task TopUpRepositoryRulesAsync(CancellationToken ct)
    {
        if (_repositoryRoot is null || _git is null || _config.Bindings.Count == 0)
        {
            return;
        }

        var setup = new RepoSetup(_repositoryRoot, format: null, _logger);
        var enabled = _config.Bindings.Where(b => b.Enabled).ToList();
        var mirrors = enabled.Select(b => b.ExportPath).ToList();

        // The projects too, not only their mirrors: a directory project's binary store is
        // scoped to the project, so the rules cannot be written without knowing which
        // projects are bound. Without this an already-adopted repository would never get
        // them - the exact gap this method exists to close.
        var projects = enabled.Select(b => b.ProjectPath).ToList();

        try
        {
            await setup.EnsureGitAttributesAsync(_git, ct, mirrors, projects).ConfigureAwait(true);

            foreach (var binding in enabled)
            {
                await setup.EnsureGitIgnoreAsync(binding.ProjectPath, ct, binding.ExportPath)
                    .ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only working tree, or one somebody has open elsewhere. The rules are a
            // protection rather than a precondition, so this is reported and the scan carries
            // on - refusing to open the repository over it would be the worse trade.
            _logger.Warn(LogCategory.Git,
                $"could not bring this repository's git rules up to date: {ex.Message}");
        }
    }

    private async Task<int> AutoAdoptAsync(RepoSnapshot snapshot, CancellationToken ct)
    {
        if (_repositoryRoot is null)
        {
            return 0;
        }

        var setup = new RepoSetup(_repositoryRoot, format: null);
        var adopted = 0;

        foreach (var project in snapshot.ProjectFiles.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (IsTemplate(project) ||
                _config.Bindings.Any(b =>
                    string.Equals(b.ProjectPath, project, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var result = await setup.AdoptAsync(project, _git, ProfileFor(project), ct)
                .ConfigureAwait(true);
            if (result.Adopted)
            {
                _config = result.Config;
                AnnounceIgnoredPaths();
                adopted++;
                _logger.Info(LogCategory.Sync,
                    $"tracking {project} automatically as {result.Decision.Binding!.Id}");
            }
            else
            {
                // Not an error: an ignored path, or a template. Said once at Debug so a
                // repository full of samples does not fill the log on every scan.
                _logger.Debug(LogCategory.Sync,
                    $"not tracking {project}: {result.Decision.Explanation}");
            }
        }

        return adopted;
    }

    /// <summary>
    /// A watched file settled. Work out which side of which binding moved, and sync it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is where the locks earn their keep. Both directions run unattended, so the only
    /// thing standing between a save and an unwanted overwrite is the direction lock: once one
    /// side is authoritative the other is refused until the work is pushed or discarded. The
    /// orchestrator applies that, the circuit breaker and the hash gate before anything runs.
    /// </para>
    /// <para>
    /// Jobs are <b>not</b> synchronous, deliberately: the coalescer's cancel window and
    /// minimum interval are what stop a save storm from launching a CODESYS process per
    /// keystroke. A refusal here is normal and expected - most settled changes are the echo of
    /// an export we just wrote.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Internal rather than private so a test can drive it with a real orchestrator and a real
    /// repository. The watcher above it is a timer, and a test that waits on one proves less
    /// while taking longer — but everything <i>below</i> this line is where the decisions are,
    /// including whether an import is answered on the user's behalf. That question was answered
    /// "yes" here for the whole life of the setting that asks it, and nothing failed.
    /// </remarks>
    internal async Task SyncSettledChangeAsync(WatchedChange change)
    {
        if (_orchestrator is null || _repositoryRoot is null)
        {
            return;
        }

        var path = Relative(change.Path);
        var (binding, source) = Locate(path);
        if (binding is null)
        {
            return;
        }

        _logger.Debug(LogCategory.Sync,
            $"{binding.Id}: {source} change settled at {path}");

        await RunSettledJobAsync(binding, source, $"automatic, {source}").ConfigureAwait(true);
    }

    /// <summary>Submit one side of one binding to the orchestrator, gates and all.</summary>
    /// <remarks>
    /// The tail of <see cref="SyncSettledChangeAsync"/>, extracted so the IDE-release retry
    /// submits through the same door rather than growing a parallel one — a second path into
    /// <c>HandleChangeAsync</c> with its own cancellation or its own confirmation answer is
    /// exactly how the import gate was once answered on the user's behalf.
    /// </remarks>
    private async Task RunSettledJobAsync(
        ProjectBinding binding, ChangeSource source, string reason)
    {
        if (_orchestrator is null || _repositoryRoot is null)
        {
            return;
        }

        // The run's own token, registered before the call so the progress scope opened from
        // inside the orchestrator can find and adopt it. Without this the automatic export -
        // by far the most common thing to be running when somebody switches branch or closes
        // the window - would have no reachable token, and "stop it and continue" would be a
        // button that did nothing.
        using var cancel = new CancellationTokenSource();
        _runCancellation[binding.Id] = cancel;

        try
        {
            var outcome = await _orchestrator.HandleChangeAsync(new SyncJob
            {
                BindingId = binding.Id,
                Source = source,

                // NOT confirmed, and there IS somebody to ask now.
                //
                // This said "confirmed, because there is nobody to ask" for as long as the
                // orchestrator has had the gate — so RequireImportConfirmation shipped as true,
                // was consulted on every import, and was answered yes by the only caller.
                // A setting that is switched on and always answered is a fiction, and this is
                // the direction that writes a binary file nobody can diff afterwards.
                //
                // The refusal comes back as AwaitingConfirmation and is surfaced on the
                // project's own row rather than as a modal: the orchestrator's own wording is
                // "review the diff and confirm", and a dialog thrown up the instant a file
                // settles is a dialog that arrives before the review. See ConfirmImportAsync.
                Confirmed = false,
                Reason = reason,
            }, cancel.Token).ConfigureAwait(true);

            RememberIfWaitingOnIde(outcome);

            var sentence = SyncNarrative.Describe(outcome);

            if (outcome.IsFailure)
            {
                var cancelled = outcome.Explanation.Contains(
                    "cancelled", StringComparison.OrdinalIgnoreCase);
                if (cancelled)
                {
                    _logger.Info(LogCategory.Sync, sentence);
                    await SweepKilledSwapsAsync().ConfigureAwait(true);
                    if (Projects.FirstOrDefault(p => p.BindingId == binding.Id) is { } cancelledRow)
                    {
                        cancelledRow.State = "cancelled — try again";
                    }
                }
                else
                {
                    _logger.Error(LogCategory.Sync, sentence);
                }

                RefreshFailure(binding.ProjectPath);
            }
            else if (outcome.DidWork)
            {
                _logger.Info(LogCategory.Sync, sentence);
            }
            else
            {
                // Coalesced, throttled, unchanged, or blocked by the lock. All routine.
                _logger.Debug(LogCategory.Sync, sentence);

                // Queued and throttled are not "nothing happened": an export IS going to run,
                // just not this instant, and the row would otherwise sit on its old state while
                // the user waits for a diff. Said in the row rather than the bar, because the
                // app is not busy - it is waiting on purpose.
                if (outcome.Verdict is SyncVerdict.Coalesced or SyncVerdict.Throttled &&
                    Projects.FirstOrDefault(p => p.BindingId == binding.Id) is { Busy: false } row)
                {
                    row.State = "queued…";
                }

                // The import that is waiting for a person. Raised here rather than polled,
                // because this is the only place that learns of it — the state machine is
                // happy, nothing failed, and the row would otherwise say "text ahead" with no
                // hint that anything is expected of anybody.
                if (outcome.Verdict is SyncVerdict.AwaitingConfirmation)
                {
                    NotePendingImport(binding.Id);
                }
            }

            if (outcome.DidWork || outcome.IsFailure)
            {
                Summary = sentence;
                LockState = SyncNarrative.Describe(outcome.State);
                await RescanAsync().ConfigureAwait(true);
                await RefreshStalenessAsync().ConfigureAwait(true);
                if (outcome.IsFailure)
                {
                    RefreshFailure(binding.ProjectPath);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fire-and-forget: an unobserved OCE is a silent failure. The orchestrator
            // normally converts cancel into Failed; this is the occupancy-wait path
            // that throws before RunAsync.
            _logger.Info(LogCategory.Sync, $"{binding.Id}: cancelled on request");
            await SweepKilledSwapsAsync().ConfigureAwait(true);
            if (Projects.FirstOrDefault(p => p.BindingId == binding.Id) is { } row)
            {
                row.State = "cancelled — try again";
            }

            RefreshFailure(binding.ProjectPath);
        }
        finally
        {
            // Removed by identity: a later change on this binding may already have replaced
            // the entry, and clearing it blindly would unregister somebody else's live run.
            _runCancellation.TryRemove(
                new KeyValuePair<string, CancellationTokenSource>(binding.Id, cancel));
        }
    }

    /// <summary>Which binding a changed path belongs to, and which side of it.</summary>
    /// <remarks>
    /// The project file is matched exactly, then the export tree by prefix, then the
    /// payload directory for a vendor whose project is a tree. Export is checked before
    /// payload because a conventional export often lives beside the pointer, inside the
    /// payload folder — a prefix on the folder would otherwise treat a mirror edit as a
    /// project save, which is the loop the whole design exists to avoid.
    /// A payload file that the format says does not count toward the hash is dropped, the
    /// same way occupancy-marker rewrites stay quiet.
    /// </remarks>
    private (ProjectBinding? Binding, ChangeSource Source) Locate(string repoRelativePath)
    {
        foreach (var binding in _config.Bindings.Where(b => b.Enabled))
        {
            if (string.Equals(Normalize(binding.ProjectPath), repoRelativePath,
                              StringComparison.OrdinalIgnoreCase))
            {
                return (binding, ChangeSource.Project);
            }
        }

        foreach (var binding in _config.Bindings.Where(b => b.Enabled))
        {
            var prefix = Normalize(binding.ExportPath).TrimEnd('/') + "/";
            if (repoRelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (binding, ChangeSource.ExportedText);
            }
        }

        foreach (var binding in _config.Bindings.Where(b => b.Enabled))
        {
            if (PayloadRelative(binding) is not { } payload)
            {
                continue;
            }

            var prefix = payload.TrimEnd('/') + "/";
            if (!repoRelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(repoRelativePath, payload, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var format = PlcFormats.ForVendor(binding.Vendor)
                         ?? PlcFormats.ForPath(binding.ProjectPath);
            var relative = string.Equals(repoRelativePath, payload, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : repoRelativePath[prefix.Length..];
            if (format is not null
                && relative.Length > 0
                && !format.CountsTowardProjectHash(relative))
            {
                return (null, ChangeSource.Project);
            }

            return (binding, ChangeSource.Project);
        }

        return (null, ChangeSource.Project);
    }

    /// <summary>
    /// Repo-relative payload path, or null when the payload is the project file itself.
    /// </summary>
    private string? PayloadRelative(ProjectBinding binding)
    {
        if (_repositoryRoot is null)
        {
            return null;
        }

        var format = PlcFormats.ForVendor(binding.Vendor) ?? PlcFormats.ForPath(binding.ProjectPath);
        if (format is null)
        {
            return null;
        }

        var project = AbsolutePath(binding.ProjectPath);
        var payload = format.PayloadPathFor(project);
        if (string.Equals(payload, project, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Normalize(System.IO.Path.GetRelativePath(_repositoryRoot, payload));
    }

    private static bool IsTemplate(string path) =>
        PlcFormats.ForPath(path)?.IsTemplate(path) == true;

    private string? ProfileFor(string projectPath)
    {
        var format = PlcFormats.ForPath(projectPath);
        if (format is not null
            && _probed.TryGetValue(format.Vendor, out var probed))
        {
            return probed.Status?.Profile;
        }

        return null;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>Absolute watcher path back to the repo-relative form the bindings use.</summary>
    private string Relative(string absolute) =>
        Normalize(System.IO.Path.GetRelativePath(_repositoryRoot!, absolute));

    /// <summary>Something is behind and an export could fix it.</summary>
    /// <summary>The project the failure pane is about, or null when nothing has failed.</summary>
    /// <remarks>
    /// Driven by the tree selection rather than by a panel-wide "is anything broken" question,
    /// because a repository can hold several projects and only one of them may have failed.
    /// </remarks>
    public ProjectRow? FailedProject { get; private set; }

    /// <summary>
    /// The failure the selected project is stuck on, or empty when it is not stuck.
    /// </summary>
    /// <remarks>
    /// The reason comes from the lock record the orchestrator persisted, so it survives a
    /// restart: a failure from yesterday's session still explains itself this morning, which
    /// is exactly when somebody asks.
    /// </remarks>
    public string FailureDetail
    {
        get => _failureDetail;
        private set => SetProperty(ref _failureDetail, value);
    }

    private string _failureDetail = string.Empty;

    /// <summary>Work out whether the selected project is in a failed state, and why.</summary>
    /// <remarks>
    /// Called on every selection change and after every rescan, so the pane cannot outlive
    /// the failure it describes - a retry that succeeds has to make it go away.
    /// </remarks>
    public void RefreshFailure(string? projectPath)
    {
        var row = Projects.FirstOrDefault(p =>
            p.Tracked && string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));

        var failed = row is not null && _orchestrator is not null &&
                     _orchestrator.StateOf(row.BindingId!) == SyncDirection.Failed;

        FailedProject = failed ? row : null;

        // What the state means, then what actually happened. The first sentence is the same
        // one the narrative gives everywhere else; the second is the toolchain's own words,
        // which are the ones worth reading.
        FailureDetail = failed
            ? SyncNarrative.Describe(SyncDirection.Failed) +
              Environment.NewLine + Environment.NewLine +
              _orchestrator!.ReasonFor(row!.BindingId!)
            : string.Empty;

        OnPropertyChanged(nameof(HasFailure));
        RaiseCommandStates();
    }

    /// <summary>True when the failure pane has something to show.</summary>
    public bool HasFailure => FailedProject is not null;

    // ------------------------------------------------------------------ pending imports

    /// <summary>
    /// Bindings whose exported text has changed and which are waiting for a human.
    /// </summary>
    /// <remarks>
    /// <b>A set, not a single value like <see cref="FailedProject"/>.</b> A pull can land edits
    /// in several mirrors at once, and a panel that could only remember the last one would
    /// silently drop the rest — each of which is a project that will not be updated until
    /// somebody says so. The banner names the first and counts the others.
    /// </remarks>
    private readonly HashSet<string> _pendingImports = new(StringComparer.Ordinal);

    /// <summary>The project whose import is waiting, or null when none is.</summary>
    public ProjectRow? PendingImportProject =>
        _pendingImports.Count == 0
            ? null
            : Projects.FirstOrDefault(p => p.BindingId is { } id && _pendingImports.Contains(id));

    /// <summary>Something is waiting to be imported.</summary>
    public bool HasPendingImport => PendingImportProject is not null;

    /// <summary>
    /// What an import would do, in the words somebody needs before agreeing to it.
    /// </summary>
    /// <remarks>
    /// It names the <c>.project</c> by path rather than the mirror, because the mirror is what
    /// they edited and the project is what is about to be rewritten — and those are different
    /// files. The backup is stated in the same breath: it is the reason this is recoverable,
    /// and somebody weighing the decision should not have to already know it exists.
    /// </remarks>
    public string PendingImportDetail
    {
        get => _pendingImportDetail;
        private set => SetProperty(ref _pendingImportDetail, value);
    }

    private string _pendingImportDetail = string.Empty;

    /// <summary>Import the exported text into the project. Asks first.</summary>
    public AsyncCommand ConfirmImportCommand { get; private set; } = null!;

    /// <summary>
    /// Every question this view model can put to the user.
    /// </summary>
    /// <remarks>
    /// Set by <see cref="ShellViewModel.Prompts"/>, which owns this view model and hands its
    /// own prompts down, so a host wires one thing rather than two. Defaults to
    /// <see cref="NullPrompts"/>, which answers "no" — the right way round for a wiring
    /// mistake, where the wrong way round is a silent import.
    /// </remarks>
    public IUserPrompts Prompts { get; set; } = NullPrompts.Instance;

    private void NotePendingImport(string bindingId)
    {
        if (!_pendingImports.Add(bindingId))
        {
            return;
        }

        RefreshPendingImport();

        _logger.Info(LogCategory.Sync,
            $"{bindingId}: exported text has changed and is waiting to be imported; " +
            "nothing has been written to the project");
    }

    private void RefreshPendingImport()
    {
        // Bindings that have gone away with a repository switch, or been imported since.
        _pendingImports.RemoveWhere(
            id => Projects.All(p => !string.Equals(p.BindingId, id, StringComparison.Ordinal)));

        var row = PendingImportProject;
        var others = _pendingImports.Count - 1;

        PendingImportDetail = row is null
            ? string.Empty
            : $"The exported text under this project has changed. Importing rewrites " +
              $"{row.Path} from it. A copy of the project is kept first, under .versio/local/." +
              (others > 0
                  ? $" {others} other project{(others == 1 ? " is" : "s are")} also waiting."
                  : string.Empty);

        OnPropertyChanged(nameof(PendingImportProject));
        OnPropertyChanged(nameof(HasPendingImport));
        ConfirmImportCommand.RaiseCanExecuteChanged();
    }

    private bool CanConfirmImport() => _orchestrator is not null && HasPendingImport;

    /// <summary>
    /// Import the exported text, once a person has agreed to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same job the watcher raised, run again with <c>Confirmed</c> set — rather than a
    /// second code path into the orchestrator. Everything the automatic attempt applies still
    /// applies: the direction lock, the hash gate, the circuit breaker, the open-project check
    /// and the backup. The only thing that changes is the answer to the one question that was
    /// outstanding.
    /// </para>
    /// <para>
    /// <b>The confirmation is refused by default.</b> Prompts nobody attached, a dismissed
    /// dialog and a closed window all mean no, and no means nothing is written.
    /// </para>
    /// </remarks>
    private async Task ConfirmImportAsync()
    {
        if (_orchestrator is null || PendingImportProject is not { BindingId: { } id } row)
        {
            return;
        }

        // One id across the whole flow, per §3 of the git-panel plan: the question, the
        // orchestrator's own dozen lines, the rescan and the staleness refresh. Without it,
        // "why did that import do what it did" is answered by reading around the lines of
        // whatever else was happening — which is the finding that came out of auditing the
        // branch-delete and sign-in flows, and this is the same shape.
        using var scope = _logger.BeginOperation(LoggerExtensions.NewOperationId());

        _logger.Click("Sync.ConfirmImport");

        // The plan is decided HERE, before the question is asked — not inside the orchestrator
        // after the answer, and not inside CODESYS 17-31 s later. Two reasons, and only the
        // second is about this commit:
        //
        //   - what an import will delete has to be nameable in the dialog, which is what
        //     PendingImportDetail becomes able to say next;
        //   - the plan agreed to has to be the plan executed. Recomputing after the click
        //     would make the dialog's account of what is about to happen true by coincidence,
        //     and a mirror that moved in between would be applied without anybody being asked.
        //
        // Null when the binding has gone or no format claims it. The import is not blocked on
        // that: the orchestrator falls back to computing one, which is exactly what every
        // watcher-triggered import does, so the failure mode here is losing the guarantee
        // rather than losing the import.
        // CancellationToken.None deliberately: the busy scope below does not exist yet, because
        // it is opened once the answer is yes. This reads a manifest and walks a mirror — the
        // work the 17-31 s launch exists to avoid — so there is nothing here worth cancelling.
        var plan = await _orchestrator.PlanImportAsync(id, CancellationToken.None)
            .ConfigureAwait(true);

        // The banner's sentence, then what this particular import would actually do. They are
        // built in different places on purpose: the banner is refreshed on every watcher event
        // and must not walk a mirror to do it, while this runs once, when somebody has asked
        // the question — which is also the only moment the answer is worth the walk.
        var detail = ImportNarrative.Describe(plan) is { Length: > 0 } what
            ? $"{PendingImportDetail}\n\n{what}"
            : PendingImportDetail;

        if (!Prompts.ConfirmImport(new DestructivePrompt(
                $"Import into {row.Name}?", detail, Confirm: "Import")))
        {
            _logger.Info(LogCategory.Sync,
                $"{id}: import declined; the project is unchanged");
            return;
        }

        using var busy = _busy.Begin(WorkKind.Import, $"Importing into {row.Name}…");

        var outcome = await _orchestrator.HandleChangeAsync(new SyncJob
        {
            BindingId = id,
            Source = ChangeSource.ExportedText,
            Confirmed = true,
            Synchronous = true,
            Reason = "confirmed by the user",

            // The plan the question was asked about, not one computed after it was answered.
            Plan = plan,
        }, busy.Token).ConfigureAwait(true);

        RememberIfWaitingOnIde(outcome);

        Summary = SyncNarrative.Describe(outcome);

        if (outcome.IsFailure)
        {
            _logger.Error(LogCategory.Sync, Summary);
        }
        else
        {
            _logger.Info(LogCategory.Sync, Summary);
        }

        // Cleared whatever happened, including on failure: a failure is reported by the failure
        // banner, and leaving this one up as well would offer the same button again for a
        // project that has just refused it.
        _pendingImports.Remove(id);
        RefreshPendingImport();

        await RescanAsync().ConfigureAwait(true);
        await RefreshStalenessAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ restoring a backup

    /// <summary>Offer the copies of the selected project and put one back.</summary>
    public AsyncCommand RestoreBackupCommand { get; private set; } = null!;

    private bool _selectedHasBackups;

    /// <summary>The selected project has copies to go back to.</summary>
    /// <remarks>
    /// Cached when the selection moves rather than read in <c>CanExecute</c>, which WPF calls
    /// far more often than anybody expects — a directory enumeration per raise would put the
    /// disk in the path of every command-state refresh in the panel.
    /// </remarks>
    public bool SelectedHasBackups
    {
        get => _selectedHasBackups;
        private set
        {
            if (SetProperty(ref _selectedHasBackups, value))
            {
                RestoreBackupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void RefreshBackupAvailability() =>
        SelectedHasBackups =
            _orchestrator is not null &&
            SelectedProject is { Tracked: true, BindingId: { } id } &&
            _orchestrator.BackupsFor(id).Count > 0;

    private bool CanRestoreBackup() => _orchestrator is not null && SelectedHasBackups;

    /// <summary>
    /// Put a previous copy of the selected project back.
    /// </summary>
    /// <remarks>
    /// The orchestrator does the dangerous part and its own guarding: it refuses while the IDE
    /// holds the project, and it copies the current state aside before overwriting it. What is
    /// here is the choosing and the reporting.
    /// </remarks>
    private async Task RestoreBackupAsync()
    {
        if (_orchestrator is null ||
            SelectedProject is not { Tracked: true, BindingId: { } id } row)
        {
            return;
        }

        using var scope = _logger.BeginOperation(LoggerExtensions.NewOperationId());
        _logger.Click("Sync.RestoreBackup");

        var available = _orchestrator.BackupsFor(id);
        if (available.Count == 0)
        {
            // The folder emptied between the button lighting up and the press — a prune, or
            // somebody clearing .versio/local by hand. Said rather than opening an empty list.
            Summary = $"{row.Name} has no saved copies to restore.";
            RefreshBackupAvailability();
            return;
        }

        var chosen = Prompts.AskWhichBackup(row.Name, available);
        if (chosen is null)
        {
            _logger.Info(LogCategory.Sync, $"{id}: restore cancelled; the project is unchanged");
            return;
        }

        using var busy = _busy.Begin(WorkKind.Import, $"Restoring {row.Name}…");

        var outcome = await _orchestrator
            .RestoreBackupAsync(id, chosen.Path, busy.Token)
            .ConfigureAwait(true);

        Summary = SyncNarrative.Describe(outcome);

        if (outcome.IsFailure || outcome.Verdict is SyncVerdict.BlockedByOpenProject)
        {
            RememberIfWaitingOnIde(outcome);
            _logger.Warn(LogCategory.Sync, Summary);
        }
        else
        {
            _logger.Info(LogCategory.Sync, Summary);
        }

        RefreshBackupAvailability();
        await RescanAsync().ConfigureAwait(true);
        await RefreshStalenessAsync().ConfigureAwait(true);
    }

    private bool CanRetry() => _orchestrator is not null && FailedProject is not null;

    /// <summary>
    /// Acknowledge the selected project's failure and sync it again.
    /// </summary>
    /// <remarks>
    /// Acknowledging and retrying are one action here, because they are one intention. Making
    /// them two buttons would mean a person clearing a failure and then wondering why nothing
    /// happened - the acknowledgement exists so that automation does not retry a failing
    /// project forever, not so that it takes two clicks to ask for one retry.
    /// </remarks>
    private async Task RetryAsync()
    {
        if (_orchestrator is null || FailedProject is not { } row)
        {
            return;
        }

        _logger.Click("Sync.Retry");
        _logger.Info(LogCategory.Sync, $"{row.Name}: failure acknowledged; retrying");

        using var busy = _busy.Begin(WorkKind.Export, $"Retrying {row.Name}…");

        await _orchestrator.AcknowledgeFailureAsync(row.BindingId!, busy.Token)
            .ConfigureAwait(true);

        // The acknowledgement only clears the gate. Without this the project would sit in
        // exactly the state it was in before, and the button would look as though it had
        // done nothing.
        var outcomes = await SatisfyGateAsync(busy.Token).ConfigureAwait(true);

        var failure = outcomes.FirstOrDefault(o => o.IsFailure);
        Summary = failure is null ? $"{row.Name} synced." : $"Still failing: {failure.Explanation}";

        RefreshFailure(row.Path);
    }

    private void RaiseCommandStates()
    {
        RetryCommand.RaiseCanExecuteChanged();
        ConfirmImportCommand.RaiseCanExecuteChanged();
        RestoreBackupCommand.RaiseCanExecuteChanged();
    }

    private void Report(Exception ex)
    {
        _logger.Error(LogCategory.Sync, ex.Message, ex);
        Summary = ex.Message;
    }
}
