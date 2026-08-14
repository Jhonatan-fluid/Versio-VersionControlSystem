using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The remote state the shell computes from a real repository, and the buttons it gates.
/// </summary>
/// <remarks>
/// <para>
/// The state machine itself is proved in <c>RemoteStandingTests</c> without a repository at
/// all. What these add is that the shell reaches it with the right inputs — in particular the
/// one that cannot be derived from a branch, whether the repository has any remote, which is
/// what separates "add a remote" from "press Publish".
/// </para>
/// <para>
/// Against real git, because the interesting values come out of porcelain v2 and
/// <c>for-each-ref</c>. A fake would answer whatever this code happens to believe, which is
/// exactly the belief under test.
/// </para>
/// </remarks>
public sealed class RemoteStandingFlowTests
{
    private static ShellViewModel Shell()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        return new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);
    }

    [Fact]
    public async Task A_repository_with_no_remote_says_so_rather_than_offering_publish()
    {
        using var repo = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.Equal(TrackingState.NoRemote, shell.Standing.State);
        Assert.False(shell.PublishCommand.CanExecute(null));
        Assert.False(shell.PushCommand.CanExecute(null));
        Assert.False(shell.PullCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_branch_never_pushed_offers_publish_rather_than_push()
    {
        using var repo = new TempRepo(withRemote: true);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        // The case the old rendering showed as an empty string, identical to being in sync,
        // while the work existed only on this machine.
        Assert.Equal(TrackingState.NoUpstream, shell.Standing.State);
        Assert.True(shell.ShowPublish);
        Assert.False(shell.ShowPush);
        Assert.True(shell.PublishCommand.CanExecute(null));
        Assert.False(shell.PushCommand.CanExecute(null));
    }

    [Fact]
    public async Task Publishing_sets_the_upstream_and_flips_the_button_back_to_push()
    {
        using var repo = new TempRepo(withRemote: true);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.True(shell.ShowPublish);

        await Eventually.Press(shell.PublishCommand, "publish");
        await Eventually.Until(() => shell.Standing.State == TrackingState.InSync,
                               "the branch became published");

        Assert.Equal("origin/main", shell.Standing.Upstream);
        Assert.False(shell.ShowPublish);
        Assert.True(shell.ShowPush);
        Assert.Contains("Up to date", shell.Standing.Headline);
    }

    [Fact]
    public async Task A_published_branch_with_local_commits_is_ahead()
    {
        using var repo = new TempRepo(published: true);
        repo.Commit();

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        Assert.Equal(TrackingState.Ahead, shell.Standing.State);
        Assert.Equal(1, shell.Standing.Ahead);
        Assert.Equal("Push (1)", shell.PushLabel);
        Assert.True(shell.PushCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_in_sync_branch_cannot_push_but_can_still_pull()
    {
        using var repo = new TempRepo(published: true);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.Equal(TrackingState.InSync, shell.Standing.State);
        Assert.False(shell.PushCommand.CanExecute(null));

        // Pull stays live: behind-ness is only as fresh as the last fetch, and a dead button
        // would strand somebody whose remote moved a second ago.
        Assert.True(shell.PullCommand.CanExecute(null));
        Assert.Equal("Pull", shell.PullLabel);
    }

    [Fact]
    public async Task Opening_a_second_repository_forgets_the_first_ones_standing()
    {
        using var withRemote = new TempRepo(published: true);
        using var without = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(withRemote.Path);
        Assert.Equal(TrackingState.InSync, shell.Standing.State);

        await shell.OpenAsync(without.Path);

        // _hasRemote is per repository. Carrying it across would make a local-only repository
        // claim it had somewhere to push.
        Assert.Equal(TrackingState.NoRemote, shell.Standing.State);
    }

    [Fact]
    public async Task Notifications_do_not_follow_a_repository_change()
    {
        using var repo = new TempRepo();
        using var other = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        shell.Notifications.Failure("Push failed.", "something");
        Assert.Single(shell.Notifications.Visible);

        await shell.OpenAsync(other.Path);

        // The failure was about a repository that is no longer open. Keeping it would attach
        // it to whatever is opened next.
        Assert.Empty(shell.Notifications.Visible);
    }

}
