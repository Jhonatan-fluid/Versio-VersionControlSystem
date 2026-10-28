using System.Text.Json;
using Versio.Core.Bindings;
using Versio.Core.Logging;
using Versio.Core.Plc;

namespace Versio.Core.Projects;

/// <summary>
/// One file as a version of the repository has it: its text, or why there is none.
/// </summary>
/// <remarks>
/// Null text with a null failure means <i>not in this tree</i>, which the loader words
/// differently from a read that failed — "has never been exported here" is a state, "could not be
/// read (RateLimited)" is a fault of the moment that the web viewer refuses to cache.
/// </remarks>
public readonly record struct SnapshotRead(string? Text, string? Failure)
{
    /// <summary>The path is not in this version of the repository.</summary>
    public static SnapshotRead Absent => default;

    public static SnapshotRead Of(string text) => new(text, null);

    /// <summary>The path is there and could not be read; <paramref name="why"/> is said on the page and in the log.</summary>
    public static SnapshotRead Failed(string why) => new(null, why);

    public bool IsAbsent => Text is null && Failure is null;
}

/// <summary>
/// A repository at one version, as the project tree needs to see it: its files, the PLC projects
/// found in them, and what could not be read.
/// </summary>
/// <param name="HasConfig">Whether <c>.versio/repo.json</c> is there, readable or not.</param>
/// <param name="Problems">What could not be read (a corrupt repo.json, a missing manifest), said on the page and in the panel.</param>
public sealed record ProjectSnapshot(
    IReadOnlyList<string> Files,
    IReadOnlyList<WorkspaceProject> Projects,
    bool HasConfig,
    IReadOnlyList<string> Problems)
{
    /// <summary>
    /// Set up for Versio: a repo.json, or a PLC project to draw without one.
    /// </summary>
    /// <remarks>
    /// "Set up for Versio" is having a PLC project to draw, not only a repo.json: a source
    /// project (an FBS export) with no repo.json drew its Devices and POUs under a notice that
    /// the repository was not set up (seen on the real pull request, FINISH.md A.8).
    /// </remarks>
    public bool IsVersio => HasConfig || Projects.Count > 0;

    /// <summary>
    /// Every exported object by path and by name: the index the desktop links member types
    /// through (<c>ShellViewModel.LinkTypes</c>) and counts a commit's objects with
    /// (<c>CommitHeading</c>), built from the same manifests.
    /// </summary>
    public ObjectIndex Objects => _objects ??= ObjectIndex.Of(Projects);

    private ObjectIndex? _objects;
}

/// <summary>
/// Builds a <see cref="ProjectSnapshot"/> from a file list and a reader: <c>repo.json</c>'s
/// bindings, each export's manifest and catalogs, and the folders a vendor saves as readable
/// source — the same reading the desktop's explorer does from disk, with the disk taken out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure over its two inputs.</b> The host decides what a read is: GitHub's blobs on the web,
/// <c>git show &lt;commit&gt;:./path</c> in the desktop's History panel, a dictionary in a test.
/// This was the web viewer's <c>RepoModelLoader</c> until 2026-10-27, written there because the
/// web was the first host to read a project at a commit rather than from a working tree; the
/// History panel is the second, and a second copy of the manifest-and-catalog walk would have
/// drifted from the first the way the two render switches did (FINDINGS 2026-10-23).
/// </para>
/// <para>
/// Catalogs (<see cref="ReadCatalogsAsync"/>) and directory-form projects
/// (<see cref="SourceProjects"/>) are read. The web loader's remark said they were not until
/// 2026-10-11, long after both were built, and it is the paragraph a later edit would have
/// trusted (found in review).
/// </para>
/// </remarks>
public static class ProjectSnapshotLoader
{
    public const string ConfigPath = ".versio/repo.json";

