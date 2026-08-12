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

    public SyncViewModel(VersioLogger logger, IClock clock)
    {
        _logger = logger;
        _clock = clock;
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
    }

    /// <summary>Clear state when the repository closes, so a stale scan is never shown.</summary>
    public void Detach()
    {
        _repositoryRoot = null;
        _git = null;
        _orchestrator = null;
        _config = new RepoConfig();
        Projects.Clear();
        Findings.Clear();
        SelectedProject = null;
        Summary = "No repository open";
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
        var installs = new CodesysLocator().FindInstalls();
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
            ? new LocalCodesysRunner(_install.ExecutablePath, new ScriptExtractor(),
                                     m => _logger.Debug(LogCategory.Codesys, m))
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
            new JsonLockStore(_repositoryRoot),
            progress: progress);

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
        _logger.Info(LogCategory.Sync, "scanning for PLC projects");

        var scanner = new RepoScanner(_repositoryRoot, _format);
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

        var setup = new RepoSetup(_repositoryRoot, _format);
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
        var previous = row.State;
        row.State = "exporting…";
        RaiseCommandStates();

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
            Summary = sentence;
            LockState = SyncNarrative.Describe(outcome.State);
            row.State = outcome.Verdict == SyncVerdict.Completed
                ? SyncNarrative.Label(outcome.State)
                : previous;

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

            // The export wrote into the working tree; whatever the verdict, what git sees
            // has probably changed and the row state was computed from a stale scan.
            await RescanAsync().ConfigureAwait(true);
        }
        finally
        {
            row.Busy = false;
            Activity = string.Empty;
            RaiseCommandStates();
        }
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
    }

    private void Report(Exception ex)
    {
        _logger.Error(LogCategory.Sync, ex.Message, ex);
        Summary = ex.Message;
    }
}
