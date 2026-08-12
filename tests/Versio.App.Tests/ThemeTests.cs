using System.Windows;
using System.Windows.Media;
using Versio.App.Theme;
using Versio.App.Views;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Switching the theme actually repaints, and every window follows it.
/// </summary>
/// <remarks>
/// <para>
/// The bug these exist for: <c>MainWindow</c> merged its own copy of <c>Theme.xaml</c> into
/// <c>Window.Resources</c>, so its lookups found that private palette first and never reached
/// the application's — the one <see cref="ThemeManager"/> swaps. The theme buttons in Settings
/// therefore did nothing to the main window, while every dialog, having no private copy,
/// followed them correctly. The two halves of the app disagreed about what colour they were.
/// </para>
/// <para>
/// Both facts are asserted here: the swap reaches <c>Application.Resources</c>, and a window
/// that is <b>already built</b> repaints when it happens — which is what
/// <c>DynamicResource</c> buys and what a second private copy takes away.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class ThemeTests
{
    private static Color Surface() =>
        ((SolidColorBrush)Application.Current.Resources["Brush.Surface.Base"]).Color;

    [Fact]
    public void The_two_palettes_are_not_the_same_paint()
    {
        StaHarness.Run(() =>
        {
            try
            {
                ThemeManager.Apply(AppTheme.Light);
                var light = Surface();

                ThemeManager.Apply(AppTheme.Dark);
                var dark = Surface();

                // If these ever match, every assertion below passes for the wrong reason.
                Assert.NotEqual(light, dark);
            }
            finally
            {
                ThemeManager.Apply(AppTheme.System);
            }
        });
    }

    [Fact]
    public void A_window_built_before_the_switch_repaints_when_it_happens()
    {
        StaHarness.Run(() =>
        {
            try
            {
                ThemeManager.Apply(AppTheme.Light);

                // Built while light, then switched underneath it. A window carrying its own
                // palette would keep the colour it was born with - which is exactly what the
                // main window did.
                var window = new BranchPromptWindow();
                var before = ((SolidColorBrush)window.Background).Color;

                ThemeManager.Apply(AppTheme.Dark);
                var after = ((SolidColorBrush)window.Background).Color;

                Assert.NotEqual(before, after);
                Assert.Equal(Surface(), after);
            }
            finally
            {
                ThemeManager.Apply(AppTheme.System);
            }
        });
    }

    [Fact]
    public void The_palette_is_swapped_where_the_application_can_see_it()
    {
        StaHarness.Run(() =>
        {
            try
            {
                ThemeManager.Apply(AppTheme.Dark);

                // Resolved through Application.Resources rather than off a window, because
                // that is the dictionary every view walks up to. A swap that landed anywhere
                // else would leave this untouched.
                Assert.Equal(AppTheme.Dark, ThemeManager.Current);

                var dark = Surface();
                ThemeManager.Apply(AppTheme.Light);

                Assert.NotEqual(dark, Surface());
                Assert.Equal(AppTheme.Light, ThemeManager.Current);
            }
            finally
            {
                ThemeManager.Apply(AppTheme.System);
            }
        });
    }

    [Fact]
    public void System_resolves_to_one_of_the_two_real_palettes()
    {
        StaHarness.Run(() =>
        {
            ThemeManager.Apply(AppTheme.Light);
            var light = Surface();
            ThemeManager.Apply(AppTheme.Dark);
            var dark = Surface();

            ThemeManager.Apply(AppTheme.System);

            // Whichever Windows prefers, it must be one of them - not a third state, and not
            // whatever happened to be loaded at startup.
            var system = Surface();
            Assert.True(system == light || system == dark,
                        $"system resolved to {system}, which is neither palette");

            Assert.Equal(ThemeManager.WindowsPrefersDark() ? dark : light, system);
        });
    }
}
