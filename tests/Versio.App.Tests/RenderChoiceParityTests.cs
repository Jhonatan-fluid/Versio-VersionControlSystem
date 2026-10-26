using System.IO;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Versio.App.ViewModels;
using Versio.Core.Logging;
using Versio.Core.Projects;
using Versio.Web;
using Versio.Web.Rendering;
using Versio.Web.Repo;
using Versio.Web.Sources;
using Versio.Web.Tests;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// Both hosts, every object of every fixture project, one answer: the desktop pane and the web
/// viewer draw what <see cref="RenderChoice"/> chose, and they choose the same.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this cannot see</b> (review, 2026-10-24): it compares the <i>kind</i> chosen, not the
/// files, the code-under-table choice or the notice beside it; it runs a clean repository, so the
/// pull request path (<c>ViewService.RenderedCompare</c>, the one that was wrong on 2026-10-23) is
/// never compared; and the fixtures are small, so a layer the web refuses as too large (and the
/// desktop reads) never arises. Each is a divergence this test would stay green through.
/// </para>
/// <para>
/// <c>ViewServiceTests</c> claimed to pin "the desktop's order" and could not: it ran one host.
/// The two orders drifted for a year of commits — no project card, no catalogs, no husk rule and
/// no web page on the web, and the File-Based Storage objects that hold nothing drawn as raw
/// metadata in a pull request — and every suite stayed green (FINDINGS 2026-10-23). This runs
/// the real shell over a repository holding the fixtures and the web's <c>ViewService</c> over a
/// fake GitHub holding the same files, selects every row on both, and compares what each drew,
/// mapped to the kind it stands for. The desktop's answer is also checked against the choice it
/// recorded, so the desktop cannot choose one thing and draw another.
/// </para>
/// <para>
/// Every tracked export, the File-Based Storage project, the TIA and TwinCAT exports, a README
/// and a web page: a kind missing from the fixtures is a kind this test does not pin, so each
/// one the choice can return is asserted present at the end.
/// </para>
/// </remarks>
public sealed class RenderChoiceParityTests
{
    private const string Page = "docs/site/index.html";

    private static readonly string[] Fixtures =
    [
        ".versio/repo.json", "README.md",
        "fixtures/projects/MachineWithMembers", "fixtures/projects/RefrigeratorControl", "fixtures/projects/RefrigeratorControl_FBS",
        "fixtures/projects/TC3_PlcSample_reStrText/TC3_PlcSample_reStrText", "fixtures/projects/CleaningStation_V20_V21/CleaningStation_V20_V21",
    ];

    [Fact]
    public async Task Both_hosts_choose_the_same_drawing_for_every_fixture_object()
    {
        var files = FakeGitHub.FromDisk(Fixtures);
        files[Page] = Encoding.UTF8.GetBytes("<html><body><h1>Plant</h1></body></html>\n");

        var web = await WebChoices(files);
        var (desktop, recorded) = await DesktopChoices(files);

        // The same rows: a row one host has and the other does not is a tree difference, which
        // is a different test's business, but it would also hide a choice this one never made.
        Assert.Equal(web.Keys.Order(StringComparer.Ordinal), desktop.Keys.Order(StringComparer.Ordinal));

        var differing = web.Keys
            .Where(path => web[path] != desktop[path])
            .Select(path => $"{path}: web {web[path]}, desktop {desktop[path]}")
            .ToList();
        Assert.True(differing.Count == 0, "the hosts chose differently:" + Environment.NewLine + string.Join(Environment.NewLine, differing));

        // The desktop drew what it chose.
        var drawnOtherwise = desktop.Keys.Where(path => recorded[path] != desktop[path]).Select(path => $"{path}: chose {recorded[path]}, drew {desktop[path]}").ToList();
        Assert.True(drawnOtherwise.Count == 0, "the desktop drew something other than its choice:" + Environment.NewLine + string.Join(Environment.NewLine, drawnOtherwise));

        // Every kind the choice can return was exercised, so none is pinned vacuously.
        var seen = web.Values.ToHashSet();
        foreach (var kind in Enum.GetValues<RenderKind>().Where(k => k != RenderKind.Nothing))
        {
            Assert.True(seen.Contains(kind), $"no fixture row draws as {kind}; the fixtures no longer cover it");
        }

        Assert.True(web.Count > 300, $"only {web.Count} rows: the fixtures shrank");
    }

    // ---- the web -------------------------------------------------------------------------------

