using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The marks that change state fade rather than snap, and the templates that do it render.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap.</b> <c>Motion.Fast</c> and <c>Motion.Base</c> sat in <c>Spacing.xaml</c> from the
/// day it was written, with an argument attached — "animation in a tool is feedback, not
/// decoration" — and the codebase contained no <c>Storyboard</c>, no <c>DoubleAnimation</c> and
/// no <c>VisualStateManager</c> at all. Every state change snapped.
/// </para>
/// <para>
/// <b>Rendering is the half that has to be measured.</b> The first version of the animations
/// referenced the durations with <c>StaticResource</c> from a dictionary that only merges
/// <c>Spacing.xaml</c> as a sibling, which compiles with zero warnings and throws
/// <c>Cannot find resource named 'Motion.Fast'</c> the moment anything renders a rail button.
/// That is this stylesheet's own stated failure mode, happening to the stylesheet — so these
/// apply the templates and lay them out rather than checking a key exists.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class MotionTests
{
    [Theory]
    [InlineData("Motion.Fast")]
    [InlineData("Motion.Base")]
    public void The_durations_are_reachable_from_the_stylesheet(string key)
    {
        // Reachable from the APPLICATION dictionary, which is where a DynamicResource looks.
        StaHarness.Run(() =>
        {
            var duration = Assert.IsType<Duration>(Application.Current.Resources[key]);

            Assert.True(duration.HasTimeSpan);
            Assert.True(duration.TimeSpan <= TimeSpan.FromMilliseconds(200),
                        $"{key} is {duration.TimeSpan.TotalMilliseconds} ms - animation in a " +
                        "tool is feedback, and past 200 ms it is something the user waits for");
        });
    }

    [Theory]
    [InlineData("Motion.MarkIn")]
    [InlineData("Motion.MarkOut")]
    [InlineData("Motion.IndicatorIn")]
    [InlineData("Motion.IndicatorOut")]
    public void Each_storyboard_carries_a_real_duration(string key)
    {
        // A DoubleAnimation whose Duration failed to resolve is Automatic - one second - which
        // would look like a bug rather than like a missing resource.
        StaHarness.Run(() =>
        {
            var storyboard = Assert.IsType<Storyboard>(Application.Current.Resources[key]);
            var animation = Assert.IsType<DoubleAnimation>(Assert.Single(storyboard.Children));

            Assert.True(animation.Duration.HasTimeSpan,
                        $"{key} has an Automatic duration, so its resource did not resolve");
            Assert.True(animation.Duration.TimeSpan <= TimeSpan.FromMilliseconds(200));
        });
    }

    [Fact]
    public void The_rail_renders_and_its_indicator_reaches_full_opacity()
    {
        // Applied and MEASURED, which is what the compile-clean StaticResource failure got
        // past. The animation is asynchronous, so the assertion waits for it rather than
        // reading the value on the frame the trigger fired.
        StaHarness.Run(async () =>
        {
            var rail = Assert.IsType<Style>(Application.Current.Resources["RailButton"]);
            var button = new RadioButton { Style = rail, Content = "" };

            button.Measure(new Size(80, 80));
            button.Arrange(new Rect(0, 0, 80, 80));
            button.UpdateLayout();

            var indicator = Indicator(button);

            Assert.Equal(0, indicator.Opacity);

            button.IsChecked = true;

            await Settled(() => indicator.Opacity >= 1);

            Assert.Equal(1, indicator.Opacity);
        });
    }

    [Fact]
    public void A_selected_row_fades_its_bar_in_and_back_out()
    {
        StaHarness.Run(async () =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["RowItem"]);
            var list = new ListBox();
            var item = new ListBoxItem { Style = style, Content = "FB_Motor" };

            list.Items.Add(item);
            list.Measure(new Size(380, 200));
            list.Arrange(new Rect(0, 0, 380, 200));
            list.UpdateLayout();

            var bar = Bar(item);

            Assert.Equal(0, bar.Opacity);

            item.IsSelected = true;
            await Settled(() => bar.Opacity >= 1);
            Assert.Equal(1, bar.Opacity);

            // And back. An ExitAction that was forgotten leaves every row that has ever been
            // selected wearing an accent bar, which is worse than no animation at all.
            item.IsSelected = false;
            await Settled(() => bar.Opacity <= 0);
            Assert.Equal(0, bar.Opacity);
        });
    }

    [Fact]
    public void A_selected_tree_row_does_the_same()
    {
        StaHarness.Run(async () =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["TreeRowItem"]);
            var tree = new TreeView();
            var item = new TreeViewItem { Style = style, Header = "Simulation.st" };

            tree.Items.Add(item);
            tree.Measure(new Size(380, 200));
            tree.Arrange(new Rect(0, 0, 380, 200));
            tree.UpdateLayout();

            var bar = Bar(item);
            item.IsSelected = true;

            await Settled(() => bar.Opacity >= 1);

            Assert.Equal(1, bar.Opacity);
        });
    }

    /// <summary>The 3 px accent bar: the narrowest Border in the template.</summary>
    private static Border Bar(Control row) =>
        Descendants(row).OfType<Border>().Single(b => b.Width is 3);

    /// <summary>The rail's indicator, found the same way.</summary>
    private static Border Indicator(Control button) =>
        Descendants(button).OfType<Border>().Single(b => b.Width is 3);

    /// <summary>Wait for an animation to finish, or give up and let the assertion say so.</summary>
    private static async Task Settled(Func<bool> done)
    {
        for (var attempt = 0; attempt < 100 && !done(); attempt++)
        {
            await Task.Delay(10);
        }
    }

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
