using System.Windows;
using System.Windows.Media;
using Versio.App.Theme;
using Versio.App.Views;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The title bar Windows draws follows the app's theme, on every window.
/// </summary>
/// <remarks>
/// <para>
/// Two halves, and the second is the one that was missing in the theme system this replaces.
/// A window painted when it loads is correct until the user switches theme with it open, at
/// which point the caption keeps the colour it was born with — which is precisely the bug
/// <see cref="ThemeManager"/> already carries a comment about, one strip further out.
/// </para>
/// <para>
/// Nothing here shows a window. The DWM call needs an HWND and a shown window, and this suite
/// deliberately has neither; what is asserted is that every window <i>reaches</i> the painter,
/// which is the part that regresses. The <c>Loaded</c> event is raised by hand — a class
/// handler fires on <see cref="UIElement.RaiseEvent"/> whether or not anything is on screen,
/// so the registration itself is under test rather than assumed.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class WindowCaptionTests
{
    [Fact]
    public void A_colour_is_handed_to_win32_blue_first()
    {
        // COLORREF is 0x00BBGGRR. Reversed, the caption comes out a warm brown - a colour
        // that reads as somebody's decision rather than a bug, which is why this one line
        // has a test to itself.
        var colour = Color.FromRgb(0x1D, 0x20, 0x26);

        Assert.Equal(0x0026201Du, WindowCaption.ToColorRef(colour));
    }

    [Fact]
    public void Pure_red_proves_the_byte_order_rather_than_a_palindrome()
    {
        // The palette's greys are nearly symmetrical, so a reversed conversion of one would
        // still look plausible. Red is asymmetric: wrong order puts it in the blue byte.
        Assert.Equal(0x000000FFu, WindowCaption.ToColorRef(Color.FromRgb(0xFF, 0x00, 0x00)));
        Assert.Equal(0x00FF0000u, WindowCaption.ToColorRef(Color.FromRgb(0x00, 0x00, 0xFF)));
    }

    [Fact]
    public void Each_palette_is_read_as_the_brightness_it_is()
    {
        // Which caption buttons Windows draws follows from this, so it is asserted against
        // the real palettes rather than invented values.
        StaHarness.Run(() =>
        {
            try
            {
                ThemeManager.Apply(AppTheme.Dark);
                Assert.True(WindowCaption.IsDark(Surface()),
                            "the dark palette's base surface must read as dark");

                ThemeManager.Apply(AppTheme.Light);
                Assert.False(WindowCaption.IsDark(Surface()),
                             "the light palette's base surface must not read as dark");
            }
            finally
            {
                ThemeManager.Apply(AppTheme.System);
            }
        });
    }

    [Fact]
    public void A_window_that_loads_is_painted_without_knowing_it_will_be()
    {
        // The whole point of a class handler: the window carries no code for this, so a
        // window added later cannot forget to.
        StaHarness.Run(() =>
        {
            WindowCaption.FollowTheme();

            var window = new Window();
            var painted = Count(window, () => Load(window));

            Assert.Equal(1, painted);
        });
    }

    [Fact]
    public void A_dialog_that_loads_is_painted_too()
    {
        // Pinned separately from the plain Window above, because "it works on the main
        // window" is exactly how this fails. A real dialog, built against the real
        // stylesheet.
        StaHarness.Run(() =>
        {
            WindowCaption.FollowTheme();

            var dialog = new BranchPromptWindow();
            var painted = Count(dialog, () => Load(dialog));

            Assert.Equal(1, painted);
        });
    }

    [Fact]
    public void Registering_the_handler_twice_does_not_paint_twice()
    {
        // The app registers at startup and a test registers again; a class handler added
        // twice is invoked twice.
        StaHarness.Run(() =>
        {
            WindowCaption.FollowTheme();
            WindowCaption.FollowTheme();
            WindowCaption.FollowTheme();

            var window = new Window();

            Assert.Equal(1, Count(window, () => Load(window)));
        });
    }

    [Fact]
    public void Switching_theme_repaints_the_windows_that_are_already_open()
    {
        // The half that a paint-on-load implementation misses, and the reason this is wired
        // into ThemeManager.Apply rather than into MainWindow.
        StaHarness.Run(() =>
        {
            try
            {
                var window = new Window();
                var dialog = new BranchPromptWindow();

                var painted = Count(
                    w => w == window || w == dialog,
                    () => ThemeManager.Apply(
                        ThemeManager.Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark));

                // Both, not just the one that happened to be first in the collection.
                Assert.Equal(2, painted);
            }
            finally
            {
                ThemeManager.Apply(AppTheme.System);
            }
        });
    }

    [Fact]
    public void An_unrealised_window_is_left_alone_rather_than_failing()
    {
        // Every window in this suite is unrealised, and RepaintOpenWindows walks all of them.
        // If a zero HWND threw, the theme switch above would take the app down instead of
        // repainting it.
        StaHarness.Run(() =>
        {
            var window = new Window();

            WindowCaption.Apply(window);
            WindowCaption.RepaintOpenWindows();
        });
    }

    private static Color Surface() =>
        ((SolidColorBrush)Application.Current.Resources["Brush.Surface.Base"]).Color;

    /// <summary>Raise Loaded the way a shown window would, without showing it.</summary>
    private static void Load(Window window) =>
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));

    private static int Count(Window expected, Action work) =>
        Count(w => w == expected, work);

    /// <summary>
    /// How many times the painter was reached for windows this test cares about.
    /// </summary>
    /// <remarks>
    /// Filtered rather than counted outright: the harness keeps one long-lived
    /// <see cref="Application"/>, so windows built by earlier tests are still in
    /// <see cref="Application.Windows"/> and a theme switch legitimately repaints them too.
    /// A bare total would depend on test order.
    /// </remarks>
    private static int Count(Func<Window, bool> matches, Action work)
    {
        var painted = 0;

        void OnApplied(Window window)
        {
            if (matches(window))
            {
                painted++;
            }
        }

        WindowCaption.Applied += OnApplied;
        try
        {
            work();
        }
        finally
        {
            WindowCaption.Applied -= OnApplied;
        }

        return painted;
    }
}
