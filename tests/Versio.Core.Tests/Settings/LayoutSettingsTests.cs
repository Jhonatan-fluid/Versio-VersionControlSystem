using Versio.Core.Settings;
using Xunit;

namespace Versio.Core.Tests;

/// <summary>
/// A restored pane size is checked against the screen it is being restored onto.
/// </summary>
/// <remarks>
/// A stored size is only as trustworthy as the display it was stored on. Somebody who drags
/// the log console to 900 px on a 4K monitor and then opens Versio on a laptop would otherwise
/// get a log taller than the window, the panels squeezed to nothing, and no splitter left on
/// screen to drag it back with — a preference that makes the application unusable and offers
/// no way to undo itself.
/// </remarks>
public sealed class LayoutSettingsTests
{
    [Fact]
    public void Nothing_stored_restores_nothing()
    {
        // Zero means "not set". A pane nobody has resized has no stored size, and the caller
        // is expected to fall back to its own default rather than be handed one.
        Assert.Equal(0, LayoutSettings.Restore(stored: 0, minimum: 80, available: 600));
    }

    [Fact]
    public void A_negative_size_is_treated_as_nothing_stored()
    {
        // Not clamped up to the minimum: a negative is a corrupt file rather than a small
        // pane, and the honest response is the default.
        Assert.Equal(0, LayoutSettings.Restore(stored: -40, minimum: 80, available: 600));
    }

    [Fact]
    public void An_ordinary_size_comes_back_unchanged()
    {
        Assert.Equal(240, LayoutSettings.Restore(stored: 240, minimum: 80, available: 600));
    }

    [Fact]
    public void A_size_from_a_bigger_screen_is_cut_to_what_fits()
    {
        Assert.Equal(600, LayoutSettings.Restore(stored: 900, minimum: 80, available: 600));
    }

    [Fact]
    public void A_size_below_the_minimum_is_raised_to_it()
    {
        Assert.Equal(80, LayoutSettings.Restore(stored: 12, minimum: 80, available: 600));
    }

    [Fact]
    public void A_window_too_small_for_the_minimum_still_gets_the_minimum()
    {
        // The clamp cannot invert. With available below minimum, Math.Clamp would throw on a
        // low bound above its high bound - which would be a crash on a small window rather
        // than a cramped panel.
        Assert.Equal(80, LayoutSettings.Restore(stored: 200, minimum: 80, available: 40));
    }

    [Fact]
    public void No_window_yet_restores_nothing()
    {
        // Called before the window has a size, ActualHeight is zero. Restoring against that
        // would clamp every stored size down to the minimum and quietly rewrite it.
        Assert.Equal(0, LayoutSettings.Restore(stored: 240, minimum: 80, available: 0));
    }
}
