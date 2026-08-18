using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.Core.Tests.Projects;

/// <summary>
/// One row per task, drawn under the task configuration that owns it.
/// </summary>
/// <remarks>
/// <para>
/// The library rows' twin, with the two differences that matter pinned here: a task
/// configuration is a <b>two-file leaf</b> — the importable <c>.tasks.yaml</c> and the
/// export-only <c>.xml</c> share one GUID and become one node — so the join cannot assume
/// the node's path is the YAML; and tasks keep the <b>catalog's own order</b>, because that
/// is the configuration's order and two tasks have no alphabet a reviewer expects.
/// </para>
/// </remarks>
public sealed class ProjectTreeTasksTests
{
    private const string Export = "plc/Machine";
    private const string Folder = "Device/Plc Logic/Application";
    private const string YamlPath = $"{Folder}/Task Configuration.tasks.yaml";
    private const string XmlPath = $"{Folder}/Task Configuration.xml";
    private const string RepoYamlPath = $"{Export}/{YamlPath}";

    private static ProjectManifest Manifest() => new()
    {
        Schema = 5,
        Project = "Machine.project",
        Objects =
        [
            // Two records, one GUID: the export writes both halves and the tree folds
            // them into one node — which is exactly the shape the join must survive.
            new ProjectObject
            {
                Name = "Task Configuration",
                Path = YamlPath,
                Guid = "taskcfg",
                Type = "TASK_CONFIGURATION",
                Form = ExportCoverage.TaskBothForm,
            },
            new ProjectObject
            {
                Name = "Task Configuration",
                Path = XmlPath,
                Guid = "taskcfg",
                Type = "TASK_CONFIGURATION",
                Form = ExportCoverage.TaskNativeForm,
            },
        ],
    };

    private static TaskRecord Task(
        string name, string priority = "1", string interval = "t#20ms",
        params string[] pous) =>
        new()
        {
            Name = name,
            Priority = priority,
            Interval = interval,
            Pous = pous,
        };

    private static TaskCatalog Catalog(params TaskRecord[] tasks) => new()
    {
        Schema = 1,
        Vendor = "codesys",
        Configuration = "Task Configuration",
        Path = $"{Folder}/Task Configuration",
        Tasks = tasks,
    };

    private static ProjectTreeNode Build(
        TaskSnapshot? snapshot,
        IReadOnlyList<FileChange>? changes = null,
        string search = "") =>
        ProjectTreeBuilder.Build(
            Manifest(), Export, changes ?? [],
            new ProjectTreeOptions { ChangedOnly = false, Search = search },
            libraries: null,
            tasks: snapshot is null ? [] : [snapshot]);

    private static ProjectTreeNode Configuration(ProjectTreeNode tree) =>
        Flatten(tree).First(n => n.Name == "Task Configuration");

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
    public void Every_task_is_a_row_under_its_two_file_configuration()
    {
        var configuration = Configuration(Build(new TaskSnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(Task("MainTask"), Task("VISU_TASK")),
        }));

        // The leaf really is two files folded into one node — the precondition that makes
        // this test different from the library one.
        Assert.Equal(2, configuration.Files.Count);

