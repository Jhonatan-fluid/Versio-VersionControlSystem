using System.Collections.ObjectModel;
using System.IO;
using Versio.App.Mvvm;
using Versio.Core.Abstractions;
using Versio.Core.Bindings;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Core.Plc.Codesys;
using Versio.Core.Sync;
using Versio.Infrastructure.Bindings;
using Versio.Infrastructure.Plc.Codesys;

namespace Versio.App.ViewModels;

/// <summary>A PLC project Versio has found in the repository.</summary>
public sealed class ProjectRow(string path, bool tracked, string state)
{
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileNameWithoutExtension(path);

    public string Folder { get; } = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";

    public bool Tracked { get; } = tracked;

    /// <summary>One short phrase for the row: "tracked", "not tracked", "exporting…".</summary>
    public string State { get; } = state;

    public string BrushKey => Tracked ? "Brush.Status.Added" : "Brush.Status.Modified";
}

/// <summary>
/// The PLC sync panel.
/// </summary>
/// <remarks>
/// <para>
/// This is the panel Versio exists for. A PLC project file is binary, so git can store it
/// but not diff it; the sync engine exports it to reviewable text beside the project and
/// imports edited text back. Everything shown here comes from the real scanner and the real
/// toolchain — there is no sample data.
/// </para>
/// <para>
/// The toolchain is probed once and its absence is <b>reported, not thrown</b>. A repository
/// full of CODESYS projects is perfectly usable as a git repository on a machine with no
/// CODESYS installed; the panel just has to say why it cannot export.
/// </para>
/// </remarks>
public sealed class SyncViewModel : ObservableObject
{
    private readonly VersioLogger _logger;
    private readonly IPlcProjectFormat _format = CodesysProjectFormat.Instance;
    private readonly OrphanScanner _orphanScanner = new();

    private string? _repositoryRoot;
    private IGitClient? _git;
    private string _toolchainState = "Not checked";
    private bool _toolchainAvailable;
    private string _summary = "No repository open";
    private string? _profile;

    public SyncViewModel(VersioLogger logger)
    {
        _logger = logger;
        RescanCommand = new AsyncCommand(RescanAsync, () => _repositoryRoot is not null);
    }

    public ObservableCollection<ProjectRow> Projects { get; } = [];

    public ObservableCollection<string> Findings { get; } = [];

    public AsyncCommand RescanCommand { get; }

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

    /// <summary>Called when the shell opens a repository.</summary>
    public async Task AttachAsync(string repositoryRoot, IGitClient git, CancellationToken ct)
    {
        _repositoryRoot = repositoryRoot;
        _git = git;
        RescanCommand.RaiseCanExecuteChanged();

        await ProbeToolchainAsync(ct).ConfigureAwait(true);
        await RescanAsync().ConfigureAwait(true);
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
    private Task ProbeToolchainAsync(CancellationToken ct)
    {
        var installs = new CodesysLocator().FindInstalls();
        var resolution = ProfileResolver.Resolve(installs);

        switch (resolution.Outcome)
        {
            case ProfileResolution.Resolved:
                _profile = resolution.Profile;
                ToolchainAvailable = true;
                ToolchainState = $"{installs[0].InstallFolder} — {resolution.Profile}";
                _logger.Info(LogCategory.Codesys, $"toolchain ready: {ToolchainState}");
                break;

            case ProfileResolution.AmbiguousNeedsUserChoice:
                ToolchainAvailable = false;
                ToolchainState =
                    $"{resolution.Candidates.Count} profiles installed — pick the one this " +
                    "repository targets before exporting";
                break;

            default:
                ToolchainAvailable = false;
                ToolchainState = resolution.Explanation;
                _logger.Warn(LogCategory.Codesys, resolution.Explanation);
                break;
        }

        return Task.CompletedTask;
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

        var store = new RepoConfigStore(_repositoryRoot);
        var config = await store.LoadAsync(CancellationToken.None).ConfigureAwait(true);

        var scanner = new RepoScanner(_repositoryRoot, _format);
        var snapshot = await scanner.ScanAsync(_git, CancellationToken.None).ConfigureAwait(true);

        var findings = _orphanScanner.Scan(config, snapshot);

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

            var bound = config.Bindings.Any(b =>
                string.Equals(b.ProjectPath, project, StringComparison.OrdinalIgnoreCase));

            Projects.Add(new ProjectRow(project, bound, bound ? "tracked" : "not tracked"));
        }

        Findings.Clear();
        foreach (var finding in findings.Where(f => f.NeedsAttention))
        {
            Findings.Add($"{finding.Health}: {finding.Path} — {finding.Explanation}");
        }

        Summary = Projects.Count switch
        {
            0 => "No PLC projects found in this repository.",
            1 => Projects[0].Tracked
                ? "1 project, tracked."
                : "1 project found. It is not tracked yet.",
            _ => $"{Projects.Count} projects, {Projects.Count(p => p.Tracked)} tracked.",
        };

        _logger.Info(LogCategory.Sync,
            $"scan: {Projects.Count} project(s), {Findings.Count} finding(s)");
    }

    /// <summary>Clear state when the repository closes, so a stale scan is never shown.</summary>
    public void Detach()
    {
        _repositoryRoot = null;
        _git = null;
        Projects.Clear();
        Findings.Clear();
        Summary = "No repository open";
        RescanCommand.RaiseCanExecuteChanged();
    }
}
