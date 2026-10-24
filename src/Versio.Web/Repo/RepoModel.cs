using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Versio.Core.Bindings;
using Versio.Core.Logging;
using Versio.Core.Plc;
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
    /// <summary>
    /// Whether a read failed for a reason of the moment (a rate limit, an outage) while this was
    /// built: its problems then say so, and it is not cached, so the next request reads again.
    /// </summary>
    public bool Partial { get; init; }

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
            // A row whose path is not one of its files is still a row the page can ask for: a
            // project (its file is the vendor's binary, or a folder of readable source with no
            // file of its own), a device object (its path is the device, its file the catalog
            // beside it), and a reference, task or device row grafted from a catalog (its path
            // carries a row marker). Until 2026-10-23 those asked for 404s, so the tree offered
            // rows the pane could not open — RenderChoiceParityTests found every one of them
            // against the desktop, which draws them. Folders alone have nothing to open.
            var keys = node.Kind == ProjectNodeKind.Folder ? node.Files : node.Files.Append(node.Path);
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
/// <b>A model built while a read failed is not cached.</b> A manifest or catalog that could not be
/// read for a reason of the moment (a rate limit, an outage) becomes a problem the page shows, and
/// the model is <see cref="RepoModel.Partial"/>; cached by commit, it went on showing that problem
/// for half an hour after GitHub answered again (found reviewing a review, 2026-10-11).
/// </para>
/// <para>
/// Catalogs (<see cref="ReadCatalogsAsync"/>) and File-Based Storage projects
/// (<see cref="SourceProjects"/>) are read. This remark said they were not until 2026-10-11, long
/// after both were built, and it is the paragraph a later edit would have trusted (found in review).
/// </para>
/// </remarks>
public sealed class RepoModelLoader(IRepoSource source, IMemoryCache cache, IVersioLogger log)
{
    private const string ConfigPath = ".versio/repo.json";
    private const long MaxConfigBytes = 4L * 1024 * 1024;

    private readonly SingleFlight<(RepoResult<RepoModel> Result, RepoAccess By)> _loading = new();

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
        //
        // A failure is not shared, though. The credential that did the reading is the one that
        // was refused or rate-limited: an anonymous load on GitHub's unauthenticated limit used to
        // hand its failure to a signed-in request that joined it, whose own token would have read
        // the commit (found in review). A caller who brought another credential and got a failed
        // or partial answer reads again with its own, coalesced with others holding the same one.
        var shared = await _loading.RunAsync(key, () => Attributed(access, owner, repo, commitSha, key)).WaitAsync(ct).ConfigureAwait(false);
        if ((shared.Result.Ok && !shared.Result.Value!.Partial) || shared.By == access)
        {
            return shared.Result;
        }

