using System.IO;
using Versio.App.ViewModels;
using Versio.Core.Iec;
using Versio.Core.Projects;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A changed file's Rendered tab says what changed, as Raw does.
/// </summary>
/// <remarks>
/// The owner, 2026-10-10, on an FBS struct whose default value had changed: the tree said M and
/// the Rendered tab drew the file as if untouched. That held for every source the Rendered tab
/// draws, on every vendor, so this goes through a loose file that belongs to no project at all:
/// marking is the pane's job, not a format's.
/// </remarks>
public sealed class RenderedSourceMarkTests
{
    private static IReadOnlyList<string> Marked(IReadOnlyList<SourceRow> rows) =>
        rows.Select(r => r switch
        {
            SourceRemovedLine x => "- " + x.Text,
            SourceCodeLine l => (l.Change == SourceChange.Added ? "+ " : "  ") + l.Text,
            _ => "?",
        }).ToList();

    [Fact]
    public async Task A_changed_line_is_drawn_struck_where_it_stood_with_its_replacement_tinted()
    {
        using var repo = new TempRepo(identity: true);
        var file = Path.Combine(repo.Path, "Settings.st");
        File.WriteAllText(file, "A := 1;\nB := 2;\nC := 3;\n");
        repo.Git("add -A");
        repo.Git("commit -q -m settings");

        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);

        File.WriteAllText(file, "A := 1;\nB := 20;\nC := 3;\n");
        await shell.RefreshAsync();

        shell.Sync.SelectedNode = Assert.Single(shell.Sync.Tree, r => r.Node.Kind == ProjectNodeKind.File && r.Node.Path == "Settings.st");

        await Eventually.Until(
            () => shell.RenderedContent is SourceRender s && s.Rows.OfType<SourceRemovedLine>().Any(),
            "the rendered source is marked");
        Assert.Equal(["  A := 1;", "- B := 2;", "+ B := 20;", "  C := 3;"], Marked(((SourceRender)shell.RenderedContent!).Rows));
    }
}
