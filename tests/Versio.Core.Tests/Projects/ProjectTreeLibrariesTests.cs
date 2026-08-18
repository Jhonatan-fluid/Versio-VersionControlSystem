using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// One row per library reference, drawn under the manager that owns it.
/// </summary>
/// <remarks>
/// <para>
/// The catalog parsed for weeks with no consumer; these tests arrive with the consumer. The
/// point of the rows is the marks: with HEAD's copy of the catalog beside the working tree's,
/// a version bump reads as <i>that library</i> changing rather than as "the library manager
/// changed" — which is all the file-level status could ever say.
/// </para>
/// <para>
/// The fixture shape mirrors the real one: a manager where most references are system
/// libraries pulled in by a plug-in, and the few somebody actually chose risk being lost
/// among them. That is why order is asserted, not only membership.
/// </para>
/// </remarks>
public sealed class ProjectTreeLibrariesTests
{
    private const string Export = "plc/Machine";
    private const string Folder = "Device/Plc Logic/Application";
    private const string YamlPath = $"{Folder}/Library Manager.libraries.yaml";
    private const string RepoYamlPath = $"{Export}/{YamlPath}";

    private static ProjectManifest Manifest() => new()
    {
        Schema = 5,
        Project = "Machine.project",
        Objects =
        [
            new ProjectObject
            {
                Name = "Library Manager",
                Path = YamlPath,
                Guid = "libman",
                Type = "LIBRARY_MANAGER",
                Form = ExportCoverage.LibrariesForm,
            },
            new ProjectObject
            {
                Name = "Main",
                Path = $"{Folder}/Main.st",
                Guid = "main",
                Type = "POU",
                Form = "ST_DECL",
            },
        ],
    };

    private static LibraryReference Reference(
        string placeholder, string redirection = "", bool system = false,
        IReadOnlyDictionary<string, string>? parameters = null) =>
        new()
        {
            Name = $"#{placeholder}",
            Placeholder = placeholder,
            Namespace = placeholder,
            DefaultResolution = $"{placeholder}, * (System)",
            Redirection = redirection,
            SystemLibrary = system,
            Parameters = parameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

    private static LibraryCatalog Catalog(params LibraryReference[] references) => new()
    {
        Schema = 1,
        Vendor = "codesys",
        Manager = "Library Manager",
        Path = $"{Folder}/Library Manager",
        References = references,
    };

    private static ProjectTreeNode Build(
        LibrarySnapshot? snapshot,
        IReadOnlyList<FileChange>? changes = null,
        bool changedOnly = false,
        string search = "") =>
        ProjectTreeBuilder.Build(
            Manifest(), Export, changes ?? [],
            new ProjectTreeOptions { ChangedOnly = changedOnly, Search = search },
            snapshot is null ? [] : [snapshot]);

    private static ProjectTreeNode Manager(ProjectTreeNode tree) =>
        Flatten(tree).First(n => n.Name == "Library Manager");

    private static IEnumerable<ProjectTreeNode> Flatten(ProjectTreeNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    [Fact]
    public void Every_reference_is_a_row_under_its_manager()
    {
        var manager = Manager(Build(new LibrarySnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(
                Reference("Standard", "Standard, 3.5.22.0 (System)"),
                Reference("VisuUserManagement", system: true)),
        }));

        Assert.Equal(2, manager.Children.Count);
        Assert.All(manager.Children, c => Assert.Equal(ProjectNodeKind.Library, c.Kind));
    }

    [Fact]
    public void Chosen_libraries_sort_before_system_ones()
    {
        // Alphabetical order would put the two system libraries around the one somebody
        // chose; the fixture's real manager buries three chosen references among eighteen
        // system ones the same way.
        var manager = Manager(Build(new LibrarySnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(
                Reference("Alarms", system: true),
                Reference("Standard", "Standard, 3.5.22.0 (System)"),
                Reference("ZBuffer", system: true)),
        }));

        Assert.Equal(["Standard", "Alarms", "ZBuffer"],
            manager.Children.Select(c => c.Name));
    }

