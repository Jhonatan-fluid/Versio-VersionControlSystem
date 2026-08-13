using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The controls nobody names a style for — WPF's own defaults — wear this app's chrome.
/// </summary>
/// <remarks>
/// <para>
/// Every other style in the stylesheet is keyed, so a view that wants it says so and a test
/// can check the key exists. These are the opposite: <b>implicit</b> styles, applying to every
/// <c>ToolTip</c>, <c>CheckBox</c> and <c>RadioButton</c> in the app without a single call site
/// mentioning them. That is what makes them worth pinning — nothing in any view refers to
/// them, so deleting one breaks the look of dozens of controls and no lookup anywhere fails.
/// </para>
/// <para>
/// Applied and measured rather than merely looked up, for the reason
/// <c>LogPanelChromeTests</c> already records: a key can hold a template that resolves fine
/// and throws the moment something tries to render it.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class StockControlStyleTests
{
    [Fact]
    public void Tooltips_have_a_style_without_anything_asking_for_one()
    {
        StaHarness.Run(() =>
        {
            // Keyed by type: this is how WPF finds an implicit style, and how a view gets one
            // it never mentions.
            Assert.True(Application.Current.Resources.Contains(typeof(ToolTip)),
                        "there is no implicit ToolTip style, so every tooltip in the app is " +
                        "the stock WPF one");
        });
    }

    [Fact]
    public void The_tooltip_template_instantiates()
    {
        StaHarness.Run(() =>
        {
            var tip = new ToolTip
            {
                Content = "Pushing is what releases the PLC direction locks",
            };

            // A ToolTip is a popup: it does not participate in a window's visual tree, and it
            // builds nothing until it opens. Measuring is what proves the template is real.
            tip.Measure(new Size(400, 200));

            Assert.True(tip.DesiredSize.Width > 0);
        });
    }

    [Theory]
    [InlineData(typeof(CheckBox))]
    [InlineData(typeof(RadioButton))]
    public void The_stock_controls_have_an_implicit_style(Type control)
    {
        // These two were the last stock chrome in the app: WPF's own Aero square and circle,
        // on a dark surface, in the settings popup and beside "Show full file".
        StaHarness.Run(() =>
        {
            Assert.True(Application.Current.Resources.Contains(control),
                        $"there is no implicit {control.Name} style, so every {control.Name} " +
                        "in the app is the stock WPF one");
        });
    }

    [Fact]
    public void A_checkbox_renders_its_own_box_in_both_states()
    {
        StaHarness.Run(() =>
        {
            foreach (var state in new[] { false, true })
            {
                var check = new CheckBox { Content = "Show full file", IsChecked = state };

                check.Measure(new Size(300, 40));
                check.Arrange(new Rect(0, 0, 300, 40));
                check.UpdateLayout();

                Assert.True(check.DesiredSize.Width > 0);

                // The 16 px box, found by size rather than by name: a template that lost it
                // would still measure fine on the strength of the label alone.
                var box = Descendants(check).OfType<Border>()
                    .FirstOrDefault(b => Math.Abs(b.ActualWidth - 16) < 0.5);

                Assert.NotNull(box);
            }
        });
    }

    [Fact]
    public void A_radio_renders_a_ring_and_shows_its_dot_only_when_checked()
    {
        StaHarness.Run(() =>
        {
            double DotOpacity(bool checkedState)
            {
                var radio = new RadioButton { Content = "Dark", IsChecked = checkedState };

                radio.Measure(new Size(300, 40));
                radio.Arrange(new Rect(0, 0, 300, 40));
                radio.UpdateLayout();

                var ellipses = Descendants(radio).OfType<Ellipse>().ToList();

                // Ring and dot, both present in both states; only the opacity moves.
                Assert.Equal(2, ellipses.Count);

                return ellipses.Single(e => Math.Abs(e.Width - 8) < 0.5).Opacity;
            }

            Assert.Equal(0, DotOpacity(false));
            Assert.Equal(1, DotOpacity(true));
        });
    }

    [Fact]
    public void The_rail_keeps_its_own_style_despite_the_implicit_one()
    {
        // RailButton is a RadioButton with an explicit Style, and an explicit style beats an
        // implicit one. That rule is obvious right up until somebody relies on it, so it is
        // pinned: if the implicit style ever won here, the rail's 40 px icon tiles would turn
        // into ring-and-label radio buttons and the whole left edge of the app would change.
        StaHarness.Run(() =>
        {
            var rail = Assert.IsType<Style>(Application.Current.Resources["RailButton"]);

            var button = new RadioButton
            {
                Style = rail,
                Content = "",
                IsChecked = true,
            };

            button.Measure(new Size(80, 80));
            button.Arrange(new Rect(0, 0, 80, 80));
            button.UpdateLayout();

            // The rail's template is a fixed 40x40 tile with no label. A radio ring would
            // measure narrower than this and carry an Ellipse pair.
            Assert.Equal(40, button.DesiredSize.Width);
            Assert.Empty(Descendants(button).OfType<Ellipse>());
        });
    }

    [Fact]
    public void A_tree_row_selects_with_the_theme_rather_than_the_system_highlight()
    {
        // The defect: ListBoxItem was retemplated in the first pass precisely because the
        // system highlight is the loudest "old app" tell, and the two TreeViews never got the
        // same treatment - so the project tree, the surface somebody looks at all day, painted
        // its selected row in Windows' own highlight blue.
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["TreeRowItem"]);
            var tree = new TreeView();
            var item = new TreeViewItem { Style = style, Header = "Simulation.st" };

            tree.Items.Add(item);
            tree.Measure(new Size(400, 200));
            tree.Arrange(new Rect(0, 0, 400, 200));
            tree.UpdateLayout();

            item.IsSelected = true;
            tree.UpdateLayout();

            var expected = (SolidColorBrush)Application.Current.Resources["Brush.Surface.Selected"];
            var system = SystemColors.HighlightBrush;

            // The fill is the row-wide Border, the widest one in the template.
            var fill = Descendants(item).OfType<Border>()
                .OrderByDescending(b => b.ActualWidth)
                .First(b => b.Background is SolidColorBrush);

            var painted = Assert.IsType<SolidColorBrush>(fill.Background);

            Assert.Equal(expected.Color, painted.Color);
            Assert.NotEqual(system.Color, painted.Color);
        });
    }

    [Fact]
    public void A_tree_row_still_round_trips_IsExpanded()
    {
        // The expensive regression. Both TreeViews bind IsExpanded two-way to their row, and
        // that binding is what drives the project tree's lazy children and the history's
        // demand-driven file lists. The retemplate puts a ToggleButton on the same property,
        // so if it fought the binding rather than following it, expanding a node in the UI
        // would stop loading anything and nothing else would look wrong.
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["TreeRowItem"]);
            var tree = new TreeView();
            var item = new TreeViewItem { Style = style, Header = "Application" };

            item.Items.Add(new TreeViewItem { Style = style, Header = "Simulation.st" });
            tree.Items.Add(item);

            tree.Measure(new Size(400, 200));
            tree.Arrange(new Rect(0, 0, 400, 200));
            tree.UpdateLayout();

            var expander = Descendants(item).OfType<ToggleButton>().First();

            // Property to control...
            item.IsExpanded = true;
            tree.UpdateLayout();
            Assert.True(expander.IsChecked);

            // ...and control back to property, which is the direction the row's binding needs.
            expander.IsChecked = false;
            tree.UpdateLayout();
            Assert.False(item.IsExpanded);
        });
    }

    [Fact]
    public void A_leaf_keeps_its_indent_but_loses_its_chevron()
    {
        // Hidden rather than collapsed: a leaf with no expander column would sit 16 px left of
        // its siblings, which reads as a broken indent rather than as "nothing to expand".
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["TreeRowItem"]);
            var tree = new TreeView();
            var leaf = new TreeViewItem { Style = style, Header = "Simulation.st" };

            tree.Items.Add(leaf);
            tree.Measure(new Size(400, 200));
            tree.Arrange(new Rect(0, 0, 400, 200));
            tree.UpdateLayout();

            var expander = Descendants(leaf).OfType<ToggleButton>().First();

            Assert.Equal(Visibility.Hidden, expander.Visibility);
            Assert.True(expander.ActualWidth > 0, "a hidden expander still holds its column");
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

    [Fact]
    public void A_long_tooltip_wraps_rather_than_running_off_the_screen()
    {
        // Several tooltips in the shell are whole sentences - the direction-lock rule, what
        // Discard cannot undo. Unwrapped, they lose the half that matters off the edge.
        StaHarness.Run(() =>
        {
            var tip = new ToolTip
            {
                Content =
                    "Also stash files git has never seen - including an export tree that was " +
                    "never committed, which is the case this option exists for",
            };

            tip.Measure(new Size(2000, 2000));

            Assert.True(tip.DesiredSize.Width <= 360,
                        $"a tooltip measured {tip.DesiredSize.Width:F0} px wide, so it is " +
                        "not wrapping");
            Assert.True(tip.DesiredSize.Height > 20, "a wrapped tooltip occupies more than one line");
        });
    }
}
