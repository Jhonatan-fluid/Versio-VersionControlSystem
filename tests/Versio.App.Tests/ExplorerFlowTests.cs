using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Diff;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Core.Projects;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Explorer clicks visualize: same <c>ShowNode</c> / <c>BuildRenderedContent</c> as the
/// Project panel, never a git patch.
/// </summary>
/// <remarks>
/// The mutation that must go red is ignoring <c>visualizeOnly</c>: then an explorer click
/// calls <c>DiffAsync</c>, and <see cref="An_explorer_click_does_not_ask_git_for_a_diff"/>
/// fails. Deleting <c>Explorer.NodeSelected</c>'s subscription fails
/// <see cref="An_explorer_selection_reaches_the_pane"/>, which is the CollaboratorSeam row
/// this extraction needed.
/// </remarks>
public sealed class ExplorerFlowTests
{
    [Fact]
    public void Selecting_a_grafted_pou_visualizes_the_file_without_hunks()
    {
        using var dir = new Scratch();
        var st = Path.Combine(dir.Path, "Simulation.st");
        File.WriteAllText(st, "PROGRAM Simulation\nEND_PROGRAM\n");

        using var shell = TestShell.Build();
        shell.PreviewRepository(dir.Path);

        var row = FileRow("Simulation", "Simulation.st", ProjectNodeKind.Object);
        shell.ShowTreeRow(row, visualizeOnly: true);

        Assert.IsType<SourceRender>(shell.RenderedContent);
        Assert.StartsWith("@@", shell.SelectedDiff);
        Assert.EndsWith(".st", shell.SelectedDiffPath);
        Assert.False(shell.CanStageHunks);
        Assert.DoesNotContain("diff --git", shell.SelectedDiff);
        Assert.DoesNotContain("--- ", shell.SelectedDiff.Split('\n')[0]);
        Assert.Equal("Structured Text", LanguageCatalog.ForPath(shell.SelectedDiffPath).Name);
    }

    [Fact]
    public void Selecting_a_python_file_names_python_for_the_highlighter()
    {
        using var dir = new Scratch();
        File.WriteAllText(Path.Combine(dir.Path, "tool.py"), "print('hello')\n");

        using var shell = TestShell.Build();
        shell.PreviewRepository(dir.Path);
        shell.ShowTreeRow(FileRow("tool.py", "tool.py"), visualizeOnly: true);

        Assert.EndsWith(".py", shell.SelectedDiffPath);
        Assert.Equal("Python", LanguageCatalog.ForPath(shell.SelectedDiffPath).Name);
        Assert.StartsWith("@@", shell.SelectedDiff);
        Assert.False(shell.CanStageHunks);
    }

    [Fact]
    public void Selecting_the_project_row_shows_the_summary_card()
    {
        using var dir = new Scratch();
        using var shell = TestShell.Build();
        shell.PreviewRepository(dir.Path);

        var row = new TreeRow(new ProjectTreeNode
        {
            Name = "Foo.project",
            Path = "fixtures/projects/Foo.project",
            Kind = ProjectNodeKind.Project,
            Detail = "tracked",
            Files = ["fixtures/projects/Foo.project"],
        }, 0);

        shell.ShowTreeRow(row, visualizeOnly: true);

        Assert.IsType<ProjectSummaryRender>(shell.RenderedContent);
        Assert.False(shell.CanStageHunks);
        Assert.DoesNotContain('\0', shell.SelectedDiff);
    }

    [Fact]
    public void Selecting_a_library_row_shows_the_catalog()
    {
        using var dir = new Scratch();
        var yaml = Path.Combine(dir.Path, "Library Manager.libraries.yaml");
        File.WriteAllText(yaml, """
            schema: 1
            references:
              - name: Standard
            """);

        using var shell = TestShell.Build();
        shell.PreviewRepository(dir.Path);

        var row = new TreeRow(new ProjectTreeNode
        {
            Name = "Standard",
            Path = "Library Manager/Standard",
            Kind = ProjectNodeKind.Library,
            Files = ["Library Manager.libraries.yaml"],
        }, 2);

        shell.ShowTreeRow(row, visualizeOnly: true);

        var catalog = Assert.IsType<LibraryCatalog>(shell.RenderedContent);
        Assert.True(catalog.IsValid);
        Assert.False(shell.CanStageHunks);
    }

