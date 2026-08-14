using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// What the commit button does, against a real repository and a real index.
/// </summary>
/// <remarks>
/// <para>
/// The rules are proved in <c>CommitPlanTests</c> without a repository. What these add is that
/// the shell <b>acts on</b> the plan rather than merely displaying it — and the one that would
/// hurt is <see cref="A_deliberate_partial_stage_is_not_swept_up"/>: the plan can say
/// <c>CommitStaged</c> while the code stages everything anyway, and nothing on screen would
/// show the difference until somebody read the commit.
/// </para>
/// <para>
/// Asserted against <c>git</c>'s own view of what landed, not against the view model's. What
/// matters is the commit, and a view model that agreed with itself would prove nothing.
/// </para>
/// </remarks>
public sealed class CommitPlanFlowTests
{
    private static ShellViewModel Shell()
    {
        var sink = new InMemoryLogSink();
        var logger = new VersioLogger(new SystemClock()).AddSink(sink);
        return new ShellViewModel(logger, new LogViewModel(sink), toolchains: (_, _) => null);
    }

    /// <summary>The paths in the newest commit, as git reports them.</summary>
    private static string[] FilesInHead(TempRepo repo)
    {
        using var git = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "show --name-only --pretty=format: HEAD",
                WorkingDirectory = repo.Path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

        var output = git!.StandardOutput.ReadToEnd();
        git.WaitForExit();

        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => l.Trim())
                         .Where(l => l.Length > 0)];
    }

    [Fact]
    public async Task Nothing_staged_and_nothing_ticked_commits_the_lot()
    {
        // The common case: change some files, press the button, get a commit. Nobody should
        // have to stage first to use this panel at all.
        using var repo = new TempRepo(unstagedFiles: 3);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.Equal(CommitIntent.StageAllThenCommit, shell.Commit.Intent);
        Assert.Equal("Stage all & commit", shell.Commit.Label);

        shell.CommitMessage = "everything";
        shell.CommitCommand.Execute(null);
        await Eventually.Until(() => shell.Unstaged.Count == 0, "the commit landed");

        Assert.Equal(3, FilesInHead(repo).Length);
    }

    [Fact]
    public async Task Only_the_ticked_files_are_committed()
    {
        using var repo = new TempRepo(unstagedFiles: 4);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        shell.Unstaged[0].IsSelected = true;
        shell.Unstaged[2].IsSelected = true;

        Assert.Equal(CommitIntent.StageTickedThenCommit, shell.Commit.Intent);
        Assert.Equal("Stage & commit (2)", shell.Commit.Label);

        shell.CommitMessage = "two of four";
        shell.CommitCommand.Execute(null);
        await Eventually.Until(() => shell.Unstaged.Count == 2, "the commit landed");

        var committed = FilesInHead(repo);

        Assert.Equal(2, committed.Length);
        Assert.Contains("file0.st", committed);
        Assert.Contains("file2.st", committed);

        // And the two that were not ticked are still sitting there, unstaged.
        Assert.DoesNotContain("file1.st", committed);
        Assert.DoesNotContain("file3.st", committed);
    }

    [Fact]
    public async Task A_deliberate_partial_stage_is_not_swept_up()
    {
        // THE CASE THAT WOULD HURT.
        //
        // Somebody staged two files on purpose and left two out. Pressing a button whose
        // caption reads "Commit" must commit exactly those two — sweeping the others in would
        // undo the decision silently, and they would find out by reading the commit, by which
        // time it is history.
        using var repo = new TempRepo(unstagedFiles: 4, stagedFiles: 2);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.Equal(2, shell.Staged.Count);
        Assert.Equal(2, shell.Unstaged.Count);
        Assert.Equal(CommitIntent.CommitStaged, shell.Commit.Intent);

        // The caption is the warning, and it is the plain one on purpose.
        Assert.Equal("Commit", shell.Commit.Label);
        Assert.Contains("left alone", shell.Commit.Explanation);

        shell.CommitMessage = "only what I staged";
        shell.CommitCommand.Execute(null);
        await Eventually.Until(() => shell.Staged.Count == 0, "the commit landed");

        var committed = FilesInHead(repo);

        Assert.Equal(2, committed.Length);
        Assert.Contains("file0.st", committed);
        Assert.Contains("file1.st", committed);

        // Still in the working tree, still unstaged, exactly as they were left.
        Assert.Equal(2, shell.Unstaged.Count);
    }

    [Fact]
    public async Task Ticks_override_what_is_already_staged()
    {
        // An explicit selection is the least ambiguous instruction the panel can receive, so it
        // beats the staged set — and the caption changes to say so.
        using var repo = new TempRepo(unstagedFiles: 4, stagedFiles: 2);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        shell.Unstaged[0].IsSelected = true;

        Assert.Equal(CommitIntent.StageTickedThenCommit, shell.Commit.Intent);
        Assert.Equal("Stage & commit (1)", shell.Commit.Label);

        shell.CommitMessage = "staged plus the ticked one";
        shell.CommitCommand.Execute(null);
        await Eventually.Until(() => shell.Unstaged.Count == 1, "the commit landed");

        // The two already staged plus the one ticked. Ticking adds to the index rather than
        // replacing it, which is what git does and what the panel shows.
        Assert.Equal(3, FilesInHead(repo).Length);
    }

    [Fact]
    public async Task An_empty_tree_offers_nothing()
    {
        using var repo = new TempRepo();
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.Equal(CommitIntent.Nothing, shell.Commit.Intent);
        Assert.False(shell.CommitCommand.CanExecute(null));
        Assert.Equal("Nothing to commit.", shell.CommitBlockedReason);
    }

    [Fact]
    public async Task A_missing_message_is_what_the_button_reports()
    {
        using var repo = new TempRepo(unstagedFiles: 2);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.False(shell.CommitCommand.CanExecute(null));
        Assert.Equal("Write a commit message first.", shell.CommitBlockedReason);

        shell.CommitMessage = "something";

        Assert.True(shell.CommitCommand.CanExecute(null));
        Assert.Equal(string.Empty, shell.CommitBlockedReason);
    }

    [Fact]
    public async Task The_caption_moves_as_the_ticks_do()
    {
        using var repo = new TempRepo(unstagedFiles: 3);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.Equal("Stage all & commit", shell.Commit.Label);

        shell.Unstaged[0].IsSelected = true;
        Assert.Equal("Stage & commit (1)", shell.Commit.Label);

        shell.Unstaged[1].IsSelected = true;
        Assert.Equal("Stage & commit (2)", shell.Commit.Label);

        shell.UnstagedAllSelected = false;
        Assert.Equal("Stage all & commit", shell.Commit.Label);
    }

    [Fact]
    public async Task Committing_does_not_push()
    {
        // Commit & Push is gone. Push is a deliberate act of its own now, so a commit must
        // leave the branch ahead rather than quietly sending it.
        using var repo = new TempRepo(published: true, unstagedFiles: 1);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        shell.CommitMessage = "local only";
        shell.CommitCommand.Execute(null);

        await Eventually.Until(() => shell.Standing.State == TrackingState.Ahead,
                               "the branch went ahead");

        Assert.Equal(1, shell.Standing.Ahead);

        // Asserted on the gating RULE rather than on the command's momentary enabled state:
        // CanExecute also carries `Ready`, which is false for as long as the refresh that
        // follows a commit is still running. That is a different concern with its own tests,
        // and folding it in here would make this assert timing rather than behaviour.
        Assert.True(shell.Standing.CanPush);
    }
}
