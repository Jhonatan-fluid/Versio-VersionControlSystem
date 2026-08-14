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

    /// <summary>A throwaway repository, optionally with a bare remote beside it.</summary>
    private sealed class Repo : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "versio-standing-" + Guid.NewGuid().ToString("N")[..8]);

        public string Path { get; }

        public string RemotePath { get; }

        public Repo(bool withRemote, bool published = false)
        {
            Directory.CreateDirectory(_root);
            Path = System.IO.Path.Combine(_root, "work");
            RemotePath = System.IO.Path.Combine(_root, "remote.git");
            Directory.CreateDirectory(Path);

            if (withRemote)
            {
                Directory.CreateDirectory(RemotePath);
                Git(RemotePath, "init -q --bare -b main");
            }

            Git(Path, "init -q -b main .");
            Git(Path, "config --local user.name TestUser");
            Git(Path, "config --local user.email test@versio.local");
            File.WriteAllText(System.IO.Path.Combine(Path, "a.txt"), "hello\n");
            Git(Path, "add .");
            Git(Path, "commit -qm first");

            if (withRemote)
            {
                Git(Path, $"remote add origin \"{RemotePath}\"");

                if (published)
                {
                    Git(Path, "push -q --set-upstream origin main");
                }
            }
        }

        public static void Git(string cwd, string arguments)
        {
            using var git = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = cwd,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

            git!.WaitForExit();
        }

        public void Commit(string text)
        {
            File.AppendAllText(System.IO.Path.Combine(Path, "a.txt"), text + "\n");
            Git(Path, "add .");
            Git(Path, "commit -qm more");
        }

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or DirectoryNotFoundException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    [Fact]
    public async Task A_repository_with_no_remote_says_so_rather_than_offering_publish()
    {
        using var repo = new Repo(withRemote: false);
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
        using var repo = new Repo(withRemote: true);
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
        using var repo = new Repo(withRemote: true);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.True(shell.ShowPublish);

        shell.PublishCommand.Execute(null);
        await WaitUntil(() => shell.Standing.State == TrackingState.InSync);

        Assert.Equal("origin/main", shell.Standing.Upstream);
        Assert.False(shell.ShowPublish);
        Assert.True(shell.ShowPush);
        Assert.Contains("Up to date", shell.Standing.Headline);
    }

    [Fact]
    public async Task A_published_branch_with_local_commits_is_ahead()
    {
        using var repo = new Repo(withRemote: true, published: true);
        repo.Commit("second");

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
        using var repo = new Repo(withRemote: true, published: true);
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
        using var withRemote = new Repo(withRemote: true, published: true);
        using var without = new Repo(withRemote: false);
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
        using var repo = new Repo(withRemote: false);
        using var other = new Repo(withRemote: false);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        shell.Notifications.Failure("Push failed.", "something");
        Assert.Single(shell.Notifications.Visible);

        await shell.OpenAsync(other.Path);

        // The failure was about a repository that is no longer open. Keeping it would attach
        // it to whatever is opened next.
        Assert.Empty(shell.Notifications.Visible);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        // AsyncCommand.Execute is `async void`, so a command cannot be awaited. Bounded, so a
        // failure is a message rather than a run that never ends.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("the condition was still false after 20 seconds");
    }
}
