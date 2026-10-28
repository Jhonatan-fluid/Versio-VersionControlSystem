using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Iec;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A commit's tree, picked, reaches the shared pane — and renders there as the Project panel
/// renders a changed object, with "working tree against HEAD" replaced by "commit against its
/// first parent".
/// </summary>
/// <remarks>
/// <para>
/// The History panel raises <c>TreeSelected</c> and the shell renders it; the subscription
/// between them is one line in the shell's constructor, and a line like that is the most
/// likely kind of change to lose its coverage silently (AGENTS.md: three moved subscriptions
/// left the whole suite green). The first test is what goes red when it is deleted.
/// </para>
/// <para>
/// The rest drive a real repository through three commits of one tracked project: the export,
/// an edit to a block, and its deletion. Each is what the owner would see clicking that row —
/// the table with the added member green, the code with the added line tinted, the deleted
/// block struck — and each would have shown a raw patch until 2026-10-27.
/// </para>
/// </remarks>
public sealed class HistoryTreeFlowTests
{
    private const string MotorPath = "plc/Machine/App/FB_Motor.st";

    /// <summary>The exporter's shape: declaration, the implementation marker, the body.</summary>
    private const string MotorV1 =
        "FUNCTION_BLOCK FB_Motor\nVAR_INPUT\n\tEnable : BOOL;\nEND_VAR\n\n// --- BEGIN IMPLEMENTATION ---\n\nIF Enable THEN\nEND_IF\n";

    private const string MotorV2 =
        "FUNCTION_BLOCK FB_Motor\nVAR_INPUT\n\tEnable : BOOL;\n\tSpeed : INT;\nEND_VAR\n\n// --- BEGIN IMPLEMENTATION ---\n\nIF Enable THEN\n\tSpeed := 1;\nEND_IF\n";

    [Fact]
    public async Task Picking_a_row_of_a_commits_tree_shows_that_file_at_that_commit()
    {
        using var repo = new TempRepo(unstagedFiles: 1);
        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);
        var git = new CountingGitClient();
        shell.History.Attach(git);
        shell.ActivePanel = ShellPanel.History;

        var commit = shell.History.Commits[0];
        commit.IsExpanded = true;
        var row = Assert.IsType<TreeRow>(commit.Children[0]);
        Assert.Equal("a.st", row.Node.Path);

        // What a click does, in the order the view does it.
        row.IsSelected = true;
        shell.History.Select(row);

        // The header is set before git is asked, so this is immediate; the commit is named in
        // it, which is what keeps a commit's diff apart from the working tree's of the same file.
        Assert.Equal(PaneMode.CommitFile, shell.PaneMode);
        Assert.Equal($"a.st @ {commit.ShortId}", shell.SelectedDiffPath);
        Assert.False(shell.CanStageHunks, "a commit is history; nothing in it can be staged");

        await Eventually.Until(() => shell.SelectedDiff.Length > 0, "the commit's patch to arrive");

