using Versio.Core.Git;
using Versio.Core.Plc;

namespace Versio.Core.Projects;

/// <summary>
/// The open clone as a folder tree, with each tracked PLC project grafted in as the same
/// object tree the Project panel already builds.
/// </summary>
/// <remarks>
/// <para>
/// Listing plus graft, nothing else. The object graph under a <c>.project</c> comes from
/// <see cref="WorkspaceTreeBuilder.BuildProjectNode"/> — a second shape here would be a
/// second answer to "what is this project", and the two would drift.
/// </para>
/// <para>
/// Empty <paramref name="changes"/> is the point: this panel visualizes, it does not review.
/// Git's status never enters, so no row carries an M/A/D glyph.
/// </para>
/// </remarks>
public static class RepoExplorerBuilder
{
    /// <param name="formats">
    /// Which format claims a path. Defaults to <see cref="PlcFormats.ForPath"/>; injected the
    /// way <c>OrphanScanner</c> takes it, so a test need not touch the static registry.
    /// </param>
    public static IReadOnlyList<ProjectTreeNode> Build(
        IReadOnlyList<string> files,
        IReadOnlyList<WorkspaceProject> projects,
        ProjectTreeOptions? options = null,
        Func<string, IPlcProjectFormat?>? formats = null)
    {
        var opts = options ?? new ProjectTreeOptions
        {
            ChangedOnly = false,
            HideUnchangedProjects = false,
        };

        var listed = files
            .Select(Normalize)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Before any claiming: an untracked project is labelled as readily as a tracked one.
        var toolchains = ToolchainLabels(
            listed.Concat(projects.Select(p => Normalize(p.ProjectPath))),
            projects,

            // A source project is a folder no ForPath lookup claims, so it would get no pill and
            // read as an ordinary directory beside the projects that have one (the owner,
            // 2026-10-10, on RefrigeratorControl_FBS).
            formats ?? (path => PlcFormats.ForPath(path) ?? PlcFormats.ForSourceProject(path)));

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var grafts = new List<ProjectTreeNode>();

        foreach (var project in projects.OrderBy(p => p.ProjectPath, StringComparer.OrdinalIgnoreCase))
        {
            var node = WorkspaceTreeBuilder.BuildProjectNode(project, [], opts, claimed);

            var exportRoot = Normalize(project.ExportPath).TrimEnd('/');
            if (exportRoot.Length > 0)
            {
                var prefix = exportRoot + "/";
                foreach (var path in listed)
                {
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(path, exportRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        claimed.Add(path);
                    }
                }
            }

            // ProjectTreeBuilder already dropped unmatched object children. A project whose
            // name does not match and that then has nothing underneath it has nothing to
            // show in this listing.
            if (!string.IsNullOrWhiteSpace(opts.Search) &&
                !ProjectTreeBuilder.Matches(node.Name, opts.Search) &&
                node.Children.Count == 0)
            {
                continue;
            }

            grafts.Add(node);
        }

        var remaining = listed
            .Where(p => !claimed.Contains(p))
            .Where(p => PathMatches(p, opts.Search));

        var root = new Folder();
        foreach (var path in remaining)
        {
            root.AddFile(path);
        }

        foreach (var graft in grafts)
        {
            root.AddGraft(graft);
        }

        return root.ToNodes("", toolchains);
    }

