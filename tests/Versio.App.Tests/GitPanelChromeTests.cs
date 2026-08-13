using System.Windows;
using System.Windows.Controls;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The git panel, actually rendered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Constructing the window is not enough for a panel that starts collapsed.</b> WPF resolves
/// a <c>StaticResource</c> inside a template when the template is instantiated, not when the
/// XAML is parsed, and it fails a binding whose property does not exist by rendering nothing at
/// all — no exception, no warning, no failing test. Both failures sail past every other test in
/// the suite and land in front of whoever clicked the rail.
/// </para>
/// <para>
/// <b>This is also what a binding trace could not do here.</b> Running the app with
/// <c>--trace-bindings</c> produced an empty file, because the git panel starts
/// <c>Collapsed</c> and its bindings were never evaluated — the exact trap
/// <c>App.xaml.cs</c> documents, where a clean trace over a panel nobody populated is
/// indistinguishable from success. Showing the panel through the rail and measuring it is the
/// version of that check which cannot pass vacuously.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class GitPanelChromeTests
{
    private static (MainWindow Window, ShellViewModel Shell) Build()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        var shell = new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);
        return (new MainWindow(shell), shell);
    }

    /// <summary>Show the git panel the way a user does, and lay it out.</summary>
    private static (Border Panel, FrameworkElement Content) ShowGitPanel(MainWindow window)
    {
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

        // Through the rail rather than by setting ActivePanel: the handler behind it is what
        // makes the panel visible, and a test that bypasses it proves less than it appears to.
        var rail = Assert.IsType<RadioButton>(window.FindName("RailGit"));
        rail.IsChecked = true;

        var panel = Assert.IsType<Border>(window.FindName("GitPanel"));
        Assert.Equal(Visibility.Visible, panel.Visibility);

        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();

        return (panel, content);
    }

    [Fact]
    public void The_git_panel_lays_out_when_the_rail_is_clicked()
    {
        StaHarness.Run(() =>
        {
            var (window, _) = Build();
            var (panel, _) = ShowGitPanel(window);

            Assert.True(panel.DesiredSize.Width > 0);
        });
    }

    [Fact]
    public void The_missing_git_banner_stays_hidden_until_the_probe_has_answered()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            ShowGitPanel(window);

            // GitPresence.Unknown, because nothing has probed. If Unknown counted as Missing
            // this banner would be up on every launch for the moment before the probe returns,
            // announcing a missing dependency that is sitting right there.
            Assert.False(shell.GitIsMissing);

            var banner = Banner(window, "GIT NOT AVAILABLE");
            Assert.Equal(Visibility.Collapsed, banner.Visibility);
        });
    }

    [Fact]
    public void The_identity_prompt_stays_hidden_with_no_repository_open()
    {
        StaHarness.Run(() =>
        {
            var (window, shell) = Build();
            ShowGitPanel(window);

            // Nothing to be missing an identity for. A prompt on an empty shell is noise.
            Assert.False(shell.IdentityIsMissing);
        });
    }

    [Fact]
    public void The_commit_buttons_show_their_reason_while_disabled()
    {
        // ToolTipService.ShowOnDisabled is the entire feature: WPF hides a tooltip on a
        // disabled control by default, so without it the one moment the explanation is wanted
        // is the one moment it cannot be seen. It is a property that is either set or silently
        // absent, which is exactly the kind of thing nothing else would ever catch.
        StaHarness.Run(() =>
        {
            var (window, _) = Build();
            var (panel, _) = ShowGitPanel(window);

            var commit = AllOf<Button>(panel)
                .Where(b => b.Content as string is "Commit" or "Commit & Push")
                .ToList();

            Assert.Equal(2, commit.Count);

            foreach (var button in commit)
            {
                Assert.True(ToolTipService.GetShowOnDisabled(button),
                            $"'{button.Content}' would hide its explanation while disabled");
                Assert.NotNull(button.ToolTip);
            }
        });
    }

    /// <summary>Find a banner by the section label inside it.</summary>
    private static FrameworkElement Banner(MainWindow window, string label)
    {
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

        var text = AllOf<TextBlock>(content).FirstOrDefault(t => t.Text == label);
        Assert.True(text is not null, $"no element carries the label '{label}'");

        // The Border that wraps it: two levels up through the StackPanel it sits in.
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(text!);
        while (parent is not null and not Border)
        {
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }

        return Assert.IsType<Border>(parent);
    }

    private static IEnumerable<T> AllOf<T>(DependencyObject root)
        where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();

            if (node is T wanted)
            {
                yield return wanted;
            }

            var children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
            {
                queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            }
        }
    }
}