    [Fact]
    public void The_row_carries_the_version_and_names_the_system_flag()
    {
        var manager = Manager(Build(new LibrarySnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(
                Reference("Standard", "Standard, 3.5.22.0 (System)"),
                Reference("VisuElems", system: true)),
        }));

        Assert.Equal("Standard, 3.5.22.0 (System)",
            manager.Children[0].Detail);
        Assert.Equal("VisuElems, * (System) · system",
            manager.Children[1].Detail);
    }

    [Fact]
    public void A_library_row_opens_the_managers_own_file()
    {
        // The reference has no file of its own: its facts live in the manager's YAML, and
        // selecting the row must open the one place they are recorded.
        var manager = Manager(Build(new LibrarySnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(Reference("Standard")),
        }));

        Assert.Equal(manager.Files, Assert.Single(manager.Children).Files);
    }

    [Fact]
    public void Without_a_baseline_nothing_is_marked()
    {
        // A fresh export's twenty references drawn as twenty additions would bury the one
        // line git actually has to say. The manager's own row already carries the file state.
        var manager = Manager(Build(
            new LibrarySnapshot
            {
                Path = RepoYamlPath,
                Current = Catalog(Reference("Standard"), Reference("Util")),
            },
            changes: [new FileChange
            {
                Path = RepoYamlPath,
                Staged = ChangeKind.Unmodified,
                Unstaged = ChangeKind.Modified,
            }]));

        Assert.Equal(ChangeKind.Modified, manager.Status);
        Assert.All(manager.Children, c => Assert.Equal(ChangeKind.Unmodified, c.Status));
        Assert.Equal(0, manager.ChangedDescendants);
    }

    [Fact]
    public void A_version_bump_reads_as_that_library()
    {
        // The sentence this whole feature exists for.
        var manager = Manager(Build(new LibrarySnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(
                Reference("Standard", "Standard, 3.5.22.0 (System)"),
                Reference("Util", "Util, 3.5.22.0 (System)")),
            Baseline = Catalog(
                Reference("Standard", "Standard, 3.5.19.0 (System)"),
                Reference("Util", "Util, 3.5.22.0 (System)")),
        }));

        Assert.Equal(ChangeKind.Modified,
            manager.Children.Single(c => c.Name == "Standard").Status);
        Assert.Equal(ChangeKind.Unmodified,
            manager.Children.Single(c => c.Name == "Util").Status);

        // And the badge rises, so the change is not hidden behind a collapsed manager.
        Assert.Equal(1, manager.ChangedDescendants);
        Assert.Equal(ChangeKind.Modified, manager.RollUp);
    }

    [Fact]
    public void A_parameter_change_is_a_change()
    {
        // Real, diffable project state: the fixture pins Util's IBLOCKSIZE, and changing
        // that number changes what the machine does.
        var manager = Manager(Build(new LibrarySnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(Reference("Util", parameters: new Dictionary<string, string>
                (StringComparer.Ordinal) { ["IBLOCKSIZE"] = "22800" })),
            Baseline = Catalog(Reference("Util", parameters: new Dictionary<string, string>
                (StringComparer.Ordinal) { ["IBLOCKSIZE"] = "4096" })),
        }));

        Assert.Equal(ChangeKind.Modified, Assert.Single(manager.Children).Status);
    }

    [Fact]
    public void An_added_reference_is_marked_added()
    {
        var manager = Manager(Build(new LibrarySnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(Reference("Standard"), Reference("Util")),
            Baseline = Catalog(Reference("Standard")),
        }));

        Assert.Equal(ChangeKind.Added,
            manager.Children.Single(c => c.Name == "Util").Status);
    }

    [Fact]
    public void A_removed_reference_still_gets_a_row_marked_deleted()
    {
        // A library that silently vanished from the panel is indistinguishable from one
        // that was never referenced — and removal is exactly the change a reviewer must see.
        var manager = Manager(Build(new LibrarySnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(Reference("Standard")),
            Baseline = Catalog(
                Reference("Standard"),
                Reference("Util", "Util, 3.5.22.0 (System)")),
        }));

        var removed = manager.Children.Single(c => c.Name == "Util");
        Assert.Equal(ChangeKind.Deleted, removed.Status);
        Assert.Equal("Util, 3.5.22.0 (System)", removed.Detail);
    }

    [Fact]
    public void An_unreadable_baseline_marks_nothing()
    {
        // Half a comparison is a guess. LibraryCatalog.Parse answers Empty for a truncated
        // file, and Empty must mean "say nothing", not "everything is new".
        var manager = Manager(Build(new LibrarySnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(Reference("Standard"), Reference("Util")),
            Baseline = LibraryCatalog.Empty,
        }));

        Assert.All(manager.Children, c => Assert.Equal(ChangeKind.Unmodified, c.Status));
    }

    [Fact]
    public void An_invalid_catalog_adds_no_rows()
    {
        var manager = Manager(Build(new LibrarySnapshot
        {
            Path = RepoYamlPath,
            Current = LibraryCatalog.Empty,
        }));

        Assert.Empty(manager.Children);
    }

    [Fact]
    public void A_snapshot_matching_no_manifest_record_is_ignored()
    {
        // The manifest is the authority on what exists: a stray catalog must not conjure a
        // manager the export never wrote.
        var tree = Build(new LibrarySnapshot
        {
            Path = $"{Export}/{Folder}/Nonexistent.libraries.yaml",
            Current = Catalog(Reference("Standard")),
        });

        Assert.DoesNotContain(Flatten(tree), n => n.Kind == ProjectNodeKind.Library);
    }

    [Fact]
    public void Search_reaches_into_the_references()
    {
        // Typing a library's name has to surface it even though the manager's own name
        // does not match — the box searches the project, and references are part of it.
        var manager = Manager(Build(
            new LibrarySnapshot
            {
                Path = RepoYamlPath,
                Current = Catalog(Reference("Standard"), Reference("Util")),
            },
            search: "Util"));

        Assert.Equal("Util", Assert.Single(manager.Children).Name);
    }

    [Fact]
    public void The_changed_only_filter_keeps_the_bumped_library_and_drops_the_rest()
    {
        var manager = Manager(Build(
            new LibrarySnapshot
            {
                Path = RepoYamlPath,
                Current = Catalog(
                    Reference("Standard", "Standard, 3.5.22.0 (System)"),
                    Reference("Util")),
                Baseline = Catalog(
                    Reference("Standard", "Standard, 3.5.19.0 (System)"),
                    Reference("Util")),
            },
            changes: [new FileChange
            {
                Path = RepoYamlPath,
                Staged = ChangeKind.Unmodified,
                Unstaged = ChangeKind.Modified,
            }],
            changedOnly: true));

        Assert.Equal("Standard", Assert.Single(manager.Children).Name);
    }

    [Fact]
    public void Two_references_with_one_name_collapse_to_one_row()
    {
        // The name is the row's identity across rebuilds; a machine-written file should
        // never repeat one, but a truncated write is a file on disk like any other.
        var manager = Manager(Build(new LibrarySnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(Reference("Standard"), Reference("Standard")),
        }));

        Assert.Single(manager.Children);
    }

    [Fact]
    public void The_workspace_tree_hands_the_catalogs_through()
    {
        // The shell builds WorkspaceProjects, not ProjectTreeBuilder calls — so the seam
        // that forgets to forward the snapshots would pass every test above and show nothing.
        var roots = WorkspaceTreeBuilder.Build(
            [new WorkspaceProject
            {
                ProjectPath = "plc/Machine.project",
                ExportPath = Export,
                Manifest = Manifest(),
                Libraries =
                [
                    new LibrarySnapshot
                    {
                        Path = RepoYamlPath,
                        Current = Catalog(Reference("Standard")),
                    },
                ],
                NeedsAttention = true,
            }],
            changes: []);

        var manager = Flatten(roots.Single()).First(n => n.Name == "Library Manager");
        Assert.Equal("Standard", Assert.Single(manager.Children).Name);
    }
}
