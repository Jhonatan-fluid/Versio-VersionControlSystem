using System.Windows;
using System.Windows.Controls;
using Versio.App.Views;
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
    ];

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
}