        var own = await _loading.RunAsync($"{key}:{access.Kind}:{access.Who}", () => Attributed(access, owner, repo, commitSha, key)).WaitAsync(ct).ConfigureAwait(false);
        return own.Result;
    }

    private async Task<(RepoResult<RepoModel>, RepoAccess)> Attributed(RepoAccess access, string owner, string repo, string commitSha, string key) =>
        (await BuildAsync(access, owner, repo, commitSha, key, CancellationToken.None).ConfigureAwait(false), access);

    private async Task<RepoResult<RepoModel>> BuildAsync(RepoAccess access, string owner, string repo, string commitSha, string key, CancellationToken ct)
    {
        var tree = await source.GetTreeAsync(access, owner, repo, commitSha, ct).ConfigureAwait(false);
        if (!tree.Ok)
        {
            return tree.As<RepoModel>();
        }

        var files = tree.Value!.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);

        // Each blob fetched once per load, however many paths share it: identical files (two
        // projects' identical catalogs) have one blob id, and parallel reads fetched it twice
        // (found by PerformanceTests' "each thing once").
        var fetches = new System.Collections.Concurrent.ConcurrentDictionary<string, Task<RepoResult<byte[]>>>(StringComparer.Ordinal);
        var partial = false;
        Task<RepoResult<byte[]>> fetch(string sha) => fetches.GetOrAdd(sha, async s =>
        {
            var read = await source.GetBlobAsync(access, owner, repo, s, MaxConfigBytes, ct).ConfigureAwait(false);
            if (read.Failure is not (RepoFailure.None or RepoFailure.TooLarge))
            {
                partial = true;
            }

            return read;
        });
        var problems = new List<string>();
        var projects = new List<WorkspaceProject>();
        var isVersio = files.TryGetValue(ConfigPath, out var configEntry);

        if (isVersio)
        {
            var config = await ReadConfigAsync(fetch, owner, repo, configEntry!, problems).ConfigureAwait(false);
            // Every manifest at once: one GitHub round trip each, and none depends on another.
            var bindings = config?.Bindings.Where(b => b.Enabled).ToList() ?? [];
            async Task<RepoResult<byte[]>?> ReadManifest(ProjectBinding b) =>
                files.TryGetValue($"{b.ExportPath.TrimEnd('/')}/{BindingConventions.ManifestFileName}", out var entry)
                    ? await fetch(entry.BlobSha).ConfigureAwait(false)
                    : null;

            var reads = bindings.Select(ReadManifest).ToList();
            await Task.WhenAll(reads).ConfigureAwait(false);

            var found = new List<(ProjectBinding Binding, ProjectManifest? Manifest)>();
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

                found.Add((binding, manifest));
            }

            // Every project's catalogs at once, as the manifests were read.
            var catalogs = await Task.WhenAll(found.Select(f => ReadCatalogsAsync(fetch, files, f.Manifest, f.Binding.ExportPath, problems))).ConfigureAwait(false);
            for (var i = 0; i < found.Count; i++)
            {
                var (binding, manifest) = found[i];
                var (libraries, tasks, devices) = catalogs[i];
                projects.Add(new WorkspaceProject
                {
                    ProjectPath = binding.ProjectPath,
                    ExportPath = binding.ExportPath,
                    Manifest = manifest,
                    Libraries = libraries,
                    Tasks = tasks,
                    Devices = devices,
                    ExportState = "tracked",
                });
            }
        }

        projects.AddRange(SourceProjects(files));

        var nodes = RepoExplorerBuilder.Build(files.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList(), projects);
        var model = new RepoModel(owner, repo, commitSha, files, nodes, isVersio, tree.Value.Truncated, problems) { Projects = projects, Partial = partial };
        if (!partial)
        {
            cache.Set(key, model, new MemoryCacheEntryOptions { Size = Math.Max(1, files.Count / 100), SlidingExpiration = TimeSpan.FromMinutes(30) });
        }

        log.Info(LogCategory.Web, $"Loaded {owner}/{repo} at {commitSha[..Math.Min(8, commitSha.Length)]}: {files.Count} files, {projects.Count} projects"
            + $"{(problems.Count > 0 ? $", {problems.Count} problems" : "")}{(partial ? ", a read failed so not cached" : "")}");
        return RepoResult<RepoModel>.Of(model);
    }

    /// <summary>
    /// A project's library, task and device catalogs, read as the desktop's <c>ReadCatalogs</c>
    /// reads them: the manifest names them by form, Core parses them, the tree grafts them.
    /// </summary>
    /// <remarks>
    /// Without them the managers drew with no reference rows (RepoExplorerBuilder treats that as
    /// a degraded view, never an error), and the first web build left them out; the tree now shows
    /// a project's libraries, tasks and devices as the desktop's does. No baseline: the viewer
    /// shows one commit, and marks between two come from the comparison, not the tree.
    /// </remarks>
    private async Task<(IReadOnlyList<LibrarySnapshot>, IReadOnlyList<TaskSnapshot>, IReadOnlyList<DeviceSnapshot>)> ReadCatalogsAsync(
        Func<string, Task<RepoResult<byte[]>>> fetch, IReadOnlyDictionary<string, TreeEntry> files, ProjectManifest? manifest, string exportPath, List<string> problems)
    {
        if (manifest is null)
        {
            return ([], [], []);
        }

        var root = exportPath.Replace('\\', '/').Trim('/');
        var wanted = manifest.Objects
            .Where(o => o.Path.Length > 0 && o.Form is ExportCoverage.LibrariesForm or ExportCoverage.TaskBothForm or ExportCoverage.DevicesForm)
            .Select(o => (o.Form, Path: $"{root}/{o.Path.Replace('\\', '/')}"))
            .Where(o => files.ContainsKey(o.Path))
            .ToList();
        var reads = await Task.WhenAll(wanted.Select(o => fetch(files[o.Path].BlobSha))).ConfigureAwait(false);

        var libraries = new List<LibrarySnapshot>();
        var tasks = new List<TaskSnapshot>();
        var devices = new List<DeviceSnapshot>();
        for (var i = 0; i < wanted.Count; i++)
        {
            // A catalog that could not be read, or read as nothing, is said: its rows used to
            // vanish silently while an unreadable manifest beside it was a problem on the page
            // (found in review). The tree still draws, without that catalog's rows.
            var (form, path) = wanted[i];
            if (!reads[i].Ok)
            {
                lock (problems)
                {
                    problems.Add($"{path} could not be read ({reads[i].Failure}).");
                }

                continue;
            }

            var text = Encoding.UTF8.GetString(reads[i].Value!);
            var parsed = false;
            switch (form)
            {
                case ExportCoverage.LibrariesForm when LibraryCatalog.Parse(text) is { IsValid: true } l:
                    libraries.Add(new LibrarySnapshot { Path = path, Current = l });
                    parsed = true;
                    break;
                case ExportCoverage.TaskBothForm when TaskCatalog.Parse(text) is { IsValid: true } t:
                    tasks.Add(new TaskSnapshot { Path = path, Current = t });
                    parsed = true;
                    break;
                case ExportCoverage.DevicesForm when DeviceCatalog.Parse(text) is { IsValid: true } d:
                    devices.Add(new DeviceSnapshot { Path = path, Current = d });
                    parsed = true;
                    break;
            }

            if (!parsed)
            {
                lock (problems)
                {
                    problems.Add($"{path} is not a catalog this version can read; its rows are not shown.");
                }
            }
        }

        return (libraries, tasks, devices);
    }

    /// <summary>
    /// Folders a vendor saves as its own readable source (the extensions each format claims as
    /// <c>SourceProjectExtensions</c>), described by their format from the file list alone, as the
    /// desktop's explorer does. Never synced, never adopted: shown and drawn.
    /// </summary>
    /// <remarks>
    /// <c>DescribeSourceProject</c> takes the list, never the disk, which is exactly what GitHub's
    /// tree gives. The first web build left these out.
    /// </remarks>
    private static IEnumerable<WorkspaceProject> SourceProjects(IReadOnlyDictionary<string, TreeEntry> files)
    {
        var extensions = PlcFormats.All.SelectMany(f => f.SourceProjectExtensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (extensions.Count == 0)
        {
            yield break;
        }

        var folders = files.Keys
            .Select(path =>
            {
                var parts = path.Split('/');
                var at = Array.FindIndex(parts, 0, parts.Length - 1, p => extensions.Contains(Path.GetExtension(p)));
                return at < 0 ? null : string.Join('/', parts.Take(at + 1));
            })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);

        foreach (var folder in folders)
        {
            if (PlcFormats.ForSourceProject(folder) is not { } format)
            {
                continue;
            }

            var relative = files.Keys.Where(k => k.StartsWith(folder + "/", StringComparison.Ordinal)).Select(k => k[(folder.Length + 1)..]).ToList();
            yield return new WorkspaceProject
            {
                ProjectPath = folder,
                ExportPath = folder,
                Manifest = format.DescribeSourceProject(folder, relative),
                ExportState = "read from its files",
            };
        }
    }

    private async Task<RepoConfig?> ReadConfigAsync(
        Func<string, Task<RepoResult<byte[]>>> fetch, string owner, string repo, TreeEntry entry, List<string> problems)
    {
        var bytes = await fetch(entry.BlobSha).ConfigureAwait(false);
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
