using System.Collections.ObjectModel;
using System.IO;
using Versio.App.Mvvm;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Git;
// RepositoryLocator lives in Core; FileSystemRepositoryProbe is its Infrastructure half.

namespace Versio.App.ViewModels;

/// <summary>Which panel the left rail is showing.</summary>
public enum ShellPanel
{
    /// <summary>Flat list of everything git reports as changed.</summary>
    Changes,

    /// <summary>Staging, commit, and the remote operations.</summary>
    Git,

    /// <summary>PLC export/import state: the direction lock, last sync, coverage.</summary>
    Sync,
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

/// <summary>One row in the changed-files list.</summary>
public sealed class ChangeRow(FileChange change)
{
    public string Path => change.Path;

    public string Glyph => change.Effective switch
    {
        ChangeKind.Added => "A",
        ChangeKind.Modified => "M",
        ChangeKind.Deleted => "D",
        ChangeKind.Renamed => "R",
        ChangeKind.Copied => "C",
        ChangeKind.Untracked => "?",
        ChangeKind.Conflicted => "!",
        _ => " ",
    };

    public string BrushKey => change.Effective switch
    {
        ChangeKind.Added => "Brush.Status.Added",
        ChangeKind.Modified => "Brush.Status.Modified",
        ChangeKind.Deleted => "Brush.Status.Deleted",
        ChangeKind.Renamed => "Brush.Status.Renamed",
        ChangeKind.Conflicted => "Brush.Status.Conflict",
        _ => "Brush.Status.Neutral",
    };

    public string Detail => change.OriginalPath is null
        ? string.Empty
        : $"← {change.OriginalPath}";

    public bool IsStaged => change.IsStaged;
}

/// <summary>
/// The shell. Opens a real repository and shows real git state.
/// </summary>
/// <remarks>
/// Deliberately wired to the real <see cref="GitCliClient"/> rather than sample data. A
/// mock shell proves nothing about whether the pieces fit; this one has already surfaced
/// the encoding and quoting bugs that sample data would have hidden.
/// </remarks>
public sealed class ShellViewModel : ObservableObject
{
    private readonly VersioLogger _logger;
    private readonly Func<string, GitCliClient> _gitFactory;
    private readonly RepositoryLocator _locator;

    private GitCliClient? _git;
    private string? _repositoryPath;
    private string _branchName = "—";
    private string _statusSummary = "No repository open";
    private string _aheadBehind = string.Empty;
    private string _selectedDiff = string.Empty;
    private ChangeRow? _selectedChange;
    private bool _isBusy;
    private ShellPanel _activePanel = ShellPanel.Changes;

    public ShellViewModel(VersioLogger logger, LogViewModel log,
                          Func<string, GitCliClient>? gitFactory = null,
                          IRepositoryProbe? probe = null)
    {
        _logger = logger;
        Log = log;
        Sync = new SyncViewModel(logger);
        _gitFactory = gitFactory ?? (path => new GitCliClient(path,
            log: message => logger.Debug(LogCategory.Git, message)));
        _locator = new RepositoryLocator(probe ?? new FileSystemRepositoryProbe());

        OpenRepositoryCommand = new AsyncCommand(OpenRepositoryAsync, onError: ReportError);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => _git is not null,
                                          onError: ReportError);

        logger.Info(LogCategory.App, "Versio started");
    }

    public LogViewModel Log { get; }

    /// <summary>The PLC sync panel — what Versio is for, as distinct from the git client.</summary>
    public SyncViewModel Sync { get; }

    public ObservableCollection<ChangeRow> Changes { get; } = [];

    /// <summary>Most-recently-opened repositories, newest first. Backs the top-bar dropdown.</summary>
    public ObservableCollection<RepoEntry> RecentRepositories { get; } = [];

    public ObservableCollection<BranchEntry> Branches { get; } = [];

    public AsyncCommand OpenRepositoryCommand { get; }

    public AsyncCommand RefreshCommand { get; }

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
            if (SetProperty(ref _activePanel, value))
            {
                _logger.Click($"Rail.{value}");
                OnPropertyChanged(nameof(PanelTitle));
            }
        }
    }

    public string PanelTitle => _activePanel switch
    {
        ShellPanel.Changes => "CHANGED FILES",
        ShellPanel.Git => "GIT",
        ShellPanel.Sync => "PLC SYNC",
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

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public ChangeRow? SelectedChange
    {
        get => _selectedChange;
        set
        {
            if (SetProperty(ref _selectedChange, value) && value is not null)
            {
                _ = LoadDiffAsync(value);
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
        _logger.Info(LogCategory.App, $"Opening {path}");

        var location = _locator.Locate(path);
        if (!location.IsUsable)
        {
            _logger.Warn(LogCategory.Git, $"{path}: {location.Explanation}");
            StatusSummary = location.Explanation;
            RepositoryRejected?.Invoke(location);
            return;
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
            return;
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
        await RefreshAsync().ConfigureAwait(true);

        // Scan for PLC projects after the git state is up, so the sync panel is populated
        // by the time the user reaches for it.
        await Sync.AttachAsync(path, client, CancellationToken.None).ConfigureAwait(true);
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

    private async Task RefreshAsync()
    {
        if (_git is null)
        {
            return;
        }

        _logger.Click("TopBar.Refresh");
        IsBusy = true;

        var operationId = Guid.NewGuid().ToString("N")[..8];
        using var scope = _logger.BeginOperation(operationId);

        try
        {
            var status = await _git.StatusAsync(CancellationToken.None).ConfigureAwait(true);

            BranchName = status.Branch.IsDetached ? "(detached)" : status.Branch.Name;
            AheadBehind = status.Branch is { Ahead: 0, Behind: 0 }
                ? string.Empty
                : $"↑{status.Branch.Ahead} ↓{status.Branch.Behind}";

            Changes.Clear();
            foreach (var change in status.Changes.OrderBy(c => c.Path, StringComparer.Ordinal))
            {
                Changes.Add(new ChangeRow(change));
            }

            StatusSummary = status.IsClean
                ? "No changes"
                : $"{status.Changes.Count} changed";

            await LoadBranchesAsync().ConfigureAwait(true);

            _logger.Info(LogCategory.Git,
                $"status: {status.Changes.Count} changed on {BranchName}");
        }
        finally
        {
            IsBusy = false;
        }
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

    private async Task LoadDiffAsync(ChangeRow row)
    {
        if (_git is null)
        {
            return;
        }

        try
        {
            var diff = await _git.DiffAsync(row.Path, row.IsStaged, CancellationToken.None)
                .ConfigureAwait(true);

            SelectedDiff = string.IsNullOrWhiteSpace(diff)
                ? $"No textual diff for {row.Path}.\n\n" +
                  "Binary files and untracked files have no diff until they are staged."
                : diff;
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
}
