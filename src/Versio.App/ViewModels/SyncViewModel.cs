using System.Collections.ObjectModel;
using Versio.App.Mvvm;
using Versio.Core.Abstractions;
using Versio.Core.Bindings;
using Versio.Core.Locking;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Core.Plc.Codesys;
using Versio.Core.Sync;
using Versio.Infrastructure.Bindings;
using Versio.Infrastructure.FileSystem;
using Versio.Infrastructure.Platform;
using Versio.Infrastructure.Plc.Codesys;
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
/// <b>Adoption and export are separate actions on purpose.</b> Adopting writes two text
/// files and is instantly reversible. The first export launches CODESYS for the better part
/// of half a minute and writes a directory tree — doing that unprompted, on a file that may
/// be a vendor sample or a scratch copy, is how a tool earns a reputation for surprising
/// people (plan §5.3).
/// </para>
/// </remarks>
public sealed class SyncViewModel : ObservableObject
{
    private readonly VersioLogger _logger;
    private readonly IClock _clock;
    private readonly HeartbeatService _heartbeat;

    /// <summary>Shared with the shell, so one progress bar covers every kind of work.</summary>
    private readonly BusyTracker _busy;
    private readonly IPlcProjectFormat _format = CodesysProjectFormat.Instance;
    private readonly OrphanScanner _orphanScanner = new();
    private readonly MachineIdentity _machine = new();

    private string? _repositoryRoot;
    private IGitClient? _git;
    private SyncOrchestrator? _orchestrator;
    private RepoConfig _config = new();
    private CodesysInstall? _install;
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
    private string? _pendingImportBindingId;
    private string _importPrompt = string.Empty;

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

