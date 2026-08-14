using System.Windows;
using System.Windows.Controls;
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
public sealed class GitPanelGeometryTests(ITestOutputHelper output)
{
    private const double PanelWidth = 380;

    private static ChangeRow Row(string path, ChangeKind staged, ChangeKind unstaged) =>
        new(new FileChange { Path = path, Staged = staged, Unstaged = unstaged });

    /// <summary>A panel carrying about as much as it ever will.</summary>
    private static (MainWindow Window, ShellViewModel Shell, Border Panel, FrameworkElement Content) Loaded()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        var shell = new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);
        var window = new MainWindow(shell);
        var content = (FrameworkElement)window.Content;

        var rail = (RadioButton)window.FindName("RailGit")!;
        rail.IsChecked = true;

        for (var i = 0; i < 4; i++)
        {
            shell.Staged.Add(Row($"src/MainProject/PLC/POU/FB_Conveyor_{i}.st",
                                 ChangeKind.Modified, ChangeKind.Unmodified));
        }

        for (var i = 0; i < 12; i++)
        {
            shell.Unstaged.Add(Row($"src/MainProject/PLC/GVL/GVL_Alarms_Section_{i}.st",
                                   ChangeKind.Unmodified, ChangeKind.Modified));
        }

        shell.CommitMessage = "Rework the conveyor interlock so a jam stops the upstream feed";
        shell.Notifications.Failure("Push failed.", "rejected — non-fast-forward");

        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();

        return (window, shell, (Border)window.FindName("GitPanel")!, content);
    }

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
    public void Both_staging_lists_get_real_room_with_content_in_them()
    {
        StaHarness.Run(() =>
        {
            var (window, _, _, _) = Loaded();

            // By name: the conflict section added a third ListBox to this panel, and indexing
            // by position would quietly start measuring that one instead.
            var staged = Assert.IsType<ListBox>(window.FindName("StagedList"));
            var unstaged = Assert.IsType<ListBox>(window.FindName("UnstagedList"));

            output.WriteLine($"staged list:   {staged.ActualHeight:F0} px for 4 rows");
            output.WriteLine($"unstaged list: {unstaged.ActualHeight:F0} px for 12 rows");

            // The layout this replaced trapped the staged list at 150 px however much was in
            // it. Both panes are star-sized now, so neither should be a sliver.
            Assert.True(staged.ActualHeight >= 60,
                        $"the staged list got {staged.ActualHeight:F0} px");
            Assert.True(unstaged.ActualHeight >= 60,
                        $"the unstaged list got {unstaged.ActualHeight:F0} px");

            // Enough for more than one row each, or the splitter has nothing to divide.
            Assert.True(staged.ActualHeight + unstaged.ActualHeight > 200,
                        "the two lists together got less room than a single one used to");
        });
    }

    [Fact]
    public void Nothing_overflows_the_panel_sideways()
    {
        // A 380 px column with a checkbox, a glyph and a long PLC path in every row is exactly
        // where a horizontal overflow appears - and an overflowing row is what somebody means
        // by "it looks broken".
        StaHarness.Run(() =>
        {
            var (_, _, panel, content) = Loaded();

            var panelRight = panel.TransformToAncestor(content)
                                  .Transform(new Point(panel.ActualWidth, 0)).X;

            var widest = 0d;
            string? offender = null;

            foreach (var element in AllOf<FrameworkElement>(panel)
                         .Where(e => e.Visibility == Visibility.Visible && e.ActualWidth > 0))
            {
                var right = element.TransformToAncestor(content)
                                   .Transform(new Point(element.ActualWidth, 0)).X;

                if (right > widest)
                {
                    widest = right;
                    offender = Describe(element);
                }
            }

            output.WriteLine($"panel right edge {panelRight:F0}, widest child {widest:F0} ({offender})");

            // A pixel of tolerance for rounding at the boundary.
            Assert.True(widest <= panelRight + 1,
                        $"{offender} reaches {widest:F0} px, past the panel's {panelRight:F0} px");
        });
    }

    [Fact]
    public void A_long_plc_path_is_trimmed_rather_than_pushing_the_row_wide()
    {
        StaHarness.Run(() =>
        {
            var (window, _, _, _) = Loaded();

            var unstaged = Assert.IsType<ListBox>(window.FindName("UnstagedList"));
            var item = AllOf<ListBoxItem>(unstaged).FirstOrDefault();

            Assert.True(item is not null, "no unstaged row was realised");
            output.WriteLine($"row: {item!.ActualWidth:F0} x {item.ActualHeight:F0}");

            Assert.True(item.ActualHeight > 0, "a realised row has no height");
            Assert.True(item.ActualWidth <= unstaged.ActualWidth + 1,
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
            _ => e.GetType().Name,
        };

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
