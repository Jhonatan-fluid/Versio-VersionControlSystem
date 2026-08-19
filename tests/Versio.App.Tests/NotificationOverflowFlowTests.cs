using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Versio.App.Mvvm;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Logging;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The overflow pill leads somewhere (UX-9).
/// </summary>
/// <remarks>
/// <para>
/// <b>What was wrong.</b> More than three live notifications collapse the rest into a pill
/// reading "+2 more". It was a <c>Border</c> holding a <c>TextBlock</c>, with no command, no
/// cursor and nothing behind it — a count of things the app declined to show, appearing only
/// at the moment several of them had gone wrong at once.
/// </para>
/// <para>
/// <b>What makes the fix possible is already there.</b> <c>NotificationCenter.Post</c> mirrors
/// every notification into the log as it posts it, deliberately, so that a dismissed card and
/// an empty log can never be the same state. The held ones are therefore already readable; the
/// only thing missing was a way to say so.
/// </para>
/// </remarks>
public sealed class NotificationOverflowFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    /// <summary>Four notifications, so one is held back.</summary>
    private static void Flood(ShellViewModel shell)
    {
        shell.Notifications.Failure("Push failed.", "one");
        shell.Notifications.Failure("Fetch failed.", "two");
        shell.Notifications.Failure("Pull failed.", "three");
        shell.Notifications.Failure("Commit failed.", "four");
    }

    [Fact]
    public void The_pill_appears_only_once_something_is_actually_held_back()
    {
        using var shell = Shell();

        Assert.False(shell.Notifications.HasOverflow);

        Flood(shell);

        Assert.True(shell.Notifications.HasOverflow);
        Assert.Equal("+1 more", shell.Notifications.OverflowText);
        Assert.Equal(NotificationCenter.VisibleLimit, shell.Notifications.Visible.Count);
    }

    [Fact]
    public void Pressing_it_opens_the_log_on_the_notifications()
    {
        using var shell = Shell();

        Flood(shell);
        Assert.False(shell.Log.IsExpanded);

        shell.ShowHeldNotificationsCommand.Execute(null);

        // Open, because the panel it sends you to is collapsed by default and an unopened log
        // is the same dead end with an extra step.
        Assert.True(shell.Log.IsExpanded);

        // And filtered to where the notifications actually are, named through the constant the
        // posting side uses rather than a second literal.
        Assert.Equal(NotificationCenter.LogCategoryName, shell.Log.Filters.CategorySummary);
        Assert.True(shell.Log.Filters.IsFiltering);
    }

    /// <summary>
    /// The one assertion that would catch the filter and the post drifting apart.
    /// </summary>
    /// <remarks>
    /// Both sides name <c>NotificationCenter.LogCategoryName</c>, so they cannot disagree by
    /// construction — but a constant proves only that two references match, not that the value
    /// is the right one. This asks the question the user does: press the pill, and are the
    /// notifications among the rows that survive?
    /// </remarks>
    [Fact]
    public void The_held_notifications_survive_the_filter_it_applies()
    {
        using var shell = TestShell.Build(out var sink);

        Flood(shell);
        shell.ShowHeldNotificationsCommand.Execute(null);

        var kept = sink.Snapshot().Where(shell.Log.Filters.Passes).ToList();

        Assert.Contains(kept, e => e.Message.Contains("Push failed.", StringComparison.Ordinal));
        Assert.Contains(kept, e => e.Message.Contains("Commit failed.", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>ShowOnly</c> turns levels on as well as categories.
    /// </summary>
    /// <remarks>
    /// The half that is easy to leave out and impossible to see afterwards: a panel whose
    /// <c>WRN</c> chip was switched off in a previous session would land showing a subset of
    /// what was asked for and look exactly like the whole of it. Failures post at
    /// <c>Error</c>, warnings at <c>Warning</c> — so a filter that only fixed the category
    /// would deliver an empty panel over a real problem.
    /// </remarks>
    [Fact]
    public void It_restores_the_levels_a_notification_can_arrive_at()
    {
        using var shell = Shell();
        var filters = shell.Log.Filters;

        foreach (var level in filters.Levels)
        {
            level.IsEnabled = false;
        }

        filters.ShowOnly(NotificationCenter.LogCategoryName);

        Assert.All(
            filters.Levels.Where(l => l.Level != LogLevel.Debug),
            l => Assert.True(l.IsEnabled, $"{l.Label} is still hidden"));

        // DBG is deliberately left alone: it is volume rather than content, and turning it on
        // unasked buries the thing being looked for under the thing that is always happening.
        Assert.False(filters.Levels.Single(l => l.Level == LogLevel.Debug).IsEnabled);
    }

    /// <summary>
    /// A batch of toggles rebuilds the panel once, not once per toggle.
    /// </summary>
    /// <remarks>
    /// <b>Not a micro-optimisation.</b> <c>LogViewModel.Rebuild</c> clears the bound collection
    /// and re-walks a sink holding up to ten thousand entries, and it runs on the UI thread
    /// inside the click. Ten toggles meant ten of those. This was already true of "All
    /// categories" and "No categories" before the pill needed it.
    /// </remarks>
    [Fact]
    public void Applying_a_filter_raises_one_change_not_one_per_toggle()
    {
        using var shell = Shell();
        var filters = shell.Log.Filters;

        var raised = 0;
        filters.Changed += () => raised++;

        filters.ShowOnly(NotificationCenter.LogCategoryName);

        Assert.Equal(1, raised);
    }

    /// <summary>The pill is a real control, in the window, wired to the command.</summary>
    /// <remarks>
    /// <b>The view model half cannot see this.</b> A command with no caller passes every test
    /// above and ships a pill that still does nothing — which is the defect, unchanged. The
    /// focus visual is asserted here rather than left to <c>FocusVisualTests</c>: that class
    /// walks keyed styles, and what matters is that THIS control wears one, since a retemplated
    /// button with no <c>FocusVisualStyle</c> falls back to WPF's dotted black adorner and
    /// nothing fails while it does.
    /// </remarks>
    [Fact]
    public void The_window_gives_it_a_command_a_name_and_a_focus_ring()
    {
        StaHarness.Run(() =>
        {
            using var shell = TestShell.Build();
            var window = new MainWindow(shell);

            var pill = Assert.IsType<Button>(window.FindName("NotificationOverflow"));

            Assert.Equal(
                nameof(ShellViewModel.ShowHeldNotificationsCommand),
                BindingOperations.GetBinding(pill, ButtonBase.CommandProperty)?.Path.Path);

            // "+2 more" read aloud is not something anybody can act on, so the name is written
            // out rather than derived from the content.
            var name = System.Windows.Automation.AutomationProperties.GetName(pill);
            Assert.Contains("log", name, StringComparison.OrdinalIgnoreCase);

            Assert.True(pill.FocusVisualStyle is Style,
                        "the overflow pill has no focus visual, so tabbing to it shows nothing");

            // THE TYPEFACE, TIED TO THE STYLE IT USED TO GET IT FROM.
            //
            // The count was a TextBlock wearing PillText, which is mono. Becoming a Button put
            // it under GhostButton, which is Font.UI - so the face changed silently, and the
            // setter that had been carrying it disappeared along with the TextBlock. Nothing in
            // the suite could see that. Asserted against PillText rather than against "mono" so
            // the two cannot drift apart later either.
            // Worn by a throwaway TextBlock rather than read off the Setter, which holds an
            // unresolved DynamicResourceExtension - the same trick FocusVisualTests uses, and
            // the only way to compare what the two controls actually END UP with.
            var reference = new TextBlock
            {
                Style = Assert.IsType<Style>(Application.Current.Resources["PillText"]),
            };

            Assert.Equal(reference.FontFamily, pill.FontFamily);

            Assert.Equal(
                "Notifications.HasOverflow",
                BindingOperations.GetBinding(pill, UIElement.VisibilityProperty)?.Path.Path);
        });
    }
}