    /// <param name="files">Every path at this version, repository-relative with forward slashes.</param>
    /// <param name="read">The text of one of those paths, or why there is none. Called only for paths in <paramref name="files"/>.</param>
    /// <param name="exportState">The words on a tracked project's row: "tracked" on the web, "as of 1a2b3c4" in the history.</param>
    /// <param name="log">Where a repaired or corrupt <c>repo.json</c> is said, under <paramref name="category"/>, naming <paramref name="subject"/> (the repository, or the commit).</param>
    public static async Task<ProjectSnapshot> LoadAsync(
        IReadOnlyList<string> files,
        Func<string, Task<SnapshotRead>> read,
        string exportState,
        IVersioLogger log,
        string category,
        string subject,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(log);

        var present = files.ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        var projects = new List<WorkspaceProject>();
        var hasConfig = present.Contains(ConfigPath);

        if (hasConfig)
        {
            var config = await ReadConfigAsync(read, log, category, subject, problems).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Every manifest at once: one round trip each on the web, and none depends on another.
            var bindings = config?.Bindings.Where(b => b.Enabled).ToList() ?? [];
            var manifestPaths = bindings.Select(b => $"{b.ExportPath.TrimEnd('/')}/{BindingConventions.ManifestFileName}").ToList();
            var reads = manifestPaths.Select(p => present.Contains(p) ? read(p) : Task.FromResult(SnapshotRead.Absent)).ToList();
            await Task.WhenAll(reads).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var found = new List<(ProjectBinding Binding, ProjectManifest? Manifest)>();
            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                var manifestPath = manifestPaths[i];
                var text = reads[i].Result;
                ProjectManifest? manifest = null;
                if (text.Text is { } json)
                {
                    manifest = ProjectManifest.Parse(json, out var manifestProblems);
                    problems.AddRange(manifestProblems.Select(p => $"{manifestPath}: {p}"));
                }
                else if (text.IsAbsent)
                {
                    problems.Add($"{binding.ProjectPath} has never been exported here: there is no {manifestPath}.");
                }
                else
                {
                    problems.Add($"{manifestPath} could not be read ({text.Failure}).");
                }

                found.Add((binding, manifest));
            }

            // Every project's catalogs at once, as the manifests were read.
            var catalogs = await Task.WhenAll(found.Select(f => ReadCatalogsAsync(read, present, f.Manifest, f.Binding.ExportPath, problems))).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
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
                    ExportState = exportState,
                });
            }
        }

        projects.AddRange(SourceProjects(files));

        return new ProjectSnapshot(files, projects, hasConfig, problems);
    }

    /// <summary>
    /// Name the objects a version deleted from the manifest that still named them: the first
    /// parent's.
    /// </summary>
    /// <param name="snapshot">The version as loaded; returned as is when nothing was deleted under an export.</param>
    /// <param name="gonePaths">Paths this version no longer holds: deletions, and a rename's old path.</param>
    /// <param name="readParent">The text of a path at the first parent — only ever asked for a manifest.</param>
    /// <remarks>
    /// <para>
    /// A commit that deletes an object also drops it from the manifest, so the tree built from
    /// this version's manifest has no name, type or vendor for it and lists it struck by its
    /// file name — <c>App/FB_Motor.st</c> where every other row says <c>FB_Motor · POU</c>. The
    /// parent's manifest is the last one that described it, and is read for exactly those
    /// paths: one extra read per binding with a deletion, none otherwise. The object joins this
    /// version's manifest so <c>ProjectTreeBuilder</c> draws it like any other, and the changes
    /// the tree is marked with say it is gone.
    /// </para>
    /// <para>
    /// A parent that cannot be read, or that did not name the path either, leaves the row as
    /// the path: said as a problem in the first case, and silently right in the second (a file
    /// under the export root that no manifest ever described is a loose file, deleted).
    /// </para>
    /// </remarks>
    public static async Task<ProjectSnapshot> WithDeletedObjectsAsync(
        ProjectSnapshot snapshot,
        IReadOnlyList<string> gonePaths,
        Func<string, Task<SnapshotRead>> readParent,
        IVersioLogger log,
        string category,
        string subject,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(gonePaths);
        ArgumentNullException.ThrowIfNull(readParent);
        ArgumentNullException.ThrowIfNull(log);

        if (gonePaths.Count == 0)
        {
            return snapshot;
        }

        var gone = gonePaths.Select(p => p.Replace('\\', '/').TrimStart('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projects = new List<WorkspaceProject>(snapshot.Projects.Count);
        var problems = new List<string>(snapshot.Problems);
        var changed = false;

        foreach (var project in snapshot.Projects)
        {
            ct.ThrowIfCancellationRequested();
            var root = project.ExportPath.Replace('\\', '/').Trim('/');
            var described = (project.Manifest?.Objects ?? [])
                .Where(o => o.Path.Length > 0)
                .Select(o => ProjectTreeBuilder.Combine(root, o.Path.Replace('\\', '/')))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = gone
                .Where(p => p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) && !described.Contains(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (missing.Count == 0)
            {
                projects.Add(project);
                continue;
            }

            var manifestPath = $"{root}/{BindingConventions.ManifestFileName}";
            var read = await readParent(manifestPath).ConfigureAwait(false);
            if (read.Text is not { } json)
            {
                if (!read.IsAbsent)
                {
                    problems.Add($"{manifestPath} at the parent could not be read ({read.Failure}); {missing.Count} deleted file(s) are listed by path.");
                }

                projects.Add(project);
                continue;
            }

            var parent = ProjectManifest.Parse(json, out var parentProblems);
            problems.AddRange(parentProblems.Select(p => $"{manifestPath} at the parent: {p}"));
            var named = parent.Objects
                .Where(o => o.Path.Length > 0 && missing.Contains(ProjectTreeBuilder.Combine(root, o.Path.Replace('\\', '/'))))
                .ToList();
            if (named.Count == 0)
            {
                projects.Add(project);
                continue;
            }

            // The whole mirror gone in this version (no manifest at all) takes the parent's
            // vendor and schema with the objects; otherwise this version's manifest keeps its
            // own and only grows the rows.
            var manifest = project.Manifest is { } own
                ? own with { Objects = [.. own.Objects, .. named] }
                : parent with { Objects = named };
            projects.Add(project with { Manifest = manifest });
            changed = true;
            log.Info(category, $"{subject}: {named.Count} deleted object(s) of {project.ProjectPath} named from the parent's manifest");
        }

        return changed || problems.Count != snapshot.Problems.Count
            ? snapshot with { Projects = projects, Problems = problems }
            : snapshot;
    }

    /// <summary>
    /// A project's library, task and device catalogs, read as the desktop's <c>ReadCatalogs</c>
    /// reads them: the manifest names them by form, Core parses them, the tree grafts them.
    /// </summary>
    /// <remarks>
    /// Without them the managers drew with no reference rows (RepoExplorerBuilder treats that as
    /// a degraded view, never an error), and the first web build left them out; the tree now shows
    /// a project's libraries, tasks and devices as the desktop's does. No baseline: a snapshot is
    /// one version, and marks between two come from the comparison, not the tree.
    /// </remarks>
    private static async Task<(IReadOnlyList<LibrarySnapshot>, IReadOnlyList<TaskSnapshot>, IReadOnlyList<DeviceSnapshot>)> ReadCatalogsAsync(
        Func<string, Task<SnapshotRead>> read, HashSet<string> present, ProjectManifest? manifest, string exportPath, List<string> problems)
    {
        if (manifest is null)
        {
            return ([], [], []);
        }

        var root = exportPath.Replace('\\', '/').Trim('/');
        var wanted = manifest.Objects
            .Where(o => o.Path.Length > 0 && o.Form is ExportCoverage.LibrariesForm or ExportCoverage.TaskBothForm or ExportCoverage.DevicesForm)
            .Select(o => (o.Form, Path: $"{root}/{o.Path.Replace('\\', '/')}"))
            .Where(o => present.Contains(o.Path))
            .ToList();
        var reads = await Task.WhenAll(wanted.Select(o => read(o.Path))).ConfigureAwait(false);

        var libraries = new List<LibrarySnapshot>();
        var tasks = new List<TaskSnapshot>();
        var devices = new List<DeviceSnapshot>();
        for (var i = 0; i < wanted.Count; i++)
        {
            // A catalog that could not be read, or read as nothing, is said: its rows used to
            // vanish silently while an unreadable manifest beside it was a problem on the page
            // (found in review). The tree still draws, without that catalog's rows.
            var (form, path) = wanted[i];
            if (reads[i].Text is not { } text)
            {
                lock (problems)
                {
                    problems.Add($"{path} could not be read ({reads[i].Failure ?? "absent"}).");
                }

                continue;
            }

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
    /// <c>DescribeSourceProject</c> takes the list, never the disk, which is exactly what a
    /// commit's tree gives. The first web build left these out.
    /// </remarks>
    private static IEnumerable<WorkspaceProject> SourceProjects(IReadOnlyList<string> files)
    {
        var extensions = PlcFormats.All.SelectMany(f => f.SourceProjectExtensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (extensions.Count == 0)
        {
            yield break;
        }

        var folders = files
            .Select(path =>
            {
                var parts = path.Split('/');
                var at = Array.FindIndex(parts, 0, parts.Length - 1, p => extensions.Contains(System.IO.Path.GetExtension(p)));
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

            var relative = files.Where(k => k.StartsWith(folder + "/", StringComparison.Ordinal)).Select(k => k[(folder.Length + 1)..]).ToList();
            yield return new WorkspaceProject
            {
                ProjectPath = folder,
                ExportPath = folder,
                Manifest = format.DescribeSourceProject(folder, relative),
                ExportState = "read from its files",
            };
        }
    }

    private static async Task<RepoConfig?> ReadConfigAsync(
        Func<string, Task<SnapshotRead>> read, IVersioLogger log, string category, string subject, List<string> problems)
    {
        var text = await read(ConfigPath).ConfigureAwait(false);
        if (text.Text is not { } json)
        {
            problems.Add($"{ConfigPath} could not be read ({text.Failure ?? "absent"}).");
            return null;
        }

        try
        {
            // The desktop's own reader, so a committed file means the same thing everywhere - the
            // defaults it fills in for a null list included, said on the page and in the log as
            // the desktop says them, because a bindings list read as empty is a page with no
            // projects and nothing else to explain why.
            var config = RepoConfigJson.Parse(json, out var repairs);
            foreach (var repair in repairs)
            {
                problems.Add($"{ConfigPath}: {repair}.");
                log.Warn(category, $"{subject}: {ConfigPath}: {repair}");
            }

            return config;
        }
        catch (JsonException ex)
        {
            // The reason travels with the problem: the reader refuses a file for its shape as well
            // as its syntax, and "not valid JSON" would send a reader of the page hunting for a
            // missing comma in a file whose only fault is a null where a binding belongs.
            problems.Add($"{ConfigPath} could not be read as a Versio configuration ({ex.Message}); the repository is shown as plain folders.");
            log.Warn(category, $"{subject}: {ConfigPath} is corrupt ({ex.Message}); shown as plain folders");
            return null;
        }
    }
}
