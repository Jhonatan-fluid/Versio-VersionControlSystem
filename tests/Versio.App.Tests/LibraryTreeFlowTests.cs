using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Git;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Library reference rows, entered from where the user enters them: a real repository,
/// a real catalog on disk, and the shell's own tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every Core test of this feature would pass with the shell wiring deleted.</b> The
/// builder hangs references under managers and marks them against a baseline, and all of it
/// is covered — but nothing in Core says the shell ever reads a catalog, ever fetches HEAD's
/// copy, or ever hands either to the builder. Deleting <c>ReadLibraryCatalogs</c>'s call or
/// the baseline fetch must turn this file red, and nothing else.
/// </para>
/// <para>
/// The baseline test drives the full pair: the catalog as HEAD has it via a real commit and
/// a real <c>git show</c>, the working tree's copy via an edit, and the mark on exactly the
/// reference whose pin moved.
/// </para>
/// </remarks>
public sealed class LibraryTreeFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    private const string Catalog = """
        schema: 1
        vendor: codesys
        manager: "Library Manager"
        path: "App/Library Manager"
        referenceCount: 2
        references:
          - name: "#Standard"
            namespace: Standard
            placeholder: Standard
            defaultResolution: "Standard, * (System)"
            redirection: "Standard, 3.5.19.0 (System)"
            systemLibrary: false
          - name: "#VisuElems"
            namespace: VisuElems
            placeholder: VisuElems
            defaultResolution: "VisuElems, * (System)"
            systemLibrary: true
        """;

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
          "objectCount": 2,
          "objects": [
            { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_FULL",
              "path": "App/FB_Motor.st", "hash": "sha256:0", "bytes": 1 },
            { "guid": "g3", "name": "Library Manager", "type": "LIBRARY_MANAGER",
              "form": "LIBRARIES", "path": "App/Library Manager.libraries.yaml",
              "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [],
          "skipped": []
        }
        """);

        File.WriteAllText(Path.Combine(export, "FB_Motor.st"), "FUNCTION_BLOCK FB_Motor\n");
        File.WriteAllText(
            Path.Combine(export, "Library Manager.libraries.yaml"), Catalog + "\n");
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

    [Fact]
    public async Task The_references_reach_the_panel_as_rows_under_their_manager()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        // The whole chain: repo.json -> manifest -> ReadLibraryCatalogs -> WorkspaceProject
        // -> the builder -> the rows. Removing any link leaves the manager childless.
        var manager = FindRow(shell.Sync.Tree, "Library Manager");
        Assert.Equal(["Standard", "VisuElems"], manager.Children.Select(c => c.Name));

        // The chosen library's pin, and the system flag on the one nobody chose.
        Assert.Equal("Standard, 3.5.19.0 (System)", manager.Children[0].Annotation);
        Assert.EndsWith("· system", manager.Children[1].Annotation);
    }

    [Fact]
    public async Task A_version_bump_in_the_working_tree_marks_that_reference()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo);
        repo.Git("add .");
        repo.Git("commit -qm exported");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        File.WriteAllText(
            Path.Combine(repo.Path, "plc", "Machine", "App", "Library Manager.libraries.yaml"),
            Catalog.Replace("3.5.19.0", "3.5.22.0") + "\n");
        await shell.RefreshAsync();

        // HEAD's copy came back through a real `git show`; the mark sits on the reference
        // whose pin moved and on nothing else.
        var manager = FindRow(shell.Sync.Tree, "Library Manager");
        Assert.Equal(ChangeKind.Modified,
            manager.Children.Single(c => c.Name == "Standard").Node.Status);
        Assert.Equal(ChangeKind.Unmodified,
            manager.Children.Single(c => c.Name == "VisuElems").Node.Status);
    }
}
