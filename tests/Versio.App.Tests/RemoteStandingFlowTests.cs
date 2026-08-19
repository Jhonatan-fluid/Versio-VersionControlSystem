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
    private static ShellViewModel Shell() => TestShell.Build();

    [Fact]
    public async Task A_repository_with_no_remote_says_so_rather_than_offering_publish()
    {
        using var repo = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.Equal(TrackingState.NoRemote, shell.Remote.Standing.State);
        Assert.False(shell.Remote.PublishCommand.CanExecute(null));
        Assert.False(shell.Remote.PushCommand.CanExecute(null));
        Assert.False(shell.Remote.PullCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_branch_never_pushed_offers_publish_rather_than_push()
    {
        using var repo = new TempRepo(withRemote: true);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        // The case the old rendering showed as an empty string, identical to being in sync,
        // while the work existed only on this machine.
        Assert.Equal(TrackingState.NoUpstream, shell.Remote.Standing.State);
        Assert.True(shell.Remote.ShowPublish);
        Assert.False(shell.Remote.ShowPush);
        Assert.True(shell.Remote.PublishCommand.CanExecute(null));
        Assert.False(shell.Remote.PushCommand.CanExecute(null));
    }

    [Fact]
    public async Task Publishing_sets_the_upstream_and_flips_the_button_back_to_push()
    {
        using var repo = new TempRepo(withRemote: true);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.True(shell.Remote.ShowPublish);

        await Eventually.Press(shell.Remote.PublishCommand, "publish");
        await Eventually.Until(() => shell.Remote.Standing.State == TrackingState.InSync,
                               "the branch became published");

        Assert.Equal("origin/main", shell.Remote.Standing.Upstream);
        Assert.False(shell.Remote.ShowPublish);
        Assert.True(shell.Remote.ShowPush);
        Assert.Contains("Up to date", shell.Remote.Standing.Headline);
    }

    [Fact]
    public async Task A_published_branch_with_local_commits_is_ahead()
    {
        using var repo = new TempRepo(published: true);
        repo.Commit();

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        Assert.Equal(TrackingState.Ahead, shell.Remote.Standing.State);
        Assert.Equal(1, shell.Remote.Standing.Ahead);
        Assert.Equal("Push (1)", shell.Remote.PushLabel);
        Assert.True(shell.Remote.PushCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_in_sync_branch_cannot_push_but_can_still_pull()
    {
        using var repo = new TempRepo(published: true);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.Equal(TrackingState.InSync, shell.Remote.Standing.State);
        Assert.False(shell.Remote.PushCommand.CanExecute(null));

        // Pull stays live: behind-ness is only as fresh as the last fetch, and a dead button
        // would strand somebody whose remote moved a second ago.
        Assert.True(shell.Remote.PullCommand.CanExecute(null));
        Assert.Equal("Pull", shell.Remote.PullLabel);
    }

    [Fact]
    public async Task Opening_a_second_repository_forgets_the_first_ones_standing()
    {
        using var withRemote = new TempRepo(published: true);
        using var without = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(withRemote.Path);
        Assert.Equal(TrackingState.InSync, shell.Remote.Standing.State);

        await shell.OpenAsync(without.Path);

        // _hasRemote is per repository. Carrying it across would make a local-only repository
        // claim it had somewhere to push.
        Assert.Equal(TrackingState.NoRemote, shell.Remote.Standing.State);
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

    /// <summary>
    /// A detached HEAD reaches the standing the panel offers the exit from (UX-10).
    /// </summary>
    /// <remarks>
    /// <b>Against a real detachment, not a hand-built <c>BranchInfo</c>.</b> Whether
    /// <c>IsDetached</c> follows from <c>State</c> is settled in Core without a repository;
    /// what cannot be settled there is whether porcelain v2 over a genuinely detached HEAD
    /// produces a branch the state machine reads as detached at all. That is the half that has
    /// been wrong before — the shell reaching the state machine with the wrong inputs — and it
    /// is what puts the button on screen.
    /// </remarks>
    [Fact]
    public async Task A_detached_head_offers_the_way_back_onto_a_branch()
    {
        using var repo = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.False(shell.Remote.Standing.IsDetached);

        repo.Git("checkout --detach HEAD");
        await Eventually.Press(shell.RefreshCommand, "refresh");

        Assert.Equal(TrackingState.Detached, shell.Remote.Standing.State);
        Assert.True(shell.Remote.Standing.IsDetached);

        // The button becomes live, which is the point: the sentence has said for two versions
        // that work here will be lost, and the only remedy lived in a menu in the top bar.
        //
        // WAITED FOR RATHER THAN ASSERTED OUTRIGHT, and measured: asserting it directly failed
        // under the full suite. It is gated on `Ready`, which is "a client, and nothing
        // running" - and the remote poller runs git subprocesses of its own on a loop, so the
        // instant after a refresh returns is not reliably idle. The standing itself IS asserted
        // outright above, because that is the thing under test; this is the gate around it.
        await Eventually.Until(
            () => shell.PutOnBranchCommand.CanExecute(null), "the escape button went live");
    }
}
