using System.Windows;
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
public sealed class ShellLayoutTests
{
    /// <summary>A window wide enough to be real and narrow enough to be honest.</summary>
    private const double Narrow = 1000;

    private static MainWindow Build()
    {
        var logger = new VersioLogger(new SystemClock());
        var sink = new InMemoryLogSink();
        logger.AddSink(sink);

        return new MainWindow(new ShellViewModel(logger, new LogViewModel(sink)));
    }

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
            var log = Assert.IsType<Border>(window.FindName("LogConsole"));

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
            var log = Assert.IsType<Border>(window.FindName("LogConsole"));

            var railBounds = BoundsIn(rail, content);
            var logBounds = BoundsIn(log, content);

            Assert.True(railBounds.Bottom >= logBounds.Bottom,
                        $"the rail ends at y={railBounds.Bottom:F0} and the log console at " +
                        $"y={logBounds.Bottom:F0}, so the rail stops short of it");
        });
    }

    [Theory]
    [InlineData("STAGED", "Unstage all")]
    [InlineData("NOT STAGED", "Discard")]
    [InlineData("NOT STAGED", "Stage all")]
    public void A_section_label_never_sits_under_its_own_buttons(string label, string button)
    {
        // Reported: "git panel cluttered ... looks really bad", with NOT STAGED and Discard
        // touching. The cause is one Grid cell holding both a label and a right-aligned button
        // group: a right-aligned child grows leftwards ACROSS the label rather than pushing
        // it, and neither knows the other is there. Two columns fix it; this is what proves
        // they stay.
        StaHarness.Run(() =>
        {
            var window = Build();

            // The git panel is collapsed until the rail selects it, and a collapsed element
            // arranges to nothing - which would make every assertion below vacuously true.
            var rail = Assert.IsType<RadioButton>(window.FindName("RailGit"));
            rail.IsChecked = true;

            var content = Arrange(window);
            var panel = Assert.IsType<Border>(window.FindName("GitPanel"));

            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.True(panel.RenderSize.Width > 0, "the git panel did not render");

            var labelBounds = BoundsIn(Text(panel, label), content);
            var buttonBounds = BoundsIn(Button(panel, button), content);

            Assert.True(labelBounds.Width > 0, $"'{label}' did not render");
            Assert.True(buttonBounds.Width > 0, $"'{button}' did not render");

            Assert.False(labelBounds.IntersectsWith(buttonBounds),
                         $"'{label}' occupies {labelBounds} and '{button}' {buttonBounds}, " +
                         "which overlap");
        });
    }

    [Fact]
    public void The_diff_header_keeps_its_label_clear_of_the_path()
    {
        // Same defect, other pane: DIFF, a checkbox and a 420 px path all shared one cell.
        StaHarness.Run(() =>
        {
            var window = Build();
            var content = Arrange(window);

            var label = Text(content, "DIFF");
            var check = Descendants(content).OfType<CheckBox>()
                .Single(c => Equals(c.Content, "Show full file"));

            Assert.False(BoundsIn(label, content).IntersectsWith(BoundsIn(check, content)),
                         "the DIFF label and 'Show full file' overlap");
        });
    }

    private static TextBlock Text(DependencyObject root, string text) =>
        Descendants(root).OfType<TextBlock>().Single(t => t.Text == text);

    private static Button Button(DependencyObject root, string content) =>
        Descendants(root).OfType<Button>().Single(b => Equals(b.Content, content));

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
