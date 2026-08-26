using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Versio.App.ViewModels;
using Versio.App.Views;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// UX-7, second half: Ctrl+F reaches the active panel's find box, Escape closes the
/// transient surfaces.
/// </summary>
/// <remarks>
/// The command is invoked rather than a keystroke synthesised, for the reason the rail-key
/// test states: WPF's input plumbing needs a focused, presented window, and none is shown
/// here. What is checked is that the gesture is bound to something that moves focus to the
/// right box — read back as the window's <i>logical</i> focus, because keyboard focus needs
/// a presentation source a headless test does not have.
/// </remarks>
[Collection("theme")]
public sealed class KeyboardFindTests : IDisposable
{
    private readonly List<ShellViewModel> _shells = [];

    private (MainWindow Window, ShellViewModel Shell) Build()
    {
        var shell = TestShell.Build();
        _shells.Add(shell);
        return (new MainWindow(shell), shell);
    }

    private static ICommand BoundTo(MainWindow window, Key key, ModifierKeys modifiers) =>
        window.InputBindings.OfType<KeyBinding>()
            .Single(b => b.Key == key && b.Modifiers == modifiers)
            .Command;

    [Theory]
    [InlineData("RailProject", "ProjectPanel", "TreeSearchBox")]
    [InlineData("RailGit", "GitPanel", "ChangesFilterBox")]
    [InlineData("RailHistory", "HistoryPanel", "HistoryFilterBox")]
    public void Ctrl_F_reaches_the_active_panel_s_find_box(string rail, string panel, string box)
    {
        StaHarness.Run(() =>
        {
            var (window, _) = Build();

            var button = Assert.IsType<RadioButton>(window.FindName(rail));
            button.IsChecked = true;

            BoundTo(window, Key.F, ModifierKeys.Control).Execute(null);

            var host = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(panel));
            var found = Assert.IsType<TextBox>(host.FindName(box));
            Assert.Same(found, FocusManager.GetFocusedElement(window));
        });
    }

    [Fact]
    public void Ctrl_F_does_nothing_on_the_explorer()
    {
        // The explorer has no find box. Yanking logical focus somewhere invisible would be
        // worse than a dead key, so the command runs and moves nothing.
        StaHarness.Run(() =>
        {
            var (window, _) = Build();

            var button = Assert.IsType<RadioButton>(window.FindName("RailExplorer"));
            button.IsChecked = true;

            var before = FocusManager.GetFocusedElement(window);
            BoundTo(window, Key.F, ModifierKeys.Control).Execute(null);

            Assert.Same(before, FocusManager.GetFocusedElement(window));
        });
    }

    [Fact]
    public void Escape_is_dead_while_nothing_dismissible_is_open()
    {
        // The CanExecute gate is most of the value: an Escape binding that is live with
        // nothing open would mark every Escape handled, silently eating the key for any
        // control that wanted it — and "the key does nothing while the command is
        // disabled" is the test the UX plan asked for by name.
        //
        // The closing half is NOT asserted here, and deliberately: a Popup whose placement
        // target sits in a never-presented window refuses to open — WPF resets IsOpen —
        // so a headless assertion on it can only ever exercise the reset, not the close.
        // Escape actually closing the settings popup and the ahead/behind preview is on
        // the owner-eyeball list with the rest of UX-7.
        StaHarness.Run(() =>
        {
            var (window, _) = Build();

            var escape = BoundTo(window, Key.Escape, ModifierKeys.None);
            var popup = Assert.IsType<Popup>(window.FindName("SettingsPopup"));

            Assert.False(popup.IsOpen);
            Assert.False(escape.CanExecute(null));

            // And executing anyway is a safe no-op, not a crash.
            escape.Execute(null);
            Assert.False(popup.IsOpen);
        });
    }

    /// <summary>Released on the STA thread that built them — see GitPanelChromeTests.</summary>
    public void Dispose() => StaHarness.Run(() =>
    {
        foreach (var shell in _shells)
        {
            shell.Dispose();
        }
    });
}
