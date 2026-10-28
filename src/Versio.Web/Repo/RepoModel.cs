using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Versio.Core.Logging;
using Versio.Core.Projects;
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
/// <b>The reading is Core's.</b> <c>repo.json</c>, the manifests, the catalogs and the
/// directory-form projects are read by <see cref="ProjectSnapshotLoader"/>, which was this class's
/// own code until 2026-10-27 and moved to Core when the desktop's History panel became the
/// second host to read a project at a commit. What stays here is what only this host knows:
/// GitHub's tree, its blobs fetched once each, the cache, and the "partial" rule below.
/// </para>
/// </remarks>
public sealed class RepoModelLoader(IRepoSource source, IMemoryCache cache, IVersioLogger log)
{
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
        // Core's loader asks for a path, this answers with the blob behind it. A path it was not
        // handed in the list is never asked for, so "absent" here is a defence, not a case.
        async Task<SnapshotRead> Read(string path)
        {
            if (!files.TryGetValue(path, out var entry))
            {
                return SnapshotRead.Absent;
            }

            var bytes = await fetch(entry.BlobSha).ConfigureAwait(false);
            return bytes.Ok ? SnapshotRead.Of(Encoding.UTF8.GetString(bytes.Value!)) : SnapshotRead.Failed(bytes.Failure.ToString());
        }

        var paths = files.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList();
        var snapshot = await ProjectSnapshotLoader.LoadAsync(paths, Read, "tracked", log, LogCategory.Web, $"{owner}/{repo}", ct).ConfigureAwait(false);
        var projects = snapshot.Projects;
        var problems = snapshot.Problems;

        var nodes = RepoExplorerBuilder.Build(paths, projects);
        // "Set up for Versio" is the snapshot's word (IsVersio): a PLC project to draw, not only
        // a repo.json - see ProjectSnapshot for the pull request that taught it.
        var model = new RepoModel(owner, repo, commitSha, files, nodes, snapshot.IsVersio, tree.Value.Truncated, problems) { Projects = projects, Partial = partial };
        if (!partial)
        {
            cache.Set(key, model, new MemoryCacheEntryOptions { Size = Math.Max(1, files.Count / 100), SlidingExpiration = TimeSpan.FromMinutes(30) });
        }

        log.Info(LogCategory.Web, $"Loaded {owner}/{repo} at {commitSha[..Math.Min(8, commitSha.Length)]}: {files.Count} files, {projects.Count} projects"
            + $"{(problems.Count > 0 ? $", {problems.Count} problems" : "")}{(partial ? ", a read failed so not cached" : "")}");
        return RepoResult<RepoModel>.Of(model);
    }
}
