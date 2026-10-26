using System.IO;
using Versio.App.ViewModels;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The large-file preflight (GP-K §9h), from the commit button down.
/// </summary>
/// <remarks>
/// The rule itself is proven in Core; what these add is the placement — the question comes
/// from what is actually staged at commit time, an accepted answer still commits, a declined
/// one commits nothing, and a repository whose <c>.gitattributes</c> already protects the
/// file is never asked at all.
/// </remarks>
public sealed class LargeFileCommitFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    private static void WriteLarge(TempRepo repo, string name)
    {
        // Just over the threshold, and sparse-friendly: the content is zeroes, the size is
        // what matters.
        var path = Path.Combine(repo.Path, name);
        using var file = File.Create(path);
        file.SetLength(Versio.Core.Git.LargeFilePreflight.DefaultThreshold + 1024);
    }

    [Fact]
    public async Task A_large_unprotected_file_is_asked_about_and_a_no_commits_nothing()
    {
        using var repo = new TempRepo();
        WriteLarge(repo, "huge.bin");
        repo.Git("add .");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        string? asked = null;
        shell.Asked().Destructive = p =>
        {
            asked = p.Title;
            return false;
        };

        shell.CommitMessage = "about to be stopped";
        await Eventually.Press(shell.CommitCommand, "commit");

        Assert.Equal("Commit a large file without LFS?", asked);

        // Declined means declined: the file is still only staged.
        Assert.Contains(shell.Pending, r => r.Path == "huge.bin");
    }

    [Fact]
    public async Task Commit_anyway_still_commits()
    {
        using var repo = new TempRepo();
        WriteLarge(repo, "huge.bin");
        repo.Git("add .");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Asked().Destructive = _ => true;

        shell.CommitMessage = "eyes open";
        await Eventually.Press(shell.CommitCommand, "commit");
        await Eventually.Until(() => shell.Pending.Count == 0, "the commit landed");
    }

    [Fact]
    public async Task A_large_file_staged_and_then_shrunk_on_disk_is_still_asked_about()
    {
        // The commit records the index, and the preflight weighed the working copy: stage
        // the large file, overwrite it on disk with five bytes, and the question never came
        // while twenty megabytes went into history (the 2026-10-24 review's item 4). The
        // partly-staged commit path leaves exactly this row alone - it is committed as the
        // index stands - so the index is what has to be measured.
        using var repo = new TempRepo();
        WriteLarge(repo, "huge.bin");
        repo.Git("add .");
        File.WriteAllText(Path.Combine(repo.Path, "huge.bin"), "tiny!");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        await Eventually.Until(
            () => shell.Pending.Any(r => r.Path == "huge.bin" && r.IsPartlyStaged),
            "the row to read as partly staged");

        string? asked = null;
        shell.Asked().Destructive = p =>
        {
            asked = p.Title;
            return false;
        };

        shell.CommitMessage = "about to be stopped";
        await Eventually.Press(shell.CommitCommand, "commit");

        Assert.Equal("Commit a large file without LFS?", asked);
        Assert.Contains(shell.Pending, r => r.Path == "huge.bin");
    }

    [Fact]
    public async Task A_file_an_lfs_rule_matches_is_not_asked_about()
    {
        using var repo = new TempRepo();
        File.WriteAllText(Path.Combine(repo.Path, ".gitattributes"),
            "*.bin filter=lfs diff=lfs merge=lfs\n");
        WriteLarge(repo, "huge.bin");
        repo.Git("add .");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        var prompted = false;
        shell.Asked().Destructive = _ => prompted = true;

        shell.CommitMessage = "protected";
        await Eventually.Press(shell.CommitCommand, "commit");
        await Eventually.Until(() => shell.Pending.Count == 0, "the commit landed");

        // The whole point of writing the rule at adoption: the question never comes up on
        // a repository configured the way Versio configures them.
        Assert.False(prompted);
    }
}
