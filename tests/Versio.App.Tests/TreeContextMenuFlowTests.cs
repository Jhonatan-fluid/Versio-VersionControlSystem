using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The tree's context-menu verbs — Stage and Discard — driven through the shell methods the
/// menu's handlers call, against a real repository.
/// </summary>
/// <remarks>
/// Reveal and Copy path are deliberately not flow-tested: one launches Explorer and the
/// other writes the user's clipboard, and a test suite that does either as a side effect is
/// worse than the coverage is worth. Their availability flags and the handlers' shape are
/// covered; the two calls themselves are a <c>Process.Start</c> and a
/// <c>Clipboard.SetText</c> with logging around them.
/// </remarks>
public sealed class TreeContextMenuFlowTests
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
          "objectCount": 3,
          "objects": [
            { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_FULL",
              "path": "App/FB_Motor.st", "hash": "sha256:0", "bytes": 1 },
            { "guid": "g2", "name": "Glob_Var", "type": "GVL", "form": "GVL_BOTH",
              "path": "App/Glob_Var.gvl.st", "hash": "sha256:0", "bytes": 1 },
            { "guid": "g2", "name": "Glob_Var", "type": "GVL", "form": "GVL_NATIVE",
              "path": "App/Glob_Var.gvl.xml", "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [],
          "skipped": []
        }
        """);

        File.WriteAllText(Path.Combine(export, "FB_Motor.st"), "FUNCTION_BLOCK FB_Motor\n");
        File.WriteAllText(Path.Combine(export, "Glob_Var.gvl.st"), "VAR_GLOBAL\n");
        File.WriteAllText(Path.Combine(export, "Glob_Var.gvl.xml"), "<gvl />\n");
    }

    private static TreeRow FindRow(IEnumerable<TreeRow> rows, string name)
    {
        foreach (var row in rows)
        {
            if (row.Name == name)
            {
                return row;
            }

            if (Search(row, name) is { } found)
            {
                return found;
            }
        }

        throw new InvalidOperationException($"no row named '{name}' in the tree");
    }

    private static TreeRow? Search(TreeRow row, string name)
    {
        foreach (var child in row.Children)
        {
            if (child.Name == name)
            {
                return child;
            }

            if (Search(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [Theory]
    [InlineData(ProjectNodeKind.Object, ChangeKind.Modified, true)]
    [InlineData(ProjectNodeKind.File, ChangeKind.Untracked, true)]
    [InlineData(ProjectNodeKind.Object, ChangeKind.Unmodified, false)]
    [InlineData(ProjectNodeKind.Library, ChangeKind.Modified, false)]
    [InlineData(ProjectNodeKind.Task, ChangeKind.Modified, false)]
    [InlineData(ProjectNodeKind.Device, ChangeKind.Modified, false)]
    [InlineData(ProjectNodeKind.Folder, ChangeKind.Unmodified, false)]
    [InlineData(ProjectNodeKind.Project, ChangeKind.Modified, false)]
    public void Stage_and_discard_are_offered_only_for_a_rows_own_changed_files(
        ProjectNodeKind kind, ChangeKind status, bool expected)
    {
        // Library, task and device rows share their catalog's file: "stage this reference"
        // would silently stage every change in the file beside it, so the manager's row is
        // where that verb honestly lives.
        var row = new TreeRow(new ProjectTreeNode
        {
            Name = "N",
            Path = "p",
            Kind = kind,
            Status = status,
        }, depth: 0);

        Assert.Equal(expected, row.CanStageOrDiscard);
    }

    [Fact]
    public async Task Staging_a_node_stages_its_files()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        await shell.StageNodeAsync(FindRow(shell.Sync.Tree, "Glob_Var"));

        // Both layers: the row is the object, and the object is both files.
        Assert.Contains(shell.Pending, r => r.Path == "plc/Machine/App/Glob_Var.gvl.st" && r.IsStaged);
        Assert.Contains(shell.Pending, r => r.Path == "plc/Machine/App/Glob_Var.gvl.xml" && r.IsStaged);
        Assert.Contains(shell.Pending, r => r.Path == "plc/Machine/App/FB_Motor.st" && !r.IsStaged);
    }

    [Fact]
    public async Task Discarding_a_node_restores_its_file_after_a_confirmation_naming_it()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);
        repo.Git("add .");
        repo.Git("commit -qm exported");

        var file = Path.Combine(repo.Path, "plc", "Machine", "App", "FB_Motor.st");
        File.WriteAllText(file, "FUNCTION_BLOCK FB_Motor // edited\n");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        string? asked = null;
        shell.Asked().Destructive = p =>
        {
            asked = p.Body;
            return true;
        };

        await shell.DiscardNodeAsync(FindRow(shell.Sync.Tree, "FB_Motor"));

        Assert.Contains("plc/Machine/App/FB_Motor.st", asked);
        Assert.Equal("FUNCTION_BLOCK FB_Motor\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task Discarding_names_only_the_files_that_actually_changed()
    {
        // The object's two layers can differ one-and-not-the-other. Handing git the clean
        // .xml as well would be harmless today and misleading in the dialog forever.
        using var repo = new TempRepo();
        GivenATrackedProject(repo);
        repo.Git("add .");
        repo.Git("commit -qm exported");

        File.WriteAllText(
            Path.Combine(repo.Path, "plc", "Machine", "App", "Glob_Var.gvl.st"),
            "VAR_GLOBAL // edited\n");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        string? asked = null;
        shell.Asked().Destructive = p =>
        {
            asked = p.Body;
            return false;
        };

        await shell.DiscardNodeAsync(FindRow(shell.Sync.Tree, "Glob_Var"));

        Assert.Contains("Glob_Var.gvl.st", asked);
        Assert.DoesNotContain("Glob_Var.gvl.xml", asked);
    }

    [Fact]
    public async Task Discarding_an_untracked_node_is_refused_with_the_reason()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        var prompted = false;
        shell.Asked().Destructive = _ => prompted = true;

        await shell.DiscardNodeAsync(FindRow(shell.Sync.Tree, "FB_Motor"));

        // Not asked, not attempted: git has no copy to restore, and a dialog about an
        // operation that cannot happen would be worse than the sentence.
        Assert.False(prompted);
        Assert.Contains("not tracked", shell.StatusSummary);
        Assert.True(File.Exists(
            Path.Combine(repo.Path, "plc", "Machine", "App", "FB_Motor.st")));
    }
}
