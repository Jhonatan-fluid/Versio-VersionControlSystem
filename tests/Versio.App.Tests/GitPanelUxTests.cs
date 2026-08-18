using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.App.Views.Panels;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// GP-L, the small UX list (§10): the clean-tree line, the remote's name in the branch card,
/// the coloured subject counter, and the push shortcut.
/// </summary>
/// <remarks>
/// §10.1 (path trimming) shipped with GP-J and §10.4 (unborn) with GP-B's standing machine —
/// the eleven-state headline already says "No commits yet". What is tested here is the four
/// that landed together, each at the seam a typo would silently break.
/// </remarks>
public sealed class GitPanelUxTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    [Fact]
    public async Task A_clean_tree_is_one_sentence_and_a_dirty_one_is_the_lists()
    {
        using var repo = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.True(shell.IsWorkingTreeClean);

        // The property computes live, so an assertion on its value alone would stay green
        // with the change notification deleted — while the XAML bound to it never updated.
        // The notification is the feature; listen for it.
        var announced = false;
        shell.PropertyChanged += (_, e) =>
            announced |= e.PropertyName == nameof(ShellViewModel.IsWorkingTreeClean);

        // One changed file is enough to bring the boxes back.
        repo.Touch("seed.txt");
        await shell.RefreshAsync();

        Assert.False(shell.IsWorkingTreeClean);
        Assert.True(announced, "IsWorkingTreeClean changed without announcing it");
    }

    [Fact]
    public void No_repository_is_not_a_clean_tree()
    {
        // The sentence says "nothing to commit", which presumes somewhere to commit to. On
        // first launch, with all three surfaces empty, it would read as a claim about a
        // repository that is not there.
        using var shell = Shell();

        Assert.False(shell.IsWorkingTreeClean);
    }

    [Fact]
    public async Task The_branch_card_says_where_push_goes()
    {
        using var repo = new TempRepo(withRemote: true);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.StartsWith("origin · ", shell.RemoteDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_remote_means_no_remote_line()
    {
        // "No remote" is the standing sentence's line; a second control saying it again
        // would be two answers to one question.
        using var repo = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.Equal(string.Empty, shell.RemoteDisplay);
    }

    [Theory]
    [InlineData(50, "Brush.Text.Disabled")]
    [InlineData(51, "Brush.Status.Modified")]
    [InlineData(72, "Brush.Status.Modified")]
    [InlineData(73, "Brush.Status.Deleted")]
    public void The_subject_counter_changes_colour_at_50_and_at_72(int length, string brush)
    {
        // The plan's own boundaries. The counter was a bare number only a hover could
        // decode; the colour is the explanation for everyone who does not hover.
        using var shell = Shell();
        shell.CommitMessage = new string('x', length);

        Assert.Equal(length, shell.CommitSubjectLength);
        Assert.Equal(brush, shell.CommitSubjectBrushKey);
    }

    [Fact]
    public void The_body_does_not_count_toward_the_subject()
    {
        using var shell = Shell();
        shell.CommitMessage = "short subject\n" + new string('y', 200);

        Assert.Equal("Brush.Text.Disabled", shell.CommitSubjectBrushKey);
    }

    [Fact]
    public async Task The_tooltips_carry_their_shortcuts_only_while_the_key_would_work()
    {
        using var repo = new TempRepo(unstagedFiles: 1, stagedFiles: 1);
        using var shell = Shell();

        // Blocked first — same shell, before anything is open: a shortcut advertised at
        // the one moment it does nothing is the wording the chrome test pins, and
        // "Nothing to commit." stays clean of it.
        Assert.DoesNotContain("Ctrl+Enter", shell.CommitExplanation);

        await shell.OpenAsync(repo.Path);
        shell.CommitMessage = "ready to go";

        Assert.EndsWith("(Ctrl+Enter)", shell.CommitExplanation, StringComparison.Ordinal);
        Assert.EndsWith("(Ctrl+Shift+P)", shell.Remote.PushTooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctrl_Shift_P_is_wired_to_the_push_command()
    {
        // The binding lives in XAML, where a typo'd path compiles clean, binds to nothing
        // and leaves a shortcut that silently never fires — the renamed-binding failure
        // mode the git panel has already had once. Through the real window, so what is
        // asserted is the panel MainWindow builds.
        StaHarness.Run(() =>
        {
            using var shell = TestShell.Build();
            var window = new MainWindow(shell);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            var panel = Assert.IsType<GitPanelView>(window.FindName("GitPanel"));

            var binding = Assert.Single(panel.InputBindings.OfType<KeyBinding>());
            Assert.Equal(Key.P, binding.Key);
            Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, binding.Modifiers);
            Assert.Same(shell.Remote.PushCommand, binding.Command);
        });
    }
}
