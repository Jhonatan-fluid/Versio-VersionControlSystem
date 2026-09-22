using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Versio.App.Views;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The caption says which repository, and the name "Versio" appears once.
/// </summary>
/// <remarks>
/// The caption said "Versio" directly above a brand row that said "Versio" (seen 2026-09-21),
/// and with two windows open on two repositories the taskbar showed two identical "Versio"s.
/// </remarks>
[Collection("theme")]
public sealed class WindowTitleTests
{
    [Fact]
    public async Task The_caption_leads_with_the_repository_once_one_is_open()
    {
        using var repo = new TempRepo();
        using var shell = TestShell.Build();

        Assert.Equal("Versio", shell.WindowTitle);

        await shell.OpenAsync(repo.Path);

        Assert.Equal($"{System.IO.Path.GetFileName(repo.Path)} — Versio", shell.WindowTitle);
    }

    [Fact]
    public void The_window_binds_its_caption_and_the_top_bar_no_longer_repeats_the_name()
    {
        StaHarness.Run(() =>
        {
            using var shell = TestShell.Build();
            var window = new MainWindow(shell);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));

            // The binding attaches before the constructor sets DataContext, so WPF defers it
            // to the dispatcher's DataBind pass; a shown window gets that pass for free.
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            Assert.Equal(shell.WindowTitle, window.Title);
            Assert.DoesNotContain(Descendants(content).OfType<TextBlock>(), t => t.Text == "Versio");
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            yield return child;

            foreach (var further in Descendants(child))
            {
                yield return further;
            }
        }
    }
}
