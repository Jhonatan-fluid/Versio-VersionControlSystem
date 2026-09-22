using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Nothing in the shell overlaps anything else, at the widths people actually use.
/// </summary>
/// <remarks>
/// <para>
/// Both facts here were reported from the running app, and neither was reachable by any test
/// that existed: the suite constructed the window and measured it at one comfortable size, and
/// two controls that overlap still measure and arrange perfectly well. An overlap is not a
/// failure in WPF — it is a layout that did what it was told.
/// </para>
/// <para>
/// So these assert <b>rendered geometry</b>, transformed into a common ancestor, at a narrow
/// window. That is the only form in which "these two things sit on top of each other" can be
/// written down.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class ShellLayoutTests : IDisposable
{
    /// <summary>A window wide enough to be real and narrow enough to be honest.</summary>
    private const double Narrow = 1000;

    private readonly List<ShellViewModel> _shells = [];

    private MainWindow Build()
    {
        var logger = new VersioLogger(new SystemClock());
        var sink = new InMemoryLogSink();
        logger.AddSink(sink);

        // Kept, and not constructed inline as an argument, so that something can dispose it.
        // See Dispose below for what it costs when nothing can.
        var shell = new ShellViewModel(logger, new LogViewModel(sink));
        _shells.Add(shell);

        return new MainWindow(shell);
    }

    /// <summary>Release the shells this class built.</summary>
    /// <remarks>
    /// <para>
    /// <b>On the STA thread, not this one.</b> Tearing a shell down clears collections that a
    /// bound <c>CollectionView</c> is watching, and WPF refuses that from any thread but the
    /// dispatcher's: disposing from the xunit thread throws <c>NotSupportedException</c> —
    /// <i>"does not support changes to its SourceCollection from a thread different from the
    /// Dispatcher thread"</i> — and fails every test in this class. Measured, by doing it.
    /// That is also the proof this teardown is not decorative, which nothing else here asserts.
    /// </para>
    /// <para>
    /// Worth having at all because that harness runs <b>one</b> STA thread with a live
    /// <c>Dispatcher.Run()</c> for the whole suite. A leaked log pump is not inert here the way
    /// it would be on a thread nobody pumps: it goes on firing every 100 ms, draining its queue
    /// into an <c>ObservableCollection</c> on the same thread every later WPF test is using.
    /// One was leaked for every test in this class, because the shell was constructed inline
    /// as an argument to the window and there was nothing to put <c>using</c> on.
    /// </para>
    /// </remarks>
    public void Dispose() => StaHarness.Run(() =>
    {
        foreach (var shell in _shells)
        {
            shell.Dispose();
        }
    });

    private static FrameworkElement Arrange(MainWindow window, double width = Narrow)
    {
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

        content.Measure(new Size(width, 900));
        content.Arrange(new Rect(0, 0, width, 900));
        content.UpdateLayout();

        return content;
    }

    private static Rect BoundsIn(FrameworkElement child, FrameworkElement ancestor) =>
        child.TransformToAncestor(ancestor).TransformBounds(new Rect(child.RenderSize));

    [Fact]
    public void The_log_console_starts_where_the_rail_ends()
    {
        // Reported: "the logs bar overlapping with the left side bar menu". The rail lived
        // inside the body row, so it stopped at the bottom of the panels while the log console
        // - a sibling row - ran the full width of the window underneath it. Two surfaces of
        // the same colour meeting in a T-junction under the rail icons.
        StaHarness.Run(() =>
        {
            var window = Build();
            var content = Arrange(window);

            var rail = Assert.IsType<Border>(window.FindName("Rail"));
            // A UserControl since the log console moved into its own file, the way the history
            // panel already had. What this test wants is something to measure, not a
            // particular container.
            var log = Assert.IsAssignableFrom<FrameworkElement>(
                window.FindName("LogConsole"));

            var railBounds = BoundsIn(rail, content);
            var logBounds = BoundsIn(log, content);

            Assert.True(railBounds.Width > 0, "the rail did not render");
            Assert.True(logBounds.Width > 0, "the log console did not render");

            Assert.False(railBounds.IntersectsWith(logBounds),
                         $"the rail occupies {railBounds} and the log console {logBounds}, " +
                         "which overlap");

            // Stated as well as implied: the log begins at or after the rail's right edge.
            Assert.True(logBounds.Left >= railBounds.Right,
                        $"the log console starts at x={logBounds.Left:F0}, left of the rail's " +
                        $"right edge at x={railBounds.Right:F0}");
        });
    }

    [Fact]
    public void The_rail_runs_past_the_body_and_alongside_the_log()
    {
        // The other half of the same fix. If the rail merely moved columns without spanning
        // the log's row, the overlap test above would pass and the left edge would end in a
        // notch beside the log console instead.
        StaHarness.Run(() =>
        {
            var window = Build();
            var content = Arrange(window);

            var rail = Assert.IsType<Border>(window.FindName("Rail"));
            // A UserControl since the log console moved into its own file, the way the history
            // panel already had. What this test wants is something to measure, not a
            // particular container.
            var log = Assert.IsAssignableFrom<FrameworkElement>(
                window.FindName("LogConsole"));

            var railBounds = BoundsIn(rail, content);
            var logBounds = BoundsIn(log, content);

            Assert.True(railBounds.Bottom >= logBounds.Bottom,
                        $"the rail ends at y={railBounds.Bottom:F0} and the log console at " +
                        $"y={logBounds.Bottom:F0}, so the rail stops short of it");
        });
    }

    [Theory]
    [InlineData("RailProject", "ProjectPanel")]
    [InlineData("RailExplorer", "ExplorerPanel")]
    [InlineData("RailGit", "GitPanel")]
    [InlineData("RailHistory", "HistoryPanel")]
    public void Every_panel_fills_the_card_it_lives_in(string rail, string panel)
    {
        // Reported from the app: "notice how it is not using the full panel" - the git panel's
        // rows stopped about 310 px into a 557 px card, leaving a column of dead surface down
        // the right of every row in it.
        //
        // The cause was one default. The card held a DockPanel, DockPanel.Dock DEFAULTS TO
        // LEFT, and only the last child fills - so of the three panels listed git, history,
        // project, only the project panel ever filled the card. The other two were docked left
        // at whatever width their content happened to want. Nothing was misaligned, nothing
        // overflowed, and every existing test passed: it is a panel correctly sized to the
        // wrong thing.
        //
        // A Grid row fixes it, and this is what keeps all three honest - including the two
        // that need a repository open before they show a single row.
        StaHarness.Run(() =>
        {
            var window = Build();

            var button = Assert.IsType<RadioButton>(window.FindName(rail));
            button.IsChecked = true;

            var content = Arrange(window, width: 1400);
            // Border for two of the three and a UserControl for the history panel,
            // which lives in its own file. What matters here is the geometry.
            var border = Assert.IsAssignableFrom<FrameworkElement>(
                window.FindName(panel));

            Assert.Equal(Visibility.Visible, border.Visibility);

            // The card is the panel's parent Border, the one the rail swaps contents inside.
            var card = Assert.IsAssignableFrom<Border>(
                VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(border)!));

            Assert.True(card.ActualWidth > 0, "the card did not render");

            // The panel's own padding is 14 + 12, and that is all it may give away.
            var slack = card.ActualWidth - border.ActualWidth;

            Assert.True(slack < 1,
                        $"{panel} rendered {border.ActualWidth:F0} px wide inside a " +
                        $"{card.ActualWidth:F0} px card, leaving {slack:F0} px of it unused");
        });
    }

    [Fact]
    public void A_section_label_never_sits_under_its_own_buttons()
    {
        // Reported: "git panel cluttered ... looks really bad", with a section label and
        // Discard touching. The cause is one Grid cell holding both a label and a
        // right-aligned button: a right-aligned child grows leftwards ACROSS the label
        // rather than pushing it. Two columns fix it; this is what proves they stay.
        StaHarness.Run(() =>
        {
            var window = Build();

            var rail = Assert.IsType<RadioButton>(window.FindName("RailGit"));
            rail.IsChecked = true;

            var content = Arrange(window);
            var panel = Assert.IsAssignableFrom<FrameworkElement>(
                window.FindName("GitPanel"));

            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.True(panel.RenderSize.Width > 0, "the git panel did not render");

            var labelBounds = BoundsIn(Text(panel, "CHANGES"), content);
            var buttonBounds = BoundsIn(Button(panel, "Discard"), content);

            Assert.True(labelBounds.Width > 0, "'CHANGES' did not render");
            Assert.True(buttonBounds.Width > 0, "'Discard' did not render");

            Assert.False(labelBounds.IntersectsWith(buttonBounds),
                         $"'CHANGES' occupies {labelBounds} and 'Discard' {buttonBounds}, " +
                         "which overlap");
        });
    }

    [Fact]
    public void The_diff_header_keeps_the_shown_name_clear_of_its_options()
    {
        // Same defect, other pane: DIFF, a checkbox and a 420 px path all shared one cell.
        //
        // Driven into a real diff with a deep path, at the narrow width. The options exist only
        // while something is being compared, so an empty pane - which this test used to measure
        // - has no checkbox to collide with, and would pass without checking anything.
        StaHarness.Run(async () =>
        {
            using var repo = new TempRepo(identity: true);
            var deep = "fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/Library/Alarm/POUs";
            Directory.CreateDirectory(Path.Combine(repo.Path, deep));
            File.WriteAllText(Path.Combine(repo.Path, deep, "FB_Alarm.st"), "FUNCTION_BLOCK FB_Alarm\n");

            var window = Build();
            var shell = (ShellViewModel)window.DataContext;
            await shell.OpenAsync(repo.Path);
            await shell.ShowWorkingTreePathAsync(deep + "/FB_Alarm.st");

            var content = Arrange(window);

            var name = Descendants(content).OfType<TextBlock>()
                .Single(t => AutomationProperties.GetName(t) == "Shown item");
            var check = Descendants(content).OfType<CheckBox>()
                .Single(c => Equals(c.Content, "Show full file"));

            Assert.Equal("FB_Alarm.st", name.Text);
            Assert.True(BoundsIn(name, content).Width > 0, "the shown name did not render");
            Assert.True(BoundsIn(check, content).Width > 0, "'Show full file' did not render");
            Assert.False(BoundsIn(name, content).IntersectsWith(BoundsIn(check, content)),
                         "the shown name and 'Show full file' overlap");

            // And over a whole file there is nothing to compare, so the options go.
            File.WriteAllText(Path.Combine(repo.Path, "clean.st"), "PROGRAM Clean\n");
            repo.Git("add clean.st");
            repo.Git("commit -q -m clean");
            await shell.ShowWorkingTreePathAsync("clean.st");
            Arrange(window);

            Assert.Equal("FILE", shell.Heading.Label);
            Assert.NotEqual(Visibility.Visible, check.Visibility);
        });
    }

    private static TextBlock Text(DependencyObject root, string text) =>
        Descendants(root).OfType<TextBlock>().Single(t => t.Text == text);

    [Fact]
    public void Each_dropdown_keeps_its_buttons_beside_it()
    {
        // Reported from the app: "the 2 drop downs are so separated from their buttons".
        //
        // Each dropdown had a * column and its buttons an Auto column beside it, which looks
        // right and is not. A control with HorizontalAlignment="Stretch" that runs into its
        // MaxWidth stops growing and is CENTRED in what is left - so on a wide window each
        // dropdown floated in the middle of a column much wider than itself, with a strip of
        // empty bar between it and the Open button that belongs to it.
        //
        // Measured wide, because that is the only width at which it happens: at 1000 px the
        // columns are narrow enough that the MaxWidth never binds and the gap never appears.
        StaHarness.Run(() =>
        {
            var window = Build();
            var content = Arrange(window, width: 2400);

            var repository = Combo(content, "Repository");
            var branch = Combo(content, "Branch");

            var open = Button(content, (string)window.FindResource("Icon.Folder"));
            var newBranch = Button(content, (string)window.FindResource("Icon.Add"));

            AssertAdjacent(BoundsIn(repository, content), BoundsIn(open, content), "Open");
            AssertAdjacent(BoundsIn(branch, content), BoundsIn(newBranch, content), "New branch");
        });
    }

    [Fact]
    public void The_two_dropdowns_are_the_same_width_and_span_the_bar()
    {
        // Reported after the first fix for the gap above, which over-corrected: capping each
        // group and left-aligning it removed the gap and introduced two more - the dropdowns
        // shrank, and the caps differed, so the bar read as weighted to one side with dead
        // space down the middle.
        //
        // Two equal * columns and no spacer. What is pinned is the pair of properties that
        // were wrong, not the pixel widths: they match each other, and together they use the
        // bar rather than huddling at one end of it.
        StaHarness.Run(() =>
        {
            var window = Build();
            const double Width = 2400;
            var content = Arrange(window, width: Width);

            var repository = Combo(content, "Repository");
            var branch = Combo(content, "Branch");

            Assert.True(Math.Abs(repository.ActualWidth - branch.ActualWidth) < 1,
                        $"the repository dropdown is {repository.ActualWidth:F0} px and the " +
                        $"branch dropdown {branch.ActualWidth:F0} px, so the bar is weighted " +
                        "to one side");

            // From the left edge of the first to the right edge of the second: the two
            // primary controls should own the bar between the brand and Settings.
            var spanned = BoundsIn(branch, content).Right - BoundsIn(repository, content).Left;

            Assert.True(spanned > Width * 0.7,
                        $"the two dropdowns span {spanned:F0} px of a {Width:F0} px bar, so " +
                        "most of it is empty");
        });
    }

    /// <summary>The button starts within a control's width of where the dropdown ends.</summary>
    /// <remarks>
    /// A tolerance rather than an exact figure: what was wrong was hundreds of pixels of bar,
    /// and pinning the margin to the pixel would make this a test of the spacing rather than
    /// of the pairing.
    /// </remarks>
    private static void AssertAdjacent(Rect dropdown, Rect button, string name)
    {
        var gap = button.Left - dropdown.Right;

        Assert.True(gap >= 0, $"{name} overlaps the dropdown it belongs to");
        Assert.True(gap < 32,
                    $"{name} sits {gap:F0} px from its dropdown, so it reads as belonging to " +
                    "nothing");
    }

    private static ComboBox Combo(DependencyObject root, string automationName) =>
        Descendants(root).OfType<ComboBox>()
            .Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == automationName);

    private static Button Button(DependencyObject root, string content) =>
        Descendants(root).OfType<Button>().Single(b => Equals(b.Content, content));

    [Fact]
    public void The_repository_path_does_not_draw_over_the_status_text()
    {
        // The status bar was one Grid cell holding both children: the left stack sized to its
        // content, and the path right-aligned and sized to ITS content. With a deep path and
        // an active message the two simply drew on top of each other, at the bottom of the
        // window, in the same 11 px face.
        //
        // It survived because the window opens WindowState="Maximized" - the collision only
        // appears once somebody restores it, which is a size no test had ever measured at.
        StaHarness.Run(() =>
        {
            var window = Build();
            var content = Arrange(window);

            var state = Assert.IsType<StackPanel>(window.FindName("StatusState"));
            var path = Assert.IsType<TextBlock>(window.FindName("StatusPath"));

            // Set directly rather than through the view model: what is being measured is the
            // layout's response to long content, and RepositoryPath is set by opening a
            // repository, which this test has no business doing.
            path.Text = @"C:\Users\engineer\Documents\Automation\Line 4\Conveyor\ControlSystem";

            var summary = Descendants(state).OfType<TextBlock>().Last();
            summary.Text = "Saved 12 changed file(s). They will be offered back on feature/hmi.";

            content.UpdateLayout();

            var stateBounds = BoundsIn(state, content);
            var pathBounds = BoundsIn(path, content);

            Assert.True(pathBounds.Width > 0, "the path did not render");
            Assert.False(stateBounds.IntersectsWith(pathBounds),
                         $"the status text occupies {stateBounds} and the path {pathBounds}, " +
                         "which overlap");
        });
    }

    [Fact]
    public void The_status_light_is_bound_rather_than_painted_green()
    {
        // The dot was Brush.Status.Added written straight into the XAML - green while git was
        // missing, while the tree was mid-merge and while a sync had failed. A binding is the
        // difference between an indicator and a decoration shaped like one, so what is pinned
        // is that one exists at all.
        StaHarness.Run(() =>
        {
            var window = Build();
            var content = Arrange(window);

            var state = Assert.IsType<StackPanel>(window.FindName("StatusState"));
            var dot = Descendants(state).OfType<System.Windows.Shapes.Ellipse>().Single();

            Assert.NotNull(System.Windows.Data.BindingOperations.GetBinding(
                dot, System.Windows.Shapes.Shape.FillProperty));

            // Nothing is open, so the light is neutral rather than reporting the health of
            // nothing. This is the launch state and it is the one people see first.
            var shell = Assert.IsType<ShellViewModel>(window.DataContext);

            Assert.Equal("Brush.Status.Neutral", shell.HealthBrushKey);
            Assert.Equal("No repository open", shell.HealthSummary);
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
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