        RescanCommand = new AsyncCommand(RescanAsync, () => _repositoryRoot is not null,
                                         onError: Report);
        AdoptCommand = new AsyncCommand(AdoptAsync, CanAdopt, onError: Report);
        ExportCommand = new AsyncCommand(ExportAsync, CanExport, onError: Report);
        AcknowledgeCommand = new AsyncCommand(AcknowledgeAsync, CanAcknowledge, onError: Report);
        ImportCommand = new AsyncCommand(ProposeImportAsync, CanImport, onError: Report);
        ConfirmImportCommand = new AsyncCommand(ConfirmImportAsync, () => ImportPending,
                                                onError: Report);
        CancelImportCommand = new RelayCommand(ClearPendingImport, () => ImportPending);
    }

    public ObservableCollection<ProjectRow> Projects { get; } = [];

    public ObservableCollection<string> Findings { get; } = [];

    public AsyncCommand RescanCommand { get; }

    /// <summary>Write a binding for the selected project. Cheap and reversible; no export.</summary>
    public AsyncCommand AdoptCommand { get; }

    /// <summary>Run an export now, bypassing the coalescer but not the lock.</summary>
    public AsyncCommand ExportCommand { get; }

    /// <summary>Clear a failed state so the project can sync again.</summary>
    public AsyncCommand AcknowledgeCommand { get; }

    /// <summary>Ask what an import would do. Writes nothing.</summary>
    public AsyncCommand ImportCommand { get; }

    /// <summary>Apply the import that was proposed.</summary>
    public AsyncCommand ConfirmImportCommand { get; }

    /// <summary>Walk away from a proposed import.</summary>
    public RelayCommand CancelImportCommand { get; }

    /// <summary>Files the import would write into the project. Empty unless one is pending.</summary>
    public ObservableCollection<string> ImportPreview { get; } = [];

    /// <summary>Whether an import has been proposed and is waiting on the user.</summary>
    public bool ImportPending => _pendingImportBindingId is not null;

    /// <summary>What the pending import would do, in one sentence. Empty when none is.</summary>
    public string ImportPrompt
    {
        get => _importPrompt;
        private set => SetProperty(ref _importPrompt, value);
    }

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
        private set => SetProperty(ref _unexportedCount, value);
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

    public ProjectRow? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!SetProperty(ref _selectedProject, value))
            {
                return;
            }

            LockState = value is { Tracked: true } && _orchestrator is not null
                ? SyncNarrative.Describe(_orchestrator.StateOf(value.BindingId!))
                : string.Empty;

            // A proposal belongs to the project it was made for. Leaving it up while the
            // selection moves is how somebody confirms an import against the wrong project
            // while reading a file list that describes a different one.
            ClearPendingImport();

            RaiseCommandStates();
        }
    }

    /// <summary>Called when the shell opens a repository.</summary>
    public async Task AttachAsync(string repositoryRoot, IGitClient git, CancellationToken ct)
    {
        _repositoryRoot = repositoryRoot;
        _git = git;

        ProbeToolchain();
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
        SelectedProject = null;
        Summary = "No repository open";
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
    /// The profile is not optional and cannot be guessed: CODESYS refuses to start headless
    /// without <c>--profile</c>, and the name does not follow from the install folder —
    /// <c>CODESYS 3.5.22.20</c> on disk is profile <c>CODESYS V3.5 SP22 Patch 2</c>. It has
    /// to be read from disk, and with several installed the user must choose rather than
    /// have Versio pick one and risk a silent project upgrade.
    /// </remarks>
    private void ProbeToolchain()
    {
        var installs = new CodesysLocator(logger: _logger).FindInstalls();
        var resolution = ProfileResolver.Resolve(installs);

        switch (resolution.Outcome)
        {
            case ProfileResolution.Resolved:
                _install = installs[0];
                _profile = resolution.Profile;
                ToolchainAvailable = true;
                ToolchainState = $"{installs[0].InstallFolder} — {resolution.Profile}";
                _logger.Info(LogCategory.Codesys, $"toolchain ready: {ToolchainState}");
                break;

            case ProfileResolution.AmbiguousNeedsUserChoice:
                _install = null;
                _profile = null;
                ToolchainAvailable = false;
                ToolchainState =
                    $"{resolution.Candidates.Count} profiles installed — pick the one this " +
                    "repository targets before exporting";
                break;

            default:
                _install = null;
                _profile = null;
                ToolchainAvailable = false;
                ToolchainState = resolution.Explanation;
                _logger.Warn(LogCategory.Codesys, resolution.Explanation);
                break;
        }
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

        ICodesysRunner runner = _install is not null && _profile is not null
            ? new LocalCodesysRunner(_install.ExecutablePath, new ScriptExtractor(_logger), _logger)
            : new UnavailableToolchainRunner(ToolchainState);

        // Constructed on the UI thread, so Progress<T> captures this context and every
        // report lands back here without an explicit dispatch.
        var progress = new Progress<CodesysProgress>(p =>
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

        var scanner = new RepoScanner(_repositoryRoot, _format, _logger);
        var snapshot = await scanner.ScanAsync(_git, CancellationToken.None).ConfigureAwait(true);

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

        // Restore the selection by path. A rescan after an adopt replaces every row, and
        // dropping the user back to nothing selected right when they are about to export is
        // a small thing that feels broken.
        SelectedProject = Projects.FirstOrDefault(p => p.Path == selectedPath);

        Summary = Projects.Count switch
        {
            0 => "No PLC projects found in this repository.",
            1 => Projects[0].Tracked
                ? "1 project, tracked."
                : "1 project found. It is not tracked yet.",
            _ => $"{Projects.Count} projects, {Projects.Count(p => p.Tracked)} tracked.",
        };

        RaiseCommandStates();

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

        OnUi(() => _ = RefreshStalenessAsync());
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

    private bool CanAdopt() => _repositoryRoot is not null && SelectedProject is { Tracked: false };

    /// <summary>Track the selected project: guard it, write the rules, write the binding.</summary>
    private async Task AdoptAsync()
    {
        if (_repositoryRoot is null || SelectedProject is not { Tracked: false } row)
        {
            return;
        }

        _logger.Click("Sync.Adopt");
        using var scope = _logger.BeginOperation(Guid.NewGuid().ToString("N")[..8]);

        var setup = new RepoSetup(_repositoryRoot, _format, _logger);
        var result = await setup.AdoptAsync(row.Path, _git, _profile, CancellationToken.None)
            .ConfigureAwait(true);

        if (!result.Adopted)
        {
            // A refusal is a decision with a reason, not an error. Say the reason where the
            // user is looking rather than only in the log.
            _logger.Warn(LogCategory.Sync,
                $"cannot track {row.Path}: {result.Decision.Explanation}");
            Summary = result.Decision.Explanation;
            return;
        }

        _logger.Info(LogCategory.Sync, $"tracking {row.Path} as {result.Decision.Binding!.Id}");

        if (result.GitAttributePatterns.Count > 0)
        {
            _logger.Info(LogCategory.Git,
                $".gitattributes: added {string.Join(", ", result.GitAttributePatterns)}" +
                (result.LfsConfigured ? " with LFS" : " without LFS (git-lfs not available)"));
        }

        await BuildOrchestratorAsync(CancellationToken.None).ConfigureAwait(true);
        await RescanAsync().ConfigureAwait(true);

        // The binding set changed, so the watch set has to change with it - otherwise the
        // project just adopted is the one project nobody is watching.
        StartWatching();
        await RefreshStalenessAsync().ConfigureAwait(true);

        Summary = ToolchainAvailable
            ? $"{row.Name} is now tracked. Export it to create the reviewable mirror."
            : $"{row.Name} is now tracked, but {VendorName} is not available to export it.";
    }

    private bool CanExport() =>
        _orchestrator is not null && SelectedProject is { Tracked: true, Busy: false };

    /// <summary>
    /// Export the selected project now.
    /// </summary>
    /// <remarks>
    /// Runs as a <see cref="SyncJob.Synchronous"/> job, which is what bypasses the coalescer
    /// and the 30-second throttle: somebody who presses <i>Export now</i> means now, and
    /// "queued behind the running sync" is not an answer to a button press. It does
    /// <b>not</b> bypass the circuit breaker, the hash gate or the direction lock — those
    /// protect the user's work, and a manual trigger is not a reason to trust the request
    /// more than the state machine.
    /// </remarks>
    private async Task ExportAsync()
    {
        if (_orchestrator is null || SelectedProject is not { Tracked: true } row)
        {
            return;
        }

        _logger.Click("Sync.Export");
        var operationId = Guid.NewGuid().ToString("N")[..8];
        using var scope = _logger.BeginOperation(operationId);

        row.Busy = true;
        row.State = "exporting…";
        RaiseCommandStates();

        using var busy = _busy.Begin($"Exporting {row.Name} with {VendorName}…");
        using var beat = _heartbeat.Track(operationId, $"exporting {row.Name}");

        try
        {
            var outcome = await _orchestrator.HandleChangeAsync(new SyncJob
            {
                BindingId = row.BindingId!,
                Source = ChangeSource.Project,
                Synchronous = true,
                Reason = "manual export",
            }, CancellationToken.None).ConfigureAwait(true);

            var sentence = SyncNarrative.Describe(outcome);

            if (outcome.IsFailure)
            {
                _logger.Error(LogCategory.Codesys, sentence);
            }
            else
            {
                _logger.Info(LogCategory.Sync, sentence);
            }

            foreach (var warning in outcome.Warnings)
            {
                _logger.Warn(LogCategory.Codesys, warning);
            }

            // The export wrote into the working tree, so every row was computed from a scan
            // that is now stale. Rescan first, then say what happened — the other way round
            // and the rescan's own summary would immediately overwrite the outcome the user
            // is waiting to read.
            row.Busy = false;
            await RescanAsync().ConfigureAwait(true);
            await RefreshStalenessAsync().ConfigureAwait(true);

            Summary = sentence;
            LockState = SyncNarrative.Describe(outcome.State);
        }
        finally
        {
            row.Busy = false;
            Activity = string.Empty;
            RaiseCommandStates();
        }
    }

    private bool CanImport() =>
        _orchestrator is not null && !ImportPending &&
        SelectedProject is { Tracked: true, Busy: false };

    /// <summary>
    /// Ask what an import would do, without doing any of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Import is the strictly more dangerous direction: an export writes text beside the
    /// project, while an import writes into the <c>.project</c> itself — the file the
    /// engineer cannot reconstruct by hand. So it is proposed and then confirmed, never
    /// applied on one press.
    /// </para>
    /// <para>
    /// <b>The confirmation is the orchestrator's own gate, not a second one bolted on here.</b>
    /// Running the job unconfirmed puts it through the circuit breaker, the hash gate and the
    /// direction lock exactly as the real thing would, and returns
    /// <see cref="SyncVerdict.AwaitingConfirmation"/> only when everything else has already
    /// agreed to proceed. A dialog written in the view model would have to duplicate all of
    /// that reasoning and would drift out of step with it.
    /// </para>
    /// </remarks>
    private async Task ProposeImportAsync()
    {
        if (_orchestrator is null || SelectedProject is not { Tracked: true } row)
        {
            return;
        }

        _logger.Click("Sync.Import");
        using var scope = _logger.BeginOperation(Guid.NewGuid().ToString("N")[..8]);

        var outcome = await _orchestrator.HandleChangeAsync(new SyncJob
        {
            BindingId = row.BindingId!,
            Source = ChangeSource.ExportedText,
            Confirmed = false,
            Synchronous = true,
            Reason = "manual import, proposed",
        }, CancellationToken.None).ConfigureAwait(true);

        if (outcome.Verdict != SyncVerdict.AwaitingConfirmation)
        {
            // Refused for some other reason - nothing changed, the lock is held the other
            // way, the breaker has tripped. Say which, and propose nothing.
            Summary = SyncNarrative.Describe(outcome);
            LockState = SyncNarrative.Describe(outcome.State);
            _logger.Info(LogCategory.Sync, Summary);
            return;
        }

        await BuildImportPreviewAsync(row).ConfigureAwait(true);

        _pendingImportBindingId = row.BindingId;
        OnPropertyChanged(nameof(ImportPending));

        ImportPrompt = ImportPreview.Count switch
        {
            0 => $"Import into {row.Name}. The exported text has changed; git reports no " +
                 "changed files, so review the tree before continuing.",
            1 => $"Import 1 changed file into {row.Name}. This writes into the project file " +
                 "itself and cannot be undone from Versio.",
            var n => $"Import {n} changed files into {row.Name}. This writes into the project " +
                     "file itself and cannot be undone from Versio.",
        };

        _logger.Info(LogCategory.Sync,
            $"import proposed for {row.Name}: {ImportPreview.Count} changed file(s)");
        RaiseCommandStates();
    }

    /// <summary>
    /// What the import would write, according to git.
    /// </summary>
    /// <remarks>
    /// Git rather than a diff of our own: the export tree is committed history, so git already
    /// knows precisely which of its files differ from the last commit, and it is the same
    /// answer the engineer sees in the changed-files panel. Deriving a second opinion here
    /// would give two lists that disagree at the worst possible moment.
    /// </remarks>
    private async Task BuildImportPreviewAsync(ProjectRow row)
    {
        ImportPreview.Clear();
        if (_git is null)
        {
            return;
        }

        var binding = _config.Bindings.FirstOrDefault(b => b.Id == row.BindingId);
        if (binding is null)
        {
            return;
        }

        var prefix = binding.ExportPath.Replace('\\', '/').TrimEnd('/') + "/";
        var status = await _git.StatusAsync(CancellationToken.None).ConfigureAwait(true);

        foreach (var change in status.Changes
                     .Where(c => c.Path.Replace('\\', '/')
                                  .StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            ImportPreview.Add(change.Path);
        }
    }

    /// <summary>Apply the import that was proposed, and only that one.</summary>
    private async Task ConfirmImportAsync()
    {
        if (_orchestrator is null || _pendingImportBindingId is not { } bindingId)
        {
            return;
        }

        var row = Projects.FirstOrDefault(p => p.BindingId == bindingId);
        if (row is null)
        {
            // The project moved out from under the proposal - a rescan, a rename, a branch
            // switch. Importing anyway would apply it to something the user never saw.
            ClearPendingImport();
            Summary = "That project is no longer listed; the import was not applied.";
            return;
        }

        _logger.Click("Sync.ConfirmImport");
        var operationId = Guid.NewGuid().ToString("N")[..8];
        using var scope = _logger.BeginOperation(operationId);

        row.Busy = true;
        row.State = "importing…";
        ClearPendingImport();
        RaiseCommandStates();

        using var busy = _busy.Begin($"Importing {row.Name} into the project…");
        using var beat = _heartbeat.Track(operationId, $"importing {row.Name}");

        try
        {
            var outcome = await _orchestrator.HandleChangeAsync(new SyncJob
            {
                BindingId = bindingId,
                Source = ChangeSource.ExportedText,
                Confirmed = true,
                Synchronous = true,
                Reason = "manual import, confirmed",
            }, CancellationToken.None).ConfigureAwait(true);

            var sentence = SyncNarrative.Describe(outcome);

            if (outcome.IsFailure)
            {
                _logger.Error(LogCategory.Codesys, sentence);
            }
            else
            {
                _logger.Info(LogCategory.Sync, sentence);
            }

            foreach (var warning in outcome.Warnings)
            {
                _logger.Warn(LogCategory.Codesys, warning);
            }

            // The import rewrote the .project, so git sees a different working tree. Rescan
            // before reporting, or the rescan's own summary overwrites the outcome.
            row.Busy = false;
            await RescanAsync().ConfigureAwait(true);
            await RefreshStalenessAsync().ConfigureAwait(true);

            Summary = sentence;
            LockState = SyncNarrative.Describe(outcome.State);
        }
        finally
        {
            row.Busy = false;
            Activity = string.Empty;
            RaiseCommandStates();
        }
    }

    private void ClearPendingImport()
    {
        if (_pendingImportBindingId is null)
        {
            return;
        }

        _pendingImportBindingId = null;
        ImportPreview.Clear();
        ImportPrompt = string.Empty;
        OnPropertyChanged(nameof(ImportPending));
    }

    private bool CanAcknowledge() =>
        _orchestrator is not null && SelectedProject is { Tracked: true } row &&
        _orchestrator.StateOf(row.BindingId!) == SyncDirection.Failed;

    private async Task AcknowledgeAsync()
    {
        if (_orchestrator is null || SelectedProject is not { Tracked: true } row)
        {
            return;
        }

        _logger.Click("Sync.Acknowledge");
        await _orchestrator.AcknowledgeFailureAsync(row.BindingId!, CancellationToken.None)
            .ConfigureAwait(true);

        _logger.Info(LogCategory.Sync, $"{row.Name}: failure acknowledged");
        LockState = SyncNarrative.Describe(_orchestrator.StateOf(row.BindingId!));
        await RescanAsync().ConfigureAwait(true);
    }

    private void RaiseCommandStates()
    {
        RescanCommand.RaiseCanExecuteChanged();
        AdoptCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        AcknowledgeCommand.RaiseCanExecuteChanged();
        ImportCommand.RaiseCanExecuteChanged();
        ConfirmImportCommand.RaiseCanExecuteChanged();
        CancelImportCommand.RaiseCanExecuteChanged();
    }

    private void Report(Exception ex)
    {
        _logger.Error(LogCategory.Sync, ex.Message, ex);
        Summary = ex.Message;
    }
}
