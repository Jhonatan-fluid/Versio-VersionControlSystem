using System.IO;
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
/// The rules are proved in <c>CommitPlanTests</c> without a repository. What these add is that
/// the shell <b>acts on</b> the plan rather than merely displaying it — in particular that
/// files not in the include set are unstaged before the commit, so leftover index content
/// cannot sneak in.
/// </remarks>
public sealed class CommitPlanFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

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
    public async Task Nothing_ticked_commits_the_lot()
    {
        using var repo = new TempRepo(unstagedFiles: 3);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        Assert.Equal(CommitIntent.CommitAll, shell.Commit.Intent);
        Assert.Equal("Commit", shell.Commit.Label);

        shell.CommitMessage = "everything";
        await Eventually.Press(shell.CommitCommand, "commit");
        await Eventually.Until(() => shell.Pending.Count == 0, "the commit landed");

        Assert.Equal(3, FilesInHead(repo).Length);
    }

    [Fact]
    public async Task Only_the_ticked_files_are_committed()
    {
        using var repo = new TempRepo(unstagedFiles: 4);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        shell.Pending[0].IsSelected = true;
        shell.Pending[2].IsSelected = true;

        Assert.Equal(CommitIntent.CommitTicked, shell.Commit.Intent);
        Assert.Equal("Commit (2)", shell.Commit.Label);

        shell.CommitMessage = "two of four";
        await Eventually.Press(shell.CommitCommand, "commit");
        await Eventually.Until(() => shell.Pending.Count == 2, "the commit landed");

        var committed = FilesInHead(repo);

        Assert.Equal(2, committed.Length);
        Assert.Contains("file0.st", committed);
        Assert.Contains("file2.st", committed);
        Assert.DoesNotContain("file1.st", committed);
        Assert.DoesNotContain("file3.st", committed);
    }

    [Fact]
    public async Task Files_already_in_the_index_are_not_a_separate_list()
    {
        // Nothing ticked means the whole list, including files git already had in the index.
        // The old CommitStaged safeguard needed a visible STAGED pane to be honest.
        using var repo = new TempRepo(unstagedFiles: 4, stagedFiles: 2);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        Assert.Equal(4, shell.Pending.Count);
        Assert.Equal(CommitIntent.CommitAll, shell.Commit.Intent);
        Assert.Equal("Commit", shell.Commit.Label);

        shell.CommitMessage = "the lot";
        await Eventually.Press(shell.CommitCommand, "commit");
        await Eventually.Until(() => shell.Pending.Count == 0, "the commit landed");

        Assert.Equal(4, FilesInHead(repo).Length);
    }

    [Fact]
    public async Task Ticking_a_subset_unstages_the_rest_before_the_commit()
    {
        // THE MUTATION CHECK. Two files already in the index, two not. Tick one of the
        // unindexed ones. If UnstageForCommitAsync is skipped, the two that were already
        // staged sneak into the commit alongside the ticked file.
        using var repo = new TempRepo(unstagedFiles: 4, stagedFiles: 2);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);

        var extra = shell.Pending.Single(r => r.Path == "file2.st");
        extra.IsSelected = true;

        Assert.Equal("Commit (1)", shell.Commit.Label);

        shell.CommitMessage = "just the ticked one";
        await Eventually.Press(shell.CommitCommand, "commit");
        await Eventually.Until(() => shell.Pending.Count == 3, "the commit landed");

        var committed = FilesInHead(repo);
        Assert.Equal(["file2.st"], committed);
    }

    /// <summary>What git prints for one read-only command in the repository.</summary>
    private static string GitOutput(TempRepo repo, string arguments)
    {
        using var git = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = repo.Path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

        var output = git!.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return output;
    }

    private static string Lines(params (int Line, string Text)[] edits)
    {
        var lines = Enumerable.Range(1, 20).Select(n => $"line {n}").ToArray();
        foreach (var (line, text) in edits)
        {
            lines[line - 1] = text;
        }

        return string.Join("\n", lines) + "\n";
    }

    [Fact]
    public async Task A_file_with_one_staged_hunk_commits_only_that_hunk()
    {
        // "Stage hunk" put one of two hunks in the index; Commit then ran `git add` on the
        // whole path - include is every row when nothing is ticked - and the second hunk
        // went into the commit as well. The button in the diff pane did nothing that the
        // commit did not undo. The 2026-10-24 review's item 32. A row that is both staged
        // and still changed in the working tree is committed as the index stands.
        using var repo = new TempRepo();
        var seed = Path.Combine(repo.Path, "seed.txt");
        File.WriteAllText(seed, Lines());
        repo.Git("commit -qam twenty");
        File.WriteAllText(seed, Lines((2, "line 2 CHANGED"), (18, "line 18 CHANGED")));

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        await shell.ShowWorkingTreePathAsync("seed.txt");
        await Eventually.Until(() => shell.CanStageHunks, "the diff to load");
        await Eventually.Press(shell.HunkCommand, 0, "stage the first hunk");
        await Eventually.Until(
            () => shell.Pending.Any(r => r.Path == "seed.txt" && r.IsPartlyStaged),
            "the row to read as partly staged");

        shell.CommitMessage = "first hunk only";
        await Eventually.Press(shell.CommitCommand, "commit");
        await Eventually.Until(
            () => GitOutput(repo, "log -1 --pretty=%s").Trim() == "first hunk only",
            "the commit landed");

        var committed = GitOutput(repo, "show HEAD --pretty=format: -- seed.txt");
        Assert.Contains("+line 2 CHANGED", committed, StringComparison.Ordinal);
        Assert.DoesNotContain("line 18 CHANGED", committed, StringComparison.Ordinal);

        // The other hunk is still where it was typed, and still listed.
        Assert.Contains("line 18 CHANGED", File.ReadAllText(seed), StringComparison.Ordinal);
        await Eventually.Until(() => shell.Pending.Any(r => r.Path == "seed.txt"), "the rest to stay pending");
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
        Assert.Equal("Commit", shell.Commit.Label);

        shell.Pending[0].IsSelected = true;
        Assert.Equal("Commit (1)", shell.Commit.Label);

        shell.Pending[1].IsSelected = true;
        Assert.Equal("Commit (2)", shell.Commit.Label);

        shell.PendingAllSelected = false;
        Assert.Equal("Commit", shell.Commit.Label);
    }

    [Fact]
    public async Task Committing_does_not_push()
    {
        using var repo = new TempRepo(published: true, unstagedFiles: 1);
        using var shell = Shell();

        await shell.OpenAsync(repo.Path);
        shell.CommitMessage = "local only";
        shell.CommitCommand.Execute(null);

        await Eventually.Until(() => shell.Remote.Standing.State == TrackingState.Ahead,
                               "the branch went ahead");

        Assert.Equal(1, shell.Remote.Standing.Ahead);
        Assert.True(shell.Remote.Standing.CanPush);
    }
}
