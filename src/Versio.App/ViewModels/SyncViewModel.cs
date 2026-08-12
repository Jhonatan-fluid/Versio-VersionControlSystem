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
/// Two actions survived, and both survived for a reason rather than by omission.
/// <see cref="ExportStaleCommand"/> exists because a project changed while Versio was closed
/// was never seen by the watcher, and without it the only way out of "not exported" is to
/// commit. <see cref="AcknowledgeCommand"/> exists because a failed binding syncs no further
/// until somebody says they have seen the failure — that is the design, and a state nothing
/// can clear is a dead end.
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

        ExportStaleCommand = new AsyncCommand(ExportStaleAsync, CanExportStale, onError: Report);
        AcknowledgeCommand = new AsyncCommand(AcknowledgeAsync, CanAcknowledge, onError: Report);
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
    /// Export every project whose mirror is behind, without waiting for a commit.
    /// </summary>
    /// <remarks>
    /// The one export button left. A project edited while Versio was closed was never seen by
    /// the watcher, so it sits stale until something asks for it; without this the only way
    /// out of that state is to start a commit, which is a strange thing to have to do to
    /// refresh a mirror.
    /// </remarks>
    public AsyncCommand ExportStaleCommand { get; }

    /// <summary>Clear every failed binding so those projects can sync again.</summary>
    public AsyncCommand AcknowledgeCommand { get; }

    /// <summary>Display name of the toolchain this repository targets.</summary>
    public string VendorName => _format.DisplayName;

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

        await ProbeToolchainAsync(ct).ConfigureAwait(true);
        await BuildOrchestratorAsync(ct).ConfigureAwait(true);
        await RescanAsync().ConfigureAwait(true);

        StartWatching();
        await RefreshStalenessAsync().ConfigureAwait(true);
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
            logger: _logger);

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
                State: Projects.FirstOrDefault(p => p.BindingId == b.Id)?.State ?? "tracked"))
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
                    Coverage: coverage);
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

        TreeSummary = (objects, changed) switch
        {
            (0, 0) when sources.Count == 0 => "No PLC projects tracked in this repository.",
            (0, 0) => "Nothing changed, and no objects exported yet.",
            (0, var c) => $"{c} changed file(s).",
            (var o, 0) => $"{o} object(s), nothing changed.",
            var (o, c) => $"{o} object(s) · {c} changed file(s).",
        };

        _logger.Debug(LogCategory.Sync,
            $"tree: {Tree.Count} root(s), {objects} object(s), {changed} changed file(s)");
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

        _logger.Info(LogCategory.Sync, "commit gate: exporting stale bindings before the commit");

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
    private bool CanExportStale() => _orchestrator is not null && UnexportedCount > 0;

    /// <summary>
    /// Export every stale binding now.
    /// </summary>
    /// <remarks>
    /// The same call the commit gate makes, deliberately: two ways of deciding what is stale
    /// would eventually disagree, and the moment they did would be a commit that recorded a
    /// project without the mirror it exports to. The jobs run synchronously so they bypass the
    /// coalescer's queue - somebody who presses this means now - but not the circuit breaker,
    /// the hash gate or the direction lock, which protect work rather than pace it.
    /// </remarks>
    private async Task ExportStaleAsync()
    {
        _logger.Click("Sync.ExportStale");

        var outcomes = await SatisfyGateAsync(CancellationToken.None).ConfigureAwait(true);
        if (outcomes.Count == 0)
        {
            Summary = "Nothing was stale.";
            return;
        }

        var failure = outcomes.FirstOrDefault(o => o.IsFailure);
        Summary = failure is null
            ? SyncNarrative.Describe(outcomes[^1])
            : $"Export failed: {failure.Explanation}";
    }

    private bool CanAcknowledge() =>
        _orchestrator is not null &&
        Projects.Any(p => p.Tracked &&
                          _orchestrator.StateOf(p.BindingId!) == SyncDirection.Failed);

    /// <summary>
    /// Clear every failed binding.
    /// </summary>
    /// <remarks>
    /// Repo-wide rather than per-row, because there is no longer a row to press it against -
    /// and because a failure nothing can clear is a dead end: a failed binding syncs no
    /// further until somebody acknowledges it, by design.
    /// </remarks>
    private async Task AcknowledgeAsync()
    {
        if (_orchestrator is null)
        {
            return;
        }

        _logger.Click("Sync.Acknowledge");

        var failed = Projects
            .Where(p => p.Tracked && _orchestrator.StateOf(p.BindingId!) == SyncDirection.Failed)
            .ToList();

        foreach (var row in failed)
        {
            await _orchestrator.AcknowledgeFailureAsync(row.BindingId!, CancellationToken.None)
                .ConfigureAwait(true);
            _logger.Info(LogCategory.Sync, $"{row.Name}: failure acknowledged");
        }

        LockState = SelectedProject is { Tracked: true } selected
            ? SyncNarrative.Describe(_orchestrator.StateOf(selected.BindingId!))
            : string.Empty;

        await RescanAsync().ConfigureAwait(true);
        await RefreshStalenessAsync().ConfigureAwait(true);
    }

    private void RaiseCommandStates()
    {
        ExportStaleCommand.RaiseCanExecuteChanged();
        AcknowledgeCommand.RaiseCanExecuteChanged();
    }

    private void Report(Exception ex)
    {
        _logger.Error(LogCategory.Sync, ex.Message, ex);
        Summary = ex.Message;
    }
}