    /// <summary>
    /// Where each detected PLC project's pill goes, and what it says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On the folder when the project is a folder; on the project's own row when it is one
    /// file.</b> A TIA Portal or TwinCAT project is the directory around its pointer — that
    /// folder is the project, and labelling it is what lets a collapsed tree say what is inside.
    /// A CODESYS <c>.project</c> is the whole project in one file, and its parent folder is
    /// often just somewhere projects are kept — <c>fixtures/projects</c> here holds two of them
    /// beside a TIA and a TwinCAT project, and a "CODESYS" pill on it would be a claim about
    /// the folder that is not true. <see cref="IPlcProjectFormat.PayloadPathFor"/> already says
    /// which kind each format is, so the tree never learns which vendors are directories.
    /// </para>
    /// <para>
    /// <b>Templates and archives are left unlabelled</b>, as the orphan scanner leaves them
    /// unlisted: a pill reads as "a project lives here", and a <c>_template.project</c> beside
    /// the real one would put a second pill on the same folder for a file nobody works in.
    /// </para>
    /// <para>
    /// Several projects in one folder join their distinct labels — a <c>.ap20</c> and a
    /// <c>.ap21</c> side by side is exactly when the version on the pill matters.
    /// </para>
    /// <para>
    /// <b>A folder that carries the pill carries the export state too.</b> The first cut
    /// moved only the pill up, and the running window showed both CODESYS rows saying
    /// "in sync" while the TIA and TwinCAT folders beside them said nothing — all four were
    /// tracked and in sync; the state was on the pointer row inside the collapsed folder.
    /// Two kinds of project drawn two ways read as two states. The pointer row keeps its
    /// own copy, since that is the row that opens the project.
    /// </para>
    /// </remarks>
    private static Dictionary<string, Pill> ToolchainLabels(
        IEnumerable<string> paths,
        IReadOnlyList<WorkspaceProject> projects,
        Func<string, IPlcProjectFormat?> formats)
    {
        var labels = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var states = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var stateOf = projects
            .GroupBy(p => Normalize(p.ProjectPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ExportState, StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var format = formats(path);
            if (format is null || format.IsTemplate(path))
            {
                continue;
            }

            var label = format.ProjectLabelFor(path);
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            var payload = Normalize(format.PayloadPathFor(path)).TrimEnd('/');
            var target = payload.Length > 0 ? payload : path;

            if (!labels.TryGetValue(target, out var list))
            {
                labels[target] = list = [];
            }

            if (!list.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(label);
            }

            // Only where the pill left the project's own row: a one-file project's row
            // already says its state, and saying it twice on one row would be noise.
            if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase) &&
                stateOf.TryGetValue(path, out var state) && !string.IsNullOrWhiteSpace(state))
            {
                if (!states.TryGetValue(target, out var held))
                {
                    states[target] = held = [];
                }

                if (!held.Contains(state, StringComparer.OrdinalIgnoreCase))
                {
                    held.Add(state);
                }
            }
        }

        return labels.ToDictionary(
            e => e.Key,
            e => new Pill(
                string.Join(" · ", e.Value.Order(StringComparer.OrdinalIgnoreCase)),
                states.TryGetValue(e.Key, out var s) ? string.Join(" · ", s) : string.Empty),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What a labelled row shows: the toolchain, and for a folder its projects' state.</summary>
    private sealed record Pill(string Label, string State);

    private static bool PathMatches(string path, string search) =>
        string.IsNullOrWhiteSpace(search) ||
        path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => ProjectTreeBuilder.Matches(part, search));

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>One directory in the listing, built before it becomes a node.</summary>
    private sealed class Folder
    {
        private readonly Dictionary<string, Folder> _folders =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _files =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, ProjectTreeNode> _grafts =
            new(StringComparer.OrdinalIgnoreCase);

        public void AddFile(string path)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return;
            }

            Walk(parts)._files[parts[^1]] = path;
        }

        public void AddGraft(ProjectTreeNode node)
        {
            var parts = Normalize(node.Path).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return;
            }

            Walk(parts)._grafts[parts[^1]] = node;
        }

        private Folder Walk(string[] parts)
        {
            var here = this;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!here._folders.TryGetValue(parts[i], out var next))
                {
                    next = new Folder();
                    here._folders[parts[i]] = next;
                }

                here = next;
            }

            return here;
        }

        public IReadOnlyList<ProjectTreeNode> ToNodes(
            string prefix, IReadOnlyDictionary<string, Pill> toolchains)
        {
            var nodes = new List<ProjectTreeNode>();

            foreach (var (name, child) in _folders.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                var path = prefix.Length == 0 ? name : prefix + "/" + name;
                var nested = child.ToNodes(path, toolchains);
                if (nested.Count == 0)
                {
                    continue;
                }

                nodes.Add(new ProjectTreeNode
                {
                    Name = name,
                    Path = path,
                    Kind = ProjectNodeKind.Folder,
                    Children = nested,
                    Toolchain = toolchains.GetValueOrDefault(path)?.Label ?? string.Empty,
                    Detail = toolchains.GetValueOrDefault(path)?.State ?? string.Empty,
                });
            }

            foreach (var (name, node) in _grafts.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                nodes.Add(toolchains.TryGetValue(Normalize(node.Path), out var pill)
                    ? node with { Toolchain = pill.Label }
                    : node);
            }

            foreach (var (name, path) in _files.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (_grafts.ContainsKey(name))
                {
                    continue;
                }

                nodes.Add(new ProjectTreeNode
                {
                    Name = name,
                    Path = path,
                    Kind = ProjectNodeKind.File,
                    Files = [path],
                    Toolchain = toolchains.GetValueOrDefault(path)?.Label ?? string.Empty,
                });
            }

            return nodes;
        }
    }
}
