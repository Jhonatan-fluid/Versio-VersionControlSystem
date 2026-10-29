using System.Windows;
using System.Windows.Controls;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The system information dialog, constructed and measured against the real stylesheet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written after the dialog was found broken in the running app.</b> Its row template used
/// <c>{StaticResource BoolToVisibility}</c>, and the converter was declared in
/// <c>MainWindow.Resources</c> — a different window, which this one cannot see. Opening
/// Settings → Help → System information threw <c>XamlParseException</c>: <i>Cannot find
/// resource named 'BoolToVisibility'</i>, from inside the <c>DataTemplate</c>, during Measure.
/// </para>
/// <para>
/// This is the third distinct instance of one blind spot, and the reason the harness exists at
/// all: <b>a window nobody constructs has never been parsed.</b> The two prompt dialogs got
/// tests when that was first written up; this dialog was added afterwards and did not, so the
/// lesson was recorded and then not applied. A test per window is the only version of that
/// lesson which cannot be forgotten.
/// </para>
/// <para>
/// The rows are measured rather than merely built, because a resource referenced from inside a
/// template is not resolved until the template is applied — which is Measure, not construction.
/// A test that only constructed the window would pass with the bug in place.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class SystemInfoWindowTests
{
    private static IReadOnlyList<SystemInfoRow> Rows() =>
    [
        // Both shapes, because the template branches on them and only one was ever exercised
        // by looking at the screen.
        SystemInfoRow.Section("TOOLCHAIN"),
        new SystemInfoRow("CODESYS", "3.5.22.20"),
        new SystemInfoRow("Profile", "CODESYS V3.5 SP22 Patch 2"),
        // The third shape, added 2026-10-29: a row with a button under its value, for a
        // vendor's fix. Measured with the other two so its template branch is applied.
        new SystemInfoRow("Fix", "Adds X to the local Windows group 'Siemens TIA Openness'.")
        {
            Action = new SystemInfoAction("Add this account to the group…", () => Task.FromResult("Added.")),
        },
    ];

    [Fact]
    public void A_rows_button_runs_its_action_shows_the_sentence_and_gathers_the_rows_again()
    {
        StaHarness.Run(async () =>
        {
            var gathered = 0;
            var window = new SystemInfoWindow(Rows(), () =>
            {
                gathered++;
                return Task.FromResult<IReadOnlyList<SystemInfoRow>>(
                    [SystemInfoRow.Section("TOOLCHAIN"), new SystemInfoRow("Status", "available")]);
            });

            await window.RunActionAsync(Rows().Single(r => r.HasAction));

            Assert.Equal("Added.", window.LastOutcome);
            Assert.Equal(1, gathered);
            Assert.True(window.IsEnabled);
            Assert.Contains("Status", window.AsText(), StringComparison.Ordinal);
            Assert.DoesNotContain("Fix", window.AsText(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_window_refuses_to_close_while_an_action_runs()
    {
        // IsEnabled disables the client area only; the title bar's close still worked, and
        // closing the modal mid-action unblocked the main window — and its Fix button — while
        // the UAC prompt was still up (review, 2026-10-29).
        StaHarness.Run(async () =>
        {
            var gate = new TaskCompletionSource<string>();
            var window = new SystemInfoWindow(Rows());
            var running = window.RunActionAsync(new SystemInfoRow("Fix", "x")
            {
                Action = new SystemInfoAction("Wait", () => gate.Task),
            });

            Assert.True(window.ActionRunning);
            window.Close();
            Assert.True(window.ActionRunning);
            Assert.Equal("Wait for the running action to finish before closing.", window.LastOutcome);

            gate.SetResult("Done.");
            await running;

            Assert.False(window.ActionRunning);
            Assert.Equal("Done.", window.LastOutcome);
            window.Close();
        });
    }

    [Fact]
    public void A_rows_action_that_throws_is_the_outcome_not_a_closed_window()
    {
        StaHarness.Run(async () =>
        {
            var window = new SystemInfoWindow(Rows());
            var row = new SystemInfoRow("Fix", "x")
            {
                Action = new SystemInfoAction("Try", () => throw new InvalidOperationException("no helper")),
            };

            await window.RunActionAsync(row);

            Assert.Equal("The action failed: no helper", window.LastOutcome);
            Assert.True(window.IsEnabled);
        });
    }

    [Fact]
    public void The_dialog_constructs_against_the_real_stylesheet()
    {
        StaHarness.Run(() =>
        {
            var window = new SystemInfoWindow(Rows());

            Assert.NotNull(window.Content);
        });
    }

    [Fact]
    public void The_rows_render_without_a_missing_resource()
    {
        // The actual regression. Measure is what applies the item template, and applying the
        // template is what resolves the StaticResource that was not there.
        StaHarness.Run(() =>
        {
            var window = new SystemInfoWindow(Rows());
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            content.Measure(new Size(700, 520));
            content.Arrange(new Rect(0, 0, 700, 520));

            Assert.True(content.DesiredSize.Height > 0);
        });
    }

    [Fact]
    public void The_visibility_converter_is_shared_rather_than_owned_by_one_window()
    {
        // Pinned as its own fact so somebody tidying the stylesheet cannot move it back into
        // a single window's resources without a test saying why it is where it is.
        StaHarness.Run(() =>
        {
            Assert.IsType<BooleanToVisibilityConverter>(
                Application.Current.Resources["BoolToVisibility"]);
        });
    }

    [Fact]
    public async Task The_versio_version_row_is_the_product_number()
    {
        // Assembly.GetName().Version is four-part (0.1.0.0). The installer and
        // Directory.Build.props are three-part. Informational version is the one
        // a human comparing Setup.exe to this dialog can actually match.
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        using var shell = new ShellViewModel(
            logger, new LogViewModel(sink), toolchains: (_, _) => null);

        var rows = await shell.BuildSystemInfoAsync(CancellationToken.None);
        var header = Array.FindIndex(rows.ToArray(), r => r.IsHeader && r.Label == "VERSIO");
        Assert.True(header >= 0, "VERSIO section is missing");
        var version = rows[header + 1];

        Assert.Equal("Version", version.Label);
        Assert.Equal(global::Versio.App.AppVersion.Display, version.Value);
        Assert.Matches(@"^\d+\.\d+\.\d+$", version.Value);
        Assert.DoesNotContain('+', version.Value);
    }
}
