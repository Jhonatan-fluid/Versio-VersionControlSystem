using System.IO;
using System.Windows;
using Versio.App.ViewModels;
using Versio.App.Views;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// An HTML file has three views - Raw, Rendered and Web page - and exactly one is on screen.
/// </summary>
/// <remarks>
/// Asked for on 2026-10-04 in those three words. The browser itself cannot run in this suite
/// (no desktop, no message pump a WebView2 accepts), so what is pinned here is everything
/// around it: which files offer the page, that the three chips can never show two views or
/// none, that the page is never the default, and that the view does not start a browser
/// while nobody is looking at it. The page itself is the owner's eyeball.
/// </remarks>
public sealed class HtmlPaneFlowTests
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

    private static async Task<ShellViewModel> SelectedHtml(TempRepo repo, string relative = "site/index.html")
    {
        var path = Path.Combine(repo.Path, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<html><body><h1>Hi</h1></body></html>");

        var shell = Shell();
        await shell.OpenAsync(repo.Path);
        shell.Sync.SelectedNode = await Row(shell, relative);
        return shell;
    }

    private static int Showing(ShellViewModel shell) =>
        (shell.ShowRawPane ? 1 : 0) + (shell.ShowRenderedPane ? 1 : 0) + (shell.ShowWebPagePane ? 1 : 0);

    [Fact]
    public async Task An_html_file_offers_all_three_views_and_opens_on_none_of_them_as_a_page()
    {
        using var repo = new TempRepo();
        using var shell = await SelectedHtml(repo);

        Assert.True(shell.HasRenderedView, "the highlighted source is the Rendered view");
        Assert.IsType<SourceRender>(shell.RenderedContent);
        Assert.True(shell.HasWebPage);
        Assert.Equal(Path.Combine(repo.Path, "site", "index.html"), shell.WebPagePath);

        // Running repository HTML is a choice, never what a click on the row does.
        Assert.False(shell.ShowWebPagePane);
        Assert.Equal(1, Showing(shell));
    }

    [Fact]
    public async Task The_three_chips_always_show_exactly_one_view()
    {
        using var repo = new TempRepo();
        using var shell = await SelectedHtml(repo);

        shell.ShowWebPage = true;
        Assert.True(shell.ShowWebPagePane);
        Assert.False(shell.ShowRendered);
        Assert.False(shell.ShowRawTab);
        Assert.Equal(1, Showing(shell));

        shell.ShowRendered = true;
        Assert.True(shell.ShowRenderedPane);
        Assert.False(shell.ShowWebPage);
        Assert.Equal(1, Showing(shell));

        shell.ShowRawTab = true;
        Assert.True(shell.ShowRawPane);
        Assert.Equal(1, Showing(shell));

        // Unpressing the active page chip lands on Raw, not on nothing.
        shell.ShowWebPage = true;
        shell.ShowWebPage = false;
        Assert.True(shell.ShowRawPane);
        Assert.Equal(1, Showing(shell));
    }

    [Fact]
    public async Task Moving_to_another_file_drops_the_page()
    {
        // The setter every selection goes through must not carry the Web tab to a row that
        // has no page: that would leave no chip pressed over a diff.
        using var repo = new TempRepo();
        File.WriteAllText(Path.Combine(repo.Path, "notes.txt"), "plain");
        using var shell = await SelectedHtml(repo);

        shell.ShowWebPage = true;
        shell.Sync.SelectedNode = await Row(shell, "notes.txt");

        Assert.False(shell.HasWebPage);
        Assert.False(shell.ShowWebPage);
        Assert.Equal(1, Showing(shell));
    }

    [Theory]
    [InlineData("page.htm")]
    [InlineData("Page.HTML")]
    public async Task Both_extensions_in_any_case_offer_the_page(string name)
    {
        using var repo = new TempRepo();
        using var shell = await SelectedHtml(repo, name);
        Assert.True(shell.HasWebPage);
    }

    [Fact]
    public async Task Other_markup_does_not_offer_the_page()
    {
        using var repo = new TempRepo();
        File.WriteAllText(Path.Combine(repo.Path, "drawing.svg"), "<svg/>");
        using var shell = Shell();
        await shell.OpenAsync(repo.Path);

        shell.Sync.SelectedNode = await Row(shell, "drawing.svg");

        Assert.False(shell.HasWebPage);
    }

    [Fact]
    public void The_page_view_starts_no_browser_while_hidden()
    {
        // A WebView2 is a browser process tree. Built at startup in every window it would be
        // paid for by every session that never opens an HTML file.
        StaHarness.Run(() =>
        {
            var view = new WebPageView { Visibility = Visibility.Collapsed, FilePath = @"C:\x\index.html" };

            Assert.Null(view.Child);
            Assert.Null(view.ShownPath);
        });
    }
}
