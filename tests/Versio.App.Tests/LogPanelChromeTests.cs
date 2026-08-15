using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Infrastructure.Platform;
using Versio.Core.Logging;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The log panel's new chrome — the level chips, the category dropdown, the paused pill —
/// against the real stylesheet.
/// </summary>
/// <remarks>
/// <para>
/// <b>The category list lives inside a <c>Popup</c>, and that is the reason this file
/// exists.</b> WPF does not build a popup's content until the popup opens, so every
/// <c>StaticResource</c> inside it is unresolved — and therefore unchecked — until somebody
/// clicks the button. It is the same blind spot already recorded for modal dialogs, one level
/// further down: a renamed style there is not a compile error, not a binding warning, and not
/// anything at all until a user opens the dropdown and the app throws in their face.
/// </para>
/// <para>
/// So the styles are resolved and <i>applied</i> here rather than merely looked up. A key
/// that exists but holds a template that cannot instantiate would pass a lookup and fail in
/// front of somebody.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class LogPanelChromeTests
{
    /// <summary>
    /// Every resource the log header and the category popup name.
    /// </summary>
    /// <remarks>
    /// Listed explicitly rather than scraped from the XAML: a test that reads the same file
    /// it is checking agrees with it by construction, whatever either of them says.
    /// </remarks>
    private static readonly string[] RequiredKeys =
    [
        "FilterChip",
        "Pill",
        "PillText",
        "MenuCheck",
        "SectionLabel",
        "GhostButton",
        "IconButton",
        "FlatTextBox",
        "IconText",
        "Icon.Play",
        "Icon.Pause",
        "Icon.Filter",
        "Icon.ChevronDown",
        "Icon.ChevronUp",
    ];

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_resource_the_log_chrome_names_exists(string key)
    {
        StaHarness.Run(() =>
        {
            Assert.True(Application.Current.Resources.Contains(key),
                        $"{key} is referenced by the log panel and is not in the stylesheet");
        });
    }

    public static TheoryData<string> Keys()
    {
        var data = new TheoryData<string>();
        foreach (var key in RequiredKeys)
        {
            data.Add(key);
        }

        return data;
    }

    [Fact]
    public void The_level_chip_template_instantiates()
    {
        StaHarness.Run(() =>
        {
            // Applying the style is the test. A key holding a malformed template resolves
            // fine and throws only when something tries to render it.
            // Fetched through Assert rather than cast directly: a missing key casts to a
            // null Style, and assigning null is perfectly legal - so this test passed with
            // the style deleted until it was checked by deleting the style.
            var style = Assert.IsType<Style>(Application.Current.Resources["FilterChip"]);

            var chip = new ToggleButton
            {
                Style = style,
                Content = "DBG",
                IsChecked = true,
            };

            chip.Measure(new Size(200, 40));

            Assert.True(chip.DesiredSize.Width > 0);
        });
    }

    [Fact]
    public void The_menu_check_template_instantiates()
    {
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["MenuCheck"]);

            var check = new CheckBox
            {
                Style = style,
                Content = "Sync",
            };

            check.Measure(new Size(200, 40));

            Assert.True(check.DesiredSize.Width > 0);
        });
    }

    [Fact]
    public void A_chip_toggles_the_level_it_names()
    {
        // The chips are bound straight to IsEnabled with no command in between, so this is
        // the whole interaction: a click is a flip.
        var filters = new LogFilterViewModel();
        var warning = filters.Levels.Single(l => l.Level == LogLevel.Warning);

        Assert.True(filters.Passes(Entry(LogLevel.Warning, LogCategory.Git)));

        warning.IsEnabled = false;

        Assert.False(filters.Passes(Entry(LogLevel.Warning, LogCategory.Git)));
        Assert.True(filters.Passes(Entry(LogLevel.Error, LogCategory.Git)));
    }

    [Fact]
    public void Levels_are_a_set_rather_than_a_threshold()
    {
        // The request a threshold cannot express, and the reason this replaced MinimumLevel:
        // show me warnings and errors, not everything from Warning up including Debug.
        var filters = new LogFilterViewModel();

        foreach (var level in filters.Levels.Where(
                     l => l.Level is LogLevel.Debug or LogLevel.Information))
        {
            level.IsEnabled = false;
        }

        Assert.False(filters.Passes(Entry(LogLevel.Debug, LogCategory.Git)));
        Assert.False(filters.Passes(Entry(LogLevel.Information, LogCategory.Git)));
        Assert.True(filters.Passes(Entry(LogLevel.Warning, LogCategory.Git)));
        Assert.True(filters.Passes(Entry(LogLevel.Error, LogCategory.Git)));
    }

    [Fact]
    public void A_category_can_be_switched_off_on_its_own()
    {
        var filters = new LogFilterViewModel();
        var git = filters.Categories.Single(c => c.Name == LogCategory.Git);

        git.IsEnabled = false;

        Assert.False(filters.Passes(Entry(LogLevel.Debug, LogCategory.Git)));
        Assert.True(filters.Passes(Entry(LogLevel.Debug, LogCategory.Sync)));
    }

    [Fact]
    public void An_unknown_category_is_shown_rather_than_hidden()
    {
        // Adding a category to LogCategory and forgetting to list it here must not make its
        // entries unreachable. A filter that hides what nobody knows to look for is the same
        // failure as a comment claiming a file nobody wrote.
        var filters = new LogFilterViewModel();

        Assert.True(filters.Passes(Entry(LogLevel.Information, "SomethingNew")));

        filters.NoCategoriesCommand.Execute(null);

        Assert.True(filters.Passes(Entry(LogLevel.Information, "SomethingNew")));
    }

    [Fact]
    public void The_summary_names_the_selection_because_a_closed_dropdown_hides_it()
    {
        var filters = new LogFilterViewModel();

        Assert.Equal("All categories", filters.CategorySummary);

        filters.NoCategoriesCommand.Execute(null);
        Assert.Equal("No categories", filters.CategorySummary);

        filters.Categories.Single(c => c.Name == LogCategory.Sync).IsEnabled = true;
        Assert.Equal(LogCategory.Sync, filters.CategorySummary);

        filters.Categories.Single(c => c.Name == LogCategory.Git).IsEnabled = true;
        Assert.Equal($"2 of {filters.Categories.Count}", filters.CategorySummary);
    }

    [Fact]
    public void Nothing_is_filtered_by_default()
    {
        // The panel must open showing everything. A filter that is on by default is how
        // somebody concludes the app has gone quiet when it has not.
        var filters = new LogFilterViewModel();

        Assert.False(filters.IsFiltering);
        Assert.All(filters.Levels, l => Assert.True(l.IsEnabled));
        Assert.All(filters.Categories, c => Assert.True(c.IsEnabled));
    }

    [Fact]
    public void The_text_filter_still_reaches_both_message_and_category()
    {
        var filters = new LogFilterViewModel { Text = "export" };

        Assert.True(filters.Passes(Entry(LogLevel.Information, LogCategory.Sync, "export done")));
        Assert.False(filters.Passes(Entry(LogLevel.Information, LogCategory.Sync, "nothing to do")));

        filters.Text = "Git";
        Assert.True(filters.Passes(Entry(LogLevel.Information, LogCategory.Git, "anything")));
    }

    [Fact]
    public void Counts_describe_the_buffer_so_the_dropdown_says_where_the_volume_is()
    {
        var filters = new LogFilterViewModel();

        filters.UpdateCounts(
        [
            Entry(LogLevel.Debug, LogCategory.Git),
            Entry(LogLevel.Debug, LogCategory.Git),
            Entry(LogLevel.Information, LogCategory.Sync),
        ]);

        Assert.Equal(2, filters.Categories.Single(c => c.Name == LogCategory.Git).Count);
        Assert.Equal(1, filters.Categories.Single(c => c.Name == LogCategory.Sync).Count);
        Assert.Equal(0, filters.Categories.Single(c => c.Name == LogCategory.Plc).Count);
    }

    /// <summary>
    /// Expanding the log reaches the panel, which shows its list and turns its chevron.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The seam this pins.</b> The log console moved out of <c>MainWindow.xaml</c> into
    /// <c>LogPanelView</c>, and with it the two handlers that were the reason it had been held
    /// back: auto-scroll on append, and the chevron glyph. A panel built by the XAML parser is
    /// handed nothing, so it wires itself when its DataContext arrives — one line, in a
    /// constructor.
    /// </para>
    /// <para>
    /// <b>Deleting that line left all 477 tests green.</b> Third time in this session that a
    /// moved subscription was watched by nothing; the chevron would have pointed the wrong way
    /// and the log would have stopped following its own tail, in front of a user, with the suite
    /// entirely happy. So: expand it and look.
    /// </para>
    /// <para>
    /// Asserted through the window rather than by constructing the panel alone, because
    /// <c>DataContextChanged</c> is exactly what would not fire if the control were newed up and
    /// handed a context by hand.
    /// </para>
    /// </remarks>
    [Fact]
    public void Expanding_the_log_shows_its_list_and_turns_its_chevron()
    {
        StaHarness.Run(() =>
        {
            var logger = new VersioLogger(new SystemClock());
            var sink = new InMemoryLogSink();
            logger.AddSink(sink);

            using var shell = new ShellViewModel(logger, new LogViewModel(sink));
            var window = new MainWindow(shell);

            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            var panel = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("LogConsole"));

            // A UserControl owns its own name scope, so the window cannot see inside it.
            var list = Assert.IsAssignableFrom<ListBox>(panel.FindName("LogList"));
            var chevron = Assert.IsAssignableFrom<TextBlock>(panel.FindName("LogChevron"));

            // Collapsed at startup: the log is a diagnostic, not the main event.
            Assert.False(shell.Log.IsExpanded);
            Assert.Equal(Visibility.Collapsed, list.Visibility);
            var closed = chevron.Text;

            shell.Log.IsExpanded = true;

            Assert.Equal(Visibility.Visible, list.Visibility);
            Assert.NotEqual(closed, chevron.Text);
        });
    }

    private static LogEvent Entry(LogLevel level, string category, string message = "message") =>
        new()
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = level,
            Category = category,
            Message = message,
        };
}
