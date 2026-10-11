using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Versio.Core.Bindings;
using Versio.Core.Logging;
using Versio.Core.Projects;
using Versio.Infrastructure.Bindings;
using Versio.Web.Sources;

namespace Versio.Web.Repo;

/// <summary>One commit of a repository, as the viewer shows it: its files and its PLC object tree.</summary>
/// <param name="IsVersio">Whether <c>.versio/repo.json</c> is there; without it the tree is plain folders.</param>
/// <param name="Problems">What could not be read (a corrupt repo.json, a missing manifest), said on the page.</param>
public sealed record RepoModel(
    string Owner,
    string Repo,
    string CommitSha,
    IReadOnlyDictionary<string, TreeEntry> Files,
    IReadOnlyList<ProjectTreeNode> Nodes,
    bool IsVersio,
    bool Truncated,
    IReadOnlyList<string> Problems)
{
    /// <summary>The bound projects as the tree was built from them.</summary>
    public IReadOnlyList<WorkspaceProject> Projects { get; init; } = [];

    /// <summary>
    /// Every exported object by path and by name: the index the desktop links member types
    /// through (<c>ShellViewModel.LinkTypes</c>), built from the same manifests.
    /// </summary>
    public ObjectIndex Objects => _objects ??= ObjectIndex.Of(Projects);

    private ObjectIndex? _objects;

    /// <summary>
    /// Every file's node, built once per model. An object wins over a plain file row for the same
    /// path, as the desktop opens the object.
    /// </summary>
    /// <remarks>
    /// The first version walked the whole tree on every call. One view is one call, but a
    /// comparison asks once per changed file, so two large commits in a large repository was
    /// changed-files times tree-size: ten thousand of each is a hundred million steps.
    /// </remarks>
    private Dictionary<string, ProjectTreeNode> ByFile => _byFile ??= Index();

    private Dictionary<string, ProjectTreeNode>? _byFile;

    private Dictionary<string, ProjectTreeNode> Index()
    {
        var index = new Dictionary<string, ProjectTreeNode>(StringComparer.Ordinal);
        foreach (var node in Flatten(Nodes))
        {
            var keys = node.Kind == ProjectNodeKind.File ? node.Files.Append(node.Path) : node.Files;
            foreach (var file in keys)
            {
                if (node.Kind == ProjectNodeKind.Object || !index.ContainsKey(file))
                {
                    index[file] = node;
                }
            }
        }

        return index;
    }

    /// <summary>The node a file belongs to: the object it is a layer of, or the file itself.</summary>
    public ProjectTreeNode? NodeFor(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (ByFile.TryGetValue(path, out var found))
        {
            return found;
        }

        // A file the tree does not draw as its own row (inside an export, but not in the
        // manifest) is still a file of the commit, and still viewable.
        return Files.ContainsKey(path)
            ? new ProjectTreeNode { Name = path[(path.LastIndexOf('/') + 1)..], Path = path, Kind = ProjectNodeKind.File, Files = [path] }
            : null;
    }

    public static IEnumerable<ProjectTreeNode> Flatten(IEnumerable<ProjectTreeNode> nodes) =>
        nodes.SelectMany(n => Flatten(n.Children).Prepend(n));
}

/// <summary>
/// Builds a <see cref="RepoModel"/> the way the desktop's explorer does: GitHub's file list in
/// place of a disk walk, <c>repo.json</c>'s bindings, each export's manifest, then
/// <see cref="RepoExplorerBuilder"/>, the builder the desktop uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cached by commit, under its repository</b>, because a commit's tree never changes. The
/// access check is not the cache's job: the caller has already resolved the commit with this
/// person's own credential (<see cref="IRepoSource.ResolveCommitAsync"/>), and a commit that
/// resolves for them in that repository is one they can read. The key carries owner and repo,
/// so one repository's entry can never answer for another (security rule 2).
/// </para>
/// <para>
/// <b>Library, task and device catalogs are not read yet</b>: the managers draw without their
/// reference rows, which RepoExplorerBuilder already treats as a degraded view and never an
/// error. File-Based Storage source projects are not grafted either. Both are listed as later
/// in docs/web/PLAN.md.
/// </para>
/// </remarks>
public sealed class RepoModelLoader(IRepoSource source, IMemoryCache cache, IVersioLogger log)
{
    private const string ConfigPath = ".versio/repo.json";
    private const long MaxConfigBytes = 4L * 1024 * 1024;

    private readonly SingleFlight<RepoResult<RepoModel>> _loading = new();

