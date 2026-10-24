using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Versio.Core.Logging;
using Versio.Core.Projects;
using Versio.Web.Http;
using Versio.Web.Rendering;
using Versio.Web.Repo;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// The kinds the web viewer draws since the choice became Core's (2026-10-23): the project card,
/// the three catalogs and the two cards from Versio's YAML and from a vendor's own files, the
/// empty-object note, the web page, and the notice over a native file no model draws — each the
/// drawing the desktop pane has, where this side drew raw source or a 404.
/// </summary>
/// <remarks>
/// <c>RenderChoiceParityTests</c> (in the desktop suite) proves both hosts choose alike over every
/// fixture row; this pins what the web's answer holds for each kind, and the two routes that
/// carry it: <c>/view</c>'s kind and <c>/raw</c>'s sandboxed page.
/// </remarks>
public sealed class RenderedKindsTests
{
    private const string Refrigerator = "fixtures/projects/RefrigeratorControl";
    private const string Application = Refrigerator + "/Device/Plc Logic/Application";
    private const string LibrariesYaml = Application + "/Library Manager.libraries.yaml";
    private const string TasksYaml = Application + "/Task Configuration.tasks.yaml";
    // The smaller of the two visualisations: Live_Visu is over the draw limit and is plain text by that rule, not this one.
    private const string Visualisation = Application + "/Visualization.vis.xml";
    private const string Fbs = "fixtures/projects/RefrigeratorControl_FBS/RefrigeratorControl.fbsproj";
    private const string FbsApplication = Fbs + "/Device.device.xml.v3^/Plc Logic.plclogic.xml.v3^/Application.application.xml.v3^";
    private const string Page = "docs/site/index.html";
    private const string GraphicalPou = "fixtures/projects/TC3_PlcSample_reStrText/TC3_PlcSample_reStrText/LibraryDocumentationSample Project/LibPOUs/A_Samples/FB_DocuSample_FunctionBlock.st";

    private readonly FakeGitHub _github = new();
    private readonly FakeGitHub.FakeRepo _repo;

    public RenderedKindsTests()
    {
        _repo = _github.Add("acme", "plant");
        var files = FakeGitHub.FromDisk(".versio/repo.json", "README.md", "fixtures/projects/RefrigeratorControl", "fixtures/projects/RefrigeratorControl_FBS",
            "fixtures/projects/TC3_PlcSample_reStrText/TC3_PlcSample_reStrText");
        files[Page] = Encoding.UTF8.GetBytes("<html><body><script>alert(1)</script><h1>Plant</h1></body></html>\n");
        _github.Commit(_repo, "main", files);

        var changed = new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
        // The pin is the RESOLUTION column (DisplayVersion: a redirection before the default), so
        // moving it moves a drawn cell; the default resolution alone would mark the row and move none.
        var yaml = Encoding.UTF8.GetString(changed[LibrariesYaml]);
        Assert.Contains("redirection: \"Standard, 3.5.22.0 (System)\"", yaml, StringComparison.Ordinal);
        changed[LibrariesYaml] = Encoding.UTF8.GetBytes(yaml.Replace("redirection: \"Standard, 3.5.22.0 (System)\"", "redirection: \"Standard, 3.5.19.0 (System)\"", StringComparison.Ordinal));
        _github.Commit(_repo, "next", changed);
    }

