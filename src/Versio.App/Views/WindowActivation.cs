using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Versio.App.Views;

/// <summary>
/// Bring another Versio's window to the front.
/// </summary>
/// <remarks>
/// <para>
/// For one moment only: a launch that finds its repository already open in another window on
/// this machine. Before this, the second launch drew a whole empty shell and put a modal over
/// it naming the other window's pid — seen on 2026-09-21, from nothing more unusual than
/// clicking Versio in the Start menu while it was already running. The user asked for their
/// repository, and the answer was a number to go and find.
/// </para>
/// <para>
/// Windows lets a process move another window to the foreground only while it is itself the
/// foreground process, which a freshly launched one is. Everything that can fail here — the
/// process gone between the marker check and now, no main window yet, the call refused —
/// answers false, and the caller falls back to the dialog. Nothing here throws.
/// </para>
/// </remarks>
internal static class WindowActivation
{
    private const int ShowRestore = 9;

    /// <summary>Restore and focus the main window of <paramref name="processId"/>.</summary>
    /// <returns>True if the window is now in front.</returns>
    public static bool TryBringToFront(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            // Minimised is the likeliest state for a window somebody forgot they had open,
            // and SetForegroundWindow alone focuses it without showing it.
            if (IsIconic(handle))
            {
                ShowWindow(handle, ShowRestore);
            }

            return SetForegroundWindow(handle);
        }
        catch (ArgumentException)
        {
            // The process exited after the marker said it was alive.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);
}
