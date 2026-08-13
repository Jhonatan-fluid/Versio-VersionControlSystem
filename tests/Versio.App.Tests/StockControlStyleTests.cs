using System.Windows;
using System.Windows.Controls;
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