    private (ViewService Views, RepoModelLoader Models) Services()
    {
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1_000_000 });
        return (new ViewService(_github, cache, Options.Create(new WebOptions()), NullLogger.Instance), new RepoModelLoader(_github, cache, NullLogger.Instance));
    }

    private async Task<(ViewService Views, RepoModel Model)> Main()
    {
        var (views, models) = Services();
        return (views, (await models.LoadAsync(RepoAccess.Anonymous, "acme", "plant", _repo.Refs["main"], CancellationToken.None)).Value!);
    }

    private static async Task<ViewResult> View(ViewService views, RepoModel model, string path, PaneView pane = PaneView.Rendered)
    {
        var result = await views.ViewAsync(RepoAccess.Anonymous, model, path, pane, false, CancellationToken.None);
        Assert.True(result.Ok, $"{path}: {result.Failure}");
        return result.Value!;
    }

    private static ProjectTreeNode Node(RepoModel model, Func<ProjectTreeNode, bool> which)
    {
        var found = RepoModel.Flatten(model.Nodes).Where(which).ToList();
        Assert.Single(found);
        return found[0];
    }

    [Fact]
    public async Task A_project_row_is_its_card_from_the_manifest_and_a_source_project_its_own()
    {
        var (views, model) = await Main();

        var tracked = await View(views, model, Refrigerator + ".project");
        var source = await View(views, model, Fbs);

        Assert.Equal("project", tracked.Kind);
        Assert.Contains("data-kind=\"project\"", tracked.Html, StringComparison.Ordinal);
        Assert.Contains("data-fact=\"Exported by\"", tracked.Html, StringComparison.Ordinal);
        Assert.Contains("data-fact=\"Objects\"", tracked.Html, StringComparison.Ordinal);
        Assert.Contains("data-fact=\"Tasks\"", tracked.Html, StringComparison.Ordinal);

        Assert.Equal("project", source.Kind);
        Assert.Contains("data-fact=\"Stored as\"", source.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Exported by", source.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_versio_catalog_draws_as_its_table_and_a_grafted_row_as_its_catalog_or_card()
    {
        var (views, model) = await Main();
        var reference = Node(model, n => n.Kind == ProjectNodeKind.Library && n.Path == LibrariesYaml + "::#Standard");
        var task = Node(model, n => n.Kind == ProjectNodeKind.Task && n.Path == TasksYaml + "::MainTask");
        var slot = RepoModel.Flatten(model.Nodes).Where(n => n.Kind == ProjectNodeKind.Device && n.Path.Contains(DeviceCatalog.SlotRowMarker, StringComparison.Ordinal)).OrderBy(n => n.Path, StringComparer.Ordinal).First();

        var libraries = await View(views, model, LibrariesYaml);
        var fromRow = await View(views, model, reference.Path);
        var tasks = await View(views, model, TasksYaml);
        var card = await View(views, model, task.Path);
        var device = await View(views, model, Refrigerator + "/Device/EtherCAT_Master");
        var module = await View(views, model, Refrigerator + "/Device/EtherCAT_Master/BNI_XG3_508_0C5_R015");
        var slotCard = await View(views, model, slot.Path);

        Assert.Equal("libraries", libraries.Kind);
        Assert.Contains("data-src-path=\"" + LibrariesYaml + "\"", libraries.Html, StringComparison.Ordinal);
        Assert.Contains("data-key=\"#Standard\"", libraries.Html, StringComparison.Ordinal);
        Assert.Equal("libraries", fromRow.Kind);
        Assert.Equal("tasks", tasks.Kind);
        Assert.Contains("data-key=\"MainTask\"", tasks.Html, StringComparison.Ordinal);
        Assert.Equal("task", card.Kind);
        Assert.Contains("data-fact=\"Calls\"", card.Html, StringComparison.Ordinal);
        Assert.Contains("PLC_PRG", card.Html, StringComparison.Ordinal);
        Assert.Equal("devices", device.Kind);
        Assert.Contains("Common.Ethercat", device.Html, StringComparison.Ordinal);
        // The IO-Link module is the one with empty slots; the master has connectors only.
        Assert.Equal("devices", module.Kind);
        Assert.Contains("Empty slots", module.Html, StringComparison.Ordinal);
        Assert.Equal("device", slotCard.Kind);
        Assert.Contains("data-fact=\"Slot\"", slotCard.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_based_storage_projects_catalogs_draw_from_the_vendors_own_files()
    {
        // The owner's library pull request drew Libraries.json as raw metadata and XML (FINDINGS
        // 2026-10-23): the web never called the vendor's catalog readers.
        var (views, model) = await Main();

        var libraries = await View(views, model, FbsApplication + "/Libraries.json");
        var tasks = await View(views, model, FbsApplication + "/Task Configuration.taskconfig.xml.v3");
        var task = await View(views, model, FbsApplication + "/Task Configuration.taskconfig.xml.v3^/MainTask.task.xml.v3");
        var device = await View(views, model, Fbs + "/Device.device.xml.v3^/EtherCAT_Master.device.xml.v3");

        Assert.Equal("libraries", libraries.Kind);
        Assert.Contains("data-key=\"#Util\"", libraries.Html, StringComparison.Ordinal);
        Assert.Equal("tasks", tasks.Kind);
        Assert.Contains("data-key=\"EtherCAT_Task\"", tasks.Html, StringComparison.Ordinal);
        Assert.Contains("data-key=\"VISU_TASK\"", tasks.Html, StringComparison.Ordinal);
        Assert.Equal("task", task.Kind);
        Assert.Contains("Simulation", task.Html, StringComparison.Ordinal);
        Assert.Equal("devices", device.Kind);
        Assert.Contains("Common.Ethercat", device.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_native_file_no_model_draws_says_so_over_its_source_and_a_plain_file_does_not()
    {
        var (views, model) = await Main();

        var visualisation = await View(views, model, Visualisation);
        var readme = await View(views, model, "README.md", PaneView.Rendered);
        var config = await View(views, model, ".versio/repo.json");

        Assert.Equal("source", visualisation.Kind);
        Assert.Contains(visualisation.Notices, n => n.StartsWith("Versio has no drawing for this ", StringComparison.Ordinal) && n.EndsWith("; this is its file as the vendor saved it.", StringComparison.Ordinal));
        Assert.Equal("markdown", readme.Kind);
        Assert.Equal("source", config.Kind);
        Assert.Empty(config.Notices);
    }

    [Fact]
    public async Task A_graphical_blocks_text_draws_the_whole_file_under_its_table_as_the_desktop_does()
    {
        // A TwinCAT block with no implementation delimiter: the format knows no body in it, and
        // the web drew its table with nothing underneath until the choice carried "whole file".
        var (views, model) = await Main();

        var view = await View(views, model, GraphicalPou);

        Assert.Equal("table", view.Kind);
        Assert.Contains("<div class=\"cd-page\"", view.Html, StringComparison.Ordinal);
        Assert.Contains("FB_DocuSample_FunctionBlock", view.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_layer_chip_on_the_native_layer_keeps_the_blocks_own_text_under_its_table()
    {
        // With the chip on an object's .xml, one choice with that layer as primary would put the
        // XML under the interface table where the block's text belongs; the text arms alone
        // follow the chip. P_Active is a property with both layers and no body to strip.
        const string Property = Application + "/Library/Alarm/POUs/FB_Alarm/P_Active";
        var (views, model) = await Main();

        var view = await View(views, model, Property + ".xml");

        Assert.Equal("table", view.Kind);
        Assert.Equal(Property + ".xml", view.Path);
        // The code block under the table names the layer it was drawn from: the .st, not the chip's .xml.
        Assert.Contains("<div class=\"cd-page\" data-src-path=\"" + Property + ".st\"", view.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"cd-page\" data-src-path=\"" + Property + ".xml\"", view.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_web_page_answers_as_one_and_raw_serves_it_sandboxed()
    {
        var (views, model) = await Main();
        var view = await View(views, model, Page);

        Assert.Equal("webpage", view.Kind);
        // The Rendered tab is the markup as source, as on the desktop; nothing from it is live here.
        Assert.Contains("&lt;script&gt;", view.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", view.Html, StringComparison.Ordinal);

        await using var host = await TestHost.StartAsync(_github);
        var raw = await host.GetAsync($"/api/repos/acme/plant/raw?ref=main&path={Uri.EscapeDataString(Page)}");
        raw.AssertStatus(HttpStatusCode.OK);
        Assert.StartsWith("text/html", raw.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        Assert.Equal(SecurityHeaders.RawPageCsp, raw.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("sandbox", SecurityHeaders.RawPageCsp, StringComparison.Ordinal);
        Assert.Contains("<h1>Plant</h1>", await raw.Body(), StringComparison.Ordinal);

        // A document that is not a page keeps the raw policy.
        var pdf = await host.GetAsync($"/api/repos/acme/plant/raw?ref=main&path={Uri.EscapeDataString("README.md")}");
        Assert.Equal(SecurityHeaders.RawCsp, pdf.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task A_catalog_compared_marks_the_row_that_moved_in_the_pages_own_words()
    {
        var (views, models) = Services();
        var older = (await models.LoadAsync(RepoAccess.Anonymous, "acme", "plant", _repo.Refs["main"], CancellationToken.None)).Value!;
        var newer = (await models.LoadAsync(RepoAccess.Anonymous, "acme", "plant", _repo.Refs["next"], CancellationToken.None)).Value!;

        var result = await views.CompareAsync(RepoAccess.Anonymous, older, newer, LibrariesYaml, PaneView.Rendered, false, CancellationToken.None);
        Assert.True(result.Ok, result.Failure.ToString());
        var view = result.Value!;

        Assert.Equal("libraries", view.Kind);
        Assert.Contains("ct-modified\" data-key=\"#Standard\" data-change=\"modified\"", view.Html, StringComparison.Ordinal);
        Assert.Contains("<del class=\"ct-was\">Standard, 3.5.22.0 (System)</del> <ins class=\"ct-now\">Standard, 3.5.19.0 (System)</ins>", view.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("last commit", view.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("older version", view.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(ViewService.NothingDrawnChanged, view.Notices);

        // The anchors name both sides of the moved row, for a comment on either.
        Assert.Contains(view.Anchors, a => a.Key == "#Standard" && a.Side == Versio.Core.Review.ReviewSide.Old);
        Assert.Contains(view.Anchors, a => a.Key == "#Standard" && a.Side == Versio.Core.Review.ReviewSide.New);
        Assert.NotNull(view.Coverage);
    }

    [Fact]
    public void The_catalog_phrase_reworded_is_still_the_one_Core_writes()
    {
        // If CatalogDiff rewords its summary, RewordCatalog stops matching and the desktop's
        // "last commit" reaches the page; this fails first.
        var compared = CatalogDiff.Compare(
            new LibraryCatalog { Schema = 1, References = [new LibraryReference { Name = "A", DefaultResolution = "1" }] },
            new LibraryCatalog { Schema = 1, References = [new LibraryReference { Name = "A", DefaultResolution = "2" }] });

        Assert.StartsWith("Since the last commit", compared.ChangeSummary, StringComparison.Ordinal);
        Assert.StartsWith("Since the last commit", compared.References[0].Mark.Was, StringComparison.Ordinal);
        var reworded = Assert.IsType<LibraryCatalog>(ViewService.RewordCatalog(compared));
        Assert.StartsWith("Since the older version", reworded.ChangeSummary, StringComparison.Ordinal);
        Assert.StartsWith("Since the older version", reworded.References[0].Mark.Was, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_row_and_its_configuration_are_not_confused_through_the_cache()
    {
        // One service, one cache: the row and its configuration share the one YAML file and every
        // blob, so a key without the row handed the row the whole table (RenderChoiceParityTests).
        var (views, model) = await Main();
        var task = Node(model, n => n.Kind == ProjectNodeKind.Task && n.Path == TasksYaml + "::MainTask");

        var tasks = await View(views, model, TasksYaml);
        var card = await View(views, model, task.Path);
        var tasksAgain = await View(views, model, TasksYaml);

        Assert.Equal("tasks", tasks.Kind);
        Assert.Equal("task", card.Kind);
        Assert.Equal("tasks", tasksAgain.Kind);
    }

    [Fact]
    public async Task The_tree_sends_the_desktops_row_whole()
    {
        await using var host = await TestHost.StartAsync(_github);
        var tree = System.Text.Json.JsonDocument.Parse(await (await host.GetAsync("/api/repos/acme/plant/tree?ref=main")).Body()).RootElement;

        static IEnumerable<System.Text.Json.JsonElement> All(System.Text.Json.JsonElement n) =>
            n.GetProperty("children").EnumerateArray().SelectMany(All).Prepend(n);
        var nodes = tree.GetProperty("nodes").EnumerateArray().SelectMany(All).ToList();
        var project = Assert.Single(nodes, n => n.GetProperty("path").GetString() == Refrigerator + ".project");
        var visualisation = Assert.Single(nodes, n => n.GetProperty("path").GetString() == Visualisation);

        Assert.False(string.IsNullOrEmpty(project.GetProperty("toolchain").GetString()));
        Assert.Equal("unmodified", project.GetProperty("status").GetString());
        Assert.Equal("unmodified", project.GetProperty("rollUp").GetString());
        Assert.Equal(0, project.GetProperty("changedDescendants").GetInt32());
        Assert.Equal("VISUALISATION", visualisation.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(visualisation.GetProperty("typeName").GetString()));
        Assert.True(visualisation.TryGetProperty("detail", out _));
    }
}
