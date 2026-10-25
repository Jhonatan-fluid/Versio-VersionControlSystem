using System.Diagnostics;
using System.Reflection;
using Versio.App.ViewModels;
using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Infrastructure.Git;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// UX-12: the last commit's message can be fixed — while nobody else can have the commit,
/// and without anything else changing.
/// </summary>
/// <remarks>
/// The rule under test is the offer, not just the verb: amend is history rewriting, safe
/// exactly as long as no remote has the commit, and that is a fact this app tracks. The
/// most load-bearing test here is the message-only invariant — a bare <c>--amend</c> would
/// quietly sweep the index into the rewritten commit, which is how a typo fix becomes an
/// unreviewed content change.
/// </remarks>
public sealed class AmendCommitTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    /// <summary>
    /// A shell whose git client is real except for <see cref="IGitClient.CommitMessageAsync"/>,
    /// which always misses — so Open and CanAmend still hold, and the load/rewrite
    /// refusals can be pressed rather than simulated.
    /// </summary>
    private static ShellViewModel MutedShell()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        return new ShellViewModel(
            logger,
            new LogViewModel(sink),
            gitFactory: path => MuteCommitMessage.Of(
                new GitCliClient(path, GitExecutable.Resolve(), logger)),
            toolchains: (_, _) => null);
    }

    /// <summary>Run git and read what it prints — TempRepo.Git is fire-and-forget.</summary>
    private static string GitRead(string workingDirectory, string arguments)
    {
        using var git = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return output.Trim();
    }

    [Fact]
    public async Task The_offer_follows_whether_head_is_unpushed()
    {
        // Fully pushed: the ordinary state most of the day, and the state in which the
        // button must not exist. One local commit later it must.
        using var repo = new TempRepo(published: true);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.False(shell.Remote.HeadIsUnpushed);

        repo.Commit("local work");
        await shell.RefreshAsync();

        Assert.True(shell.Remote.HeadIsUnpushed);
    }

    [Fact]
    public async Task The_first_press_fills_the_box_and_rewrites_nothing()
    {
        using var repo = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        var before = GitRead(repo.Path, "rev-parse HEAD");

        await Eventually.Press(shell.AmendCommitCommand, "amend (first press)");

        Assert.Equal("first", shell.CommitMessage);
        Assert.Equal(before, GitRead(repo.Path, "rev-parse HEAD"));
    }

    [Fact]
    public async Task A_failed_head_read_is_a_failure_not_a_loop()
    {
        // The first press with an empty box loads HEAD. When that read used to return
        // "", the box stayed empty and the status said "press again" — a loop that
        // looked like the feature. The copy path already drives a missing id through
        // the real client; this drives the load path, which only ever asks for HEAD.
        using var repo = new TempRepo();
        using var shell = MutedShell();

        await shell.OpenAsync(repo.Path);
        var before = GitRead(repo.Path, "rev-parse HEAD");

        await Eventually.Press(shell.AmendCommitCommand, "amend (failed load)");

        Assert.Equal(string.Empty, shell.CommitMessage);
        Assert.Equal(before, GitRead(repo.Path, "rev-parse HEAD"));
        Assert.DoesNotContain("press Amend again", shell.StatusSummary, StringComparison.Ordinal);
        var card = Assert.Single(shell.Notifications.Visible);
        Assert.Contains("Could not read", card.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_head_read_on_the_rewrite_refuses_without_asking()
    {
        // The second press names both subjects in the confirmation. If the re-read
        // of HEAD fails, asking with an empty old subject would be the same lie as
        // loading an empty box: a dialog about a rewrite that cannot run.
        using var repo = new TempRepo();
        using var shell = MutedShell();

        await shell.OpenAsync(repo.Path);
        var before = GitRead(repo.Path, "rev-parse HEAD");

        string? asked = null;
        shell.Asked().Destructive = p =>
        {
            asked = p.Title;
            return true;
        };

        shell.CommitMessage = "typed anyway";
        await Eventually.Press(shell.AmendCommitCommand, "amend (failed rewrite read)");

        Assert.Null(asked);
        Assert.Equal("typed anyway", shell.CommitMessage);
        Assert.Equal(before, GitRead(repo.Path, "rev-parse HEAD"));
        var card = Assert.Single(shell.Notifications.Visible);
        Assert.Contains("Could not read", card.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_second_press_rewrites_after_naming_both_subjects()
    {
        using var repo = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        var before = GitRead(repo.Path, "rev-parse HEAD");

        // The confirmation must name both messages: the accident it exists to catch is a
        // message typed FOR THE NEXT COMMIT landing on the last one via a mispress.
        string? asked = null;
        shell.Asked().Destructive = p =>
        {
            asked = p.Title + "\n" + p.Body;
            return true;
        };

        shell.CommitMessage = "first, corrected";
        await Eventually.Press(shell.AmendCommitCommand, "amend (second press)");

        Assert.Equal("first, corrected", GitRead(repo.Path, "log -1 --format=%B"));
        Assert.NotEqual(before, GitRead(repo.Path, "rev-parse HEAD"));
        Assert.Equal(string.Empty, shell.CommitMessage);

        Assert.NotNull(asked);
        Assert.Contains("first", asked);
        Assert.Contains("first, corrected", asked);
    }

    [Fact]
    public async Task A_staged_file_does_not_ride_into_the_amended_commit()
    {
        // THE MESSAGE-ONLY INVARIANT. A bare `--amend` folds the index into the rewritten
        // commit, so fixing a typo would silently commit whatever was staged since — an
        // unreviewed content change wearing a reviewed commit's clothes. The staged file
        // must still be staged afterwards, and the commit must still carry only what it
        // carried before.
        using var repo = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        repo.Touch("sneaky.st");
        TempRepo.Git(repo.Path, "add sneaky.st");

        shell.Asked().Destructive = _ => true;
        shell.CommitMessage = "first, reworded";
        await Eventually.Press(shell.AmendCommitCommand, "amend");

        Assert.Equal("first, reworded", GitRead(repo.Path, "log -1 --format=%B"));
        Assert.DoesNotContain("sneaky.st",
            GitRead(repo.Path, "show --format= --name-only HEAD"));
        Assert.Equal("sneaky.st", GitRead(repo.Path, "diff --cached --name-only"));
    }

    [Fact]
    public async Task A_push_that_landed_since_the_last_refresh_still_refuses()
    {
        // The button lights from the last refresh; a terminal push can land between the
        // glance and the press. The flow's own rev-list is the verdict that counts.
        using var repo = new TempRepo(published: true);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        repo.Commit("local work");
        await shell.RefreshAsync();
        Assert.True(shell.Remote.HeadIsUnpushed);

        // Pushed from "a terminal". Execute directly rather than through Eventually.Press:
        // the push touches .git, the watcher notices, and a refresh can put the button out
        // BEFORE a wait on CanExecute ever samples it — this test lost exactly that race
        // once. Executing models the press that won it, which is the case under test; the
        // flow's own rev-list must refuse whichever side the glance was on.
        TempRepo.Git(repo.Path, "push -q");

        shell.Asked().Destructive = _ => true;
        shell.CommitMessage = "rewritten anyway";
        var before = GitRead(repo.Path, "rev-parse HEAD");

        shell.AmendCommitCommand.Execute(null);
        await Eventually.Until(
            () => !shell.AmendCommitCommand.IsRunning, "the amend flow finished");

        Assert.Equal(before, GitRead(repo.Path, "rev-parse HEAD"));
        Assert.Equal("local work", GitRead(repo.Path, "log -1 --format=%B"));

        // The typed message survives the refusal — losing the copy must not also lose
        // the thing, same argument the clipboard helper makes.
        Assert.Equal("rewritten anyway", shell.CommitMessage);
    }

    [Fact]
    public async Task A_commit_that_landed_while_the_dialog_was_open_is_not_the_one_rewritten()
    {
        // The dialog is modal and takes as long as a person takes; `commit --amend` rewrites
        // whatever HEAD is when it runs. A commit from a terminal in between meant the
        // rewrite hit a commit the dialog never named. The 2026-10-24 review's item 41. The
        // Destructive stub IS the terminal here: it commits, then answers yes.
        using var repo = new TempRepo(published: true);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        repo.Commit("local work");
        await shell.RefreshAsync();
        Assert.True(shell.Remote.HeadIsUnpushed);

        shell.Asked().Destructive = _ =>
        {
            repo.Commit("landed meanwhile");
            return true;
        };
        shell.CommitMessage = "rewritten";

        shell.AmendCommitCommand.Execute(null);
        await Eventually.Until(
            () => !shell.AmendCommitCommand.IsRunning, "the amend flow finished");

        Assert.Equal("landed meanwhile", GitRead(repo.Path, "log -1 --format=%s"));
        Assert.Equal("local work", GitRead(repo.Path, "log -1 --skip=1 --format=%s"));
        Assert.Contains(shell.Notifications.Visible,
                        c => c.Headline.Contains("Not amended", StringComparison.Ordinal));
        Assert.Equal("rewritten", shell.CommitMessage);
    }

    [Fact]
    public async Task A_push_that_landed_while_the_dialog_was_open_still_refuses()
    {
        // Same window, the other race: the pre-dialog rev-list said unpushed, the person
        // thought for a minute, and the push landed from a terminal in between.
        using var repo = new TempRepo(published: true);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        repo.Commit("local work");
        await shell.RefreshAsync();

        shell.Asked().Destructive = _ =>
        {
            TempRepo.Git(repo.Path, "push -q");
            return true;
        };
        shell.CommitMessage = "rewritten anyway";
        var before = GitRead(repo.Path, "rev-parse HEAD");

        shell.AmendCommitCommand.Execute(null);
        await Eventually.Until(
            () => !shell.AmendCommitCommand.IsRunning, "the amend flow finished");

        Assert.Equal(before, GitRead(repo.Path, "rev-parse HEAD"));
        Assert.Contains(shell.Notifications.Visible,
                        c => c.Headline.Contains("Not amended", StringComparison.Ordinal));
    }
}

/// <summary>
/// Same client the shell would have built, except <c>CommitMessageAsync</c> always misses.
/// </summary>
/// <remarks>
/// <see cref="GitCliClient"/> is sealed, and forwarding every other <see cref="IGitClient"/>
/// method to stub one is how the next method on that interface gets forgotten. The
/// proxy intercepts the one name; everything else is a real git call against the
/// temp repository, so Open and CanAmend still hold. Public: <c>DispatchProxy.Create</c>
/// constructs the type from another assembly and cannot see an internal one.
/// Cannot be sealed: the proxy generator subclasses it.
/// </remarks>
public class MuteCommitMessage : DispatchProxy
{
    private IGitClient _inner = null!;

    public static IGitClient Of(IGitClient inner)
    {
        var proxy = Create<IGitClient, MuteCommitMessage>();
        ((MuteCommitMessage)(object)proxy)._inner = inner;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IGitClient.CommitMessageAsync))
        {
            return Task.FromResult<string?>(null);
        }

        return targetMethod.Invoke(_inner, args);
    }
}
