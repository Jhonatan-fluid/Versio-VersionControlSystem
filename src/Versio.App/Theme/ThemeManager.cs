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

    /// <summary>
    /// Read a stored theme, treating anything unrecognised as "follow the system".
    /// </summary>
    /// <remarks>
    /// Core stores the choice as a string because it does not know about the WPF layer, so
    /// the parse lives here. Unrecognised is not an error: a settings file hand-edited to
    /// nonsense, or written by a later version that grew a fourth option, must cost a
    /// preference rather than a launch.
    /// </remarks>
    public static AppTheme Parse(string? stored) =>
        Enum.TryParse<AppTheme>(stored, ignoreCase: true, out var theme)
            ? theme
            : AppTheme.System;

    /// <summary>
    /// Repaint when Windows changes its own theme, but only while following it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Follow system was a snapshot, not a subscription.</b> The palette was chosen once at
    /// startup by reading the registry, and nothing listened afterwards — so flipping Windows
    /// into dark mode with Versio open left it in the light palette until it was restarted,
    /// on the setting that is the default and that says by name that it follows.
    /// </para>
    /// <para>
    /// Guarded on <see cref="Current"/> rather than unsubscribed and resubscribed as the
    /// choice changes: the event is process-wide and idempotent handling is simpler to be
    /// sure of than a subscription whose lifetime tracks a setting.
    /// </para>
    /// </remarks>
    public static void FollowSystemChanges()
    {
        if (_listening)
        {
            return;
        }

        _listening = true;

        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            // General covers the app-theme flip. Narrower categories miss it on some builds,
            // and re-applying the palette we are already on costs one dictionary rebuild.
            if (e.Category is Microsoft.Win32.UserPreferenceCategory.General
                           or Microsoft.Win32.UserPreferenceCategory.Color
                && Current == AppTheme.System)
            {
                Application.Current?.Dispatcher.Invoke(() => Apply(AppTheme.System));
            }
        };
    }

    private static bool _listening;

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

        // The caption is not part of the resource system, so nothing above repaints it. Every
        // window that is already open would otherwise keep the caption of the theme it was
        // born in until the app restarted - the same half-working theme switch this class was
        // written to fix, one strip further out.
        WindowCaption.RepaintOpenWindows();
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
