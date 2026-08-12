using System.Windows;

namespace Versio.App.Theme;

/// <summary>Which palette to paint with.</summary>
public enum AppTheme
{
    /// <summary>Follow the Windows app theme.</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// Swaps the palette dictionary at runtime.
/// </summary>
/// <remarks>
/// Only the palette is replaced. Because every brush in <c>Semantic.xaml</c> resolves its
/// colour with <c>DynamicResource</c>, the whole window re-themes without a restart and
/// without any view knowing a theme exists.
/// </remarks>
public static class ThemeManager
{
    private const int PaletteIndex = 0;

    // Absolute pack URIs, naming the assembly. A relative URI resolves against the ENTRY
    // assembly, which is Versio.exe when the app runs and something else entirely under any
    // other host - a test runner, for one, where it fails with "cannot locate resource". The
    // palette lives in this assembly whoever started the process, so it says so.
    private static readonly Uri LightPalette = new(
        "pack://application:,,,/Versio;component/Theme/tokens/Palette.Light.xaml");

    private static readonly Uri DarkPalette = new(
        "pack://application:,,,/Versio;component/Theme/tokens/Palette.Dark.xaml");

    private static readonly Uri Theme = new(
        "pack://application:,,,/Versio;component/Theme/Theme.xaml");

    public static AppTheme Current { get; private set; } = AppTheme.System;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var effective = theme == AppTheme.System
            ? (WindowsPrefersDark() ? AppTheme.Dark : AppTheme.Light)
            : theme;

        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var index = application.Resources.MergedDictionaries
            .Select((d, i) => (Dictionary: d, Index: i))
            .FirstOrDefault(x => x.Dictionary.Source?.OriginalString.EndsWith(
                "Theme.xaml", StringComparison.OrdinalIgnoreCase) == true).Index;

        if (application.Resources.MergedDictionaries.Count == 0)
        {
            return;
        }

        // The WHOLE theme is rebuilt and swapped in, not just the palette inside it.
        //
        // Replacing the palette alone looked right and did nothing: measured, Color.Surface.Base
        // changed from #FFFFFFFF to #FF1D2026 while Brush.Surface.Base - a SolidColorBrush
        // whose Color is a DynamicResource on that very colour - stayed white, and so did every
        // window painted with it. A Freezable sitting in a resource dictionary resolves its
        // DynamicResource once and is not re-resolved when a sibling dictionary underneath it
        // is replaced. So the theme buttons changed a colour nothing was looking at any more.
        //
        // Rebuilding the dictionary makes every brush a new instance, and replacing the entry
        // in Application.Resources is what invalidates the DynamicResource lookups in windows
        // that are already open - which is the whole point of a theme switch.
        var rebuilt = new ResourceDictionary { Source = Theme };
        if (rebuilt.MergedDictionaries.Count > PaletteIndex)
        {
            rebuilt.MergedDictionaries[PaletteIndex] = new ResourceDictionary
            {
                Source = effective == AppTheme.Dark ? DarkPalette : LightPalette,
            };
        }

        application.Resources.MergedDictionaries[index] = rebuilt;
    }

    /// <summary>
    /// Read the Windows app-theme preference.
    /// </summary>
    /// <remarks>
    /// Registry rather than a UWP API so the app has no WinRT dependency. A missing value
    /// means light, which is the Windows default.
    /// </remarks>
    public static bool WindowsPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
