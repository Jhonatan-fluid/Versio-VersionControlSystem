using System.Collections.ObjectModel;
using System.IO;
using Versio.App.Mvvm;
using Versio.Core.Abstractions;
using Versio.Core.Bindings;
using Versio.Core.Logging;
using Versio.Core.Projects;

namespace Versio.App.ViewModels;

/// <summary>
/// The open clone as a folder tree, with PLC projects grafted in as object trees.
/// </summary>
/// <remarks>
/// Visualize, not review: git's status never enters the builder, so no row carries a change
/// glyph and a click never asks git for a diff. The object graph under a <c>.project</c> is
/// the same <see cref="WorkspaceTreeBuilder.BuildProjectNode"/> the Project panel uses.
/// </remarks>
public sealed class ExplorerViewModel : ObservableObject
{
    private readonly IVersioLogger _log;
    private readonly Func<IReadOnlyList<WorkspaceProject>> _projects;
    private readonly TreeExpansion _expansion = new(expandTopLevelFolders: true);

    private IGitClient? _git;
    private string? _root;
    private bool _isVisible;
    private string _search = string.Empty;
    private string _summary = string.Empty;
    private string _signature = string.Empty;
    private TreeRow? _selectedNode;
    private int _generation;

    public ExplorerViewModel(IVersioLogger logger, Func<IReadOnlyList<WorkspaceProject>> projects)
    {
        _log = logger;
        _projects = projects;
    }

    public ObservableCollection<TreeRow> Tree { get; } = [];

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    /// <summary>
    /// The panel is in front, so a listing nobody asked for has become one they did.
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value) && value)
            {
                _ = RefreshAsync();
            }
        }
    }

    public TreeRow? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!SetProperty(ref _selectedNode, value) || value is null)
            {
                return;
            }

            if (!value.IsFolder)
            {
                NodeSelected?.Invoke(value);
            }
        }
    }

    /// <summary>
    /// A row was picked. The shell renders it; this view model does not know what a pane is.
    /// </summary>
    public event Action<TreeRow>? NodeSelected;

    public void Attach(IGitClient git, string root)
    {
        _git = git;
        _root = root;
        _signature = string.Empty;
        Tree.Clear();
        Search = string.Empty;
        Summary = string.Empty;

        if (IsVisible)
        {
            _ = RefreshAsync();
        }
    }

    public void Detach()
    {
        _git = null;
        _root = null;
        _signature = string.Empty;
        _expansion.Clear();
        Tree.Clear();
        Search = string.Empty;
        Summary = string.Empty;
        SelectedNode = null;
    }

    /// <summary>
    /// The working tree moved. Reload only while somebody is looking — a listing of the
    /// whole clone on every save is the cost this panel exists to avoid paying in the
    /// background.
    /// </summary>
    public void Invalidate()
    {
        _signature = string.Empty;

        if (IsVisible)
        {
            _ = RefreshAsync();
        }
    }

    public async Task RefreshAsync()
    {
        var generation = ++_generation;

        if (_git is null || _root is null)
        {
            Tree.Clear();
            Summary = string.Empty;
            return;
        }

        IReadOnlyList<string> files;
        try
        {
            files = await _git.ListWorkingTreeAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error(LogCategory.Git, "could not list the working tree", ex);
            return;
        }

        if (generation != _generation)
        {
            return;
        }

        var projects = _projects();
        var search = Search;

        // File list plus each manifest's write time: an export that does not change the
        // object count would otherwise look identical and leave the grafted tree stale.
        var signature = string.Join('\u001f',
            files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                 .Append("s=" + search)
                 .Concat(projects.Select(p => p.ProjectPath + "=" + ManifestStamp(p))));

        if (signature == _signature && Tree.Count > 0)
        {
            return;
        }

        var roots = await Task.Run(() => RepoExplorerBuilder.Build(
            files,
            projects,
            new ProjectTreeOptions
            {
                ChangedOnly = false,
                HideUnchangedProjects = false,
                Search = search,
            })).ConfigureAwait(true);

        if (generation != _generation)
        {
            return;
        }

        _signature = signature;
        TreeRow.Reconcile(Tree, roots, 0, _expansion);

        var filesShown = roots.Sum(CountFiles);
        Summary = filesShown == 0 && Tree.Count == 0
            ? "Nothing to list in this repository."
            : Tree.Count == 1
                ? "1 top-level entry."
                : $"{Tree.Count} top-level entries.";
    }

    private string ManifestStamp(WorkspaceProject project)
    {
        if (_root is null)
        {
            return "none";
        }

        var path = Path.Combine(
            _root,
            project.ExportPath.Replace('/', Path.DirectorySeparatorChar),
            BindingConventions.ManifestFileName);

        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString() : "none";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    private static int CountFiles(ProjectTreeNode node) =>
        (node.Kind == ProjectNodeKind.File ? 1 : 0) + node.Children.Sum(CountFiles);
}
