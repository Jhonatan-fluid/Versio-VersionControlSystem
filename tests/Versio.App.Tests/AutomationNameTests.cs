using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Nothing in the shell announces itself as a Private Use Area codepoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> <c>AutomationProperties</c> appeared zero times in the application.
/// <c>IconButton</c> puts the glyph in <c>Content</c> and WPF derives a control's automation
/// name from its content, so Open, Clone, New branch, Refresh, Settings, the log's Pause and
/// its Clear all announced <c>U+E8B7</c>, <c>U+E896</c>, <c>U+E710</c> and friends — the whole
/// top bar and the whole of primary navigation. Every one of them had a <c>ToolTip</c>
/// written, and WPF does not use <c>ToolTip</c> as an automation name.
/// </para>
/// <para>
/// This walks the real window and asks each control what it is called, rather than checking
/// that a setter exists. A name inherited from a style is a name; a name that resolves to the
/// glyph anyway is the bug, and only the peer can tell the two apart.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class AutomationNameTests : IDisposable
{
    private readonly List<ShellViewModel> _shells = [];

    /// <summary>The Segoe Fluent Icons range. Anything here is a picture, not a word.</summary>
    private static bool IsPrivateUse(char c) => c is >= '' and <= '';

    /// <summary>The shell, laid out, with the element the visual tree actually hangs off.</summary>
    /// <remarks>
    /// <b>Not the Window.</b> Nothing here shows one, and an unshown Window has no visual
    /// children — <c>VisualTreeHelper</c> walked from it returns nothing, so a test that
    /// looked for icon buttons found zero and would have passed had it not counted them
    /// first. The content element is the root of everything that was arranged.
    /// </remarks>
    private (MainWindow Window, FrameworkElement Root) Build()
    {
        var logger = new VersioLogger(new SystemClock());
        var sink = new InMemoryLogSink();
        logger.AddSink(sink);

        // Kept, and not constructed inline as an argument, so that something can dispose it.
        // See Dispose below for what it costs when nothing can.
        var shell = new ShellViewModel(logger, new LogViewModel(sink));
        _shells.Add(shell);

        var window = new MainWindow(shell);
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();

        return (window, content);
    }

    /// <summary>Release the shells this class built.</summary>
    /// <remarks>
    /// <para>
    /// <b>On the STA thread, not this one.</b> Tearing a shell down clears collections that a
    /// bound <c>CollectionView</c> is watching, and WPF refuses that from any thread but the
    /// dispatcher's: disposing from the xunit thread throws <c>NotSupportedException</c> —
    /// <i>"does not support changes to its SourceCollection from a thread different from the
    /// Dispatcher thread"</i> — and fails every test in this class. Measured, by doing it.
    /// That is also the proof this teardown is not decorative, which nothing else here asserts.
    /// </para>
    /// <para>
    /// Worth having at all because that harness runs <b>one</b> STA thread with a live
    /// <c>Dispatcher.Run()</c> for the whole suite. A leaked log pump is not inert here the way
    /// it would be on a thread nobody pumps: it goes on firing every 100 ms, draining its queue
    /// into an <c>ObservableCollection</c> on the same thread every later WPF test is using.
    /// One was leaked for every test in this class, because the shell was constructed inline
    /// as an argument to the window and there was nothing to put <c>using</c> on.
    /// </para>
    /// </remarks>
    public void Dispose() => StaHarness.Run(() =>
    {
        foreach (var shell in _shells)
        {
            shell.Dispose();
        }
    });

    [Fact]
    public void No_control_in_the_shell_is_named_after_a_glyph()
    {
        StaHarness.Run(() =>
        {
            var (_, root) = Build();
            var offenders = new List<string>();

            foreach (var control in Descendants(root).OfType<Control>())
            {
                var name = NameOf(control);

                if (name.Any(IsPrivateUse))
                {
                    var codepoints = string.Join(
                        " ", name.Where(IsPrivateUse).Select(c => $"U+{(int)c:X4}"));

                    offenders.Add($"{control.GetType().Name} announces {codepoints}");
                }
            }

            Assert.True(offenders.Count == 0, string.Join("\n", offenders));
        });
    }

    [Fact]
    public void Every_icon_only_button_has_a_name()
    {
        // An icon-only button with no name is unusable by a screen reader and unreachable by
        // voice control - there is nothing to say to press it.
        StaHarness.Run(() =>
        {
            var (_, root) = Build();

            var iconButtons = Descendants(root).OfType<Button>()
                .Where(b => b.Content is string text && text.Length > 0 && text.All(IsPrivateUse))
                .ToList();

            Assert.True(iconButtons.Count >= 5,
                        $"only {iconButtons.Count} icon-only buttons were found, so this test " +
                        "is no longer looking at the top bar");

            foreach (var button in iconButtons)
            {
                Assert.False(string.IsNullOrWhiteSpace(NameOf(button)),
                             "an icon-only button has no automation name");
            }
        });
    }

    [Fact]
    public void The_rail_is_named_for_the_panels_rather_than_for_their_tooltips()
    {
        // A name is what a reader says every time focus lands, and these three tooltips are
        // whole sentences. The short name comes from Tag; the sentence stays in HelpText.
        StaHarness.Run(() =>
        {
            var (window, _) = Build();
            var rail = Assert.IsType<Border>(window.FindName("Rail"));

            var names = Descendants(rail).OfType<RadioButton>()
                .Select(NameOf)
                .ToList();

            Assert.Equal(["Project", "Explorer", "Git", "History"], names);

            foreach (var button in Descendants(rail).OfType<RadioButton>())
            {
                Assert.False(string.IsNullOrWhiteSpace(
                                 AutomationProperties.GetHelpText(button)),
                             "a rail button lost the sentence that explains it");
            }
        });
    }

    [Fact]
    public void A_name_that_changes_with_the_tooltip_follows_it()
    {
        // The log's Pause button swaps its glyph AND its tooltip on IsPaused - it says Resume
        // while paused. Binding the name to the tooltip is what keeps the two in step; a name
        // written once at the call site would still say "Freeze the list" while paused.
        StaHarness.Run(() =>
        {
            var (window, _) = Build();
            var shell = Assert.IsType<ShellViewModel>(window.DataContext);
            // A UserControl since the log console moved into its own file, the way the history
            // panel already had. What this test wants is something to measure, not a
            // particular container.
            var log = Assert.IsAssignableFrom<FrameworkElement>(
                window.FindName("LogConsole"));

            var pause = Descendants(log).OfType<Button>()
                .Single(b => NameOf(b).Contains("Freeze", StringComparison.Ordinal));

            shell.Log.TogglePauseCommand.Execute(null);
            window.UpdateLayout();

            Assert.Contains("Resume", NameOf(pause), StringComparison.Ordinal);
        });
    }

    /// <summary>What a screen reader would actually say for this control.</summary>
    /// <remarks>
    /// Through the automation peer rather than by reading <c>AutomationProperties.Name</c>.
    /// The peer is what applies the fallback to content, and the fallback is the whole defect
    /// — reading the property directly would have returned empty and looked fine.
    /// </remarks>
    private static string NameOf(Control control) =>
        UIElementAutomationPeer.CreatePeerForElement(control)?.GetName() ?? string.Empty;

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