        // And leaving the panel lets go of the row, as it does for every other tree.
        shell.ActivePanel = ShellPanel.Git;
        Assert.False(row.IsSelected, "the tree row is still highlighted, so it cannot be clicked again");
    }

    [Fact]
    public async Task A_changed_block_renders_its_table_and_code_marked_against_the_parent()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo, MotorV1);
        Commit(repo, "export");
        File.WriteAllText(Path.Combine(repo.Path, MotorPath.Replace('/', Path.DirectorySeparatorChar)), MotorV2);
        Commit(repo, "motor");

        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);
        shell.ActivePanel = ShellPanel.History;

        var top = await CommitAt(shell, 0);
        Assert.Equal("motor", top.Subject);
        var row = await RowNamed(top, "FB_Motor");
        Assert.Equal(("POU", "M"), (row.TypeLabel, row.Glyph));

        row.IsSelected = true;
        shell.History.Select(row);

        Assert.Equal(PaneMode.CommitFile, shell.PaneMode);
        Assert.Equal($"{MotorPath} @ {top.ShortId}", shell.SelectedDiffPath);

        // The table with the new member added, the code with the new line tinted and the old
        // line left alone: the Project panel's marks, against the parent rather than HEAD.
        await Eventually.Until(
            () => shell.RenderedContent is InterfaceRender { Table: { } t, Source: { } s }
                  && t.Members.Any(m => m.Name == "Speed" && m.Change == MemberChange.Added)
                  && s.Rows.OfType<SourceCodeLine>().Any(l => l.Change == SourceChange.Added),
            "the block to be marked against the parent commit");

        var render = Assert.IsType<InterfaceRender>(shell.RenderedContent);
        Assert.Equal("FB_Motor", render.Table.Name);
        Assert.Contains(render.Table.Members, m => m.Name == "Enable" && m.Change == MemberChange.None);
        var lines = render.Source!.Rows.OfType<SourceCodeLine>().ToList();
        Assert.Contains(lines, l => l.Change == SourceChange.Added && l.Text.Contains("Speed := 1", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Change == SourceChange.None && l.Text.Contains("IF Enable", StringComparison.Ordinal));

        // Raw is the commit's own patch, and nothing in it can be staged.
        await Eventually.Until(() => shell.SelectedDiff.StartsWith("diff --git", StringComparison.Ordinal), "the commit's patch");
        Assert.False(shell.CanStageHunks);
    }

    [Fact]
    public async Task The_first_commits_block_renders_with_every_member_and_line_added()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo, MotorV1);
        Commit(repo, "export");
        File.WriteAllText(Path.Combine(repo.Path, MotorPath.Replace('/', Path.DirectorySeparatorChar)), MotorV2);
        Commit(repo, "motor");

        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);
        shell.ActivePanel = ShellPanel.History;

        // The root commit: its parent is nothing, so everything it holds is new.
        var first = await CommitAt(shell, 1);
        Assert.Equal("export", first.Subject);
        var row = await RowNamed(first, "FB_Motor");
        Assert.Equal("A", row.Glyph);

        shell.History.Select(row);

        // Every member, not every row: a section heading (VAR_INPUT) is a row the diff leaves
        // unmarked by design, so the members under it are what read as added.
        await Eventually.Until(
            () => shell.RenderedContent is InterfaceRender { Table: { } t, Source: { } s }
                  && t.Members.Any(m => !m.IsSectionHeading)
                  && t.Members.Where(m => !m.IsSectionHeading).All(m => m.Change == MemberChange.Added)
                  && s.Rows.OfType<SourceCodeLine>().Any() && s.Rows.OfType<SourceCodeLine>().All(l => l.Change == SourceChange.Added),
            "every member and line to read as added");
    }

    [Fact]
    public async Task A_file_the_commit_deleted_is_drawn_from_the_parent_with_every_line_struck()
    {
        using var repo = new TempRepo();
        GivenATrackedProject(repo, MotorV1);
        Commit(repo, "export");
        File.Delete(Path.Combine(repo.Path, MotorPath.Replace('/', Path.DirectorySeparatorChar)));
        GivenATrackedProject(repo, motor: null);
        Commit(repo, "remove");

        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);
        shell.ActivePanel = ShellPanel.History;

        var top = await CommitAt(shell, 0);
        Assert.Equal("remove", top.Subject);
        // Named by its path under the export root: the manifest no longer lists it, so the
        // tree has no object name for it yet (that is the next commit's work).
        var row = await RowNamed(top, "App/FB_Motor.st");
        Assert.True(row.IsDeleted);

        shell.History.Select(row);

        // The web viewer's rule: a deleted object is still drawn as what it was, every row
        // struck. The parent's copy is a block, so it draws as its table over its code, with
        // every member removed and no code line left standing.
        await Eventually.Until(
            () => SourceRows(shell.RenderedContent) is { } rows
                  && rows.OfType<SourceRemovedLine>().Any()
                  && !rows.OfType<SourceCodeLine>().Any(),
            "the deleted block to be drawn struck",
            () => $"rendered: {shell.RenderedContent?.GetType().Name ?? "nothing"}");
        var render = Assert.IsType<InterfaceRender>(shell.RenderedContent);
        Assert.Equal("FB_Motor", render.Table.Name);
        Assert.All(render.Table.Members.Where(m => !m.IsSectionHeading), m => Assert.Equal(MemberChange.Removed, m.Change));
        Assert.Contains(SourceRows(render)!.OfType<SourceRemovedLine>(), l => l.Text.Contains("IF Enable", StringComparison.Ordinal));
    }

    private static IReadOnlyList<SourceRow>? SourceRows(object? rendered) => rendered switch
    {
        SourceRender s => s.Rows,
        InterfaceRender { Source: { } s } => s.Rows,
        _ => null,
    };

    // ------------------------------------------------------------------ the repository

    /// <summary>
    /// A repository with one tracked project whose export tree is on disk: <c>repo.json</c>, the
    /// binary, the manifest, the files. Written as files rather than through the adoption path,
    /// as <c>PlcAwareNamingFlowTests</c> does: adopting runs a toolchain probe, and these tests
    /// are about what a commit of it looks like.
    /// </summary>
    /// <param name="motor">The block's text, or null for a manifest that no longer lists it.</param>
    private static void GivenATrackedProject(TempRepo repo, string? motor)
    {
        var versio = Path.Combine(repo.Path, ".versio");
        Directory.CreateDirectory(versio);
        File.WriteAllText(Path.Combine(versio, "repo.json"), """
        {
          "schema": 1,
          "bindings": [
            { "id": "b1", "projectPath": "plc/Machine.project", "exportPath": "plc/Machine", "enabled": true }
          ]
        }
        """);

        var export = Path.Combine(repo.Path, "plc", "Machine", "App");
        Directory.CreateDirectory(export);
        File.WriteAllBytes(Path.Combine(repo.Path, "plc", "Machine.project"), [0x00, 0x01, 0x02, 0x03]);

        var motorEntry = motor is null
            ? string.Empty
            : """
              { "guid": "g1", "name": "FB_Motor", "type": "POU", "form": "ST_FULL",
                "path": "App/FB_Motor.st", "hash": "sha256:0", "bytes": 1 },
              """;
        File.WriteAllText(Path.Combine(repo.Path, "plc", "Machine", "_manifest.json"), $$"""
        {
          "schema": 4,
          "bindingId": "b1",
          "project": "Machine.project",
          "objectCount": 2,
          "objects": [
            {{motorEntry}}
            { "guid": "g2", "name": "Glob_Var", "type": "GVL", "form": "GVL_BOTH",
              "path": "App/Glob_Var.gvl.st", "hash": "sha256:0", "bytes": 1 },
            { "guid": "g2", "name": "Glob_Var", "type": "GVL", "form": "GVL_BOTH",
              "path": "App/Glob_Var.gvl.xml", "hash": "sha256:0", "bytes": 1 }
          ],
          "warnings": [],
          "skipped": []
        }
        """);

        if (motor is not null)
        {
            File.WriteAllText(Path.Combine(export, "FB_Motor.st"), motor);
        }

        File.WriteAllText(Path.Combine(export, "Glob_Var.gvl.st"), "VAR_GLOBAL\n\tRunning : BOOL;\nEND_VAR\n");
        File.WriteAllText(Path.Combine(export, "Glob_Var.gvl.xml"), "<gvl />\n");
    }

    private static void Commit(TempRepo repo, string message)
    {
        repo.Git("add -A");
        repo.Git($"commit -qm {message}");
    }

    private static async Task<CommitRow> CommitAt(ShellViewModel shell, int index)
    {
        await Eventually.Until(() => shell.History.Commits.Count > index, $"commit {index} to be listed");
        return shell.History.Commits[index];
    }

    /// <summary>Expand a commit and wait for the row named <paramref name="name"/> in its tree.</summary>
    private static async Task<TreeRow> RowNamed(CommitRow commit, string name)
    {
        commit.IsExpanded = true;
        TreeRow? found = null;
        await Eventually.Until(
            () => (found = Flatten(commit.Children.OfType<TreeRow>()).FirstOrDefault(r => r.Name == name)) is not null,
            $"a row named '{name}' under {commit.ShortId}");
        return found!;
    }

    private static IEnumerable<TreeRow> Flatten(IEnumerable<TreeRow> rows) =>
        rows.SelectMany(r => Flatten(r.Children).Prepend(r));
}
