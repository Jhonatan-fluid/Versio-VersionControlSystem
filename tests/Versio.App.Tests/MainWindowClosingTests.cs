using System.Windows;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Guarding;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The window actually closes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only test in this project that shows a window</b>, and it earns the exception. The
/// close guard is driven from <c>Window.OnClosing</c>, and the failure it shipped with could
/// not be reproduced any other way: <c>RequestCloseAsync</c> returns synchronously when nothing
/// is running, so the <c>await</c> never yielded and <c>Close</c> ran inside the <c>Closing</c>
/// event WPF was still raising. WPF throws on that, the crash handler swallowed it on the
/// dispatcher, and the window simply stayed open — <b>Versio could not be closed at all</b>.
/// </para>
/// <para>
/// Every view-model test passed throughout, because they drive
/// <see cref="ShellViewModel.RequestCloseAsync"/> and never a window. Only a real
/// <c>Closing</c> event has the re-entrancy state that makes the call illegal.
/// </para>
/// <para>
/// The window is positioned far off-screen and kept out of the taskbar, so a suite that
/// otherwise shows nothing does not start flashing windows at whoever runs it.
/// </para>
/// </remarks>
public sealed class MainWindowClosingTests
{
    private static (MainWindow Window, ShellViewModel Shell) BuildOffScreen()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        using var shell = new ShellViewModel(logger, new LogViewModel(sink));

        var window = new MainWindow(shell)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            Left = -32000,
            Top = -32000,
            Width = 400,
            Height = 300,
        };

        return (window, shell);
    }

    [Fact]
    public void An_idle_window_closes_when_asked()
    {
        StaHarness.Run(() =>
        {
            var (window, _) = BuildOffScreen();
            var closed = false;

            window.Closed += (_, _) => closed = true;
            window.Show();

            window.Close();

            // The second close is posted behind the Closing event, so the dispatcher has to
            // be pumped before it runs. This is the mechanism under test, not a workaround:
            // calling Close inline is exactly the bug.
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            Assert.True(closed, "the window did not close");
        });
    }

    [Fact]
    public void Declining_to_close_during_an_import_leaves_the_window_open()
    {
        // Don't close is still how an import keeps the window up. Stop would kill it.
        StaHarness.Run(() =>
        {
            var (window, shell) = BuildOffScreen();
            var closed = false;

            window.Closed += (_, _) => closed = true;
            window.Show();

            using var importing = shell.Busy.Begin(WorkKind.Import, "Importing into Machine…");

            shell.Asked().Guard = _ => GuardChoice.Cancel;

            window.Close();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            Assert.False(closed, "the window closed over a running import");

            importing.Dispose();
            shell.Asked().Guard = null;
            window.Close();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        });
    }
}
