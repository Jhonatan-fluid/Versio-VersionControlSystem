using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The ahead/behind previews (GP-K §9d), against a real repository with a real upstream.
/// </summary>
/// <remarks>
/// <para>
/// The ranges are proven direction-correct in Core and against git in the integration
/// suite; what these add is the seam — the chips reach the client, the incoming half names
/// objects through the same index the panel uses, and the strip follows the counts it
/// explains rather than the clicks that opened it.
/// </para>
/// <para>
/// "Behind" is manufactured without a second clone: commit, push, then
/// <c>reset --hard HEAD~1</c> — the upstream keeps the commit, this machine no longer has
/// it, and the tracking ref already knows. The local mirror still holds the previous
/// version of the object, which is the realistic shape: incoming edits to objects this
/// machine has are exactly what the object-naming exists for.
/// </para>
/// </remarks>
public sealed class GitPreviewFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    private static void GivenATrackedProject(TempRepo repo)
    {
        var versio = Path.Combine(repo.Path, ".versio");
        Directory.CreateDirectory(versio);

        File.WriteAllText(Path.Combine(versio, "repo.json"), """
        {
          "schema": 1,
          "bindings": [
            {
              "id": "b1",
              "projectPath": "plc/Machine.project",
              "exportPath": "plc/Machine",
              "enabled": true
            }
          ]
        }
        """);

        var export = Path.Combine(repo.Path, "plc", "Machine", "App");
        Directory.CreateDirectory(export);

        File.WriteAllBytes(
            Path.Combine(repo.Path, "plc", "Machine.project"), [0x00, 0x01, 0x02, 0x03]);

        File.WriteAllText(Path.Combine(repo.Path, "plc", "Machine", "_manifest.json"), """
        {
          "schema": 4,
          "bindingId": "b1",
          "project": "Machine.project",
          "objectCount": 1,
          "objects": [
            { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_FULL",
              "path": "App/FB_Motor.st", "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [],
          "skipped": []
        }
        """);

        File.WriteAllText(Path.Combine(export, "FB_Motor.st"), "FUNCTION_BLOCK FB_Motor\n");
    }

    [Fact]
    public async Task The_ahead_chip_lists_the_commits_a_push_would_send()
    {
        using var repo = new TempRepo(published: true);
        repo.Commit("my new work");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        Assert.True(shell.Remote.HasAhead);

        await Eventually.Press(shell.Remote.ShowOutgoingCommand, "open the outgoing preview");

        Assert.True(shell.Remote.IsPreviewOpen);
        Assert.Equal("1 commit a push would send:", shell.Remote.PreviewTitle);
        Assert.Equal("my new work", Assert.Single(shell.Remote.PreviewCommits).Subject);

        // Outgoing carries no objects line: those changes were made here, and the commit
        // subjects already name them.
        Assert.Equal(string.Empty, shell.Remote.PreviewObjects);
    }

    [Fact]
    public async Task The_same_chip_again_closes_the_preview()
    {
        using var repo = new TempRepo(published: true);
        repo.Commit("my new work");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        await Eventually.Press(shell.Remote.ShowOutgoingCommand, "open");
        Assert.True(shell.Remote.IsPreviewOpen);

        await Eventually.Press(shell.Remote.ShowOutgoingCommand, "close");
        Assert.False(shell.Remote.IsPreviewOpen);
        Assert.Empty(shell.Remote.PreviewCommits);
    }

    [Fact]
    public async Task The_behind_chip_names_the_objects_a_pull_would_touch()
    {
        // The safety-relevant half: files arriving under an export tree will be imported
        // into a .project, and "which objects are about to move" deserves its answer
        // before the pull.
        using var repo = new TempRepo(published: true);
        GivenATrackedProject(repo);
        repo.Git("add .");
        repo.Git("commit -qm \"export the machine\"");
        repo.Git("push -q");

        File.WriteAllText(
            Path.Combine(repo.Path, "plc", "Machine", "App", "FB_Motor.st"),
            "FUNCTION_BLOCK FB_Motor // edited by a colleague\n");
        repo.Git("add .");
        repo.Git("commit -qm \"edit FB_Motor\"");
        repo.Git("push -q");
        repo.Git("reset --hard -q HEAD~1");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        Assert.True(shell.Remote.HasBehind);

        await Eventually.Press(shell.Remote.ShowIncomingCommand, "open the incoming preview");

        Assert.True(shell.Remote.IsPreviewOpen);
        Assert.Equal("1 commit a pull would bring:", shell.Remote.PreviewTitle);
        Assert.Equal("edit FB_Motor", Assert.Single(shell.Remote.PreviewCommits).Subject);

        // Through the same index and sentence the commit summary uses — the preview and
        // the panel cannot disagree about what the object is called.
        Assert.Equal("1 POU changed in Machine", shell.Remote.PreviewObjects);
    }

    [Fact]
    public async Task The_preview_follows_the_counts_it_explains()
    {
        using var repo = new TempRepo(published: true);
        repo.Commit("my new work");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        await Eventually.Press(shell.Remote.ShowOutgoingCommand, "open");
        Assert.True(shell.Remote.IsPreviewOpen);

        // The push it previewed happens; a strip still listing yesterday's commits under a
        // badge that is gone would be the stale-lists defect one panel over.
        shell.Remote.NoteStatus(new BranchInfo
        {
            Name = "main",
            Upstream = "origin/main",
            Ahead = 0,
            Behind = 0,
        });

        Assert.False(shell.Remote.IsPreviewOpen);
    }

    [Fact]
    public async Task The_preview_follows_a_branch_switch_even_when_the_counts_agree()
    {
        // NoteStatus compared Ahead/Behind alone, and only a change there reloaded or closed
        // the open preview. Two branches each one commit ahead of their own upstream have
        // identical counts, so checking out the other left the first branch's commit on
        // screen under the second branch's badge. The 2026-10-24 review's item 16.
        using var repo = new TempRepo(published: true);
        repo.Commit("on main");
        repo.Git("checkout -q -b topic origin/main");
        repo.Commit("on topic");
        repo.Git("checkout -q main");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        await Eventually.Press(shell.Remote.ShowOutgoingCommand, "open");
        Assert.Equal("on main", Assert.Single(shell.Remote.PreviewCommits).Subject);

        repo.Git("checkout -q topic");
        await shell.RefreshAsync();

        await Eventually.Until(
            () => !shell.Remote.IsPreviewOpen
                  || (shell.Remote.PreviewCommits.Count == 1
                      && shell.Remote.PreviewCommits[0].Subject == "on topic"),
            "the preview to follow the branch switch",
            () => string.Join(", ", shell.Remote.PreviewCommits.Select(c => c.Subject)));
    }
}
