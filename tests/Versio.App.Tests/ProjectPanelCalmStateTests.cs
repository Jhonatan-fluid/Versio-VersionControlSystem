using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.App.Views.Panels;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The project panel on a day when nothing has changed.
/// </summary>
/// <remarks>
/// <para>
/// Seen in the running window on 2026-09-21, not derived: <c>C:\GIT\Versio</c> open, four
/// tracked projects all in sync, and the middle of the project panel said <i>"Open a
/// repository to see what is in it"</i>. The empty-state trigger read <c>Tree.Count == 0</c>,
/// and the tree hides in-sync projects on purpose — so the calmest state the tool has told its
/// user to open the repository they had open, with the truth in a grey footnote.
/// </para>
/// <para>
/// Nothing tested the summary or the empty state at all. These go through a real repository
/// and a committed mirror, because "in sync" is git's verdict and a stub would be asserting
/// the stub.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class ProjectPanelCalmStateTests
{
    private const string OpenARepository = "Open a repository to see what is in it";

    private static void GivenACommittedProject(TempRepo repo)
    {
        var versio = Path.Combine(repo.Path, ".versio");
        Directory.CreateDirectory(versio);

        File.WriteAllText(Path.Combine(versio, "repo.json"), """
        {
          "schema": 1,
          "bindings": [
            {
              "id": "b1",
              "projectPath": "plc/Machine.project",
              "exportPath": "plc/Machine",
              "enabled": true
            }
          ]
        }
        """);

        var application = Path.Combine(repo.Path, "plc", "Machine", "Application");
        Directory.CreateDirectory(application);

        File.WriteAllBytes(
            Path.Combine(repo.Path, "plc", "Machine.project"), [0x00, 0x01, 0x02, 0x03]);

        File.WriteAllText(Path.Combine(repo.Path, "plc", "Machine", "_manifest.json"), """
        {
          "schema": 4,
          "bindingId": "b1",
          "project": "Machine.project",
          "objectCount": 1,
          "objects": [
            { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_FULL",
              "path": "Application/FB_Motor.st", "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [],
          "skipped": []
        }
        """);

        File.WriteAllText(Path.Combine(application, "FB_Motor.st"), "FUNCTION_BLOCK FB_Motor\n");

        repo.Git("add -A");
        repo.Git("commit -q -m tracked");
    }

    /// <summary>
    /// Open, then commit whatever opening wrote, so the tree has genuinely nothing to show.
    /// </summary>
    /// <remarks>
    /// Opening is not read-only: adoption writes repository files of its own, and those are
    /// changes the tree rightly lists. A calm state is what is left once they are committed.
    /// </remarks>
    private static async Task OpenAndSettleAsync(TempRepo repo, ShellViewModel shell)
    {
        await shell.OpenAsync(repo.Path);
        repo.Git("add -A");
        repo.Git("commit -q --allow-empty -m settled");
        await shell.RefreshAsync();

        Assert.True(shell.Sync.Tree.Count == 0,
            "the tree still lists: " + string.Join(", ", shell.Sync.Tree.Select(r => r.Name)) +
            " | projects: " + string.Join(", ", shell.Sync.Projects.Select(p => $"{p.BindingId}={p.State}")));
    }

    /// <summary>
    /// A shell whose catch-up export "succeeds".
    /// </summary>
    /// <remarks>
    /// Without a toolchain the project can never be exported, so the commit gate rightly calls
    /// it "not exported" and the tree keeps it on screen as needing attention — there is no
    /// calm state to test. The capturing toolchain records the export as done without writing
    /// anything, which leaves the committed mirror above as the truth.
    /// </remarks>
    private static ShellViewModel CalmShell() =>
        TestShell.Build(out _, toolchain: new CapturingToolchain());

    [Fact]
    public async Task A_project_with_nothing_changed_is_listed_as_in_sync_not_hidden()
    {
        using var repo = new TempRepo(identity: true);
        GivenACommittedProject(repo);

        using var shell = CalmShell();
        await OpenAndSettleAsync(repo, shell);

        Assert.True(shell.Sync.HasRepository);

        var row = Assert.Single(shell.Sync.InSyncProjects);
        Assert.Equal("Machine.project", row.Name);
        Assert.Equal("plc/Machine.project", row.Path.Replace('\\', '/'));
        Assert.DoesNotContain("not listed", shell.Sync.TreeSummary, StringComparison.Ordinal);
        Assert.Contains("in sync", shell.Sync.TreeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_moves_the_project_out_of_the_calm_list_and_into_the_tree()
    {
        // The other direction, and the one that proves the list is derived from what the tree
        // left out rather than from the bindings: a project must never be in both places.
        using var repo = new TempRepo(identity: true);
        GivenACommittedProject(repo);

        using var shell = CalmShell();
        await OpenAndSettleAsync(repo, shell);
        Assert.Single(shell.Sync.InSyncProjects);

        File.WriteAllText(
            Path.Combine(repo.Path, "plc", "Machine", "Application", "FB_Motor.st"),
            "FUNCTION_BLOCK FB_Motor\nVAR END_VAR\n");
        await shell.RefreshAsync();

        Assert.Empty(shell.Sync.InSyncProjects);
        Assert.Contains(shell.Sync.Tree, r => r.Name == "Machine.project");
    }

    [Fact]
    public void Leaving_the_repository_empties_the_calm_list()
    {
        // Detach clears the tree; a leftover calm list would name the old repository's
        // projects under the new one.
        using var shell = TestShell.Build();
        shell.Sync.InSyncProjects.Add(new InSyncProjectRow("Old.project", "old/Old.project", "in sync"));

        shell.ResetForRepositoryChange(@"C:\work\alpha", @"C:\work\beta");

        Assert.Empty(shell.Sync.InSyncProjects);
        Assert.False(shell.Sync.HasRepository);
    }

    [Fact]
    public void The_window_says_open_a_repository_only_when_none_is_open()
    {
        // The half that is XAML. The view model can be right and the trigger still read the
        // tree; this realises the real panel and looks at what it draws.
        StaHarness.Run(async () =>
        {
            using var repo = new TempRepo(identity: true);
            GivenACommittedProject(repo);

            using var shell = CalmShell();
            var window = new MainWindow(shell);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var panel = Assert.IsType<ProjectPanelView>(window.FindName("ProjectPanel"));

            Lay(content);
            Assert.Equal(Visibility.Visible, EmptyState(panel).Visibility);

            await OpenAndSettleAsync(repo, shell);
            Lay(content);
            Assert.NotEqual(Visibility.Visible, EmptyState(panel).Visibility);

            var calm = CalmList(panel);
            Assert.Equal(Visibility.Visible, calm.Visibility);

            // Under the search box while the tree is empty, so the calm day reads top-down.
            Assert.Equal(Dock.Top, DockPanel.GetDock(calm));
        });
    }

    private static void Lay(FrameworkElement content)
    {
        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();
    }

    private static TextBlock EmptyState(DependencyObject panel) =>
        Assert.Single(Descendants(panel).OfType<TextBlock>(), t => t.Text == OpenARepository);

    private static StackPanel CalmList(DependencyObject panel) =>
        Assert.Single(Descendants(panel).OfType<StackPanel>(),
            s => AutomationProperties.GetName(s) == "Projects in sync");

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            yield return child;

            foreach (var further in Descendants(child))
            {
                yield return further;
            }
        }
    }
}
