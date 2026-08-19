using Versio.Core.Git;

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
    public static IReadOnlyList<ProjectTreeNode> Build(
        IReadOnlyList<string> files,
        IReadOnlyList<WorkspaceProject> projects,
        ProjectTreeOptions? options = null)
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

        return root.ToNodes("");
    }

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

        public IReadOnlyList<ProjectTreeNode> ToNodes(string prefix)
        {
            var nodes = new List<ProjectTreeNode>();

            foreach (var (name, child) in _folders.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                var path = prefix.Length == 0 ? name : prefix + "/" + name;
                var nested = child.ToNodes(path);
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
                });
            }

            foreach (var (name, node) in _grafts.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                nodes.Add(node);
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
                });
            }

            return nodes;
        }
    }
}
