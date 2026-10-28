using System.Windows;
using System.Windows.Controls;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Logging;
using Versio.Core.Updates;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The update overlay in the real window: that it shows over everything while an update runs,
/// and that its buttons reach the view model.
/// </summary>
/// <remarks>
/// <para>
/// <c>UpdatesViewModelTests</c> drives the stages; nothing there can tell whether the window
/// draws them. A misspelt binding path fails silently in WPF - the overlay would simply never
/// appear, which is the exact defect the overlay was built to fix.
/// </para>
/// <para>
/// Keys aimed behind the overlay are dropped by a <c>PreviewKeyDown</c> handler, which needs a
/// presented window to receive input and is not reachable here. That half is on the
/// owner-eyeball list with the look of the card.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class UpdateOverlayWindowTests
{
    /// <summary>Finds 0.5.0 and fails to download it, so every step completes synchronously.</summary>
    private sealed class FailingSource : IUpdateSource
    {
        public string CurrentVersion => "0.4.4";

        public string? Unavailable => null;

        public bool HasDownloadedUpdate => false;

        public Task<UpdateCheck> CheckAsync(CancellationToken ct) =>
            Task.FromResult(UpdateCheck.Available(new UpdateOffer("0.5.0", string.Empty)));

        public Task DownloadAsync(UpdateOffer offer, IProgress<int>? progress, CancellationToken ct) =>
            throw new System.Net.Http.HttpRequestException("connection reset");

        public void ApplyAfterExit(bool restart)
        {
        }
    }

    [Fact]
    public void The_overlay_covers_the_window_only_while_an_update_is_under_way()
    {
        StaHarness.Run(() =>
        {
            var sink = new InMemoryLogSink();
            var logger = new VersioLogger(new SystemClock()).AddSink(sink);
            using var shell = new ShellViewModel(
                logger, new LogViewModel(sink), toolchains: (_, _) => null, updates: new FailingSource());
            var window = new MainWindow(shell);

            var content = (FrameworkElement)window.Content;
            Lay(content);
            var overlay = Assert.IsType<Grid>(window.FindName("UpdateOverlay"));
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);

            // After the window, which installs its own prompts on the shell.
            shell.Asked().Update = _ => UpdateChoice.UpdateNow;
            _ = shell.Updates.CheckNowAsync();

            Assert.Equal(UpdateStage.Failed, shell.Updates.Stage);
            Lay(content);
            Assert.Equal(Visibility.Visible, overlay.Visibility);

            // Over the panels and over the toasts' ZIndex 10, spanning every row and column.
            Assert.True(Panel.GetZIndex(overlay) > 10);
            Assert.Equal(5, Grid.GetRowSpan(overlay));
            Assert.Equal(2, Grid.GetColumnSpan(overlay));

            var tryAgain = Assert.IsType<Button>(window.FindName("UpdateTryAgainButton"));
            var cancel = Assert.IsType<Button>(window.FindName("UpdateCancelButton"));
            Assert.Equal(Visibility.Visible, tryAgain.Visibility);
            Assert.Equal(Visibility.Collapsed, cancel.Visibility);
            Assert.Same(shell.Updates.TryAgainCommand, tryAgain.Command);

            shell.Updates.CloseOverlayCommand.Execute(null);
            Lay(content);

            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            window.Close();
        });
    }

    /// <summary>Bindings in a never-shown window settle on layout, not on construction.</summary>
    private static void Lay(FrameworkElement content)
    {
        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();
    }
}
