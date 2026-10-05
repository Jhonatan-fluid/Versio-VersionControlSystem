using System.IO;
using System.Windows;
using System.Windows.Documents;
using Versio.App.ViewModels;
using Versio.App.Views;
using Versio.Core.Text;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A <c>.md</c> file's Rendered tab is a page, and its links go somewhere.
/// </summary>
/// <remarks>
/// <para>
/// Seen 2026-10-04: <c>docs/outpost/PLAN.md</c> selected in the Project panel, Rendered
/// pressed, and the pane drew the raw text with line numbers - the Markdown fell through to
/// the ST source layout, because nothing in <c>BuildRenderedContent</c> knew what a document
/// was. The Markdown arm is a pure addition in front of <c>SourceRender</c>, so deleting it
/// would leave everything else green; the first test here is what goes red.
/// </para>
/// </remarks>
[Collection("theme")]
public sealed class MarkdownPaneFlowTests
{
    private static ShellViewModel Shell() => TestShell.Build();

    private static async Task<TreeRow> Row(ShellViewModel shell, string name)
    {
        TreeRow? found = null;
        await Eventually.Until(
            () => (found = shell.Sync.Tree.FirstOrDefault(r => r.Name == name)) is not null,
            $"a row named '{name}' appeared in the tree");
        return found!;
    }

    private static void Write(TempRepo repo, string relative, string text)
    {
        var path = Path.Combine(repo.Path, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task A_markdown_file_renders_as_a_document_not_as_source()
    {
        using var repo = new TempRepo();
        Write(repo, "docs/PLAN.md", "# Plan\n\n| Key | Value |\n|---|---|\n| a | b |\n\n- one\n- two\n");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = await Row(shell, "docs/PLAN.md");

        var page = Assert.IsType<MarkdownRender>(shell.RenderedContent);
        Assert.Equal("docs/PLAN.md", page.Path);
        Assert.Collection(page.Blocks,
            b => Assert.IsType<MdHeading>(b),
            b => Assert.IsType<MdTable>(b),
            b => Assert.IsType<MdList>(b));

        // A new file opens on its diff, as every changed file does; Rendered is one press away.
        Assert.True(shell.HasRenderedView);
    }

    [Fact]
    public async Task A_relative_link_opens_the_other_document_rendered()
    {
        using var repo = new TempRepo();
        Write(repo, "docs/outpost/PLAN.md", "See [status](../STATUS.md#next).");
        Write(repo, "docs/STATUS.md", "# Status\n");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Sync.SelectedNode = await Row(shell, "docs/outpost/PLAN.md");

        shell.OpenLinkCommand.Execute("../STATUS.md#next");

        var page = Assert.IsType<MarkdownRender>(shell.RenderedContent);
        Assert.Equal("docs/STATUS.md", page.Path);
        Assert.Equal("docs/STATUS.md", shell.SelectedDiffPath);
        Assert.True(shell.ShowRenderedPane);
    }

    [Theory]
    [InlineData("../../../../outside.md")]   // climbs out of the repository
    [InlineData("MISSING.md")]               // not there
    [InlineData("#a-heading")]               // in-page anchor
    [InlineData("Main.st")]                  // a file with its own row and renderer
    public async Task A_link_that_cannot_be_followed_leaves_the_page_where_it_was(string target)
    {
        using var repo = new TempRepo();
        Write(repo, "docs/PLAN.md", "text");
        Write(repo, "docs/Main.st", "PROGRAM Main\nEND_PROGRAM\n");

        using var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Sync.SelectedNode = await Row(shell, "docs/PLAN.md");
        var before = shell.RenderedContent;

        shell.OpenLinkCommand.Execute(target);

        Assert.Same(before, shell.RenderedContent);
        Assert.Equal("docs/PLAN.md", shell.SelectedDiffPath);
    }

    [Fact]
    public void The_page_is_built_escaped_and_walled_without_starting_a_browser()
    {
        // The browser cannot run in this suite; the page it would be handed can be read. What
        // matters: the document's own markup arrives as text, the security policy is there,
        // diagrams are in the form mermaid reads, and a hidden view starts no browser.
        StaHarness.Run(() =>
        {
            var view = new MarkdownPageView
            {
                Visibility = Visibility.Collapsed,
                Blocks = Markdown.Parse("# T\n\n<script>bad()</script>\n\n```mermaid\nflowchart TB\n a --> b\n```"),
            };

            var page = view.PageHtml;
            Assert.Contains("Content-Security-Policy", page, StringComparison.Ordinal);
            Assert.Contains($"script-src {MarkdownPage.Origin};", page, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>bad", page, StringComparison.Ordinal);
            Assert.Contains("<pre class=\"mermaid\">flowchart TB\n a --&gt; b</pre>", page, StringComparison.Ordinal);
            Assert.Null(view.Child);
        });
    }

    [Fact]
    public void A_theme_switch_rebuilds_the_page_in_the_new_colours()
    {
        // The page is HTML with the colours baked in; nothing about it is a DynamicResource.
        // The resource-referenced ThemeBrush is the only thing that notices a switch.
        StaHarness.Run(() =>
        {
            Versio.App.Theme.ThemeManager.Apply(Versio.App.Theme.AppTheme.Dark);
            // In a window, as in the app: WPF walks open windows to tell resource references
            // about a theme switch, and an element in no window is never told.
            var view = new MarkdownPageView { Visibility = Visibility.Collapsed, Blocks = Markdown.Parse("text") };
            var window = new Window { Content = view };
            var dark = view.PageHtml;

            Versio.App.Theme.ThemeManager.Apply(Versio.App.Theme.AppTheme.Light);
            var light = view.PageHtml;

            Assert.NotEqual(dark, light);
            Assert.Contains("data-mermaid-theme=\"dark\"", dark, StringComparison.Ordinal);
            Assert.Contains("data-mermaid-theme=\"default\"", light, StringComparison.Ordinal);

            window.Close();
            Versio.App.Theme.ThemeManager.Apply(Versio.App.Theme.AppTheme.System);
        });
    }

    [Fact]
    public void Mermaid_is_embedded_in_the_app()
    {
        // The page is not allowed a network, so a missing resource would be every diagram
        // silently drawn as nothing.
        using var stream = typeof(MarkdownPageView).Assembly
            .GetManifestResourceStream("Versio.App.Assets.mermaid.mermaid.min.js");

        Assert.NotNull(stream);
        Assert.True(stream!.Length > 1_000_000, "mermaid.min.js is embedded but implausibly small");
    }

    [Fact]
    public void The_view_draws_tables_lists_code_and_links_as_themselves()
    {
        // The half that is WPF: the view model can hand over the right blocks and the view
        // still draw them as paragraphs. This looks at the document it actually built.
        StaHarness.Run(() =>
        {
            var view = new MarkdownView
            {
                Blocks = Markdown.Parse(
                    "# T\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n1. x\n2. y\n\n```\ncode\n```\n\n[l](https://e.x)"),
            };

            var blocks = view.Document.Blocks.ToList();
            Assert.Collection(blocks,
                b => Assert.IsType<Paragraph>(b),
                b => Assert.Equal(2, Assert.IsType<Table>(b).Columns.Count),
                b => Assert.Equal(2, Assert.IsType<List>(b).ListItems.Count),
                b => Assert.IsType<Paragraph>(b),
                b => Assert.Single(Assert.IsType<Paragraph>(b).Inlines.OfType<Hyperlink>()));

            var link = ((Paragraph)blocks[^1]).Inlines.OfType<Hyperlink>().Single();
            Assert.Equal("https://e.x", link.CommandParameter);
        });
    }
}
