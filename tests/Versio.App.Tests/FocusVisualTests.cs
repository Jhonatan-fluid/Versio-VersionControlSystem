using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Keyboard focus is visible, and visible in this app's colours.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these pin.</b> Every control in the stylesheet was retemplated and not one of
/// them said anything about focus, so the whole application fell through to WPF's own focus
/// adorner — a 1 px dotted <i>black</i> rectangle. On <c>Surface.Base</c> at <c>#1D2026</c> that
/// is invisible, and on light it is exactly the piece of 2009 chrome the stylesheet's header
/// spends four paragraphs removing. Tabbing through the git panel gave no way to tell whether
/// Enter would fetch, pull, push or discard, and Discard is in that row.
/// </para>
/// <para>
/// Nothing failed while it was missing, which is why it needs a test rather than a reviewer:
/// a control with no <c>FocusVisualStyle</c> builds, lays out and renders perfectly.
/// </para>
/// <para>
/// Two shapes are checked because there are two. Controls that stand alone get
/// <c>FocusRing</c>, an adorner drawn outside their bounds. Rows in a scrolling list get a
/// trigger inside their own template instead, because an adorner that expands outward is
/// clipped by the viewport — the first and last visible row would lose the edge nearest the
/// clip, which is the edge somebody is looking at while they arrow towards it.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class FocusVisualTests
{
    /// <summary>The styles whose call sites are keyed, and the control each one dresses.</summary>
    public static TheoryData<string, Type> RingedStyles => new()
    {
        { "GhostButton", typeof(Button) },
        { "IconButton", typeof(Button) },
        { "AccentButton", typeof(Button) },
        { "RailButton", typeof(RadioButton) },
        { "FlatCombo", typeof(ComboBox) },
        { "FlatTextBox", typeof(TextBox) },
        { "FilterChip", typeof(ToggleButton) },
        { "SegmentChip", typeof(ToggleButton) },
    };

    [Theory]
    [MemberData(nameof(RingedStyles))]
    public void A_keyed_control_carries_a_focus_visual(string key, Type control)
    {
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources[key]);
            var element = (Control)Activator.CreateInstance(control)!;
            element.Style = style;

            Assert.True(element.FocusVisualStyle is Style,
                        $"{key} has no FocusVisualStyle, so a control wearing it falls back " +
                        "to WPF's dotted black adorner - invisible on a dark surface");
        });
    }

    [Theory]
    [InlineData(typeof(CheckBox))]
    [InlineData(typeof(RadioButton))]
    public void The_implicit_styles_carry_one_too(Type control)
    {
        // These two are reached by nothing: no call site names them, so a missing setter here
        // would go unnoticed in the settings popup, beside "Show full file", and on every row
        // of the log's category dropdown at once.
        StaHarness.Run(() =>
        {
            var element = (Control)Activator.CreateInstance(control)!;

            Assert.True(element.FocusVisualStyle is Style,
                        $"the implicit {control.Name} style sets no FocusVisualStyle");
        });
    }

    [Fact]
    public void The_ring_is_drawn_in_the_accent_rather_than_in_black()
    {
        // The whole point. WPF's default adorner is a black dotted rectangle, and "there is a
        // focus visual" would still be true of it.
        StaHarness.Run(() =>
        {
            var ring = Assert.IsType<Style>(Application.Current.Resources["FocusRing"]);
            var accent = (SolidColorBrush)Application.Current.Resources["Brush.Accent.Default"];

            // A FocusVisualStyle has no TargetType, so it is applied to a Control host the way
            // the adorner does, and then measured - a key can hold a template that resolves
            // fine and throws the first time something renders it.
            var host = new ContentControl { Style = ring, Width = 80, Height = 32 };

            host.Measure(new Size(120, 60));
            host.Arrange(new Rect(0, 0, 120, 60));
            host.UpdateLayout();

            var border = Descendants(host).OfType<Border>().Single();
            var painted = Assert.IsType<SolidColorBrush>(border.BorderBrush);

            Assert.Equal(accent.Color, painted.Color);
            Assert.True(border.BorderThickness.Left >= 1);
        });
    }

    [Fact]
    public void A_list_row_outlines_itself_when_the_keyboard_reaches_it()
    {
        // Focus is NOT selection, and in the staged and not-staged lists they genuinely come
        // apart: both are extended-selection lists, so Ctrl+Arrow walks the focus without
        // moving what is picked. Without this the walk is invisible.
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["RowItem"]);
            var list = new ListBox();
            var item = new ListBoxItem { Style = style, Content = "FB_Motor" };
            list.Items.Add(item);

            InAWindow(list, () =>
            {
                Assert.Equal(0, FillBorderThickness(item));

                Assert.True(item.Focus(), "the row refused focus");
                Assert.True(item.IsKeyboardFocused, "the row never took keyboard focus");

                Assert.True(FillBorderThickness(item) >= 1,
                            "a keyboard-focused row draws no outline, so arrowing through " +
                            "an extended-selection list shows nothing");
            });
        });
    }

    [Fact]
    public void A_tree_row_outlines_itself_when_the_keyboard_reaches_it()
    {
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["TreeRowItem"]);
            var tree = new TreeView();
            var item = new TreeViewItem { Style = style, Header = "Simulation.st" };
            tree.Items.Add(item);

            InAWindow(tree, () =>
            {
                Assert.Equal(0, FillBorderThickness(item));

                Assert.True(item.Focus(), "the tree row refused focus");
                Assert.True(item.IsKeyboardFocused, "the tree row never took keyboard focus");

                Assert.True(FillBorderThickness(item) >= 1,
                            "a keyboard-focused tree row draws no outline");
            });
        });
    }

    [Fact]
    public void A_text_box_watermark_steps_aside_for_the_keyboard_and_sits_where_the_caret_will()
    {
        // The commit box, reported 2026-10-14: a 72 px box saying VerticalContentAlignment=Top
        // whose caret blinked mid-box beside a mid-box "Summary of the change…" that did not
        // go away on focus — it read as text to delete. The template hardcoded Center on both
        // the content host and the placeholder, so the property was a silent lie at every
        // usage site, and the placeholder hid on the first keystroke rather than on focus.
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["FlatTextBox"]);
            var box = new TextBox
            {
                Style = style,
                Tag = "Summary of the change…",
                AcceptsReturn = true,
                Height = 72,
                VerticalContentAlignment = VerticalAlignment.Top,
            };

            InAWindow(box, () =>
            {
                var placeholder = Descendants(box).OfType<TextBlock>()
                    .Single(t => (string)t.Text == "Summary of the change…");
                var host = Descendants(box).OfType<ScrollViewer>().Single();

                // Both follow the box's own alignment, so the hint sits where typing starts.
                Assert.Equal(VerticalAlignment.Top, placeholder.VerticalAlignment);
                Assert.Equal(VerticalAlignment.Top, host.VerticalAlignment);

                Assert.Equal(Visibility.Visible, placeholder.Visibility);

                Assert.True(box.Focus(), "the box refused focus");
                Assert.Equal(Visibility.Collapsed, placeholder.Visibility);

                // Left empty, the hint comes back when the keyboard leaves.
                Keyboard.ClearFocus();
                Assert.Equal(Visibility.Visible, placeholder.Visibility);

                // With text, it stays away whether focused or not.
                box.Text = "Fix the pump interlock";
                Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
            });
        });
    }

    [Fact]
    public void A_focused_row_is_still_tellable_apart_from_a_selected_one()
    {
        // An outline rather than the selected fill, so a row that is both does not look like
        // a row that is only one. Selection is a fill plus the accent bar; focus is an edge.
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["RowItem"]);
            var list = new ListBox();
            var item = new ListBoxItem { Style = style, Content = "FB_Motor" };
            list.Items.Add(item);

            InAWindow(list, () =>
            {
                item.Focus();

                var painted = Assert.IsType<SolidColorBrush>(FillBorder(item).Background);
                var selected =
                    (SolidColorBrush)Application.Current.Resources["Brush.Surface.Selected"];

                // Focused and not selected: outlined, but not filled as though it were picked.
                Assert.NotEqual(selected.Color, painted.Color);
            });
        });
    }

    /// <summary>Run <paramref name="assertions"/> with <paramref name="content"/> on screen.</summary>
    /// <remarks>
    /// A real window, shown. Keyboard focus belongs to a <see cref="PresentationSource"/> and
    /// not to the visual tree: an element merely measured and arranged in isolation refuses
    /// <c>Focus()</c> and reports <c>IsKeyboardFocused</c> false forever, which is how the
    /// first version of these tests failed while the feature underneath them worked.
    /// <para>
    /// Positioned off any plausible desktop and never activated, so a test run does not flash
    /// a window over whatever the developer is doing.
    /// </para>
    /// </remarks>
    private static void InAWindow(UIElement content, Action assertions)
    {
        var window = new Window
        {
            Content = content,
            Width = 380,
            Height = 200,
            Left = -4000,
            Top = -4000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };

        window.Show();

        try
        {
            window.UpdateLayout();
            assertions();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The row-wide fill Border, which is the one the focus trigger outlines.</summary>
    private static Border FillBorder(Control row) =>
        Descendants(row).OfType<Border>()
            .OrderByDescending(b => b.ActualWidth)
            .First(b => b.Background is SolidColorBrush);

    private static double FillBorderThickness(Control row) =>
        FillBorder(row).BorderThickness.Left;

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