        Assert.Equal(2, configuration.Children.Count);
        Assert.All(configuration.Children, c => Assert.Equal(ProjectNodeKind.Task, c.Kind));
    }

    [Fact]
    public void Tasks_keep_the_catalogs_own_order()
    {
        // Alphabetical would swap these. The catalog's order is the configuration's own,
        // which is how the IDE shows them.
        var configuration = Configuration(Build(new TaskSnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(Task("VISU_TASK"), Task("MainTask")),
        }));

        Assert.Equal(["VISU_TASK", "MainTask"],
            configuration.Children.Select(c => c.Name));
    }

    [Fact]
    public void The_row_says_the_schedule_and_the_priority()
    {
        var configuration = Configuration(Build(new TaskSnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(Task("MainTask", priority: "1", interval: "t#20ms")),
        }));

        Assert.Equal("t#20ms · prio 1", Assert.Single(configuration.Children).Detail);
    }

    [Fact]
    public void A_task_row_opens_the_configurations_files()
    {
        var configuration = Configuration(Build(new TaskSnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(Task("MainTask")),
        }));

        Assert.Equal(configuration.Files, Assert.Single(configuration.Children).Files);
    }

    [Fact]
    public void Without_a_baseline_nothing_is_marked()
    {
        var configuration = Configuration(Build(
            new TaskSnapshot
            {
                Path = RepoYamlPath,
                Current = Catalog(Task("MainTask"), Task("VISU_TASK")),
            },
            changes: [new FileChange
            {
                Path = RepoYamlPath,
                Staged = ChangeKind.Unmodified,
                Unstaged = ChangeKind.Modified,
            }]));

        Assert.Equal(ChangeKind.Modified, configuration.Status);
        Assert.All(configuration.Children,
            c => Assert.Equal(ChangeKind.Unmodified, c.Status));
    }

    [Fact]
    public void A_priority_edit_reads_as_that_task()
    {
        var configuration = Configuration(Build(new TaskSnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(Task("MainTask", priority: "2"), Task("VISU_TASK", "31")),
            Baseline = Catalog(Task("MainTask", priority: "1"), Task("VISU_TASK", "31")),
        }));

        Assert.Equal(ChangeKind.Modified,
            configuration.Children.Single(c => c.Name == "MainTask").Status);
        Assert.Equal(ChangeKind.Unmodified,
            configuration.Children.Single(c => c.Name == "VISU_TASK").Status);
        Assert.Equal(1, configuration.ChangedDescendants);
    }

    [Fact]
    public void Rewiring_a_tasks_pous_is_a_change()
    {
        // What a task calls is the schedule's substance; adding a POU to a task changes
        // what the machine runs every cycle.
        var configuration = Configuration(Build(new TaskSnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(Task("MainTask", "1", "t#20ms", "PLC_PRG", "Extra")),
            Baseline = Catalog(Task("MainTask", "1", "t#20ms", "PLC_PRG")),
        }));

        Assert.Equal(ChangeKind.Modified, Assert.Single(configuration.Children).Status);
    }

    [Fact]
    public void A_task_deleted_from_the_catalog_still_gets_a_row_at_the_end()
    {
        // Deleting a task from the YAML is deliberately NOT applied by the import — the
        // task is left alone — so the row matters twice over: it shows the edit, and the
        // reviewer needs to know the project still runs it.
        var configuration = Configuration(Build(new TaskSnapshot
        {
            Path = RepoYamlPath,
            Current = Catalog(Task("MainTask")),
            Baseline = Catalog(Task("VISU_TASK", "31", "100"), Task("MainTask")),
        }));

        Assert.Equal(["MainTask", "VISU_TASK"],
            configuration.Children.Select(c => c.Name));
        Assert.Equal(ChangeKind.Deleted, configuration.Children[1].Status);
    }

    [Fact]
    public void Search_reaches_into_the_tasks()
    {
        var configuration = Configuration(Build(
            new TaskSnapshot
            {
                Path = RepoYamlPath,
                Current = Catalog(Task("MainTask"), Task("VISU_TASK")),
            },
            search: "VISU"));

        Assert.Equal("VISU_TASK", Assert.Single(configuration.Children).Name);
    }

    [Fact]
    public void The_workspace_tree_hands_the_task_catalogs_through()
    {
        var roots = WorkspaceTreeBuilder.Build(
            [new WorkspaceProject
            {
                ProjectPath = "plc/Machine.project",
                ExportPath = Export,
                Manifest = Manifest(),
                Tasks =
                [
                    new TaskSnapshot
                    {
                        Path = RepoYamlPath,
                        Current = Catalog(Task("MainTask")),
                    },
                ],
                NeedsAttention = true,
            }],
            changes: []);

        var configuration = Flatten(roots.Single())
            .First(n => n.Name == "Task Configuration");
        Assert.Equal("MainTask", Assert.Single(configuration.Children).Name);
    }
}
