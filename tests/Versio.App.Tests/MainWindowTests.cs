using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The main window is constructed against the real stylesheet.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because its absence shipped a crash.</b> Adding the log panel's category
/// dropdown — a <c>ToggleButton</c> wearing <c>GhostButton</c>, whose <c>TargetType</c> was
/// <c>Button</c> — made <c>MainWindow</c> throw <c>XamlParseException</c> at construction, so
/// the application started, logged, and never showed a window at all. The build was clean and
/// 883 tests were green when it did that.
/// </para>
/// <para>
/// Every instrument in place at the time missed it, and each for its own reason worth
/// recording:
/// </para>
/// <list type="bullet">
///   <item><b>The compiler</b> turns XAML into BAML without resolving a single
///   <c>StaticResource</c>. A style reference is just a key until something asks for it.</item>
///   <item><b>The dialog tests</b> cover the two modals and the stylesheet, and the main
///   window was the one window nothing constructed — on the reasoning that it opens on every
///   launch and could not go unnoticed. It went unnoticed.</item>
///   <item><b>The resource-key tests</b> assert <c>GhostButton</c> exists, which it did. The
///   defect was not a missing key; it was a key that does not fit the element asking for
///   it, which no lookup can see.</item>
///   <item><b>The binding tracer</b> reported nothing, and that was read as a clean result
///   for two runs. A window that never opens evaluates no bindings and produces an empty
///   trace — indistinguishable from a window with no broken bindings. Exactly the shape of
///   vacuous pass this repository keeps catching.</item>
/// </list>
/// <para>
/// One construction of the real window closes all four. It is also the cheapest possible
/// regression test for the whole file: every <c>StaticResource</c> in <c>MainWindow.xaml</c>
/// is resolved and applied here, on every run, in 200 ms.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class MainWindowTests
{
    private static MainWindow Build()
    {
        var logger = new VersioLogger(new SystemClock());
        var sink = new InMemoryLogSink();
        logger.AddSink(sink);

        // No repository is opened: construction is the thing under test, and a window that
        // parses is a window that parses whether or not it has data. Opening one would drag
        // git.exe and a real working tree into a test about XAML.
        return new MainWindow(new ShellViewModel(logger, new LogViewModel(sink)));
    }

    [Fact]
    public void The_main_window_constructs_against_the_real_stylesheet()
    {
        StaHarness.Run(() =>
        {
            var window = Build();

            Assert.NotNull(window.Content);
        });
    }

    [Fact]
    public void Every_style_the_main_window_names_fits_the_element_that_wears_it()
    {
        // The specific defect: a Style whose TargetType does not match its element throws
        // from the property setter, not from the lookup. Applying the template is what
        // surfaces it, so the window is measured rather than merely built.
        StaHarness.Run(() =>
        {
            var window = Build();

            // The CONTENT is measured, not the window. An unshown Window has no HWND and
            // reports a zero DesiredSize however well its tree laid out, so asserting on the
            // window would have been a check that could only fail - the mirror image of the
            // vacuous pass, and it did fail the first time it was written.
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));

            Assert.True(content.DesiredSize.Width > 0);
        });
    }

    [Fact]
    public void A_ghost_styled_toggle_button_is_legal()
    {
        // The category dropdown has to be a ToggleButton - it stays pressed while its popup
        // is open - and it wears the same ghost styling as the buttons beside it. Pinned on
        // its own so the reason GhostButton targets ButtonBase cannot be optimised away by
        // somebody tidying the stylesheet.
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["GhostButton"]);

            var toggle = new ToggleButton { Style = style, Content = "All categories" };
            toggle.Measure(new Size(300, 40));

            Assert.True(toggle.DesiredSize.Width > 0);
        });
    }

    [Fact]
    public void The_ghost_style_still_fits_a_plain_button()
    {
        // Widening the TargetType must not break what it already dressed.
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["GhostButton"]);

            var button = new Button { Style = style, Content = "Fetch" };
            button.Measure(new Size(300, 40));

            Assert.True(button.DesiredSize.Width > 0);
        });
    }
}