    private static async Task<Dictionary<string, RenderKind>> WebChoices(Dictionary<string, byte[]> files)
    {
        var github = new FakeGitHub();
        var repo = github.Add("acme", "plant");
        github.Commit(repo, "main", files);
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000_000 });
        var views = new ViewService(github, cache, Options.Create(new WebOptions()), NullLogger.Instance);
        var models = new RepoModelLoader(github, cache, NullLogger.Instance);
        var model = (await models.LoadAsync(RepoAccess.Anonymous, "acme", "plant", repo.Refs["main"], CancellationToken.None)).Value!;

        var choices = new Dictionary<string, RenderKind>(StringComparer.Ordinal);
        foreach (var node in RepoModel.Flatten(model.Nodes).Where(n => n.Kind != ProjectNodeKind.Folder))
        {
            var result = await views.ViewAsync(RepoAccess.Anonymous, model, node.Path, PaneView.Rendered, false, CancellationToken.None);
            choices[node.Path] = result.Ok ? WebKind(result.Value!.Kind) : RenderKind.Nothing;
        }

        return choices;
    }

    /// <summary>The web's answer, as the kind it stands for; what the page cannot draw is nothing.</summary>
    private static RenderKind WebKind(string kind) => kind switch
    {
        "project" => RenderKind.ProjectCard,
        "libraries" => RenderKind.LibraryCatalog,
        "tasks" => RenderKind.TaskCatalog,
        "task" => RenderKind.TaskCard,
        "devices" => RenderKind.DeviceCatalog,
        "device" => RenderKind.DeviceCard,
        "table" => RenderKind.MemberTable,
        "ladder" => RenderKind.Ladder,
        "note" => RenderKind.EmptyObject,
        "markdown" => RenderKind.Markdown,
        "webpage" => RenderKind.WebPage,
        "source" => RenderKind.Source,
        _ => RenderKind.Nothing,
    };

    // ---- the desktop ---------------------------------------------------------------------------

    private static async Task<(Dictionary<string, RenderKind> Drawn, Dictionary<string, RenderKind> Chosen)> DesktopChoices(Dictionary<string, byte[]> files)
    {
        using var repo = new TempRepo(identity: true);
        foreach (var (path, bytes) in files)
        {
            var target = Path.Combine(repo.Path, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }

        // Exactly the fake GitHub's files: the seed the temp repository was born with would be a
        // row only this host has.
        File.Delete(Path.Combine(repo.Path, "seed.txt"));
        repo.Git("-c core.autocrlf=false -c core.longpaths=true add -A");
        repo.Git("commit -q -m fixtures");

        using var shell = TestShell.Build();
        await shell.OpenAsync(repo.Path);
        await shell.RefreshAsync();

        // The Explorer: every row, a clean project included. The Project panel hides those.
        shell.ActivePanel = ShellPanel.Explorer;
        await shell.Explorer.RefreshAsync();

        var drawn = new Dictionary<string, RenderKind>(StringComparer.Ordinal);
        var chosen = new Dictionary<string, RenderKind>(StringComparer.Ordinal);
        // The shell writes .gitignore and .gitattributes into a repository it opens; the fake
        // GitHub holds only what was committed, so those two rows are this host's alone.
        foreach (var row in Rows(shell.Explorer.Tree).Where(r => r.Node.Kind != ProjectNodeKind.Folder && r.Node.Path is not (".gitignore" or ".gitattributes")))
        {
            shell.Explorer.SelectedNode = row;
            drawn[row.Node.Path] = DesktopKind(shell);
            // A project row's card is the row's own arm of ShowNode, which does not go through the
            // choice; its kind is what the choice would say.
            chosen[row.Node.Path] = row.IsProject ? RenderKind.ProjectCard : shell.RenderedChoice?.Kind ?? RenderKind.Nothing;
        }

        return (drawn, chosen);
    }

    private static IEnumerable<TreeRow> Rows(IEnumerable<TreeRow> rows) =>
        rows.SelectMany(r => Rows(r.Children).Prepend(r));

    /// <summary>The desktop's answer, as the kind it stands for: the record on the pane, and the web-page chip.</summary>
    private static RenderKind DesktopKind(ShellViewModel shell) => shell.RenderedContent switch
    {
        ProjectSummaryRender => RenderKind.ProjectCard,
        LibraryCatalog => RenderKind.LibraryCatalog,
        TaskCatalog => RenderKind.TaskCatalog,
        TaskDetailRender => RenderKind.TaskCard,
        DeviceCatalog => RenderKind.DeviceCatalog,
        DeviceDetailRender => RenderKind.DeviceCard,
        MemberTable or InterfaceRender => RenderKind.MemberTable,
        LadderRender => RenderKind.Ladder,
        MarkdownRender { IsNote: true } => RenderKind.EmptyObject,
        MarkdownRender => RenderKind.Markdown,
        SourceRender when shell.HasWebPage => RenderKind.WebPage,
        SourceRender => RenderKind.Source,
        _ => RenderKind.Nothing,
    };
}
