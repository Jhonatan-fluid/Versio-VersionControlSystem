using System.Collections.ObjectModel;
using System.IO;
using Versio.App.Mvvm;
using Versio.Core.Abstractions;
using Versio.Core.Bindings;
using Versio.Core.Git;
using Versio.Core.Locking;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Core.Projects;
using Versio.Core.Sync;
using Versio.Infrastructure.Bindings;
using Versio.Infrastructure.FileSystem;
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
public sealed class ProjectRow(string path, string? bindingId) : ObservableObject
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
        (false, _) => "Brush.Status.Modified",
        (_, "failed") => "Brush.Status.Deleted",
        (_, "conflict") => "Brush.Status.Conflict",
        (_, "in sync") => "Brush.Status.Added",
        _ => "Brush.Status.Renamed",
    };
}

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
    /// <summary>
    /// The format for the repository's vendor.
    /// </summary>
    /// <remarks>
    /// Resolved from the registry rather than named. The first registered format is the
    /// default until a repository declares which vendor it holds — which is the next step
    /// here, since a binding already records its own vendor.
    /// </remarks>
    private IPlcProjectFormat _format = PlcFormats.All[0];
    private readonly OrphanScanner _orphanScanner = new();
    private readonly MachineIdentity _machine = new();

    private string? _repositoryRoot;
    private IGitClient? _git;
    private SyncOrchestrator? _orchestrator;
    private RepoConfig _config = new();
    private IPlcToolchain? _toolchain;
    private PlcToolchainStatus? _status;

    /// <summary>Which vendor this repository's projects belong to. One today; per-binding
    /// once a repository can hold more than one brand.</summary>
    private PlcVendor _vendor = PlcFormats.All[0].Vendor;
    private string? _profile;

    private string _toolchainState = "Not checked";
    private bool _toolchainAvailable;
    private string _summary = "No repository open";
    private string _lockState = string.Empty;
    private string _activity = string.Empty;
    private ProjectRow? _selectedProject;
    private FileChangeWatcher? _watcher;

    /// <summary>Captured at construction, which happens on the UI thread.</summary>
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    private string _stalenessSummary = string.Empty;
    private int _unexportedCount;
    private string _treeSearch = string.Empty;
    private string _treeSummary = string.Empty;
    private string _coverageSummary = string.Empty;

    public SyncViewModel(VersioLogger logger, IClock clock, BusyTracker? busy = null)
    {
        _logger = logger;
        _clock = clock;
        _busy = busy ?? new BusyTracker();
        _heartbeat = new HeartbeatService(clock, logger);

        // The beat fires on a timer thread. Assigning a string property from there is safe —
        // WPF marshals property notifications — but the collections are not, which is why
        // nothing else is touched here.
        _heartbeat.Beat += op => Activity = op.Describe(clock);

        RetryCommand = new AsyncCommand(RetryAsync, CanRetry, onError: Report);
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

    public ObservableCollection<string> Findings { get; } = [];

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
    /// With <c>On git action</c> as the default sync mode there is deliberately no
    /// export-on-save, so a project can sit unexported all afternoon. Counting it is what
    /// stops the panel from implying everything is in sync.
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

    /// <summary>Whether the once-per-repository catch-up export has already run.</summary>
    private bool _caughtUp;

    public IReadOnlyList<string> ExportPaths =>
        [.. _config.Bindings.Where(b => b.Enabled).Select(b => b.ExportPath)];

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

        // Armed for this repository. Set before any of the loading below, so a second
        // AttachAsync - reopening the same repository, say - gets a fresh sweep.
        _caughtUp = false;

        await ProbeToolchainAsync(ct).ConfigureAwait(true);
        await BuildOrchestratorAsync(ct).ConfigureAwait(true);
        await RescanAsync().ConfigureAwait(true);

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

        using var busy = _busy.Begin(behind == 1
            ? "Exporting 1 project that changed while Versio was closed…"
            : $"Exporting {behind} projects that changed while Versio was closed…");

        var outcomes = await SatisfyGateAsync(ct).ConfigureAwait(true);

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

    /// <summary>Clear state when the repository closes, so a stale scan is never shown.</summary>
    public void Detach()
    {
        // Before anything else: a FileSystemWatcher holds a handle on the directory, and
        // leaking one keeps the folder locked against rename or delete for the life of the
        // process - on a repository somebody is trying to move, a visible bug.
        StopWatching();

        _repositoryRoot = null;
        _git = null;
        _orchestrator = null;
        _caughtUp = false;
        _staleBindings.Clear();
        _config = new RepoConfig();
        Projects.Clear();
        Findings.Clear();
        Tree.Clear();

        // The expanded set is keyed by repo-relative path, and two repositories share those
        // freely - "docs" is open in both. Carrying it across would open folders in the new
        // repository because of a click in the old one.
        _expansion.Clear();
        _treeSignature = string.Empty;
        _coverage.Clear();
        CoverageSummary = string.Empty;

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
    /// Find the toolchain and settle on a profile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asks the toolchain, whichever vendor's it is. This used to construct a
    /// <c>CodesysLocator</c>, call <c>ProfileResolver</c>, read a <c>CodesysInstall</c> and
    /// build a <c>LocalCodesysRunner</c> — five brand names in a view model with no business
    /// knowing any of them.
    /// </para>
    /// <para>
    /// The profile is opaque here. It is not optional and it cannot be guessed — CODESYS
    /// refuses to start headless without one, and the name does not follow from the install
    /// folder — but that is the toolchain's problem now, and a different vendor will have a
    /// different one.
    /// </para>
    /// </remarks>
    private async Task ProbeToolchainAsync(CancellationToken ct)
    {
        _toolchain = PlcToolchains.For(_vendor, _logger);

        if (_toolchain is null)
        {
            // Recognised but not drivable: a project Versio can see and cannot run. A state to
            // report, not an error - the repository is still a perfectly good git repository.
            _status = null;
            _profile = null;
            ToolchainAvailable = false;
            ToolchainState = $"{_vendor} projects can be tracked but not exported yet";
            _logger.Warn(LogCategory.Plc, ToolchainState);
            return;
        }

        _status = await _toolchain.ProbeAsync(ct).ConfigureAwait(true);
        _profile = _status.Profile;
        ToolchainAvailable = _status.Available;
        ToolchainState = _status.Explanation ?? (_status.Available ? "ready" : "unavailable");
    }

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

        var store = new RepoConfigStore(_repositoryRoot);
        _config = await store.LoadAsync(ct).ConfigureAwait(true);

        if (_config.Bindings.Count == 0)
        {
            _orchestrator = null;
            return;
        }

        // The toolchain builds its own runner, and always returns one: an unavailable
        // toolchain yields a runner that fails every job with the reason, so the locks, their
        // persistence and their recovery keep working on a machine that cannot export.
        var runner = _toolchain?.CreateRunner(_status ?? new PlcToolchainStatus
        {
            Available = false,
            Explanation = ToolchainState,
        }) ?? new UnavailableToolchainRunner(ToolchainState);

        // Constructed on the UI thread, so Progress<T> captures this context and every
        // report lands back here without an explicit dispatch.
        var progress = new Progress<PlcProgress>(p =>
            _heartbeat.Report(p.OperationId, p.Done, p.Total, p.Detail ?? p.Phase));

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
            runner,
            new JsonLockStore(_repositoryRoot, _logger),
            progress: progress,
            logger: _logger,

            // Lets the engine find out that CODESYS has the project open before it spends
            // 17-31 s being told so - and, for an export, work around it by reading a copy.
            occupancy: new ProjectOccupancyProbe(
                new WindowsFileSystem(), _machine, new ProcessProbe()));

        var decisions = await _orchestrator
            .RecoverAsync(new LockRecoveryPolicy(_machine, new ProcessProbe()), ct)
            .ConfigureAwait(true);

        foreach (var decision in decisions)
        {
            _logger.Info(LogCategory.Lock, $"{decision.BindingId}: {decision.Explanation}");
        }
    }

    /// <summary>Scan the repository for PLC projects and classify what was found.</summary>
    private async Task RescanAsync()
    {
        if (_repositoryRoot is null)
        {
            return;
        }

        using var scope = _logger.BeginOperation(Guid.NewGuid().ToString("N")[..8]);
        using var busy = _busy.Begin("Scanning for PLC projects…");
        _logger.Info(LogCategory.Sync, "scanning for PLC projects");

        var scanner = new RepoScanner(_repositoryRoot, PlcFormats.All, _logger);
        var snapshot = await scanner.ScanAsync(_git, CancellationToken.None).ConfigureAwait(true);

        // Detection and tracking are one step now: anything found is tracked, and the locks
        // take it from there.
        if (await AutoAdoptAsync(snapshot, CancellationToken.None).ConfigureAwait(true) > 0)
        {
            await BuildOrchestratorAsync(CancellationToken.None).ConfigureAwait(true);
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
            if (_format.IsTemplate(project))
            {
                continue;
            }

            var binding = _config.Bindings.FirstOrDefault(b =>
                string.Equals(b.ProjectPath, project, StringComparison.OrdinalIgnoreCase));

            var row = new ProjectRow(project, binding?.Id);
            row.State = DescribeRow(binding, snapshot);
            Projects.Add(row);
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

        Summary = Projects.Count switch
        {
            0 => "No PLC projects found in this repository.",
            1 => Projects[0].Tracked
                ? "1 project, tracked."
                : "1 project found. It is not tracked yet.",
            _ => $"{Projects.Count} projects, {Projects.Count(p => p.Tracked)} tracked.",
        };

        RaiseCommandStates();
        await RefreshTreeAsync().ConfigureAwait(true);

        _logger.Info(LogCategory.Sync,
            $"scan: {Projects.Count} project(s), {Findings.Count} finding(s)");
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
    /// <b>This deliberately does not export anything.</b> The default sync mode is
    /// <c>On git action</c>, decided by measurement: a CODESYS cold start is 17-31 s, so
    /// exporting after every settled save would run the toolchain most of the working day for
    /// changes nobody looks at until they commit. What the watcher buys is <i>awareness</i> -
    /// the panel can say "2 projects not yet exported" instead of implying everything is in
    /// sync, which the plan calls out explicitly as the thing the status bar must not do.
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

        _watcher = new FileChangeWatcher(_clock, logger: _logger);
        _watcher.Settled += OnFileSettled;

        foreach (var binding in _config.Bindings.Where(b => b.Enabled))
        {
            _watcher.WatchFile(AbsolutePath(binding.ProjectPath));

            // The export tree may not exist yet on a freshly adopted project; the watcher
            // says so and carries on, and the first export creates it.
            _watcher.WatchTree(AbsolutePath(binding.ExportPath));
        }

        _logger.Info(LogCategory.Watch,
            $"watching {_config.Bindings.Count(b => b.Enabled)} tracked project(s) for changes");
    }

    private void StopWatching()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.Settled -= OnFileSettled;
        _watcher.Dispose();
        _watcher = null;
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
        _logger.Debug(LogCategory.Watch,
            $"settled: {change.Path} (tier {change.Tier})");

        OnUi(() => _ = SyncSettledChangeAsync(change));
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

    /// <summary>Run on the UI thread, whichever thread we are called from.</summary>
    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
            return;
        }

        _ui.Post(_ => action(), null);
    }

    /// <summary>
    /// The whole working tree: every tracked project with its objects, and every other
    /// changed file beside them.
    /// </summary>
    public ObservableCollection<TreeRow> Tree { get; } = [];

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
            if (!SetProperty(ref _selectedNode, value) || value is null)
            {
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
        if (_repositoryRoot is null)
        {
            Tree.Clear();
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
                                || _staleBindings.Contains(b.Id)))
            .ToList();

        // Everything the tree is derived from, in one string. A manifest's write time stands
        // in for its contents: it is written by an export and by nothing else, so a changed
        // timestamp means a changed export, and hashing eleven files on every keystroke to
        // learn the same thing would be a poor trade.
        var signature = string.Join('',
        [
            search,
            .. sources.Select(x =>
                $"{x.ProjectPath}|{x.ExportPath}|{x.State}|{ManifestStamp(x.ManifestPath)}"),
            .. status.Changes
                .OrderBy(c => c.Path, StringComparer.Ordinal)
                .Select(c => $"{c.Path}|{c.Effective}|{c.IsStaged}"),
        ]);

        if (signature == _treeSignature && Tree.Count > 0)
        {
            return;
        }

        _treeSignature = signature;

        var built = await Task.Run(() =>
        {
            var coverage = new Dictionary<string, CoverageReport>(StringComparer.OrdinalIgnoreCase);
            var projects = new List<WorkspaceProject>();

            foreach (var source in sources)
            {
                var manifest = File.Exists(source.ManifestPath)
                    ? ProjectManifest.Parse(File.ReadAllText(source.ManifestPath))
                    : null;

                var report = ExportCoverage.Of(manifest);
                coverage[source.ProjectPath] = report;

                projects.Add(new WorkspaceProject
                {
                    ProjectPath = source.ProjectPath,
                    ExportPath = source.ExportPath,
                    Manifest = manifest,

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
                });
            }

            return (Roots: WorkspaceTreeBuilder.Build(projects, status.Changes,
                        new ProjectTreeOptions { ChangedOnly = false, Search = search }),
                    Coverage: coverage,
                    Tracked: projects.Count);
        }).ConfigureAwait(true);

        var roots = built.Roots;

        _coverage.Clear();
        foreach (var (path, report) in built.Coverage)
        {
            _coverage[path] = report;
        }

        CoverageSummary = built.Coverage.Values.FirstOrDefault(c => c.HasGaps)?.Describe()
                          ?? string.Empty;

        Tree.Clear();
        foreach (var node in roots)
        {
            Tree.Add(new TreeRow(node, 0, _expansion));
        }

        var objects = roots.Sum(CountObjects);
        var changed = status.Changes.Count;
        var shownProjects = roots.Count(r => r.Kind == ProjectNodeKind.Project);
        var hidden = built.Tracked - shownProjects;

        // An empty panel has to say why it is empty. A tracked project that is simply in sync
        // is now hidden, and a panel that went blank without explaining itself reads as a
        // broken scan - which is the failure this wording exists to prevent.
        TreeSummary = (Tree.Count, hidden) switch
        {
            (0, 0) when built.Tracked == 0 => "No PLC projects tracked in this repository.",
            (0, 1) => "Nothing has changed. 1 tracked project is in sync, so it is not listed.",
            (0, var h) when h > 1 =>
                $"Nothing has changed. {h} tracked projects are in sync, so they are not listed.",
            (0, _) => "Nothing has changed.",
            (_, 0) => $"{objects} object(s) · {changed} changed file(s).",
            var (_, h) => $"{objects} object(s) · {changed} changed file(s) · " +
                          $"{h} project(s) in sync, not listed.",
        };

        _logger.Debug(LogCategory.Sync,
            $"tree: {Tree.Count} root(s), {objects} object(s), {changed} changed file(s), " +
            $"{hidden} project(s) hidden as in sync");
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

    /// <summary>What a project's export covers, or null when it is not a tracked project.</summary>
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
        _orchestrator is null
            ? Task.FromResult(new CommitGateResult
            {
                Decision = GateDecision.Proceed,
                Explanation = "no tracked PLC projects in this repository",
            })
            : _orchestrator.EvaluateCommitGateAsync(ct);

    /// <summary>
    /// Export every stale binding, so the commit records the project and its mirror together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the primary path by which the export tree is written at all, not a safety net.
    /// With <c>On git action</c> as the default sync mode there is no export on save, so the
    /// commit is where the mirror catches up - which is why it runs rather than merely
    /// refusing, and why it is worth the 17-31 s it costs.
    /// </para>
    /// <para>
    /// Each job is synchronous, so it bypasses the coalescer and runs now rather than queueing
    /// behind a background sync the user cannot see. The circuit breaker, the hash gate and
    /// the direction lock still apply.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<SyncOutcome>> SatisfyGateAsync(CancellationToken ct)
    {
        if (_orchestrator is null)
        {
            return [];
        }

        var operationId = Guid.NewGuid().ToString("N")[..8];
        using var scope = _logger.BeginOperation(operationId);
        using var busy = _busy.Begin("Exporting before commit…");
        using var beat = _heartbeat.Track(operationId, "commit gate export");

        // Says what it is doing, not why the caller asked. This same path now serves the
        // commit gate, the catch-up on open and a retry, and a line claiming a commit is
        // about to happen was wrong for two of the three - in a log that is read precisely
        // when somebody is trying to work out what the app did on its own.
        _logger.Info(LogCategory.Sync, "exporting every project whose mirror is behind");

        var outcomes = await _orchestrator.SatisfyCommitGateAsync(ct).ConfigureAwait(true);

        foreach (var outcome in outcomes)
        {
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
    private async Task<int> AutoAdoptAsync(RepoSnapshot snapshot, CancellationToken ct)
    {
        if (_repositoryRoot is null)
        {
            return 0;
        }

        var setup = new RepoSetup(_repositoryRoot, _format);
        var adopted = 0;

        foreach (var project in snapshot.ProjectFiles.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (_format.IsTemplate(project) ||
                _config.Bindings.Any(b =>
                    string.Equals(b.ProjectPath, project, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var result = await setup.AdoptAsync(project, _git, _profile, ct).ConfigureAwait(true);
            if (result.Adopted)
            {
                _config = result.Config;
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
    private async Task SyncSettledChangeAsync(WatchedChange change)
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

        var outcome = await _orchestrator.HandleChangeAsync(new SyncJob
        {
            BindingId = binding.Id,
            Source = source,

            // Confirmed, because there is nobody to ask. The project is copied aside before
            // an import writes into it, which is what makes that answer defensible.
            Confirmed = true,
            Reason = $"automatic, {source}",
        }, CancellationToken.None).ConfigureAwait(true);

        var sentence = SyncNarrative.Describe(outcome);

        if (outcome.IsFailure)
        {
            _logger.Error(LogCategory.Sync, sentence);
        }
        else if (outcome.DidWork)
        {
            _logger.Info(LogCategory.Sync, sentence);
        }
        else
        {
            // Coalesced, throttled, unchanged, or blocked by the lock. All routine.
            _logger.Debug(LogCategory.Sync, sentence);
        }

        if (outcome.DidWork || outcome.IsFailure)
        {
            Summary = sentence;
            LockState = SyncNarrative.Describe(outcome.State);
            await RescanAsync().ConfigureAwait(true);
            await RefreshStalenessAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Which binding a changed path belongs to, and which side of it.</summary>
    /// <remarks>
    /// The project file is matched exactly and the export tree by prefix, in that order. A
    /// project sitting beside its own export directory - <c>plc/A.project</c> next to
    /// <c>plc/A</c> - would match a careless prefix test both ways, and exporting because of
    /// an export is the loop the whole design exists to avoid.
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

        return (null, ChangeSource.Project);
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

        using var busy = _busy.Begin($"Retrying {row.Name}…");

        await _orchestrator.AcknowledgeFailureAsync(row.BindingId!, CancellationToken.None)
            .ConfigureAwait(true);

        // The acknowledgement only clears the gate. Without this the project would sit in
        // exactly the state it was in before, and the button would look as though it had
        // done nothing.
        var outcomes = await SatisfyGateAsync(CancellationToken.None).ConfigureAwait(true);

        var failure = outcomes.FirstOrDefault(o => o.IsFailure);
        Summary = failure is null ? $"{row.Name} synced." : $"Still failing: {failure.Explanation}";

        RefreshFailure(row.Path);
    }

    private void RaiseCommandStates() => RetryCommand.RaiseCanExecuteChanged();

    private void Report(Exception ex)
    {
        _logger.Error(LogCategory.Sync, ex.Message, ex);
        Summary = ex.Message;
    }
}
