using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;
using Xunit.Abstractions;

namespace Versio.App.Tests;

/// <summary>
/// The git panel measured with real content in it, at the width it actually gets.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nobody can look at this panel from a test, and that is exactly why it needs measuring.</b>
/// "Does it look right" is a human question, but most of the ways a layout goes wrong are
/// arithmetic and can be asserted: a control that renders at zero height, a row wider than the
/// column holding it, a list squeezed out by everything stacked above it. Those are the failures
/// that make somebody say the panel looks broken, and none of them needs an eye to catch.
/// </para>
/// <para>
/// Measured at <b>380 px</b>, which is the panel's real column width, with a realistic amount of
/// content: banners up, both lists populated, a commit message typed. An empty panel lays out
/// perfectly at any size and proves nothing.
/// </para>
/// <para>
/// The numbers are written to the test output as well as asserted, so a change in the layout
/// shows up as a diff in the run rather than only as a pass or a fail.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class GitPanelGeometryTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The width the panel column opens at.</summary>
    private const double PanelWidth = 380;

    /// <summary>
    /// The narrowest the user can drag the panel, from <c>PanelColumn</c>'s own MinWidth.
    /// </summary>
    /// <remarks>
    /// <b>Measured as well as 380, because 380 is a default and not a constraint.</b> The
    /// column is <c>Width="380" MinWidth="260"</c> and the chosen width is <i>persisted</i>
    /// (<c>MainWindow.xaml.cs</c>, <c>PanelWidth</c> in the saved layout), so a panel dragged
    /// narrow once is narrow on every later launch. 120 px of the budget can be gone before a
    /// single row is drawn, and every test here used to assume it never was.
    /// </remarks>
    private const double NarrowPanelWidth = 260;

    private static ChangeRow Row(string path, ChangeKind staged, ChangeKind unstaged) =>
        new(new FileChange { Path = path, Staged = staged, Unstaged = unstaged });

    private readonly List<ShellViewModel> _shells = [];

    /// <summary>A panel carrying about as much as it ever will.</summary>
    /// <remarks>
    /// <para>
    /// <b>Every parameter defaults to what this fixture has always done</b>, so the four tests
    /// that predate them measure exactly the panel they always measured. That is deliberate:
    /// widening a shared fixture to suit one new test is how the others quietly start checking
    /// something else, and a test that goes on passing while measuring one thing fewer is the
    /// failure this file exists to prevent.
    /// </para>
    /// <para>
    /// <b>The defaults are also why the overflow was invisible.</b> Nothing was ever ticked, so
    /// every action button rendered its short <c>"{verb} all"</c> caption and both header
    /// captions were the empty string — and twelve rows caps every count at two digits. The
    /// widest state the headers can reach was not merely untested, it could not be built here.
    /// </para>
    /// </remarks>
    private (MainWindow Window, ShellViewModel Shell, FrameworkElement Panel,
                    FrameworkElement Content) Loaded(
        double panelWidth = PanelWidth,
        int pendingRows = 16,
        bool tickEverything = false)
    {
        // NOT `using var` here. That used to dispose the shell at the closing brace of
        // this helper, before any test measured the window it had just built — the same
        // defect GitPanelChromeTests already records. Held until Dispose, on the STA
        // thread that built it.
        var shell = TestShell.Build();
        _shells.Add(shell);
        var window = new MainWindow(shell);
        var content = (FrameworkElement)window.Content;

        var rail = (RadioButton)window.FindName("RailGit")!;
        rail.IsChecked = true;

        // Asserted rather than probed. The width used to arrive only from the XAML's own
        // Width="380", so this file's PanelWidth constant documented a premise it did not
        // enforce - and a rename of the column would have left every case here measuring 380
        // for ever while claiming to measure 260.
        var column = Assert.IsType<ColumnDefinition>(window.FindName("PanelColumn"));
        column.Width = new GridLength(panelWidth, GridUnitType.Pixel);

        for (var i = 0; i < pendingRows; i++)
        {
            shell.Pending.Add(Row($"src/MainProject/PLC/POU/FB_Conveyor_{i}.st",
                                 ChangeKind.Modified, ChangeKind.Unmodified));
        }

        if (tickEverything)
        {
            foreach (var row in shell.Pending)
            {
                row.IsSelected = true;
            }
        }

        shell.CommitMessage = "Rework the conveyor interlock so a jam stops the upstream feed";
        shell.Notifications.Failure("Push failed.", "rejected — non-fast-forward");

        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();

        return (window, shell, (FrameworkElement)window.FindName("GitPanel")!, content);
    }

    /// <summary>Release the shells this class built, on the thread that built them.</summary>
    public void Dispose() => StaHarness.Run(() =>
    {
        foreach (var shell in _shells)
        {
            shell.Dispose();
        }
    });

    [Fact]
    public void Everything_in_the_panel_renders_at_a_usable_size()
    {
        StaHarness.Run(() =>
        {
            var (_, _, panel, _) = Loaded();

            Assert.True(panel.ActualWidth > 0, "the panel did not render");
            output.WriteLine($"panel: {panel.ActualWidth:F0} x {panel.ActualHeight:F0}");

            // A control that arranges to nothing is invisible without being hidden, which is
            // the failure mode that reads as "the change did not work".
            foreach (var element in AllOf<FrameworkElement>(panel)
                         .Where(e => e.Visibility == Visibility.Visible)
                         .Where(e => e is Button or ListBox or TextBox or CheckBox))
            {
                Assert.True(element.ActualHeight > 0,
                            $"{Describe(element)} rendered at zero height");
                Assert.True(element.ActualWidth > 0,
                            $"{Describe(element)} rendered at zero width");
            }
        });
    }

    [Fact]
    public void The_changes_list_gets_real_room_with_content_in_it()
    {
        StaHarness.Run(() =>
        {
            var (window, _, _, _) = Loaded();

            var list = Inside<ListBox>(window, "ChangesList");

            output.WriteLine($"changes list: {list.ActualHeight:F0} px for 16 rows");

            Assert.True(list.ActualHeight >= 60,
                        $"the changes list got {list.ActualHeight:F0} px");
        });
    }

    /// <summary>
    /// Nothing reaches past the panel's right edge, at either width, however wide the labels get.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Four cases, because this was one and the one it was could not fail.</b> It ran at 380 px
    /// with nothing ticked, which is simultaneously the widest column and the narrowest labels —
    /// the single most forgiving combination the panel has. The overflow a user reported needs
    /// the opposite corner: a dragged-narrow column and three-digit counts in every button.
    /// </para>
    /// <para>
    /// <b>The mechanism it is watching for.</b> Both list headers are
    /// <c>Auto | Auto | * | Auto</c>. A WPF Grid measures its Auto columns against infinity and
    /// gives each its full desired width; the star column takes what is left, floored at zero
    /// and never negative. So once the Autos together want more than the row has, the star
    /// collapses and the Autos keep their width anyway — the grid overflows its own slot and
    /// the right-hand children arrange past the panel, over the splitter and the diff pane.
    /// Nothing in that chain clips.
    /// </para>
    /// <para>
    /// <b>And every input to that sum grows with the number of changes</b>, which is why it
    /// appears when the lists fill and not before: <c>StagingSelection.Label</c> renders
    /// <c>"{verb} {selected}"</c>, so the unstaged header carries <c>Discard 127</c> and
    /// <c>Stage 127</c> side by side, while <c>NOT STAGED</c> sits in an Auto column in the
    /// letter-spaced SectionLabel face with no trimming and no ceiling.
    /// </para>
    /// </remarks>
    [Theory]
    // The case this test has always run. It must keep passing: the fix is not allowed to
    // buy the narrow column by giving something up at the default one.
    [InlineData(PanelWidth, 12, false)]
    // Default width, widest labels. Isolates the label growth from the column width.
    [InlineData(PanelWidth, 127, true)]
    // Narrowest column, short labels. Isolates the column width from the label growth.
    [InlineData(NarrowPanelWidth, 12, false)]
    // Both at once, which is the state being reported.
    [InlineData(NarrowPanelWidth, 127, true)]
    public void Nothing_overflows_the_panel_sideways(double width, int pendingRows, bool ticked)
    {
        StaHarness.Run(() =>
        {
            var (_, _, panel, content) = Loaded(
                panelWidth: width, pendingRows: pendingRows, tickEverything: ticked);

            var panelRight = panel.TransformToAncestor(content)
                                  .Transform(new Point(panel.ActualWidth, 0)).X;

            var widest = 0d;
            string? offender = null;

            // THE ANCESTOR WALK REPLACES AN ActualWidth FILTER, AND THAT WAS THE THIRD
            // REASON THIS TEST PASSED.
            //
            // It read `e.Visibility == Visibility.Visible && e.ActualWidth > 0`. The width
            // clause is there to skip controls that are not laid out - but an empty TextBlock
            // also measures zero wide, and with nothing ticked BOTH header captions were the
            // empty string. So the one control in each row designed to absorb the slack was
            // excluded from the only test watching the row.
            //
            // It cannot simply be dropped, either, and the first attempt to was wrong: a
            // child of a COLLAPSED parent still reports Visibility.Visible for itself, so
            // removing the width clause started reporting the conflict section's buttons -
            // which are not on screen in this fixture - as the widest thing in the panel.
            // The width clause had been doing ancestor filtering by accident.
            //
            // So the rule is stated instead of emerging: an element counts when it and every
            // ancestor up to the panel are visible. UIElement.IsVisible says exactly this and
            // is deliberately NOT used - it also requires a presentation source, and this
            // window is never shown, so it is false for everything here and would make the
            // whole test vacuous.
            foreach (var element in AllOf<FrameworkElement>(panel)
                         .Where(e => IsRenderedWithin(e, panel)))
            {
                var right = element.TransformToAncestor(content)
                                   .Transform(new Point(element.ActualWidth, 0)).X;

                if (right > widest)
                {
                    widest = right;
                    offender = Describe(element);
                }
            }

            output.WriteLine(
                $"width {width:F0}, {pendingRows} changes, ticked={ticked}: " +
                $"panel right edge {panelRight:F0}, widest child {widest:F0} ({offender})");

            // A pixel of tolerance for rounding at the boundary.
            Assert.True(widest <= panelRight + 1,
                        $"{offender} reaches {widest:F0} px, past the panel's {panelRight:F0} px");
        });
    }

    /// <summary>
    /// Nothing in the remote row sits on top of anything else, at either width.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The overflow test above cannot see this.</b> It asks whether anything crosses the
    /// panel's right edge, and two buttons can collide perfectly happily well inside it — a
    /// StackPanel hands every child the width it asks for and then overflows <i>itself</i>,
    /// leaving the children drawn over one another with the panel none the wiser. That is what
    /// was happening here.
    /// </para>
    /// <para>
    /// <b>The captions are set directly, and that is the point rather than a shortcut.</b>
    /// <c>Remote.Standing</c> has a private setter, so a test cannot manufacture a diverged
    /// branch — but the question is what the row does with <i>those strings</i>, and measured
    /// at 260 px it is decisive: Fetch, <c>Pull (12)</c> and <c>Push (127)</c> want 207 px in
    /// a 206 px row. The three verbs alone were a pixel over before anything was put beside
    /// them.
    /// </para>
    /// <para>
    /// They wrap now, so the row takes a second line in exactly the case that could not fit on
    /// one and stays on a single line everywhere else — which the third case here pins.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(NarrowPanelWidth, true)]
    [InlineData(PanelWidth, true)]
    [InlineData(NarrowPanelWidth, false)]
    public void Nothing_in_the_remote_row_overlaps(double width, bool diverged)
    {
        StaHarness.Run(() =>
        {
            var (window, _, panel, content) = Loaded(panelWidth: width);

            var toggle = Assert.IsType<ToggleButton>(panel.FindName("BranchMenuButton"));
            var row = Assert.IsAssignableFrom<FrameworkElement>(
                VisualTreeHelper.GetParent(toggle));

            if (diverged)
            {
                foreach (var b in AllOf<Button>(row))
                {
                    if ((b.Content as string) == "Pull")
                    {
                        b.Content = "Pull (12)";
                    }
                    else if ((b.Content as string) == "Push")
                    {
                        b.Content = "Push (127)";
                    }
                }

                content.Measure(new Size(1400, 900));
                content.Arrange(new Rect(0, 0, 1400, 900));
                content.UpdateLayout();
            }

            var boxes = new List<(string Name, Rect Box)>();

            foreach (var b in AllOf<ButtonBase>(row).Where(b => b.ActualWidth > 0))
            {
                var origin = b.TransformToAncestor(row).Transform(new Point(0, 0));
                boxes.Add((b is ToggleButton ? "Branch" : b.Content?.ToString() ?? "?",
                           new Rect(origin, new Size(b.ActualWidth, b.ActualHeight))));
            }

            Assert.True(boxes.Count >= 3, "the remote row realised almost nothing");

            foreach (var (name, box) in boxes)
            {
                output.WriteLine($"  {name,-12} {box}");
            }

            for (var i = 0; i < boxes.Count; i++)
            {
                for (var j = i + 1; j < boxes.Count; j++)
                {
                    var overlap = Rect.Intersect(boxes[i].Box, boxes[j].Box);

                    // Half a pixel of tolerance for rounding where two edges meet.
                    Assert.False(overlap is { Width: > 0.5, Height: > 0.5 },
                                 $"{boxes[i].Name} and {boxes[j].Name} overlap at {overlap}");
                }
            }

            // One line unless the captions genuinely cannot fit on one. A row that wrapped
            // whenever it felt like it would cost the file lists height for nothing.
            if (!diverged)
            {
                Assert.True(row.ActualHeight < 40,
                            $"the remote row wrapped at {row.ActualHeight:F0} px with ordinary captions");
            }
        });
    }

    [Fact]
    public void A_long_plc_path_is_trimmed_rather_than_pushing_the_row_wide()
    {
        StaHarness.Run(() =>
        {
            var (window, _, _, _) = Loaded();

            var list = Inside<ListBox>(window, "ChangesList");
            var item = AllOf<ListBoxItem>(list).FirstOrDefault();

            Assert.True(item is not null, "no change row was realised");
            output.WriteLine($"row: {item!.ActualWidth:F0} x {item.ActualHeight:F0}");

            // Two 11 px caption lines plus a chip. A 24 px tree row is what made the
            // filename of one row draw on top of the path of the row above it.
            Assert.True(item.ActualHeight >= 40,
                        $"a two-line change row is {item.ActualHeight:F0} px tall");
            Assert.True(item.ActualWidth <= list.ActualWidth + 1,
                        "a row is wider than the list holding it");
        });
    }

    [Fact]
    public void The_commit_box_and_its_buttons_still_fit_under_everything_else()
    {
        // Three banners can be up at once - missing git, identity, credentials - and they are
        // all docked above the commit box. The one that gets squeezed out is the one at the
        // bottom, which is the box you type in.
        StaHarness.Run(() =>
        {
            var (window, _, panel, _) = Loaded();

            var box = AllOf<TextBox>(panel).FirstOrDefault(t => t.Name == "CommitMessageBox");
            Assert.True(box is not null, "the commit message box is not there");

            output.WriteLine($"commit box: {box!.ActualWidth:F0} x {box.ActualHeight:F0}");

            Assert.True(box.ActualHeight > 40, $"the commit box got {box.ActualHeight:F0} px");

            var commit = AllOf<Button>(panel).First(b => (b.Content as string) == "Commit");
            Assert.True(commit.ActualHeight > 0, "the Commit button was squeezed out");
        });
    }

    private static string Describe(FrameworkElement e) =>
        e switch
        {
            Button b => $"Button '{b.Content}'",
            TextBox t => $"TextBox '{t.Name}'",
            ListBox => "ListBox",
            CheckBox c => $"CheckBox '{c.Content}'",
            TextBlock t => $"TextBlock '{Shorten(t.Text)}'",

            // A CONTAINER HAS TO SAY WHICH ONE IT IS.
            //
            // The overflow this test now catches reported its offender as the bare word
            // "Grid" - true, useless, and there are a dozen of them in this panel. A layout
            // failure names a rectangle, so the only way back to the markup is through what
            // the rectangle contains. The first text inside it is enough: "STAGED",
            // "NOT STAGED", "COMMIT MESSAGE" each identify their row unambiguously.
            Panel or Border or ContentControl =>
                $"{e.GetType().Name} around '{Shorten(FirstTextIn(e))}'",

            _ => e.GetType().Name,
        };

    /// <summary>
    /// True when this element and every ancestor up to <paramref name="root"/> are visible.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>UIElement.IsVisible</c>.</b> That property also requires the element to be
    /// connected to a live presentation source, and these tests measure a window that is never
    /// shown — so it is false for every element here and any filter built on it would pass by
    /// measuring nothing. This asks the one question that is actually meaningful in an
    /// unshown tree: is anything between here and the panel collapsed?
    /// </remarks>
    private static bool IsRenderedWithin(FrameworkElement element, FrameworkElement root)
    {
        for (DependencyObject? node = element; node is not null;
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }

            if (ReferenceEquals(node, root))
            {
                return true;
            }
        }

        // Ran out of tree before reaching the panel. Not part of what is being measured.
        return false;
    }

    /// <summary>The first text of any depth inside a container, for naming it in a failure.</summary>
    private static string FirstTextIn(DependencyObject root) =>
        AllOf<TextBlock>(root)
            .Select(t => t.Text)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
        ?? "no text";

    private static string Shorten(string s) =>
        s.Length <= 40 ? s : s[..40] + "…";

    /// <summary>A named control inside the git panel.</summary>
    /// <remarks>
    /// The panel lives in its own file, and a UserControl owns its own XAML name scope — so
    /// <c>window.FindName("StagedList")</c> returns null however visible the list is. The
    /// panel is reached from the window, which does still name it, and the list from the
    /// panel. Naming is still worth the indirection: the conflict section added a third
    /// ListBox here, and indexing by position would quietly measure that one instead.
    /// </remarks>
    private static T Inside<T>(MainWindow window, string name) where T : FrameworkElement
    {
        var panel = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("GitPanel"));

        return Assert.IsType<T>(panel.FindName(name));
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

            var children = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
    }
}