    public async Task<RepoResult<RepoModel>> LoadAsync(RepoAccess access, string owner, string repo, string commitSha, CancellationToken ct)
    {
        var key = $"model:{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}:{commitSha}";
        if (cache.TryGetValue(key, out RepoModel? cached) && cached is not null)
        {
            return RepoResult<RepoModel>.Of(cached);
        }

        // Concurrent requests for one commit share one load (the page's tree and view arrive
        // together; measured, they each loaded the model). Every caller here has already resolved
        // this commit with its own credential, so whichever credential does the reading, each
        // caller is handed only what it could read. The shared load is not cancelled by one
        // caller leaving: the others are waiting on it.
        return await _loading.RunAsync(key, () => BuildAsync(access, owner, repo, commitSha, key, CancellationToken.None)).WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<RepoResult<RepoModel>> BuildAsync(RepoAccess access, string owner, string repo, string commitSha, string key, CancellationToken ct)
    {
        var tree = await source.GetTreeAsync(access, owner, repo, commitSha, ct).ConfigureAwait(false);
        if (!tree.Ok)
        {
            return tree.As<RepoModel>();
        }

        var files = tree.Value!.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var problems = new List<string>();
        var projects = new List<WorkspaceProject>();
        var isVersio = files.TryGetValue(ConfigPath, out var configEntry);

        if (isVersio)
        {
            var config = await ReadConfigAsync(access, owner, repo, configEntry!, problems, ct).ConfigureAwait(false);
            // Every manifest at once: one GitHub round trip each, and none depends on another.
            var bindings = config?.Bindings.Where(b => b.Enabled).ToList() ?? [];
            async Task<RepoResult<byte[]>?> ReadManifest(ProjectBinding b) =>
                files.TryGetValue($"{b.ExportPath.TrimEnd('/')}/{BindingConventions.ManifestFileName}", out var entry)
                    ? await source.GetBlobAsync(access, owner, repo, entry.BlobSha, MaxConfigBytes, ct).ConfigureAwait(false)
                    : null;

            var reads = bindings.Select(ReadManifest).ToList();
            await Task.WhenAll(reads).ConfigureAwait(false);

            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                var manifestPath = $"{binding.ExportPath.TrimEnd('/')}/{BindingConventions.ManifestFileName}";
                ProjectManifest? manifest = null;
                if (reads[i].Result is { } bytes)
                {
                    if (bytes.Ok)
                    {
                        manifest = ProjectManifest.Parse(Encoding.UTF8.GetString(bytes.Value!), out var manifestProblems);
                        problems.AddRange(manifestProblems.Select(p => $"{manifestPath}: {p}"));
                    }
                    else
                    {
                        problems.Add($"{manifestPath} could not be read ({bytes.Failure}).");
                    }
                }
                else
                {
                    problems.Add($"{binding.ProjectPath} has never been exported here: there is no {manifestPath}.");
                }

                projects.Add(new WorkspaceProject
                {
                    ProjectPath = binding.ProjectPath,
                    ExportPath = binding.ExportPath,
                    Manifest = manifest,
                    ExportState = "tracked",
                });
            }
        }

        var nodes = RepoExplorerBuilder.Build(files.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList(), projects);
        var model = new RepoModel(owner, repo, commitSha, files, nodes, isVersio, tree.Value.Truncated, problems) { Projects = projects };
        cache.Set(key, model, new MemoryCacheEntryOptions { Size = Math.Max(1, files.Count / 100), SlidingExpiration = TimeSpan.FromMinutes(30) });
        log.Info(LogCategory.Web, $"Loaded {owner}/{repo} at {commitSha[..Math.Min(8, commitSha.Length)]}: {files.Count} files, {projects.Count} projects{(problems.Count > 0 ? $", {problems.Count} problems" : "")}");
        return RepoResult<RepoModel>.Of(model);
    }

    private async Task<RepoConfig?> ReadConfigAsync(
        RepoAccess access, string owner, string repo, TreeEntry entry, List<string> problems, CancellationToken ct)
    {
        var bytes = await source.GetBlobAsync(access, owner, repo, entry.BlobSha, MaxConfigBytes, ct).ConfigureAwait(false);
        if (!bytes.Ok)
        {
            problems.Add($"{ConfigPath} could not be read ({bytes.Failure}).");
            return null;
        }

        try
        {
            // The desktop's own reader, so a committed file means the same thing in both.
            return RepoConfigStore.Parse(Encoding.UTF8.GetString(bytes.Value!));
        }
        catch (JsonException)
        {
            problems.Add($"{ConfigPath} is not valid JSON; the repository is shown as plain folders.");
            log.Warn(LogCategory.Web, $"{owner}/{repo}: {ConfigPath} is corrupt; shown as plain folders");
            return null;
        }
    }
}
