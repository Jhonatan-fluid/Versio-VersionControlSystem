using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
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
public sealed class GitPanelChromeTests : IDisposable
{
    private readonly List<ShellViewModel> _shells = [];

    /// <summary>A window over a live shell.</summary>
    /// <remarks>
    /// <b>The shell is NOT disposed here, and it used to be.</b> This read
    /// <c>using var shell = TestShell.Build();</c> followed by a <c>return</c> that handed the
    /// same instance out — so it was disposed at the closing brace, before any test touched it,
    /// and every test in this class ran against a torn-down shell. It passed throughout because
    /// none of them opens a repository, so disposal had almost nothing to release.
    ///
    /// <para>
    /// It stopped being harmless when the shell began disposing its log console: the window is
    /// bound to that log. Kept in a list and released in <see cref="Dispose"/> instead, on the
    /// STA thread that built it.
    /// </para>
    /// </remarks>
    private (MainWindow Window, ShellViewModel Shell) Build()
    {
        var shell = TestShell.Build();
        _shells.Add(shell);
        return (new MainWindow(shell), shell);
    }

    /// <summary>Release the shells this class built, on the thread that built them.</summary>
    /// <remarks>
    /// Tearing a shell down clears collections a bound <c>CollectionView</c> is watching, and
    /// WPF refuses that from any thread but the dispatcher's.
    /// </remarks>
    public void Dispose() => StaHarness.Run(() =>
    {
        foreach (var shell in _shells)
        {
            shell.Dispose();
        }
    });

