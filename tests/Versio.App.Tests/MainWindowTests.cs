using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Guarding;
using Versio.App.Views;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The main window is constructed against the real stylesheet.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because its absence shipped a crash.</b> Adding the log panel's category
/// dropdown — a <c>ToggleButton</c> wearing <c>GhostButton</c>, whose <c>TargetType</c> was
/// <c>Button</c> — made <c>MainWindow</c> throw <c>XamlParseException</c> at construction, so
/// the application started, logged, and never showed a window at all. The build was clean and
/// 883 tests were green when it did that.
/// </para>
/// <para>
/// Every instrument in place at the time missed it, and each for its own reason worth
/// recording:
/// </para>
/// <list type="bullet">
///   <item><b>The compiler</b> turns XAML into BAML without resolving a single
///   <c>StaticResource</c>. A style reference is just a key until something asks for it.</item>
///   <item><b>The dialog tests</b> cover the two modals and the stylesheet, and the main
///   window was the one window nothing constructed — on the reasoning that it opens on every
///   launch and could not go unnoticed. It went unnoticed.</item>
///   <item><b>The resource-key tests</b> assert <c>GhostButton</c> exists, which it did. The
///   defect was not a missing key; it was a key that does not fit the element asking for
///   it, which no lookup can see.</item>
///   <item><b>The binding tracer</b> reported nothing, and that was read as a clean result
///   for two runs. A window that never opens evaluates no bindings and produces an empty
///   trace — indistinguishable from a window with no broken bindings. Exactly the shape of
///   vacuous pass this repository keeps catching.</item>
/// </list>
/// <para>
/// One construction of the real window closes all four. It is also the cheapest possible
/// regression test for the whole file: every <c>StaticResource</c> in <c>MainWindow.xaml</c>
/// is resolved and applied here, on every run, in 200 ms.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class MainWindowTests
{
    private static MainWindow Build() => BuildWithShell().Window;

    private static (MainWindow Window, ShellViewModel Shell) BuildWithShell()
    {
        var logger = new VersioLogger(new SystemClock());
        var sink = new InMemoryLogSink();
        logger.AddSink(sink);

        // No repository is opened: construction is the thing under test, and a window that
        // parses is a window that parses whether or not it has data. Opening one would drag
        // git.exe and a real working tree into a test about XAML.
        using var shell = new ShellViewModel(logger, new LogViewModel(sink));
        return (new MainWindow(shell), shell);
    }

    [Fact]
    public void The_main_window_constructs_against_the_real_stylesheet()
    {
        StaHarness.Run(() =>
        {
            var window = Build();

            Assert.NotNull(window.Content);
        });
    }

    [Fact]
    public void Every_style_the_main_window_names_fits_the_element_that_wears_it()
    {
        // The specific defect: a Style whose TargetType does not match its element throws
        // from the property setter, not from the lookup. Applying the template is what
        // surfaces it, so the window is measured rather than merely built.
        StaHarness.Run(() =>
        {
            var window = Build();

            // The CONTENT is measured, not the window. An unshown Window has no HWND and
            // reports a zero DesiredSize however well its tree laid out, so asserting on the
            // window would have been a check that could only fail - the mirror image of the
            // vacuous pass, and it did fail the first time it was written.
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));

            Assert.True(content.DesiredSize.Width > 0);
        });
    }

    /// <summary>
    /// The history panel lays out, with rows in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Constructing the window is not enough to cover a panel that starts collapsed. A
    /// <c>StaticResource</c> inside a <see cref="DataTemplate"/> is not resolved when the XAML
    /// is parsed — it is resolved when the template is instantiated, which happens the first
    /// time a row is realised. A missing key in there would sail past every other test in this
    /// file and throw in front of whoever clicked the rail.
    /// </para>
    /// <para>
    /// So the panel is switched to through the rail button a user would press, filled from the
    /// counting client the history tests use, and one commit is expanded — which is what makes
    /// the second-level template, the one shared with the staged and unstaged lists, actually
    /// build.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_history_panel_lays_out_with_commits_and_files_in_it()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = BuildWithShell();
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            shell.History.Attach(new CountingGitClient());

            // Through the rail, not by setting the panel directly: the handler behind this is
            // what makes the panel visible and tells the history it is being looked at.
            var rail = Assert.IsType<RadioButton>(window.FindName("RailHistory"));
            rail.IsChecked = true;

            Assert.Equal(ShellPanel.History, shell.ActivePanel);
            Assert.NotEmpty(shell.History.Commits);

            shell.History.Commits[0].IsExpanded = true;
            Assert.NotEmpty(shell.History.Commits[0].Children);

            var panel = Assert.IsAssignableFrom<FrameworkElement>(
                window.FindName("HistoryPanel"));
            Assert.Equal(Visibility.Visible, panel.Visibility);

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            Assert.True(panel.DesiredSize.Width > 0);

            // The rows themselves, not just the panel around them. A template that failed to
            // build leaves a TreeView that measures perfectly well and shows nothing.
            var tree = Assert.IsAssignableFrom<TreeView>(
                VisualChild(panel, static v => v is TreeView));

            var item = Assert.IsAssignableFrom<TreeViewItem>(
                VisualChild(tree, static v => v is TreeViewItem));

            Assert.True(item.DesiredSize.Height > 0);
        });
    }

    [Fact]
    public void Both_trees_wear_the_themed_row_style()
    {
        // StockControlStyleTests proves TreeRowItem does the right thing; this proves the two
        // TreeViews actually ask for it. Without this, the style could be perfect and unused -
        // and the project tree, which needs a repository before it has any rows to look at,
        // would go on painting Windows' highlight blue with every test still green.
        //
        // Asserted against the style objects rather than by reading the XAML, because a test
        // that reads the file it is checking agrees with it by construction.
        StaHarness.Run(() =>
        {
            var window = Build();
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            var expected = Assert.IsType<Style>(Application.Current.Resources["TreeRowItem"]);

            // EVERY PANEL IS SHOWN IN TURN, AND THAT IS NOT BELT-AND-BRACES.
            //
            // A panel that lives in its own file is a UserControl, and a UserControl applies
            // its template when it is first measured — so while it is collapsed its content
            // is not in the visual tree at all and a walk from the window cannot see it. The
            // old markup had every panel inline, where a collapsed Border still held its
            // children, so a single walk found everything.
            //
            // Walking once would therefore still pass while quietly checking one tree fewer
            // each time a panel moves out — the exact failure this test exists to prevent,
            // reintroduced by the thing that was supposed to be a pure move.
            var trees = new List<TreeView>();

            foreach (var rail in new[] { "RailProject", "RailGit", "RailHistory" })
            {
                Assert.IsType<RadioButton>(window.FindName(rail)).IsChecked = true;

                content.Measure(new Size(1400, 900));
                content.Arrange(new Rect(0, 0, 1400, 900));
                content.UpdateLayout();

                trees.AddRange(AllOf<TreeView>(content).Except(trees));
            }

            // The project tree and the history tree. If a third appears, it needs the same
            // treatment, and this is where that gets noticed.
            Assert.Equal(2, trees.Count);

            foreach (var tree in trees)
            {
                var style = Assert.IsType<Style>(tree.ItemContainerStyle);

                Assert.Same(expected, style.BasedOn);
            }
        });
    }

    [Fact]
    public void Everything_in_the_top_bar_is_the_same_height()
    {
        // The defect this pins: Size.Control.Height was 28 and IconButton hardcoded 32, so
        // every dropdown in the top bar sat 4 px shorter than the buttons beside it. Nobody
        // could name it and everybody could see it - the bar read as ragged.
        //
        // Measured after arrange rather than read off the styles, because that is the question:
        // two controls can name the same token and still disagree if one of them also sets a
        // padding or a border that changes what it renders as.
        StaHarness.Run(() =>
        {
            var window = Build();
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            // First ComboBox in tree order is the repository dropdown; SettingsButton is an
            // IconButton in the same row.
            var combo = Assert.IsAssignableFrom<ComboBox>(
                VisualChild(content, static v => v is ComboBox));
            var button = Assert.IsType<Button>(window.FindName("SettingsButton"));

            Assert.True(combo.ActualHeight > 0, "the repository dropdown did not render");
            Assert.Equal(combo.ActualHeight, button.ActualHeight);
        });
    }

    [Fact]
    public void The_repository_dropdown_grows_with_the_window()
    {
        // The dropdowns were pinned at 230 and 200 px, so the whole bar crowded into the left
        // 700 px of a wide window. They grow with it now.
        //
        // THE CAP THIS USED TO ASSERT IS GONE, DELIBERATELY. Bounding each dropdown was the
        // second version of the top bar and it was wrong in a way a cap cannot avoid: the
        // bound sits on the group, so the two came out at different widths and the bar read as
        // weighted to one side with dead space down the middle. Two equal * columns replaced
        // it — see ShellLayoutTests, which pins the properties that actually matter now (the
        // two match each other, and together they span the bar) rather than a pixel ceiling.
        //
        // Left here rather than deleted because the ORIGINAL defect is still worth holding:
        // a dropdown that does not grow at all is what this was written for.
        StaHarness.Run(() =>
        {
            var window = Build();
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            double WidthAt(double windowWidth)
            {
                content.Measure(new Size(windowWidth, 900));
                content.Arrange(new Rect(0, 0, windowWidth, 900));
                content.UpdateLayout();

                var combo = Assert.IsAssignableFrom<ComboBox>(
                    VisualChild(content, static v => v is ComboBox));

                return combo.ActualWidth;
            }

            var narrow = WidthAt(1000);
            var wide = WidthAt(2200);

            Assert.True(wide > narrow,
                        $"the dropdown measured {narrow:F0} px at 1000 and {wide:F0} px at " +
                        "2200, so it is still pinned");

            // It shares the bar with the branch dropdown and the two button clusters, so it
            // cannot take all of a wide window however much it grows.
            Assert.True(wide < 2200 * 0.6,
                        $"the dropdown grew to {wide:F0} px of a 2200 px window, which is more " +
                        "than its half of the bar");
        });
    }

    [Fact]
    public void The_brand_mark_draws()
    {
        // The mark exists in assets/versio.svg and in the .ico, and until now appeared nowhere
        // a user could see it. A ControlTemplate rather than a DrawingImage on purpose - see
        // Brand.xaml - so this checks the template instantiates and occupies its box.
        StaHarness.Run(() =>
        {
            var template = Assert.IsType<ControlTemplate>(
                Application.Current.Resources["BrandMark"]);

            var mark = new ContentControl { Template = template, Width = 20, Height = 20 };

            mark.Measure(new Size(40, 40));
            mark.Arrange(new Rect(0, 0, 20, 20));
            mark.UpdateLayout();

            Assert.Equal(20, mark.ActualWidth);

            // The plates themselves, not just the box around them: a Canvas with nothing in
            // it arranges to exactly the same 20x20.
            var plate = VisualChild(mark, static v => v is Border);
            Assert.NotNull(plate);
        });
    }

    [Fact]
    public void The_settings_popup_builds_when_it_is_opened()
    {
        // The blind spot LogPanelChromeTests records for the category dropdown, one popup
        // over. WPF builds nothing inside a Popup until it opens, so every style in here -
        // the theme radios, the section labels, the ghost buttons - was unresolved and
        // therefore unchecked until a user clicked the gear. This is the click.
        StaHarness.Run(() =>
        {
            var window = Build();
            var popup = Assert.IsType<Popup>(window.FindName("SettingsPopup"));

            popup.IsOpen = true;
            try
            {
                var content = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);

                content.Measure(new Size(400, 600));
                content.Arrange(new Rect(0, 0, 400, 600));
                content.UpdateLayout();

                Assert.True(content.DesiredSize.Height > 0);

                // The radios specifically: they are the only RadioButtons in the app that do
                // NOT wear RailButton, so they are the ones the implicit style dresses.
                var radio = Assert.IsAssignableFrom<RadioButton>(
                    VisualChild(content, static v => v is RadioButton));

                Assert.True(radio.DesiredSize.Width > 0);
            }
            finally
            {
                // Left open, this stays on screen for the rest of the run and the next test
                // measures through it.
                popup.IsOpen = false;
            }
        });
    }

    /// <summary>
    /// The rail's change count appears, counts, and survives switching panels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The last clause is the whole point of moving it.</b> The pill this replaces was
    /// drawn by <c>OnRailChanged</c> on the Project panel alone, so the one number saying the
    /// working tree had anything in it was hidden from the two panels where you cannot
    /// otherwise see it. A test that only checked "the badge shows 3" would have passed
    /// against the old behaviour too; walking every rail is what distinguishes them.
    /// </para>
    /// <para>
    /// <b>And nothing else would notice if this broke.</b> A WPF binding to a property that
    /// does not exist fails silently and leaves the badge empty and collapsed — which no
    /// layout or geometry assertion anywhere can tell apart from a clean tree.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_rail_carries_the_change_count_from_every_panel()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = BuildWithShell();
            var content = (FrameworkElement)window.Content;
            var badge = Assert.IsType<Border>(window.FindName("RailGitBadge"));

            void Layout()
            {
                content.Measure(new Size(1400, 900));
                content.Arrange(new Rect(0, 0, 1400, 900));
                content.UpdateLayout();
            }

            Layout();

            // Nothing open, nothing changed: no badge at all. The pill this replaces drew a
            // literal "0" here.
            Assert.Equal(Visibility.Collapsed, badge.Visibility);

            for (var i = 0; i < 3; i++)
            {
                shell.Changes.Add(new ChangeRow(new FileChange
                {
                    Path = $"src/App/POU/FB_{i}.st",
                    Staged = ChangeKind.Unmodified,
                    Unstaged = ChangeKind.Modified,
                }));
            }

            Layout();

            Assert.Equal(Visibility.Visible, badge.Visibility);

            var text = AllOf<TextBlock>(badge).Single();
            Assert.Equal("3", text.Text);

            // From EVERY panel, which the pill could not do. Project last, because that is
            // the one the old behaviour did work on - ending there would let a regression
            // that only shows Project pass.
            foreach (var rail in new[] { "RailGit", "RailHistory", "RailProject" })
            {
                Assert.IsType<RadioButton>(window.FindName(rail)).IsChecked = true;
                Layout();

                Assert.Equal(Visibility.Visible, badge.Visibility);
                Assert.Equal("3", AllOf<TextBlock>(badge).Single().Text);
            }

            // And it goes away again rather than sticking at its high-water mark.
            shell.Changes.Clear();
            Layout();

            Assert.Equal(Visibility.Collapsed, badge.Visibility);
        });
    }

    /// <summary>Every descendant of type <typeparamref name="T"/>, breadth-first.</summary>
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

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    /// <summary>First descendant matching <paramref name="wanted"/>, breadth-first.</summary>
    private static DependencyObject? VisualChild(
        DependencyObject root, Func<DependencyObject, bool> wanted)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();

            if (node != root && wanted(node))
            {
                return node;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }

        return null;
    }

    [Fact]
    public void A_ghost_styled_toggle_button_is_legal()
    {
        // The category dropdown has to be a ToggleButton - it stays pressed while its popup
        // is open - and it wears the same ghost styling as the buttons beside it. Pinned on
        // its own so the reason GhostButton targets ButtonBase cannot be optimised away by
        // somebody tidying the stylesheet.
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["GhostButton"]);

            var toggle = new ToggleButton { Style = style, Content = "All categories" };
            toggle.Measure(new Size(300, 40));

            Assert.True(toggle.DesiredSize.Width > 0);
        });
    }

    [Fact]
    public void The_ghost_style_still_fits_a_plain_button()
    {
        // Widening the TargetType must not break what it already dressed.
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["GhostButton"]);

            var button = new Button { Style = style, Content = "Fetch" };
            button.Measure(new Size(300, 40));

            Assert.True(button.DesiredSize.Width > 0);
        });
    }

    [Fact]
    public void The_progress_bar_and_the_sync_engine_share_one_busy_tracker()
    {
        // The bar binds to the shell's tracker; the sync panel opened its scopes on one it
        // built itself, because the constructor parameter was optional and nobody passed it.
        // Both halves were correct in isolation, which is why it survived: the catch-up
        // export and the commit-gate export both open a scope, and the bar never moved for
        // either. Reference equality is the only thing that can state this.
        var logger = new VersioLogger(new SystemClock());
        var sink = new InMemoryLogSink();
        logger.AddSink(sink);

        using var shell = new ShellViewModel(logger, new LogViewModel(sink));

        Assert.Same(shell.Busy, shell.Sync.Busy);
    }

    [Fact]
    public void The_bar_reports_busy_while_the_sync_panel_holds_a_scope()
    {
        // Same fact from the other side: opening a scope on the sync panel must be visible
        // through the property the window actually binds to.
        var logger = new VersioLogger(new SystemClock());
        var sink = new InMemoryLogSink();
        logger.AddSink(sink);

        using var shell = new ShellViewModel(logger, new LogViewModel(sink));

        Assert.False(shell.Busy.IsBusy);

        using (shell.Sync.Busy.Begin(WorkKind.Export, "Exporting something…"))
        {
            Assert.True(shell.Busy.IsBusy);
            Assert.Equal("Exporting something…", shell.Busy.Message);
        }

        Assert.False(shell.Busy.IsBusy);
    }
}
