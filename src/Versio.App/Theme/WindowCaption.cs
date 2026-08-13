using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Versio.App.Theme;

/// <summary>
/// Paints the title bar Windows draws, so it carries the app's theme like everything else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every surface in the app followed the theme except the one strip the app does not
/// draw.</b> A dark window with a white caption above it is the most visible unthemed pixel
/// area in the product, and it is not something a style can reach: the non-client area belongs
/// to the window manager, and the only way in is to ask DWM for it.
/// </para>
/// <para>
/// The caption is painted with <c>Surface.Base</c> rather than merely switched to
/// <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>. Immersive dark mode gives a generic dark grey that is
/// close to the top bar and not equal to it, and a seam one shade off reads worse than an
/// honest contrast — so the exact colour is handed over instead, and the caption and the top
/// bar become one surface.
/// </para>
/// <para>
/// Applied through a class handler on <see cref="FrameworkElement.LoadedEvent"/> for every
/// <see cref="Window"/> rather than from each window's constructor. Four windows exist today
/// and the fifth would have been the one somebody forgot; a class handler cannot be forgotten
/// by a window that has not been written yet.
/// </para>
/// </remarks>
public static class WindowCaption
{
    // Documented DWMWINDOWATTRIBUTE values. The three colour attributes are Windows 11
    // 22000+; DwmSetWindowAttribute returns a failure HRESULT on anything older, which is
    // handled rather than thrown - see Paint.
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    /// <summary>
    /// Register the one handler that paints every window this application ever opens.
    /// </summary>
    /// <remarks>
    /// Call once, at startup. Loaded rather than SourceInitialized because a class handler
    /// needs a routed event, and by Loaded the HWND exists - which is all Paint requires.
    /// </remarks>
    public static void FollowTheme()
    {
        // Idempotent. A class handler registered twice is called twice, and this is called
        // once by the app and once per test run that needs it.
        if (_following)
        {
            return;
        }

        _following = true;

        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window)
                {
                    Apply(window);
                }
            }));
    }

    /// <summary>
    /// Repaint every window that is currently open.
    /// </summary>
    /// <remarks>
    /// The other half of the story, and the half that is easy to miss: painting on load alone
    /// leaves an open window wearing the caption of the theme it was born in until somebody
    /// restarts the app. <see cref="ThemeManager.Apply"/> calls this immediately after
    /// swapping the palette.
    /// </remarks>
    public static void RepaintOpenWindows()
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        foreach (Window window in application.Windows)
        {
            Apply(window);
        }
    }

    /// <summary>Paint one window's caption with the current theme.</summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        Applied?.Invoke(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            // Not yet realised. Nothing to paint, and nothing wrong: the class handler will
            // be back when the window is loaded.
            return;
        }

        Paint(handle, Resolve("Color.Surface.Base"), Resolve("Color.Text.Primary"));
    }

    /// <summary>
    /// Hand the colours to DWM, with the pre-Windows-11 fallback behind them.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="Apply"/> so the interop can be exercised against a real HWND
    /// without a <see cref="Window"/>, and so <see cref="Painted"/> has one place to count.
    /// </remarks>
    private static void Paint(IntPtr handle, Color? caption, Color? text)
    {
        // Dark mode first, and unconditionally. On Windows 11 the colour attributes below
        // override it; on Windows 10 they fail and this is the whole effect. Asking for both
        // costs two syscalls and removes a version check that would have to be maintained.
        var dark = caption is { } fill && IsDark(fill) ? 1 : 0;
        if (Set(handle, UseImmersiveDarkMode, dark) != 0)
        {
            Set(handle, UseImmersiveDarkModeBefore20H1, dark);
        }

        if (caption is { } captionColor)
        {
            Set(handle, CaptionColor, (int)ToColorRef(captionColor));
            Set(handle, BorderColor, (int)ToColorRef(captionColor));
        }

        if (text is { } textColor)
        {
            Set(handle, TextColor, (int)ToColorRef(textColor));
        }
    }

    /// <summary>
    /// A colour in the byte order Win32 wants: <c>0x00BBGGRR</c>.
    /// </summary>
    /// <remarks>
    /// <b>COLORREF is not RGB.</b> Reversed, the app's blue-grey caption comes back a warm
    /// brown - a colour that looks like a decision somebody made rather than a bug, which is
    /// exactly why this is the one line in the file with a test of its own.
    /// </remarks>
    internal static uint ToColorRef(Color color) =>
        (uint)color.R | ((uint)color.G << 8) | ((uint)color.B << 16);

    /// <summary>
    /// Whether a surface is dark enough that the caption's own controls should invert.
    /// </summary>
    /// <remarks>
    /// Rec. 601 luma, which is coarse and sufficient: the question is only which of two
    /// caption button sets Windows should draw, and the two palettes are nowhere near the
    /// boundary.
    /// </remarks>
    internal static bool IsDark(Color color) =>
        ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) < 128.0;

    /// <summary>
    /// Raised for every window this class is asked to paint. Test seam; nothing in the app
    /// subscribes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The DWM call itself cannot be observed from a test — it succeeds into the window
    /// manager and returns zero — and it needs an HWND, which means showing a window, which
    /// no test in this suite does. So what is asserted instead is <i>reach</i>: that every
    /// window which loads gets here, and that every open window gets here again when the theme
    /// changes. Those are the two things that were missing and the two that can regress. A
    /// caption painter wired to the main window alone looks perfect until a dialog opens.
    /// </para>
    /// <para>
    /// Raised before the HWND check on purpose, so an unrealised window still counts as
    /// visited. The alternative made the seam untestable without showing windows, which is the
    /// thing being avoided.
    /// </para>
    /// </remarks>
    internal static event Action<Window>? Applied;

    private static bool _following;

    private static Color? Resolve(string key) =>
        Application.Current?.TryFindResource(key) as Color?;

    private static int Set(IntPtr handle, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // dwmapi.dll is present on every Windows this app supports. If it is somehow not,
            // the window keeps the system caption and the app keeps working.
            return -1;
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);
}
