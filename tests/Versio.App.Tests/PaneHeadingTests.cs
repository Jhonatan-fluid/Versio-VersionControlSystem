using System.IO;
using Versio.App.ViewModels;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The pane header names what it shows.
/// </summary>
/// <remarks>
/// It said DIFF over everything, with the path trimmed from its end so the object's name was
/// what the ellipsis ate. Seen in the running window on 2026-09-21, over a whole unchanged file
/// browsed from the explorer.
/// </remarks>
public sealed class PaneHeadingTests
{
    private const string Deep =
        "fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/Library/Alarm/POUs/FB_Alarm.st";

    [Fact]
    public void A_file_leads_with_its_name_and_follows_with_its_folder()
    {
        var heading = PaneHeading.Of(PaneMode.File, Deep);

        Assert.Equal("FILE", heading.Label);
        Assert.Equal("FB_Alarm.st", heading.Title);
        Assert.Equal(
            "fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/Library/Alarm/POUs",
            heading.Subtitle);
        Assert.False(heading.IsComparing);
    }

    [Fact]
    public void Only_a_comparison_offers_the_diff_options()
    {
        Assert.True(PaneHeading.Of(PaneMode.Diff, "a/b.st").IsComparing);
        Assert.True(PaneHeading.Of(PaneMode.Commit, "0c742a4 Subject", "0c742a4").IsComparing);
        Assert.True(PaneHeading.Of(PaneMode.CommitFile, "a/b.st @ 0c742a4", "0c742a4", "a/b.st").IsComparing);

        Assert.False(PaneHeading.Of(PaneMode.File, "a/b.st").IsComparing);
        Assert.False(PaneHeading.Of(PaneMode.Project, "plc/Machine.project").IsComparing);
        Assert.False(PaneHeading.None.IsComparing);
    }

    [Fact]
    public void A_commit_is_titled_by_its_subject_and_placed_by_its_id()
    {
        var heading = PaneHeading.Of(
            PaneMode.Commit, "0c742a4 The Rendered view draws a file as itself", "0c742a4");

        Assert.Equal("COMMIT", heading.Label);
        Assert.Equal("The Rendered view draws a file as itself", heading.Title);
        Assert.Equal("0c742a4", heading.Subtitle);
    }

    [Fact]
    public void A_file_in_a_commit_says_which_commit_beside_its_folder()
    {
        // The header's old job, kept: without the id this reads exactly like the working-tree
        // diff of the same file, which is the confusion a reviewer must not make.
        var heading = PaneHeading.Of(
            PaneMode.CommitFile, "docs/STATUS.md @ 0c742a4", "0c742a4", "docs/STATUS.md");

        Assert.Equal("STATUS.md", heading.Title);
        Assert.Equal("docs @ 0c742a4", heading.Subtitle);
    }

    [Fact]
    public void A_file_at_the_root_has_no_folder()
    {
        Assert.Equal(string.Empty, PaneHeading.Of(PaneMode.Diff, "README.md").Subtitle);
        Assert.Equal("@ abc1234",
            PaneHeading.Of(PaneMode.CommitFile, "README.md @ abc1234", "abc1234", "README.md").Subtitle);
    }

    [Fact]
    public void An_empty_pane_keeps_the_label_and_names_nothing()
    {
        Assert.Equal(PaneHeading.None, PaneHeading.Of(PaneMode.Diff, string.Empty));
        Assert.Equal("DIFF", PaneHeading.None.Label);
    }

    // ------------------------------------------------------------- through the shell

    [Fact]
    public async Task A_changed_file_reads_as_a_diff_and_an_unchanged_one_as_a_file()
    {
        // The two branches of the one route: ShowPathAsync is asked for a diff either way,
        // and only the file on disk decides which the pane ends up showing.
        using var repo = new TempRepo(identity: true, unstagedFiles: 1);
        File.WriteAllText(Path.Combine(repo.Path, "clean.st"), "PROGRAM Clean\nEND_PROGRAM\n");
        repo.Git("add clean.st");
        repo.Git("commit -q -m clean");

        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);

        await shell.ShowWorkingTreePathAsync("file0.st");

        Assert.Equal(PaneMode.Diff, shell.PaneMode);
        Assert.Equal("DIFF", shell.Heading.Label);
        Assert.Equal("file0.st", shell.Heading.Title);
        Assert.True(shell.Heading.IsComparing);

        await shell.ShowWorkingTreePathAsync("clean.st");

        Assert.Equal(PaneMode.File, shell.PaneMode);
        Assert.Equal("FILE", shell.Heading.Label);
        Assert.Equal("clean.st", shell.Heading.Title);
        Assert.False(shell.Heading.IsComparing);
    }
}
