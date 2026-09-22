using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Versio.App.Views;
using Versio.App.Views.Panels;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The git panel with nothing to commit does not dress up what cannot be pressed.
/// </summary>
/// <remarks>
/// From the running window on 2026-09-21, a clean tree: the disabled Commit was still a bright
/// accent slab, and a "0" sat over the empty message box counting nothing.
/// </remarks>
[Collection("theme")]
public sealed class GitPanelCleanStateTests
{
    [Fact]
    public void A_disabled_accent_button_gives_up_its_fill()
    {
        StaHarness.Run(() =>
        {
            var style = Assert.IsType<Style>(Application.Current.Resources["AccentButton"]);
            var accent = (SolidColorBrush)Application.Current.Resources["Brush.Accent.Default"];

            var enabled = new Button { Style = style, Content = "Commit" };
            var disabled = new Button { Style = style, Content = "Commit", IsEnabled = false };

            Assert.Equal(accent.Color, Assert.IsType<SolidColorBrush>(enabled.Background).Color);
            Assert.NotEqual(accent.Color, Assert.IsType<SolidColorBrush>(disabled.Background).Color);
        });
    }

    [Fact]
    public void The_subject_counter_appears_only_once_there_is_a_subject()
    {
        StaHarness.Run(() =>
        {
            using var shell = TestShell.Build();
            var window = new MainWindow(shell);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var panel = Assert.IsType<GitPanelView>(window.FindName("GitPanel"));
            panel.Visibility = Visibility.Visible;

            Lay(content);
            var counter = Assert.Single(Descendants(panel).OfType<TextBlock>(),
                t => AutomationProperties.GetName(t) == "Subject length");
            Assert.NotEqual(Visibility.Visible, counter.Visibility);

            shell.CommitMessage = "Fix the pump interlock";
            Lay(content);
            Assert.Equal(Visibility.Visible, counter.Visibility);
            Assert.Equal("22", counter.Text);
        });
    }

    private static void Lay(FrameworkElement content)
    {
        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();
    }

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
