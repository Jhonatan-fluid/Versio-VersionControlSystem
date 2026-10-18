using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
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
    public async Task Opening_a_fresh_clone_that_matches_HEAD_exports_nothing()
    {
        // 2026-10-16: no .versio/local/state.json on a second machine, so the catch-up took
        // every committed, untouched fixture as "changed since last export" and ran the
        // toolchain over all four. Nothing had changed, so nothing should run.
        using var repo = new TempRepo(identity: true);
        GivenACommittedProject(repo);

        var toolchain = new CapturingToolchain();
        using var shell = TestShell.Build(out _, toolchain: toolchain);
        await shell.OpenAsync(repo.Path);

        Assert.Empty(toolchain.Jobs);
        Assert.Equal(0, shell.Sync.UnexportedCount);
        Assert.Equal(string.Empty, shell.Sync.StalenessSummary);
    }

    [Fact]
    public async Task A_discard_that_leaves_the_project_at_HEAD_is_not_reported_as_unexported()
    {
        // 2026-10-17: Discard clears the export record on purpose, and the matches-HEAD check
        // ran only when the repository was opened, so two discarded projects - untouched and
        // identical to HEAD - would have been exported by the next commit. It runs on every
        // staleness refresh now.
        using var repo = new TempRepo(identity: true);
        GivenACommittedProject(repo);

        var toolchain = new CapturingToolchain();
        using var shell = TestShell.Build(out _, toolchain: toolchain);

        // Settled first, as a real repository is: opening writes the .gitattributes and
        // .gitignore rules, and a project file restored under an uncommitted LFS rule is a
        // different test.
        await OpenAndSettleAsync(repo, shell);
        Assert.Equal(0, shell.Sync.UnexportedCount);

        // A MIRROR file, as on 2026-10-17: Discard releases a binding only for files inside
        // its mirror (BindingConventions.OwnerOf), and that release is what clears the record.
        // An earlier version of this test discarded the project file, never reached the
        // release, and stayed green with the wiring deleted.
        const string mirrorFile = "plc/Machine/Application/FB_Motor.st";
        File.WriteAllText(Path.Combine(repo.Path, "plc", "Machine", "Application", "FB_Motor.st"),
            "FUNCTION_BLOCK FB_Motor\nVAR x : INT; END_VAR\n");
        await shell.RefreshAsync();
        await Eventually.Until(() => shell.Pending.Any(r => r.Path == mirrorFile),
            "the mirror edit is listed");

        shell.Pending.Single(r => r.Path == mirrorFile).IsSelected = true;
        shell.Asked().Destructive = _ => true;
        var jobsBefore = toolchain.Jobs.Count;
        await Eventually.Press(shell.DiscardCommand, "discard");
        await Eventually.Until(() => shell.Pending.All(r => r.Path != mirrorFile),
            "the mirror edit was discarded",
            () => $"pending: {string.Join(", ", shell.Pending.Select(r => r.Path + (r.IsSelected ? "*" : "")))} | " +
                  $"status: {shell.StatusSummary} | jobs: {toolchain.Jobs.Count}");
        await shell.RefreshAsync();

        Assert.Equal(0, shell.Sync.UnexportedCount);
        Assert.Equal(string.Empty, shell.Sync.StalenessSummary);
        Assert.Equal(jobsBefore, toolchain.Jobs.Count);
    }

    [Fact]
    public async Task A_failed_export_is_named_as_a_failure_not_as_unexported_changes()
    {
        // Seen 2026-10-16: four exports failed for reasons outside the projects, which were
        // identical to HEAD. The banner said "4 projects have changes that have not been
        // exported", every row said "not exported", and the Changes panel was correctly
        // empty. So the user went looking for changes that did not exist.
        using var repo = new TempRepo(identity: true);
        GivenACommittedProject(repo);

        // A real change, so the open has an export to attempt. An untouched clone is adopted
        // from HEAD and runs nothing (the test above).
        File.WriteAllBytes(Path.Combine(repo.Path, "plc", "Machine.project"), [0x09, 0x09]);

        using var shell = TestShell.Build(out _, toolchain: new CapturingToolchain
        {
            FailWith = "profile 'CODESYS V3.5 SP22 Patch 2' is not installed",
        });

        // Not OpenAndSettleAsync: that asserts a calm tree, and a failed project rightly
        // stays in it.
        await shell.OpenAsync(repo.Path);
        repo.Git("add -A");
        repo.Git("commit -q --allow-empty -m settled");
        await shell.RefreshAsync();

        // The failure is the project's only story: git has nothing to say about it.
        Assert.Contains(shell.Sync.Tree, r => r.Name == "Machine.project");

        Assert.Contains("failed to sync", shell.Sync.StalenessSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("have not been exported", shell.Sync.StalenessSummary,
            StringComparison.Ordinal);
        Assert.False(shell.Sync.HasUnexportedChanges);

        var row = Assert.Single(shell.Sync.Projects, p => p.BindingId == "b1");
        Assert.Equal("failed", row.State);

        // And the card says why, in the toolchain's words rather than "export failed" alone.
        shell.Sync.RefreshFailure(row.Path);
        Assert.Contains("is not installed", shell.Sync.FailureDetail, StringComparison.Ordinal);
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

            // The sentence carries the two ways to act on it, and Open reaches the window's
            // folder picker through the routed command (the picker itself cannot open here).
            var open = OpenButton(panel);
            var actions = (FrameworkElement)((FrameworkElement)open.Parent).Parent;
            Assert.Equal(Visibility.Visible, actions.Visibility);
            Assert.Same(ApplicationCommands.Open, open.Command);
            Assert.Contains(window.CommandBindings.OfType<CommandBinding>(),
                b => b.Command == ApplicationCommands.Open);

            // Reaching the button is the half visibility cannot prove. The TreeView
            // shares this Grid, is always present, and Transparent is hit-testable —
            // declared over the buttons, it swallowed every click while they drew
            // normally (seen on the running app, 2026-10-14). Ask the question the
            // mouse asks: what does a click at the button's centre actually land on?
            AssertClickable(content, open);
            AssertClickable(content, CloneButton(panel));

            await OpenAndSettleAsync(repo, shell);
            Lay(content);
            Assert.NotEqual(Visibility.Visible, EmptyState(panel).Visibility);
            Assert.NotEqual(Visibility.Visible, actions.Visibility);

            var calm = CalmList(panel);
            Assert.Equal(Visibility.Visible, calm.Visibility);

            // At the bottom even with the tree empty, and drawn as a note: the top of the panel
            // belongs to what needs review, and a calm project does not. It used to dock under
            // the search box with a green dot per row, and read as the panel's content (seen
            // 2026-10-03).
            Assert.Equal(Dock.Bottom, DockPanel.GetDock(calm));
            Assert.Empty(Descendants(calm).OfType<System.Windows.Shapes.Ellipse>());
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

    private static Button OpenButton(DependencyObject panel) =>
        Assert.Single(Descendants(panel).OfType<Button>(), b => Equals(b.Content, "Open a repository…"));

    private static Button CloneButton(DependencyObject panel) =>
        Assert.Single(Descendants(panel).OfType<Button>(), b => Equals(b.Content, "Clone…"));

    private static void AssertClickable(FrameworkElement root, Button button)
    {
        var centre = button.TranslatePoint(
            new Point(button.ActualWidth / 2, button.ActualHeight / 2), root);
        var hit = VisualTreeHelper.HitTest(root, centre)?.VisualHit;

        for (var walk = hit; walk is not null; walk = VisualTreeHelper.GetParent(walk))
        {
            if (ReferenceEquals(walk, button))
            {
                return;
            }
        }

        Assert.Fail(
            $"a click at {centre} lands on {hit?.GetType().Name ?? "nothing"}, " +
            $"not on \"{button.Content}\" — something hit-testable is drawn over it");
    }

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