    [Fact]
    public void Selecting_a_task_row_shows_the_task_card()
    {
        using var dir = new Scratch();
        File.WriteAllText(Path.Combine(dir.Path, "Task Configuration.tasks.yaml"), """
            schema: 1
            tasks:
              - name: MainTask
                priority: "1"
                interval: "t#20ms"
            """);

        using var shell = TestShell.Build();
        shell.PreviewRepository(dir.Path);

        var row = new TreeRow(new ProjectTreeNode
        {
            Name = "MainTask",
            Path = "Task Configuration/MainTask",
            Kind = ProjectNodeKind.Task,
            Files = ["Task Configuration.tasks.yaml"],
        }, 2);

        shell.ShowTreeRow(row, visualizeOnly: true);

        var card = Assert.IsType<TaskDetailRender>(shell.RenderedContent);
        Assert.Equal("MainTask", card.Task.Name);
        Assert.False(shell.CanStageHunks);
    }

    [Fact]
    public void An_explorer_click_does_not_ask_git_for_a_diff()
    {
        using var dir = new Scratch();
        File.WriteAllText(Path.Combine(dir.Path, "tool.py"), "print(1)\n");

        var git = new CountingGitClient
        {
            DiffError = new InvalidOperationException("explorer must not call DiffAsync"),
            DiffText = "diff --git a/tool.py b/tool.py\n--- a/tool.py\n+++ b/tool.py\n",
        };

        using var shell = TestShell.Build();
        shell.PreviewRepository(dir.Path, git);

        shell.ShowTreeRow(FileRow("tool.py", "tool.py"), visualizeOnly: true);

        Assert.Equal(0, git.WorkingTreeDiffCalls);
        Assert.StartsWith("@@", shell.SelectedDiff);
        Assert.DoesNotContain("diff --git", shell.SelectedDiff);
        Assert.False(shell.CanStageHunks);
    }

    [Fact]
    public async Task A_project_panel_click_still_asks_git_for_a_diff()
    {
        using var dir = new Scratch();
        File.WriteAllText(Path.Combine(dir.Path, "tool.py"), "print(1)\n");

        var git = new CountingGitClient
        {
            DiffError = null,
            DiffText = "diff --git a/tool.py b/tool.py\n--- a/tool.py\n+++ b/tool.py\n@@ -1 +1 @@\n-a\n+b\n",
        };

        using var shell = TestShell.Build();
        shell.PreviewRepository(dir.Path, git);

        await shell.ShowWorkingTreePathAsync("tool.py");

        Assert.True(git.WorkingTreeDiffCalls > 0);
        Assert.Contains("diff --git", shell.SelectedDiff);
    }

    [Fact]
    public void An_explorer_selection_reaches_the_pane()
    {
        // The subscription is one line. Deleting it left every other test green.
        using var dir = new Scratch();
        File.WriteAllText(Path.Combine(dir.Path, "tool.py"), "print(1)\n");

        using var shell = TestShell.Build();
        shell.PreviewRepository(dir.Path);

        shell.Explorer.SelectedNode = FileRow("tool.py", "tool.py");

        Assert.StartsWith("@@", shell.SelectedDiff);
        Assert.False(shell.CanStageHunks);
    }

    [Fact]
    public async Task An_empty_listing_says_so()
    {
        var git = new CountingGitClient { WorkingTree = [] };
        var explorer = new ExplorerViewModel(new VersioLogger(new SystemClock()), () => []);
        explorer.Attach(git, Path.GetTempPath());
        explorer.IsVisible = true;
        await explorer.RefreshAsync();

        Assert.Empty(explorer.Tree);
        Assert.Equal("Nothing to list in this repository.", explorer.Summary);
    }

    private static TreeRow FileRow(string name, string path,
                                   ProjectNodeKind kind = ProjectNodeKind.File) =>
        new(new ProjectTreeNode
        {
            Name = name,
            Path = path,
            Kind = kind,
            Files = [path],
        }, 0);

    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "versio-explorer-" + Guid.NewGuid().ToString("N"));

        public Scratch() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a locked file must not fail the suite.
            }
        }
    }
}
