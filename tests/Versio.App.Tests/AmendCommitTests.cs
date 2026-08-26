using System.Diagnostics;
using Versio.App.ViewModels;
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

        // Pushed from "a terminal": the shell has not refreshed and still offers amend.
        TempRepo.Git(repo.Path, "push -q");

        shell.Asked().Destructive = _ => true;
        shell.CommitMessage = "rewritten anyway";
        var before = GitRead(repo.Path, "rev-parse HEAD");

        await Eventually.Press(shell.AmendCommitCommand, "amend against a pushed head");

        Assert.Equal(before, GitRead(repo.Path, "rev-parse HEAD"));
        Assert.Equal("local work", GitRead(repo.Path, "log -1 --format=%B"));

        // The typed message survives the refusal — losing the copy must not also lose
        // the thing, same argument the clipboard helper makes.
        Assert.Equal("rewritten anyway", shell.CommitMessage);
    }
}
