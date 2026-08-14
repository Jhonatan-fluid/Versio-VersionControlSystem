using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The scrollbar works on both axes, not only the one it was written for.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> One template served both orientations and every axis-dependent decision
/// inside it was written for vertical. <c>Width="10"</c> was a plain setter with nothing to
/// release it, so a <b>horizontal</b> bar was arranged 10 px <i>wide</i> — measured at 10 px
/// across a 400 px viewport. The thumb hardcoded its <c>Width</c>, which is thickness on one
/// axis and length on the other; <c>IsDirectionReversed</c> was true on both, which inverts a
/// horizontal drag; and the invisible-but-hit-testable trough buttons issued <c>PageUp</c> and
/// <c>PageDown</c>, so clicking the trough of a horizontal bar scrolled vertically.
/// </para>
/// <para>
/// It survived because horizontal bars appear in exactly two places — the diff view and the
/// log console — and nothing in the suite had ever arranged one. <c>StockControlStyleTests</c>
/// measures checkboxes, radios and tree rows; the scrollbar was styled, so it was never on
/// the list of controls to check.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class ScrollBarChromeTests
{
    [Fact]
    public void A_horizontal_bar_spans_its_host_rather_than_ten_pixels()
    {
        // The regression, stated as the number that was wrong.
        StaHarness.Run(() =>
        {
            var bar = Arranged(Orientation.Horizontal, width: 400, height: 40);

            Assert.True(bar.ActualWidth > 300,
                        $"a horizontal scrollbar arranged {bar.ActualWidth:F0} px wide in a " +
                        "400 px host, so it is pinned to the vertical bar's thickness");
        });
    }

    [Fact]
    public void A_vertical_bar_spans_its_host_rather_than_ten_pixels()
    {
        StaHarness.Run(() =>
        {
            var bar = Arranged(Orientation.Vertical, width: 40, height: 300);

            Assert.True(bar.ActualHeight > 200,
                        $"a vertical scrollbar arranged {bar.ActualHeight:F0} px tall in a " +
                        "300 px host");
        });
    }

    [Fact]
    public void Each_axis_keeps_its_thickness_and_releases_the_other_dimension()
    {
        // Width is thickness on a vertical bar and the whole span on a horizontal one. A
        // dimension that is not this axis's thickness has to be released, or it pins the bar.
        StaHarness.Run(() =>
        {
            var vertical = Arranged(Orientation.Vertical, width: 40, height: 300);
            var horizontal = Arranged(Orientation.Horizontal, width: 400, height: 40);

            Assert.Equal(10, vertical.Width);
            Assert.True(double.IsNaN(vertical.Height), "a vertical bar pins its height");

            Assert.Equal(10, horizontal.Height);
            Assert.True(double.IsNaN(horizontal.Width), "a horizontal bar pins its width");
        });
    }

    [Fact]
    public void The_thumb_is_thin_across_the_bar_and_long_along_it()
    {
        // The thumb hardcoded Width="6" for both, which on a horizontal bar made it 6 px
        // LONG - a dot, at whatever position the track put it.
        StaHarness.Run(() =>
        {
            var horizontal = Arranged(Orientation.Horizontal, width: 400, height: 40);
            var thumb = Descendants(horizontal).OfType<Thumb>().Single();

            // The Thumb itself fills the track's thickness; the Border inside it is what
            // gets painted, and its thickness is the thing that was on the wrong axis.
            var painted = Descendants(thumb).OfType<Border>().Single();

            Assert.Equal(6, painted.Height);
            Assert.True(double.IsNaN(painted.Width),
                        "the horizontal thumb pins its width, so it is a 6 px dot rather " +
                        "than a bar as long as the viewport is wide");
            Assert.True(thumb.ActualWidth > 20,
                        $"the horizontal thumb is only {thumb.ActualWidth:F0} px long, so it " +
                        "is being sized on the wrong axis");
        });
    }

    [Fact]
    public void A_horizontal_track_is_not_reversed()
    {
        // True is correct for vertical, where the maximum is at the bottom, and inverts the
        // drag on horizontal - the thumb walks left as the content scrolls right.
        StaHarness.Run(() =>
        {
            var vertical = Arranged(Orientation.Vertical, width: 40, height: 300);
            var horizontal = Arranged(Orientation.Horizontal, width: 400, height: 40);

            Assert.True(Track(vertical).IsDirectionReversed);
            Assert.False(Track(horizontal).IsDirectionReversed);
        });
    }

    [Fact]
    public void A_horizontal_trough_pages_sideways()
    {
        // The trough buttons are Opacity="0" and still hit-testable, so these commands are
        // what a click in the empty part of the bar actually runs.
        StaHarness.Run(() =>
        {
            var horizontal = Arranged(Orientation.Horizontal, width: 400, height: 40);
            var commands = Descendants(horizontal).OfType<RepeatButton>()
                .Select(b => b.Command)
                .ToList();

            Assert.Contains(ScrollBar.PageLeftCommand, commands);
            Assert.Contains(ScrollBar.PageRightCommand, commands);
            Assert.DoesNotContain(ScrollBar.PageUpCommand, commands);
            Assert.DoesNotContain(ScrollBar.PageDownCommand, commands);
        });
    }

    [Fact]
    public void Dragging_a_horizontal_thumb_moves_the_value_forwards()
    {
        // The behavioural end of the direction-reversed check: not "the flag is false" but
        // "the number goes the way the hand went".
        StaHarness.Run(() =>
        {
            var bar = Arranged(Orientation.Horizontal, width: 400, height: 40);
            var track = Track(bar);

            // What the Track itself reports for a rightwards drag from the thumb. This is
            // the calculation the ScrollBar's own drag handler consumes.
            var delta = track.ValueFromDistance(120, 0);

            Assert.True(delta > 0,
                        $"dragging the thumb right changed the value by {delta:F1}, so the " +
                        "track is reversed and the content scrolls the wrong way");
        });
    }

    /// <summary>A bar of the given orientation, laid out inside a host of the given size.</summary>
    /// <remarks>
    /// A real arrange pass rather than a style lookup. The whole defect was a style that
    /// resolved perfectly and produced the wrong geometry, so a test that only asked whether
    /// the style existed would have passed throughout.
    /// </remarks>
    private static ScrollBar Arranged(Orientation orientation, double width, double height)
    {
        var bar = new ScrollBar
        {
            Orientation = orientation,
            Minimum = 0,
            Maximum = 100,
            ViewportSize = 25,
            Value = 10,
        };

        var host = new Grid { Width = width, Height = height };
        host.Children.Add(bar);

        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();

        return bar;
    }

    private static Track Track(ScrollBar bar) => Descendants(bar).OfType<Track>().Single();

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
