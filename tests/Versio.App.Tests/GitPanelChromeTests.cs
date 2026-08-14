using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The git panel, actually rendered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Constructing the window is not enough for a panel that starts collapsed.</b> WPF resolves
/// a <c>StaticResource</c> inside a template when the template is instantiated, not when the
/// XAML is parsed, and it fails a binding whose property does not exist by rendering nothing at
/// all — no exception, no warning, no failing test. Both failures sail past every other test in
/// the suite and land in front of whoever clicked the rail.
/// </para>
/// <para>
/// <b>This is also what a binding trace could not do here.</b> Running the app with
/// <c>--trace-bindings</c> produced an empty file, because the git panel starts
/// <c>Collapsed</c> and its bindings were never evaluated — the exact trap
/// <c>App.xaml.cs</c> documents, where a clean trace over a panel nobody populated is
/// indistinguishable from success. Showing the panel through the rail and measuring it is the
/// version of that check which cannot pass vacuously.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class GitPanelChromeTests
{
    private static (MainWindow Window, ShellViewModel Shell) Build()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        var shell = new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);
        return (new MainWindow(shell), shell);
    }

    /// <summary>Show the git panel the way a user does, and lay it out.</summary>
    private static (Border Panel, FrameworkElement Content) ShowGitPanel(MainWindow window)
    {
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

        // Through the rail rather than by setting ActivePanel: the handler behind it is what
        // makes the panel visible, and a test that bypasses it proves less than it appears to.
        var rail = Assert.IsType<RadioButton>(window.FindName("RailGit"));
        rail.IsChecked = true;

        var panel = Assert.IsType<Border>(window.FindName("GitPanel"));
        Assert.Equal(Visibility.Visible, panel.Visibility);

        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();

        return (panel, content);
    }

    [Fact]
    public void The_git_panel_lays_out_when_the_rail_is_clicked()
    {
        StaHarness.Run(() =>
        {
            var (window, _) = Build();
            var (panel, _) = ShowGitPanel(window);

            Assert.True(panel.DesiredSize.Width > 0);
        });
    }

    [Fact]
    public void The_missing_git_banner_stays_hidden_until_the_probe_has_answered()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            ShowGitPanel(window);

            // GitPresence.Unknown, because nothing has probed. If Unknown counted as Missing
            // this banner would be up on every launch for the moment before the probe returns,
            // announcing a missing dependency that is sitting right there.
            Assert.False(shell.GitIsMissing);

            var banner = Banner(window, "GIT NOT AVAILABLE");
            Assert.Equal(Visibility.Collapsed, banner.Visibility);
        });
    }

    [Fact]
    public void The_identity_prompt_stays_hidden_with_no_repository_open()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            ShowGitPanel(window);

            // Nothing to be missing an identity for. A prompt on an empty shell is noise.
            Assert.False(shell.IdentityIsMissing);
        });
    }

    [Fact]
    public void There_is_exactly_one_commit_button()
    {
        // "Commit & Push" is gone: it bundled two operations with different failure modes, and
        // its failure story was the worst in the panel - the commit succeeds, the push fails,
        // and one message has to describe both. If a second one ever reappears beside it, this
        // is where that gets noticed.
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var (panel, _) = ShowGitPanel(window);

            var commit = AllOf<Button>(panel)
                .Where(b => ReferenceEquals(b.Command, shell.CommitCommand))
                .ToList();

            Assert.Single(commit);
        });
    }

    [Fact]
    public void The_commit_button_shows_its_reason_while_disabled()
    {
        // ToolTipService.ShowOnDisabled is the entire feature: WPF hides a tooltip on a
        // disabled control by default, so without it the one moment the explanation is wanted
        // is the one moment it cannot be seen. It is a property that is either set or silently
        // absent, which is exactly the kind of thing nothing else would ever catch.
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var (panel, _) = ShowGitPanel(window);

            var commit = AllOf<Button>(panel)
                .Single(b => ReferenceEquals(b.Command, shell.CommitCommand));

            Assert.True(ToolTipService.GetShowOnDisabled(commit),
                        "the commit button would hide its explanation while disabled");

            // Dead with no repository open, and saying why.
            Assert.False(commit.IsEnabled);
            Assert.Equal("Nothing to commit.", commit.ToolTip);
        });
    }

    [Fact]
    public void The_commit_caption_follows_what_the_button_will_do()
    {
        // The caption is the safeguard. "Commit" over a staged set means the unstaged files are
        // being left behind, and somebody who staged three on purpose has to be able to see
        // that before pressing rather than by reading the commit afterwards.
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var (panel, content) = ShowGitPanel(window);

            var commit = AllOf<Button>(panel)
                .Single(b => ReferenceEquals(b.Command, shell.CommitCommand));

            Assert.Equal("Commit", commit.Content);

            shell.Unstaged.Add(new ChangeRow(new FileChange
            {
                Path = "a.st",
                Staged = ChangeKind.Unmodified,
                Unstaged = ChangeKind.Modified,
            }));

            content.UpdateLayout();
            Assert.Equal("Stage all & commit", commit.Content);

            shell.Unstaged[0].IsSelected = true;
            content.UpdateLayout();
            Assert.Equal("Stage & commit (1)", commit.Content);
        });
    }

    [Fact]
    public void A_notification_renders_as_a_card_with_its_text_in_it()
    {
        // GP-0's lesson applied: a DataTemplate's StaticResources are resolved when the
        // template is instantiated, not when the XAML is parsed, and a binding to a property
        // that does not exist renders nothing at all — silently. Posting a real notification
        // and finding its words on screen is the only check that covers either.
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            shell.Notifications.Failure("Push failed.", "rejected — non-fast-forward");

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            var texts = AllOf<TextBlock>(content).Select(t => t.Text).ToList();

            Assert.Contains("Push failed.", texts);
            Assert.Contains("rejected — non-fast-forward", texts);
        });
    }

    [Fact]
    public void A_transient_notification_shows_no_dismiss_control()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            shell.Notifications.Success("Pushed.");

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            // The controls live in a StackPanel bound to IsSticky. A close button on a card
            // about to vanish is a target that moves out from under the cursor.
            Assert.False(shell.Notifications.Visible[0].IsSticky);
        });
    }

    [Fact]
    public void Every_remote_button_explains_itself_while_disabled()
    {
        // With no repository open all four are dead. That is the moment the tooltip matters,
        // and WPF hides tooltips on disabled controls unless ShowOnDisabled is set — so the
        // property is the entire feature and is silently absent if it is forgotten.
        StaHarness.Run(() =>
        {
            var (window, _) = Build();
            var (panel, _) = ShowGitPanel(window);

            var buttons = AllOf<Button>(panel)
                .Where(b => b.Command is not null)
                .ToList();

            Assert.NotEmpty(buttons);

            foreach (var button in buttons.Where(b => b.ToolTip is not null))
            {
                Assert.True(ToolTipService.GetShowOnDisabled(button),
                            "a git-panel button would hide its explanation while disabled");
            }
        });
    }

    [Fact]
    public void Push_and_publish_never_both_show()
    {
        // They are different operations - a plain push on a branch with no upstream fails
        // outright - so exactly one is offered at a time. Both visible would mean two buttons
        // for one slot; neither would mean a state with no way forward.
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            ShowGitPanel(window);

            Assert.NotEqual(shell.ShowPush, shell.ShowPublish);
        });
    }

    [Fact]
    public void Both_staging_lists_scroll_and_neither_can_be_crushed()
    {
        // The layout this replaced: the staged list was docked top with a hard
        // MaxHeight="150" and the unstaged list took everything left. A forty-file stage was
        // trapped in four rows next to an empty list holding four hundred pixels of nothing.
        StaHarness.Run(() =>
        {
            var (window, _) = Build();
            var (panel, _) = ShowGitPanel(window);

            var lists = AllOf<ListBox>(panel).ToList();

            // Staged and unstaged. A third would need the same treatment, and this is where
            // that gets noticed.
            Assert.Equal(2, lists.Count);

            foreach (var list in lists)
            {
                Assert.Equal(ScrollBarVisibility.Auto,
                             ScrollViewer.GetVerticalScrollBarVisibility(list));

                // Both virtualize now. The staged one could not before, behind its ceiling.
                Assert.True(VirtualizingPanel.GetIsVirtualizing(list),
                            "a staging list does not virtualize");

                var row = (FrameworkElement)VisualTreeHelper.GetParent(list)!;
                var height = Grid.GetRow(list) < 0 ? 0 : 1;
                Assert.True(height > 0 || row is not null);
            }

            // Neither pane may be squeezed to nothing by the other.
            var grid = AllOf<Grid>(panel)
                .FirstOrDefault(g => g.RowDefinitions.Count == 5);

            Assert.True(grid is not null, "the two-pane staging grid is not there");
            Assert.All(grid!.RowDefinitions.Where(r => r.Height.IsStar),
                       r => Assert.True(r.MinHeight > 0, "a staging pane can be crushed to nothing"));
        });
    }

    [Fact]
    public void The_panes_can_be_resized_against_each_other()
    {
        StaHarness.Run(() =>
        {
            var (window, _) = Build();
            var (panel, _) = ShowGitPanel(window);

            var splitter = AllOf<GridSplitter>(panel).FirstOrDefault();

            Assert.True(splitter is not null, "there is no splitter between the two panes");
            Assert.Equal(GridResizeDirection.Rows, splitter!.ResizeDirection);
            Assert.Equal(GridResizeBehavior.PreviousAndNext, splitter.ResizeBehavior);
        });
    }

    [Fact]
    public void A_realised_staging_row_carries_a_checkbox_bound_to_its_own_tick()
    {
        // REALISED, not loaded from the template in isolation.
        //
        // DataTemplate.LoadContent() was the first attempt and it threw: the template nests a
        // StaticResource for the shared row body, and loading it outside the tree loses the
        // resource context that would resolve it. Which is fine - the template is not used
        // that way - but it means the only honest check is to put a row in the list and see
        // what the ListBox builds. That also covers more: a binding to a property that does
        // not exist renders nothing at all, silently, and only a realised row shows it.
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var (panel, content) = ShowGitPanel(window);

            shell.Unstaged.Add(new ChangeRow(new FileChange
            {
                Path = "src/App/POU/FB_Motor.st",
                Staged = ChangeKind.Unmodified,
                Unstaged = ChangeKind.Modified,
            }));

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            var check = AllOf<CheckBox>(panel)
                .FirstOrDefault(c => c.DataContext is ChangeRow);

            Assert.True(check is not null, "a realised staging row has no checkbox");

            var binding = BindingOperations.GetBinding(check!, ToggleButton.IsCheckedProperty);
            Assert.Equal("IsSelected", binding?.Path.Path);

            // TwoWay, or ticking a box would change nothing the view model can see.
            Assert.Equal(BindingMode.TwoWay, binding!.Mode);

            // And the shared body came with it: the path is on screen, so the nested
            // StaticResource resolved in the place that matters.
            var texts = AllOf<TextBlock>(panel).Select(t => t.Text).ToList();
            Assert.Contains("src/App/POU/FB_Motor.st", texts);
        });
    }

    [Fact]
    public void Ticking_a_realised_row_reaches_the_view_model()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var (panel, content) = ShowGitPanel(window);

            shell.Unstaged.Add(new ChangeRow(new FileChange
            {
                Path = "a.st",
                Staged = ChangeKind.Unmodified,
                Unstaged = ChangeKind.Modified,
            }));

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            var check = AllOf<CheckBox>(panel).First(c => c.DataContext is ChangeRow);
            check.IsChecked = true;

            // The whole chain: control -> row -> shell count. A one-way binding would pass
            // every other assertion in this file and fail here.
            Assert.Equal(1, shell.UnstagedSelectedCount);
        });
    }

    /// <summary>Find a banner by the section label inside it.</summary>
    private static FrameworkElement Banner(MainWindow window, string label)
    {
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

        var text = AllOf<TextBlock>(content).FirstOrDefault(t => t.Text == label);
        Assert.True(text is not null, $"no element carries the label '{label}'");

        // The Border that wraps it: two levels up through the StackPanel it sits in.
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(text!);
        while (parent is not null and not Border)
        {
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }

        return Assert.IsType<Border>(parent);
    }

    private static IEnumerable<T> AllOf<T>(DependencyObject root)
        where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();

            if (node is T wanted)
            {
                yield return wanted;
            }

            var children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
            {
                queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            }
        }
    }
}
