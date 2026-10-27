using Versio.App.ViewModels;
using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Core.Settings;
using Versio.Core.Updates;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The update check, from launch and from the gear menu, against a fake release feed.
/// </summary>
/// <remarks>
/// <para>
/// Driven through a real shell rather than the view model alone, because the parts most likely
/// to break quietly are the shell's: that the dismissal reaches settings.json, that the
/// question goes to the shell's prompts, that the notices land in the shell's list.
/// </para>
/// <para>
/// What none of these can reach is the window: that <c>RestartRequested</c> closes it through
/// the close guard, and that a refused close calls <c>RestartDeclined</c>. Those two are wired
/// in <c>MainWindow.xaml.cs</c> and were checked by running the app (docs/STATUS.md).
/// </para>
/// </remarks>
public sealed class UpdatesViewModelTests
{
    private sealed class FakeSource : IUpdateSource
    {
        public string CurrentVersion { get; init; } = "0.4.4";

        public string? Unavailable { get; init; }

        public UpdateCheck Answer { get; set; } = UpdateCheck.UpToDate();

        public Exception? DownloadFails { get; init; }

        public int Checks { get; private set; }

        public int Downloads { get; private set; }

        public bool? AppliedWithRestart { get; private set; }

        public bool HasDownloadedUpdate { get; private set; }

        public Task<UpdateCheck> CheckAsync(CancellationToken ct)
        {
            Checks++;
            return Task.FromResult(Answer);
        }

        public Task DownloadAsync(UpdateOffer offer, IProgress<int>? progress, CancellationToken ct)
        {
            Downloads++;
            if (DownloadFails is not null)
            {
                throw DownloadFails;
            }

            progress?.Report(100);
            HasDownloadedUpdate = true;
            return Task.CompletedTask;
        }

        public void ApplyAfterExit(bool restart) => AppliedWithRestart = restart;
    }

    private sealed class MemoryStore(AppSettings initial) : IAppSettingsStore
    {
        public AppSettings Saved { get; private set; } = initial;

        public Task<AppSettings> ReadAsync(CancellationToken ct) => Task.FromResult(Saved);

        public Task WriteAsync(AppSettings settings, CancellationToken ct)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private static readonly UpdateOffer Newer = new("0.5.0", "## New\n- **faster** export");

    private static ShellViewModel Build(IUpdateSource source, MemoryStore? store = null)
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);

