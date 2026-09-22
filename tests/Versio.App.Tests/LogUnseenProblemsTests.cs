using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A collapsed log says what is worth opening it for, not how chatty it has been.
/// </summary>
/// <remarks>
/// Its pill used to carry the total row count, which Debug drives: seen climbing by sixty a
/// minute in an idle window on 2026-09-21. It now carries the warnings and errors that
/// arrived while the log was closed, and the total only while it is open.
/// </remarks>
[Collection("theme")]
public sealed class LogUnseenProblemsTests
{
    private static LogEvent Entry(LogLevel level) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Level = level,
        Category = LogCategory.App,
        Message = level.ToString(),
    };

    /// <summary>Deliver what is queued without waiting for the 100 ms pump.</summary>
    /// <remarks>Un-pausing drains immediately; that is the public route, so it is the one used.</remarks>
    private static void Drain(LogViewModel log)
    {
        log.IsPaused = true;
        log.IsPaused = false;
    }

    [Fact]
    public void Warnings_and_errors_that_arrive_while_collapsed_are_counted_and_debug_is_not()
    {
        StaHarness.Run(() =>
        {
            var sink = new InMemoryLogSink();
            using var log = new LogViewModel(sink);

            sink.Emit(Entry(LogLevel.Debug));
            sink.Emit(Entry(LogLevel.Information));
            sink.Emit(Entry(LogLevel.Warning));
            Drain(log);

            Assert.Equal(1, log.UnseenProblems);
            Assert.False(log.UnseenHasError);

            sink.Emit(Entry(LogLevel.Error));
            Drain(log);

            Assert.Equal(2, log.UnseenProblems);
            Assert.True(log.UnseenHasError);
        });
    }

    [Fact]
    public void Opening_the_log_marks_them_seen_and_nothing_counts_while_it_is_open()
    {
        StaHarness.Run(() =>
        {
            var sink = new InMemoryLogSink();
            using var log = new LogViewModel(sink);

            sink.Emit(Entry(LogLevel.Error));
            Drain(log);
            log.IsExpanded = true;

            Assert.Equal(0, log.UnseenProblems);
            Assert.False(log.UnseenHasError);

            sink.Emit(Entry(LogLevel.Warning));
            Drain(log);

            Assert.Equal(0, log.UnseenProblems);
        });
    }

    [Fact]
    public void The_collapsed_strip_shows_the_unseen_pill_and_not_the_total()
    {
        StaHarness.Run(() =>
        {
            var sink = new InMemoryLogSink();
            var logger = new VersioLogger(new SystemClock()).AddSink(sink);
            using var shell = new ShellViewModel(logger, new LogViewModel(sink));
            var window = new MainWindow(shell);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            logger.Warn(LogCategory.App, "something worth a look");
            Drain(shell.Log);
            Lay(content);

            Assert.Equal(Visibility.Visible, Pill(content, "Unseen warnings and errors").Visibility);
            Assert.NotEqual(Visibility.Visible, Pill(content, "Log entries").Visibility);

            shell.Log.IsExpanded = true;
            Lay(content);

            Assert.NotEqual(Visibility.Visible, Pill(content, "Unseen warnings and errors").Visibility);
            Assert.Equal(Visibility.Visible, Pill(content, "Log entries").Visibility);
        });
    }

    private static void Lay(FrameworkElement content)
    {
        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();
    }

    private static Border Pill(DependencyObject root, string name) =>
        Assert.Single(Descendants(root).OfType<Border>(),
            b => AutomationProperties.GetName(b) == name);

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
