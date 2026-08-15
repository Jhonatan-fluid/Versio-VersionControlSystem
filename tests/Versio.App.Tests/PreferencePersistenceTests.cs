using Versio.App.Theme;
using Versio.App.ViewModels;
using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Core.Settings;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Preferences survive the launch after the one that set them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The theme was a setting that was not saved.</b> Startup called
/// <c>ThemeManager.Apply(AppTheme.System)</c> unconditionally and <c>AppSettings</c> held no
/// theme, so choosing Dark on a light-mode Windows held for the session and was gone next
/// launch — with the settings popup showing "Follow system" checked, as though the user had
/// chosen it. <c>AppSettings</c>' own remarks had nominated it as "the obvious one" and that
/// was as far as it got.
/// </para>
/// <para>
/// <b>The second half is the one that would have broken silently.</b> Both writes constructed
/// <c>new AppSettings { Recent = _recent }</c>, which is correct exactly while the record has
/// one field — so the moment a second one existed, opening any repository would have reset it.
/// That is the regression these pin hardest: it is invisible until the launch after next.
/// </para>
/// </remarks>
public sealed class PreferencePersistenceTests
{
    private sealed class MemoryStore : IAppSettingsStore
    {
        public MemoryStore(AppSettings? initial = null) => Saved = initial ?? AppSettings.Empty;

        public AppSettings Saved { get; private set; }

        public Task<AppSettings> ReadAsync(CancellationToken ct) => Task.FromResult(Saved);

        public Task WriteAsync(AppSettings settings, CancellationToken ct)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private static ShellViewModel Build(IAppSettingsStore store)
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);

        return new ShellViewModel(
            logger, new LogViewModel(sink), toolchains: (_, _) => null, settings: store);
    }

    [Fact]
    public async Task A_chosen_theme_is_written_down()
    {
        var store = new MemoryStore();
        using var shell = Build(store);

        await shell.LoadRecentAsync();
        await shell.RememberThemeAsync("Dark");

        Assert.Equal("Dark", store.Saved.Theme);
    }

    [Fact]
    public async Task A_chosen_theme_comes_back_on_the_next_launch()
    {
        var store = new MemoryStore();

        var first = Build(store);
        await first.LoadRecentAsync();
        await first.RememberThemeAsync("Dark");

        var second = Build(store);
        await second.LoadRecentAsync();

        Assert.Equal("Dark", second.StoredTheme);
        Assert.Equal(AppTheme.Dark, ThemeManager.Parse(second.StoredTheme));
    }

    [Fact]
    public async Task Saving_the_layout_does_not_forget_the_theme()
    {
        // The clobbering defect, from the layout side.
        var store = new MemoryStore();
        using var shell = Build(store);

        await shell.LoadRecentAsync();
        await shell.RememberThemeAsync("Light");
        await shell.RememberLayoutAsync(new LayoutSettings { LogHeight = 320 });

        Assert.Equal("Light", store.Saved.Theme);
        Assert.Equal(320, store.Saved.Layout.LogHeight);
    }

    [Fact]
    public async Task Saving_the_theme_does_not_forget_the_recent_list()
    {
        var store = new MemoryStore(new AppSettings
        {
            Recent = [new RecentRepository { Path = @"C:\Git\Conveyor" }],
        });

        using var shell = Build(store);
        await shell.LoadRecentAsync();
        await shell.RememberThemeAsync("Dark");

        var remembered = Assert.Single(store.Saved.Recent);
        Assert.Equal(@"C:\Git\Conveyor", remembered.Path);
    }

    [Fact]
    public async Task An_unreadable_theme_falls_back_to_following_the_system()
    {
        // A settings file hand-edited to nonsense, or written by a later version that grew a
        // fourth option. It must cost a preference, never a launch.
        var store = new MemoryStore(new AppSettings { Theme = "Solarized" });
        using var shell = Build(store);

        await shell.LoadRecentAsync();

        Assert.Equal(AppTheme.System, ThemeManager.Parse(shell.StoredTheme));
    }

    [Theory]
    [InlineData("Dark", AppTheme.Dark)]
    [InlineData("dark", AppTheme.Dark)]
    [InlineData("Light", AppTheme.Light)]
    [InlineData("System", AppTheme.System)]
    [InlineData("", AppTheme.System)]
    [InlineData(null, AppTheme.System)]
    public void The_stored_theme_parses_case_insensitively(string? stored, AppTheme expected)
    {
        // Case-insensitive because the value is a hand-editable string in a JSON file, and
        // "dark" is what somebody who opened it would type.
        Assert.Equal(expected, ThemeManager.Parse(stored));
    }

    [Fact]
    public async Task Writing_the_same_theme_twice_does_not_rewrite_the_file()
    {
        // Called from a Checked handler, and SyncThemeRadios re-checks the active radio every
        // time the settings popup opens - so this runs on every visit to the menu, not only
        // on a change.
        var writes = 0;
        var store = new CountingStore(() => writes++);
        using var shell = Build(store);

        await shell.LoadRecentAsync();
        await shell.RememberThemeAsync("Dark");
        await shell.RememberThemeAsync("Dark");

        Assert.Equal(1, writes);
    }

    private sealed class CountingStore(Action onWrite) : IAppSettingsStore
    {
        private AppSettings _saved = AppSettings.Empty;

        public Task<AppSettings> ReadAsync(CancellationToken ct) => Task.FromResult(_saved);

        public Task WriteAsync(AppSettings settings, CancellationToken ct)
        {
            _saved = settings;
            onWrite();
            return Task.CompletedTask;
        }
    }
}
