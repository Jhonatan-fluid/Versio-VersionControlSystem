using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The merge-request button, from where the user reaches it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Against a real repository, because the button is a function of git's own answers.</b> The
/// forge comes from the remote URL, the gate from the branch's ahead count and the working
/// tree's cleanliness, and the target from the resolved trunk — four readings that a fake status
/// would supply by construction, agreeing with the code whether or not it asks.
/// </para>
/// <para>
/// <c>MergeRequestPlanTests</c> covers the rules. This covers that the shell reads them, which
/// is the half that was broken in the phase before this one and passed every unit test while it
/// was.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class MergeRequestFlowTests
{
    private static ShellViewModel Shell()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        return new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);
    }

    [Fact]
    public async Task A_repository_with_no_forge_shows_no_button()
    {
        // The bare repository beside it is a local path, which is not a forge and never will
        // be. Most test repositories and plenty of real ones look exactly like this.
        using var repo = new TempRepo(published: true);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.False(shell.MergeRequest.IsVisible);
        Assert.False(shell.OpenMergeRequestCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_github_remote_is_recognised_and_offers_to_create_one()
    {
        // originUrl rather than a real remote: this is about what a URL MEANS, and nothing is
        // ever fetched. TempRepo has the parameter for exactly this.
        using var repo = new TempRepo(originUrl: "https://github.com/acme/machine.git");
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        repo.Git("checkout -q -b feature/pumps");
        await shell.RefreshAsync();

        var plan = shell.MergeRequest;

        Assert.True(plan.IsVisible);
        Assert.Equal("Create pull request", plan.Label);
    }

    [Fact]
    public async Task The_default_branch_is_refused_with_a_reason()
    {
        using var repo = new TempRepo(originUrl: "https://github.com/acme/machine.git");
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        // Still on main, which TempRepo creates and DefaultBranch resolves by convention.
        Assert.True(shell.MergeRequest.IsVisible);
        Assert.False(shell.MergeRequest.CanOpen);
        Assert.Contains("default branch", shell.MergeRequest.BlockedReason);

        // And the tooltip carries the reason rather than going empty on a dead control.
        Assert.Equal(shell.MergeRequest.BlockedReason, shell.MergeRequestTooltip);
    }

    [Fact]
    public async Task A_dirty_tree_is_refused_and_the_button_goes_live_when_it_is_committed()
    {
        using var repo = new TempRepo(originUrl: "https://github.com/acme/machine.git");
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        repo.Git("checkout -q -b feature/pumps");
        repo.Touch("work.st");
        await shell.RefreshAsync();

        Assert.False(shell.MergeRequest.CanOpen);
        Assert.Equal("Commit your changes first.", shell.MergeRequest.BlockedReason);

        // The whole chain again from the other side: commit, and the refusal has to go away.
        // A plan computed once and cached would still be saying "commit your changes".
        repo.Git("add .");
        repo.Git("commit -qm work");
        await shell.RefreshAsync();

        Assert.NotEqual("Commit your changes first.", shell.MergeRequest.BlockedReason);
    }

    [Fact]
    public async Task The_configured_forge_reaches_the_button()
    {
        // The case the setting exists for: a self-hosted instance nothing can recognise from
        // its hostname. This also proves the shell reads repo.json for it — and repo.json is
        // loaded by Sync.AttachAsync, which runs AFTER the first refresh, so a forge resolved
        // once at open would be Unknown here and the button would never appear.
        using var repo = new TempRepo(originUrl: "https://git.acme.internal/group/machine.git");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(repo.Path, ".versio"));
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(repo.Path, ".versio", "repo.json"),
            """
            { "schema": 1, "bindings": [], "ignore": [], "forge": "GitLab" }
            """);

        // Committed, or the fixture's own file leaves the tree dirty and the button is
        // correctly refused for a reason that has nothing to do with what is under test.
        repo.Git("add .");
        repo.Git("commit -qm versio");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        repo.Git("checkout -q -b feature/pumps");
        await shell.RefreshAsync();

        Assert.True(shell.MergeRequest.IsVisible);

        // "merge request" rather than "pull request" is producible only by ForgeKind.GitLab,
        // and nothing but repo.json can supply that for this hostname. The URL is not asserted
        // here: this fixture's origin is a URL that means something rather than a remote that
        // exists, so the branch has no upstream and the plan is correctly refusing to build one.
        Assert.Equal("Create merge request", shell.MergeRequest.Label);
    }

    [Fact]
    public async Task Switching_branch_forgets_the_request_found_for_the_previous_one()
    {
        // A number found for one branch says nothing about another, and leaving it would send
        // somebody to a different branch's request.
        using var repo = new TempRepo(originUrl: "https://github.com/acme/machine.git");
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        repo.Git("checkout -q -b feature/one");
        await shell.RefreshAsync();
        Assert.Equal(MergeRequestIntent.Create, shell.MergeRequest.Intent);

        repo.Git("checkout -q -b feature/two");
        await shell.RefreshAsync();

        // Still Create, never GoTo carried across from the branch we left.
        Assert.Equal(MergeRequestIntent.Create, shell.MergeRequest.Intent);
    }

    [Fact]
    public void The_shell_gives_the_remote_watch_the_request_probe()
    {
        // THE LINK NOTHING ELSE COVERS. RemotePollerTests proves the loop calls whatever it is
        // handed; the flow tests above prove the rules are right. Both stay green with
        // `onProbed: null` at the one construction site, which is precisely the shape of the
        // three unreachable-caller bugs this codebase has already had — so the wiring itself is
        // asserted rather than assumed.
        //
        // It does NOT prove the probe finds a real pull request: that needs a reachable remote
        // AND a hostname a forge can be detected from, which are mutually exclusive without a
        // network. RequestRefProbeTests covers the git half against a real bare remote, and the
        // end-to-end is on the §13 script.
        using var shell = Shell();

        Assert.True(shell.Poller.AsksASecondQuestion);
    }

    [Fact]
    public void The_button_is_in_the_branch_card_and_bound_to_the_plan()
    {
        // Realised, per this panel's standing rule: a binding to a property that does not exist
        // renders nothing at all, silently, and only a built control shows it.
        StaHarness.Run(() =>
        {
            var sink = new InMemoryLogSink();
            var logger = new VersioLogger(new SystemClock()).AddSink(sink);
            var shell = new ShellViewModel(
                logger, new LogViewModel(sink), toolchains: (_, _) => null);
            var window = new MainWindow(shell);

            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var rail = Assert.IsType<RadioButton>(window.FindName("RailGit"));
            rail.IsChecked = true;

            content.Measure(new Size(1400, 900));
            content.Arrange(new Rect(0, 0, 1400, 900));
            content.UpdateLayout();

            var panel = Assert.IsAssignableFrom<FrameworkElement>(
                window.FindName("GitPanel"));

            var button = AllOf<Button>(panel).Single(b =>
                BindingOperations.GetBinding(b, ButtonBase.CommandProperty)?.Path.Path
                    == nameof(ShellViewModel.OpenMergeRequestCommand));

            // No repository is open, so there is no forge and the button must not be there.
            Assert.Equal(Visibility.Collapsed, button.Visibility);

            // The caption follows the plan rather than being a literal, and the tooltip
            // survives the button being disabled.
            Assert.Equal(
                "MergeRequest.Label",
                BindingOperations.GetBinding(button, ContentControl.ContentProperty)?.Path.Path);
            Assert.True(ToolTipService.GetShowOnDisabled(button));
        });
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