        return new ShellViewModel(
            logger, new LogViewModel(sink), toolchains: (_, _) => null,
            settings: store ?? new MemoryStore(AppSettings.Empty), updates: source);
    }

    [Fact]
    public async Task Launch_offers_a_newer_version_with_its_notes_cut_for_the_dialog()
    {
        var source = new FakeSource { Answer = UpdateCheck.Available(Newer) };
        using var shell = Build(source);
        await shell.LoadRecentAsync();
        UpdatePrompt? asked = null;
        shell.Asked().Update = p =>
        {
            asked = p;
            return UpdateChoice.Later;
        };

        await shell.Updates.CheckOnLaunchAsync(CancellationToken.None);

        Assert.NotNull(asked);
        Assert.Equal("0.4.4", asked.Current);
        Assert.Equal("0.5.0", asked.Offered);
        Assert.Equal("New\n- faster export", asked.Notes);
    }

    [Fact]
    public async Task Later_is_written_down_and_the_next_launch_does_not_ask_again()
    {
        var store = new MemoryStore(AppSettings.Empty);
        var source = new FakeSource { Answer = UpdateCheck.Available(Newer) };

        using (var first = Build(source, store))
        {
            await first.LoadRecentAsync();
            first.Asked().Update = _ => UpdateChoice.Later;
            await first.Updates.CheckOnLaunchAsync(CancellationToken.None);
        }

        Assert.Equal("0.5.0", store.Saved.DismissedUpdateVersion);

        using var second = Build(source, store);
        await second.LoadRecentAsync();
        var asked = 0;
        second.Asked().Update = _ =>
        {
            asked++;
            return UpdateChoice.Later;
        };

        await second.Updates.CheckOnLaunchAsync(CancellationToken.None);

        Assert.Equal(0, asked);
        Assert.Equal(0, source.Downloads);
    }

    [Fact]
    public async Task Asking_from_the_gear_menu_offers_a_version_the_launch_was_told_Later_about()
    {
        var store = new MemoryStore(new AppSettings { DismissedUpdateVersion = "0.5.0" });
        var source = new FakeSource { Answer = UpdateCheck.Available(Newer) };
        using var shell = Build(source, store);
        await shell.LoadRecentAsync();
        var asked = 0;
        shell.Asked().Update = _ =>
        {
            asked++;
            return UpdateChoice.Later;
        };

        await shell.Updates.CheckNowAsync();

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task Update_now_downloads_then_asks_the_window_to_close_rather_than_closing_anything()
    {
        var source = new FakeSource { Answer = UpdateCheck.Available(Newer) };
        using var shell = Build(source);
        await shell.LoadRecentAsync();
        shell.Asked().Update = _ => UpdateChoice.UpdateNow;
        var restarts = 0;
        shell.Updates.RestartRequested += () => restarts++;

        await shell.Updates.CheckOnLaunchAsync(CancellationToken.None);

        Assert.Equal(1, source.Downloads);
        Assert.Equal(1, restarts);
        Assert.True(shell.Updates.RestartWanted);

        shell.Updates.ApplyOnExit();
        Assert.True(source.AppliedWithRestart);
    }

    [Fact]
    public async Task A_refused_close_keeps_the_download_and_installs_it_at_exit_without_restarting()
    {
        var source = new FakeSource { Answer = UpdateCheck.Available(Newer) };
        using var shell = Build(source);
        await shell.LoadRecentAsync();
        shell.Asked().Update = _ => UpdateChoice.UpdateNow;
        await shell.Updates.CheckOnLaunchAsync(CancellationToken.None);

        shell.Updates.RestartDeclined();

        Assert.False(shell.Updates.RestartWanted);
        Assert.Contains(shell.Notifications.Visible, n => n.Headline == "Update ready");

        shell.Updates.ApplyOnExit();
        Assert.False(source.AppliedWithRestart);
    }

    [Fact]
    public async Task A_failed_download_neither_restarts_nor_installs()
    {
        var source = new FakeSource
        {
            Answer = UpdateCheck.Available(Newer),
            DownloadFails = new System.Net.Http.HttpRequestException("connection reset"),
        };
        using var shell = Build(source);
        await shell.LoadRecentAsync();
        shell.Asked().Update = _ => UpdateChoice.UpdateNow;
        var restarts = 0;
        shell.Updates.RestartRequested += () => restarts++;

        await shell.Updates.CheckNowAsync();

        Assert.Equal(0, restarts);
        Assert.Contains(shell.Notifications.Visible, n => n.Headline.StartsWith("Could not download", StringComparison.Ordinal));
        shell.Updates.ApplyOnExit();
        Assert.Null(source.AppliedWithRestart);
    }

    [Fact]
    public async Task Up_to_date_is_silent_at_launch_and_answered_when_asked()
    {
        var source = new FakeSource { Answer = UpdateCheck.UpToDate() };
        using var shell = Build(source);
        await shell.LoadRecentAsync();

        await shell.Updates.CheckOnLaunchAsync(CancellationToken.None);
        Assert.Empty(shell.Notifications.Visible);

        await shell.Updates.CheckNowAsync();
        Assert.Contains(shell.Notifications.Visible, n => n.Headline == "Versio is up to date");
    }

    [Fact]
    public async Task A_failed_check_is_silent_at_launch_and_explained_when_asked()
    {
        var source = new FakeSource { Answer = UpdateCheck.Failed("GitHub refused the stored sign-in") };
        using var shell = Build(source);
        await shell.LoadRecentAsync();

        await shell.Updates.CheckOnLaunchAsync(CancellationToken.None);
        Assert.Empty(shell.Notifications.Visible);

        await shell.Updates.CheckNowAsync();
        var card = Assert.Single(shell.Notifications.Visible);
        Assert.Equal("Could not check for updates", card.Headline);
        Assert.Equal("GitHub refused the stored sign-in", card.Detail);
    }

    [Fact]
    public async Task A_copy_that_cannot_update_never_asks_the_feed()
    {
        // A development build: no installation to replace, so nothing goes to GitHub at all.
        var source = new FakeSource { Unavailable = "a development build", Answer = UpdateCheck.Available(Newer) };
        using var shell = Build(source);
        await shell.LoadRecentAsync();
        shell.Asked().Update = _ => UpdateChoice.UpdateNow;

        await shell.Updates.CheckOnLaunchAsync(CancellationToken.None);
        await shell.Updates.CheckNowAsync();

        Assert.Equal(0, source.Checks);
        Assert.Equal(0, source.Downloads);
        Assert.Contains(shell.Notifications.Visible, n => n.Headline == "Updates are off");
    }

    [Fact]
    public async Task A_shell_built_without_a_source_cannot_update()
    {
        // The default every other test gets: constructing a shell must never reach GitHub.
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        using var shell = new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);
        shell.Asked().Update = _ => UpdateChoice.UpdateNow;
        var restarts = 0;
        shell.Updates.RestartRequested += () => restarts++;

        await shell.Updates.CheckNowAsync();

        Assert.Equal(0, restarts);
        Assert.Equal("Updates are off for this copy", shell.Updates.Status);
    }
}
