using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The option not chosen in a choice still looks like something to press.
/// </summary>
/// <remarks>
/// Rendered | Raw were filter chips, whose OFF state is 35 % opacity because off means "not
/// applied". A choice has no off, and "Raw" read as disabled beside "Rendered" — seen in the
/// running window on 2026-09-21.
/// </remarks>
[Collection("theme")]
public sealed class SegmentChipTests
{
    [Fact]
    public void The_unchosen_option_is_drawn_at_full_strength()
    {
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["SegmentChip"]);
            var chip = new ToggleButton { Style = style, Content = "Raw", IsChecked = false };

            var host = new Grid();
            host.Children.Add(chip);
            host.Measure(new Size(200, 40));
            host.Arrange(new Rect(0, 0, 200, 40));
            host.UpdateLayout();

            var label = (ContentPresenter)chip.Template.FindName("Label", chip);
            Assert.Equal(1.0, label.Opacity);
            Assert.Equal(1.0, chip.Opacity);
        });
    }

    [Fact]
    public void The_pane_header_uses_it_for_rendered_and_raw()
    {
        // The style can be right and the header still wear the filter chip.
        StaHarness.Run(() =>
        {
            using var shell = TestShell.Build();
            var window = new Versio.App.Views.MainWindow(shell);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));

            var segment = Application.Current.Resources["SegmentChip"];
            var raw = Descendants(content).OfType<ToggleButton>().Single(t => Equals(t.Content, "Raw"));
            var rendered = Descendants(content).OfType<ToggleButton>().Single(t => Equals(t.Content, "Rendered"));

            Assert.Same(segment, raw.Style);
            Assert.Same(segment, rendered.Style);
        });
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

    [Fact]
    public void The_chosen_option_is_told_apart_by_more_than_opacity()
    {
        // Fill and weight, so the state survives a glance and a greyscale screenshot.
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["SegmentChip"]);
            var on = new ToggleButton { Style = style, Content = "Rendered", IsChecked = true };
            var off = new ToggleButton { Style = style, Content = "Raw", IsChecked = false };

            var host = new StackPanel();
            host.Children.Add(on);
            host.Children.Add(off);
            host.Measure(new Size(200, 80));
            host.Arrange(new Rect(0, 0, 200, 80));
            host.UpdateLayout();

            Assert.NotEqual(off.FontWeight, on.FontWeight);

            var onFill = ((Border)on.Template.FindName("Root", on)).Background;
            var offFill = ((Border)off.Template.FindName("Root", off)).Background;
            Assert.NotEqual(Colors.Transparent, Assert.IsType<SolidColorBrush>(onFill).Color);
            Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(offFill).Color);
        });
    }
}