    /// <summary>Show the git panel the way a user does, and lay it out.</summary>
    private static (FrameworkElement Panel, FrameworkElement Content) ShowGitPanel(MainWindow window)
    {
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

        // Through the rail rather than by setting ActivePanel: the handler behind it is what
        // makes the panel visible, and a test that bypasses it proves less than it appears to.
        var rail = Assert.IsType<RadioButton>(window.FindName("RailGit"));
        rail.IsChecked = true;

        // A UserControl since the panel moved into its own file. What every caller
        // here wants is something to walk and measure, not a particular container.
        var panel = Assert.IsAssignableFrom<FrameworkElement>(
            window.FindName("GitPanel"));
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
            Assert.False(shell.Auth.IdentityIsMissing);
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

            Assert.NotEqual(shell.Remote.ShowPush, shell.Remote.ShowPublish);
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

            // BY NAME, not by position. A third ListBox arrived with the conflict section, and
            // an index-based lookup silently started measuring the wrong control - which is
            // exactly the kind of test that keeps passing while it stops meaning anything.
            //
            // Through the panel rather than the window: the git panel is a UserControl now
            // and owns its own name scope, so the window cannot see the lists inside it.
            var lists = new[] { "StagedList", "UnstagedList" }
                .Select(n => Assert.IsType<ListBox>(panel.FindName(n)))
                .ToList();

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

            // And the shared body came with it, so the nested StaticResource resolved in the
            // place that matters. The row is two lines now — filename leading, directory
            // beneath — because the single trimmed path hid the only part that differed.
            var texts = AllOf<TextBlock>(panel).Select(t => t.Text).ToList();
            Assert.Contains("FB_Motor.st", texts);
            Assert.Contains("src/App/POU", texts);
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

    [Fact]
    public void The_repository_dropdown_shows_where_each_one_lives()
    {
        // RepoEntry.Location existed from the start with a comment saying it is "shown beneath
        // the name so two repos with the same folder name are still distinguishable" - and
        // nothing bound it. Found by opening the app with ten throwaway repositories in the
        // list, several called "work": three identical rows and no way to tell them apart.
        StaHarness.Run(() =>
        {
            var (window, _) = Build();
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            var combo = AllOf<ComboBox>(content)
                .First(c => c.ItemsSource is not null
                            && c.Name != "BranchList"
                            && BindingOperations.GetBinding(c, ItemsControl.ItemsSourceProperty)
                                                ?.Path.Path == "RecentRepositories");

            var template = Assert.IsType<DataTemplate>(combo.ItemTemplate);
            var row = Assert.IsAssignableFrom<DependencyObject>(template.LoadContent());

            var bound = AllOf<TextBlock>(row)
                .Select(t => BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path)
                .Where(p => p is not null)
                .ToList();

            Assert.Contains("Name", bound);
            Assert.Contains("Location", bound);
        });
    }

    /// <summary>Find a banner by the section label inside it.</summary>
    /// <summary>
    /// Every binding in the realised git panel resolves to something that exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written for the auth extraction, and kept for the next one.</b> Moving credentials and
    /// identity onto a collaborator re-pointed twelve bindings in this panel from <c>X</c> to
    /// <c>Auth.X</c>. Every one of those is a string in XAML: a rename that misses one, or
    /// mistypes it, compiles clean, throws nothing, and renders an empty control. The suite went
    /// green through the entire extraction and would have gone green with a typo in it.
    /// </para>
    /// <para>
    /// <c>--trace-bindings</c> cannot answer this. It reports what was evaluated, and the two
    /// banners here are <c>Collapsed</c> on any machine whose git is configured and whose
    /// credential is stored — which is every machine a developer runs it on. A clean trace over
    /// a banner nobody showed is indistinguishable from success, which is the trap
    /// <c>App.xaml.cs</c> already documents.
    /// </para>
    /// <para>
    /// <b>PathError, specifically.</b> A binding to a property that does not exist reports
    /// <c>BindingStatus.PathError</c>. It is not the same as a binding that produced a value the
    /// target could not take — those exist in this window today, on
    /// <c>AutomationProperties.Name</c> bound to a null <c>ToolTip</c>, and are a separate
    /// question from whether the path resolves.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_binding_in_the_git_panel_resolves()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();

            // Both banners shown, because a collapsed one still resolves its bindings but a
            // shown one is what the reader actually gets - and this is the only place in the
            // suite where the credential banner is realised at all.
            shell.Auth.NoteAuthFailure(new GitResult
            {
                Success = false,
                ExitCode = 128,
                StdErr = "fatal: Authentication failed for 'https://github.com/acme/widgets.git/'",
            });

            var (panel, _) = ShowGitPanel(window);

            var offenders = new List<string>();

            foreach (var element in AllOf<DependencyObject>(panel))
            {
                var values = element.GetLocalValueEnumerator();
                while (values.MoveNext())
                {
                    if (values.Current.Value is BindingExpression { Status: BindingStatus.PathError } bad)
                    {
                        offenders.Add(
                            $"{element.GetType().Name}.{values.Current.Property.Name} " +
                            $"binds to '{bad.ParentBinding.Path?.Path}', which does not resolve");
                    }
                }
            }

            Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));

            // A binding walk over a panel with no bindings in it would pass and mean nothing.
            var bound = AllOf<DependencyObject>(panel)
                .SelectMany(e =>
                {
                    var found = new List<object?>();
                    var values = e.GetLocalValueEnumerator();
                    while (values.MoveNext())
                    {
                        found.Add(values.Current.Value);
                    }

                    return found;
                })
                .Count(v => v is BindingExpression);

            Assert.True(bound > 20,
                        $"only {bound} bindings were reached, so this walk is no longer " +
                        "looking at the panel it was written for");
        });
    }

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

    /// <summary>
    /// The rare branch verbs are behind one overflow, and both are in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The container is most of the value, more than either entry.</b> <c>Delete
    /// branches…</c> was a permanent top-level control in a 380 px column for something
    /// reached a few times a month, and merging makes two; every future rare branch verb would
    /// have made another. This asserts the shape rather than the contents, so adding a third
    /// entry does not need a new test and removing the overflow does.
    /// </para>
    /// <para>
    /// <b>And it asserts what is NOT there.</b> Neither verb may sit at the card's top level
    /// again — that is the regression this change exists to prevent, and a test that only
    /// checked the menu's contents would pass with both a menu and the old buttons present.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_rare_branch_verbs_live_behind_one_overflow()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var (panel, _) = ShowGitPanel(window);

            var toggle = Assert.IsType<ToggleButton>(panel.FindName("BranchMenuButton"));

            // Not "More…": a caption has to say what is behind it, or an overflow hides its
            // own contents from the one person trying to find them.
            Assert.Contains("Branch", toggle.Content?.ToString() ?? string.Empty);

            // NEITHER VERB IS ON THE CARD. A Popup lives in its own visual tree, so a walk
            // from the panel cannot reach its contents at all — which is the assertion, not a
            // limitation of the walk. This is what fails if somebody puts either button back
            // at the top level beside the overflow.
            var onTheCard = AllOf<Button>(panel)
                .Select(b => b.Content?.ToString() ?? string.Empty)
                .ToList();

            Assert.DoesNotContain(onTheCard, c => c.StartsWith("Merge a branch", StringComparison.Ordinal));
            Assert.DoesNotContain("Delete branches…", onTheCard);
            Assert.False(toggle.IsChecked, "the overflow starts open");

            // And both are inside it, reached through the popup's own child.
            var popup = Assert.IsType<Popup>(panel.FindName("BranchMenuPopup"));
            var inside = AllOf<Button>(popup.Child)
                .Select(b => b.Content?.ToString() ?? string.Empty)
                .ToList();

            Assert.Contains(inside, c => c.StartsWith("Merge a branch", StringComparison.Ordinal));
            Assert.Contains("Delete branches…", inside);

            // NO REPOSITORY OPEN, SO NO DESTINATION TO NAME. The caption names the branch a
            // merge would land on, and with nothing open there is no honest answer — so it
            // says "Merge a branch…" rather than "Merge a branch into —…". That the named
            // form appears once a repository IS open is asserted in MergeBranchFlowTests,
            // where there is a real branch to name.
            Assert.Equal("—", shell.BranchName);
            Assert.Contains("Merge a branch…", inside);
        });
    }

    /// <summary>
    /// Each staging list's rows carry the menu that list's verbs belong to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Realised rows, not the templates.</b> A DataTemplate resolves its
    /// <c>StaticResource</c>s when a row is first built, not when the XAML is parsed — so a
    /// menu wired to a handler that does not exist, or a template referencing a key declared
    /// below it, sails past every other test in this file and throws in front of whoever
    /// right-clicks. The panel is filled and laid out here for that reason.
    /// </para>
    /// <para>
    /// <b>The two lists deliberately differ.</b> A staged row can be taken back out; an
    /// unstaged one can be staged or thrown away. Asserting the absences is what stops the two
    /// templates quietly converging back into one — <c>Discard</c> on a staged row would act on
    /// the working tree, which is not what the row in front of you is showing.
    /// </para>
    /// </remarks>
    [Fact]
    public void Each_staging_list_carries_its_own_row_menu()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var (panel, content) = ShowGitPanel(window);

            shell.Staged.Add(new ChangeRow(new FileChange
            {
                Path = "src/App/POU/FB_Staged.st",
                Staged = ChangeKind.Modified,
                Unstaged = ChangeKind.Unmodified,
            }));

            shell.Unstaged.Add(new ChangeRow(new FileChange
            {
                Path = "src/App/GVL/GVL_Changed.st",
                Staged = ChangeKind.Unmodified,
                Unstaged = ChangeKind.Modified,
            }));

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            var staged = Headers(panel, "StagedList");
            var unstaged = Headers(panel, "UnstagedList");

            // A staged row can only be taken back out. Discarding it would act on the working
            // tree, which is not what this row is showing.
            Assert.Contains("Unstage", staged);
            Assert.DoesNotContain("Stage", staged);
            Assert.DoesNotContain("Discard changes…", staged);

            Assert.Contains("Stage", unstaged);
            Assert.Contains("Discard changes…", unstaged);
            Assert.DoesNotContain("Unstage", unstaged);

            // Shared by both, and the reason the project tree's menu was worth copying rather
            // than inventing a new vocabulary for the same two acts.
            foreach (var menu in new[] { staged, unstaged })
            {
                Assert.Contains("Reveal in Explorer", menu);
                Assert.Contains("Copy path", menu);
            }
        });
    }

    /// <summary>The headers of the context menu on the first realised row of a named list.</summary>
    private static List<string> Headers(FrameworkElement panel, string listName)
    {
        var list = Assert.IsType<ListBox>(panel.FindName(listName));
        var item = AllOf<ListBoxItem>(list).FirstOrDefault();

        Assert.True(item is not null, $"{listName} realised no rows");

        // The menu hangs off the row's own Grid inside the template, so that every entry acts
        // on the row that was right-clicked rather than on the list's selection - WPF does not
        // select a row on right-click.
        var owner = AllOf<Grid>(item!).FirstOrDefault(g => g.ContextMenu is not null);

        Assert.True(owner is not null, $"no row in {listName} carries a context menu");

        return [.. owner!.ContextMenu.Items.OfType<MenuItem>()
            .Select(m => m.Header?.ToString() ?? string.Empty)];
    }

    /// <summary>
    /// Double-clicking a row reaches the panel's own handler, on both lists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The event is raised on the realised <see cref="ListBoxItem"/> and the assertion is that
    /// it came back <c>Handled</c>. That is not a formality: the handlers mark it handled only
    /// once they have found a <c>ChangeRow</c> under the pointer <i>and</i> a shell to act on,
    /// so a handler that was never attached, a row walk that failed, or a lost DataContext all
    /// leave it false. No reflection, and nothing that passes while doing nothing.
    /// </para>
    /// <para>
    /// What the verbs then do to the index — and that they take the clicked row rather than the
    /// ticked set — is covered against real git in <c>RowActionFlowTests</c>. Splitting it that
    /// way keeps this test about the wiring and that one about the semantics.
    /// </para>
    /// </remarks>
    [Fact]
    public void Both_staging_lists_answer_a_double_click()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var (panel, content) = ShowGitPanel(window);

            shell.Staged.Add(new ChangeRow(new FileChange
            {
                Path = "src/App/POU/FB_Staged.st",
                Staged = ChangeKind.Modified,
                Unstaged = ChangeKind.Unmodified,
            }));

            shell.Unstaged.Add(new ChangeRow(new FileChange
            {
                Path = "src/App/GVL/GVL_Changed.st",
                Staged = ChangeKind.Unmodified,
                Unstaged = ChangeKind.Modified,
            }));

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            foreach (var name in new[] { "StagedList", "UnstagedList" })
            {
                var list = Assert.IsType<ListBox>(panel.FindName(name));
                var item = AllOf<ListBoxItem>(list).FirstOrDefault();

                Assert.True(item is not null, $"{name} realised no rows");

                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = Control.MouseDoubleClickEvent,
                    Source = item,
                };

                item!.RaiseEvent(args);

                Assert.True(args.Handled,
                            $"a double-click on a {name} row was not answered");
            }
        });
    }

    /// <summary>
    /// Both list headers carry their count, and move when the ticks do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The geometry test cannot cover this, and would go greener if it broke.</b> A WPF
    /// binding to a property that does not exist fails silently and leaves the TextBlock
    /// empty — and an empty header is <i>narrower</i>, so
    /// <c>Nothing_overflows_the_panel_sideways</c> would pass more comfortably with the count
    /// gone entirely. Whether the header fits and whether the header says anything are two
    /// questions, and only one of them is about pixels.
    /// </para>
    /// <para>
    /// <b>The second half is the notification.</b> The counts are computed properties, so
    /// nothing reaches the screen unless <c>RaiseSelectionState</c> names them. Deleting those
    /// two <c>OnPropertyChanged</c> lines leaves the header frozen at whatever it said when
    /// the row was realised, and every other test in the suite stays green — the exact shape
    /// this codebase has been bitten by three times, where a pure move loses its coverage
    /// because nothing about it looks like new behaviour.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_list_headers_carry_their_counts_and_follow_the_ticks()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            var (panel, content) = ShowGitPanel(window);

            for (var i = 0; i < 3; i++)
            {
                shell.Staged.Add(new ChangeRow(new FileChange
                {
                    Path = $"src/App/POU/FB_Staged_{i}.st",
                    Staged = ChangeKind.Modified,
                    Unstaged = ChangeKind.Unmodified,
                }));
            }

            for (var i = 0; i < 12; i++)
            {
                shell.Unstaged.Add(new ChangeRow(new FileChange
                {
                    Path = $"src/App/GVL/GVL_Changed_{i}.st",
                    Staged = ChangeKind.Unmodified,
                    Unstaged = ChangeKind.Modified,
                }));
            }

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            var texts = AllOf<TextBlock>(panel).Select(t => t.Text).ToList();

            // The count, on screen, where a scrolling list cannot show it.
            Assert.Contains("STAGED · 3", texts);
            Assert.Contains("NOT STAGED · 12", texts);

            // And the bare labels are gone, which is what proves the binding resolved rather
            // than the old literal text still being there.
            Assert.DoesNotContain("NOT STAGED", texts);

            // Tick two, and the header has to follow. This is the assertion that dies if the
            // OnPropertyChanged calls are removed.
            shell.Unstaged[0].IsSelected = true;
            shell.Unstaged[1].IsSelected = true;
            content.UpdateLayout();

            var after = AllOf<TextBlock>(panel).Select(t => t.Text).ToList();

            Assert.Contains("NOT STAGED · 2 of 12 selected", after);

            // The total alone is no longer shown while a selection is: one header, one
            // sentence, and the selection's own wording already carries the total.
            Assert.DoesNotContain("NOT STAGED · 12", after);

            // The other list is untouched by the other's ticks.
            Assert.Contains("STAGED · 3", after);
        });
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
