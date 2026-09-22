using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Diff;
using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A whole commit opens with its message, its author and date, and what it touched.
/// </summary>
/// <remarks>
/// It opened as its patch alone (seen 2026-09-21): the body, where this repository keeps its
/// reasoning, was nowhere in the app.
/// </remarks>
[Collection("theme")]
public sealed class CommitHeadingTests
{
    private static readonly DiffDocument TwoFiles = UnifiedDiffParser.Parse(
        "diff --git a/a.st b/a.st\n--- a/a.st\n+++ b/a.st\n@@ -1 +1,2 @@\n x\n+y\n" +
        "diff --git a/b.md b/b.md\n--- a/b.md\n+++ b/b.md\n@@ -1 +1 @@\n-p\n+q\n");

    [Fact]
    public void The_body_is_the_message_after_its_subject()
    {
        var heading = CommitHeading.Of(null, "Subject line\n\nWhy it changed.\nAnd what it cost.\n",
            TwoFiles, ObjectIndex.Empty);

        Assert.Equal("Why it changed.\nAnd what it cost.", heading.Body);
        Assert.True(heading.HasBody);
    }

    [Fact]
    public void A_subject_only_message_has_no_body_to_show()
    {
        var heading = CommitHeading.Of(null, "Just a subject\n", TwoFiles, ObjectIndex.Empty);

        Assert.False(heading.HasBody);
    }

    [Fact]
    public void Files_and_lines_are_counted_from_the_patch()
    {
        var heading = CommitHeading.Of(null, null, TwoFiles, ObjectIndex.Empty);

        Assert.Equal("2 files · +2 −1", heading.Stats);

        // Nothing here is a PLC object, so there is no object sentence to show.
        Assert.False(heading.HasObjects);
    }

    [Fact]
    public void The_byline_names_the_author_and_a_local_date()
    {
        var when = new DateTimeOffset(2026, 9, 20, 20, 31, 0, TimeSpan.Zero);
        var commit = new CommitEntry
        {
            Id = "0c742a4", ShortId = "0c742a4", Subject = "s", Author = "someone", When = when,
        };

        var heading = CommitHeading.Of(commit, null, TwoFiles, ObjectIndex.Empty);

        Assert.Equal(
            $"someone · {when.ToLocalTime():yyyy-MM-dd HH:mm}", heading.Byline);
    }

    [Fact]
    public async Task Opening_a_commit_from_the_history_fills_the_header_and_leaving_empties_it()
    {
        using var repo = new TempRepo(identity: true);
        File.WriteAllText(Path.Combine(repo.Path, "a.st"), "x\n");
        repo.Git("add a.st");
        repo.Git("commit -q -m \"Add a\" -m \"Because the pump needed it.\"");

        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);

        // The history loads when its panel is shown, as it does for a person.
        shell.History.IsVisible = true;
        await Eventually.Until(() => shell.History.Commits.Count > 0, "history loaded");

        var row = shell.History.Commits.First(c => c.Entry.Subject == "Add a");
        shell.History.Select(row);
        await Eventually.Until(() => shell.CommitDetail is not null, "the commit header to fill");

        Assert.Equal("Because the pump needed it.", shell.CommitDetail!.Body);
        Assert.Equal("1 file · +1 −0", shell.CommitDetail.Stats);
        Assert.StartsWith(row.Entry.Author, shell.CommitDetail.Byline, StringComparison.Ordinal);

        // Any other view of the pane is not a whole commit, and has no header.
        await shell.ShowWorkingTreePathAsync("a.st");
        Assert.Null(shell.CommitDetail);
    }

    [Fact]
    public void The_window_shows_the_header_only_over_a_whole_commit()
    {
        StaHarness.Run(async () =>
        {
            using var repo = new TempRepo(identity: true);
            File.WriteAllText(Path.Combine(repo.Path, "a.st"), "x\n");
            repo.Git("add a.st");
            repo.Git("commit -q -m \"Add a\" -m \"Body.\"");

            using var shell = TestShell.Build();
            var window = new Versio.App.Views.MainWindow(shell);
            var content = Assert.IsAssignableFrom<System.Windows.FrameworkElement>(window.Content);
            await shell.OpenAsync(repo.Path);
            shell.History.IsVisible = true;
            await Eventually.Until(() => shell.History.Commits.Count > 0, "history loaded");

            var header = Descendants(content).OfType<System.Windows.Controls.Border>()
                .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Commit details");
            Assert.NotEqual(System.Windows.Visibility.Visible, header.Visibility);

            shell.History.Select(shell.History.Commits[0]);
            await Eventually.Until(() => shell.CommitDetail is not null, "the commit header to fill");

            Assert.Equal(System.Windows.Visibility.Visible, header.Visibility);
        });
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            yield return child;

            foreach (var further in Descendants(child))
            {
                yield return further;
            }
        }
    }
}
