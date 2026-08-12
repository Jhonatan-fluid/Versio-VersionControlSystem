using System.Windows;

namespace Versio.App.Theme;

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

    private static readonly Uri LightPalette =
        new("Theme/tokens/Palette.Light.xaml", UriKind.Relative);

    private static readonly Uri DarkPalette =
        new("Theme/tokens/Palette.Dark.xaml", UriKind.Relative);

    public static AppTheme Current { get; private set; } = AppTheme.System;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var effective = theme == AppTheme.System
            ? (WindowsPrefersDark() ? AppTheme.Dark : AppTheme.Light)
            : theme;

        var themeDictionary = Application.Current?.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Theme.xaml",
                StringComparison.OrdinalIgnoreCase) == true);

        var target = themeDictionary ?? Application.Current?.Resources;
        if (target is null || target.MergedDictionaries.Count <= PaletteIndex)
        {
            return;
        }

        target.MergedDictionaries[PaletteIndex] = new ResourceDictionary
        {
            Source = effective == AppTheme.Dark ? DarkPalette : LightPalette,
        };
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
