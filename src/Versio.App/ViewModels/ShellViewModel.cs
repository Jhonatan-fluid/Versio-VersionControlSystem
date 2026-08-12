using System.Collections.ObjectModel;
using System.IO;
using Versio.App.Mvvm;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Git;

namespace Versio.App.ViewModels;

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

    private GitCliClient? _git;
    private string? _repositoryPath;
    private string _branchName = "—";
    private string _statusSummary = "No repository open";
    private string _aheadBehind = string.Empty;
    private string _selectedDiff = string.Empty;
    private ChangeRow? _selectedChange;
    private bool _isBusy;

    public ShellViewModel(VersioLogger logger, LogViewModel log,
                          Func<string, GitCliClient>? gitFactory = null)
    {
        _logger = logger;
        Log = log;
        _gitFactory = gitFactory ?? (path => new GitCliClient(path,
            log: message => logger.Debug(LogCategory.Git, message)));

        OpenRepositoryCommand = new AsyncCommand(OpenRepositoryAsync, onError: ReportError);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => _git is not null,
                                          onError: ReportError);

        logger.Info(LogCategory.App, "Versio started");
    }

    public LogViewModel Log { get; }

    public ObservableCollection<ChangeRow> Changes { get; } = [];

    public ObservableCollection<string> RecentRepositories { get; } = [];

    public AsyncCommand OpenRepositoryCommand { get; }

    public AsyncCommand RefreshCommand { get; }

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

    /// <summary>Open a repository by path. Separate from the command so the folder picker
    /// stays in the view, where a dialog belongs.</summary>
    public async Task OpenAsync(string path)
    {
        _logger.Info(LogCategory.App, $"Opening repository {path}");
        var client = _gitFactory(path);

        if (!await client.IsRepositoryAsync(CancellationToken.None).ConfigureAwait(true))
        {
            _logger.Warn(LogCategory.Git, $"{path} is not a git repository");
            StatusSummary = "Not a git repository";
            return;
        }

        _git = client;
        RepositoryPath = path;

        if (!RecentRepositories.Contains(path))
        {
            RecentRepositories.Insert(0, path);
        }

        RefreshCommand.RaiseCanExecuteChanged();
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

            _logger.Info(LogCategory.Git,
                $"status: {status.Changes.Count} changed on {BranchName}");
        }
        finally
        {
            IsBusy = false;
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
