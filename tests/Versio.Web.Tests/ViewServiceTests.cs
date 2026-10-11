using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Versio.Core.Logging;
using Versio.Core.Projects;
using Versio.Web.Rendering;
using Versio.Web.Repo;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// What the page shows for an object, decided as the desktop decides it, over this repository's
/// real exports: tables, ladders, code under an interface, Markdown, source; and two commits
/// compared, including a change nothing in the drawing shows.
/// </summary>
public sealed class ViewServiceTests
{
    private const string Alarm = "fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/Library/Alarm/DUTs";
    private const string Settings = Alarm + "/ST_AlarmSettings.st";
    private const string Severity = Alarm + "/E_AlarmSeverity.st";
    private const string Simulation = "fixtures/projects/MachineWithMembers/Device/Plc Logic/Application/Simulation.st";
    private const string Signals = "fixtures/projects/MachineWithMembers/Device/Plc Logic/Application/Signals.xml";

    private readonly FakeGitHub _github = new();
    private readonly FakeGitHub.FakeRepo _repo;
    private readonly WebOptions _options = new();

    public ViewServiceTests()
    {
        _repo = _github.Add("acme", "plant");
        var files = FakeGitHub.FromDisk(".versio/repo.json", "README.md", "fixtures/projects/MachineWithMembers", "fixtures/projects/RefrigeratorControl");
        _github.Commit(_repo, "main", files);

        var changed = new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
        Edit(changed, Settings, "Enable				: BOOL := TRUE;", "Enable				: BOOL := FALSE;");
        Edit(changed, Severity, "{attribute 'strict'}", "{attribute 'strict'} ");
        Edit(changed, Simulation, "Glob_Var.TempActual-0.1;", "Glob_Var.TempActual-0.2;");
        _github.Commit(_repo, "next", changed);
    }

    private static void Edit(Dictionary<string, byte[]> files, string path, string from, string to)
    {
        var text = Encoding.UTF8.GetString(files[path]);
        Assert.Contains(from, text, StringComparison.Ordinal);
        files[path] = Encoding.UTF8.GetBytes(text.Replace(from, to, StringComparison.Ordinal));
    }

    private (ViewService Views, RepoModelLoader Models) Services()
    {
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1_000_000 });
        return (new ViewService(_github, cache, Options.Create(_options), NullLogger.Instance), new RepoModelLoader(_github, cache, NullLogger.Instance));
    }

    private async Task<RepoModel> Model(RepoModelLoader models, string branch) =>
        (await models.LoadAsync(RepoAccess.Anonymous, "acme", "plant", _repo.Refs[branch], CancellationToken.None)).Value!;

    private async Task<ViewResult> View(string path, PaneView view = PaneView.Rendered)
    {
        var (views, models) = Services();
        var result = await views.ViewAsync(RepoAccess.Anonymous, await Model(models, "main"), path, view, false, CancellationToken.None);
        Assert.True(result.Ok, $"{path}: {result.Failure}");
        return result.Value!;
    }

    private async Task<ViewResult> Compare(string path, PaneView view = PaneView.Rendered, bool sideBySide = false)
    {
        var (views, models) = Services();
        var result = await views.CompareAsync(RepoAccess.Anonymous, await Model(models, "main"), await Model(models, "next"), path, view, sideBySide, CancellationToken.None);
        Assert.True(result.Ok, $"{path}: {result.Failure}");
        return result.Value!;
    }

    [Fact]
    public async Task The_tree_is_the_desktops_with_each_bound_project_grafted_in()
    {
        var (_, models) = Services();
        var model = await Model(models, "main");

        Assert.True(model.IsVersio);
        Assert.Contains(RepoModel.Flatten(model.Nodes), n => n.Kind == ProjectNodeKind.Project && n.Path == "fixtures/projects/RefrigeratorControl.project");
        Assert.Contains(RepoModel.Flatten(model.Nodes), n => n.Kind == ProjectNodeKind.Object && n.Name == "ST_AlarmSettings");

        // The two bindings whose exports are not in this commit are said, not silently dropped.
        Assert.Contains(model.Problems, p => p.Contains("has never been exported here", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_members_type_links_to_the_object_it_names_and_a_library_type_does_not()
    {
        // ST_AlarmSettings.Severity is an E_AlarmSeverity, exported in the same project: linked to
        // its file. STRING(20) and BOOL are elementary and stay text, as on the desktop.
        var view = await View(Settings);

        Assert.Contains($"<a class=\"mt-link\" data-link=\"{Severity}\">E_AlarmSeverity</a>", view.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-link=\"\"", view.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("mt-ref-changed", view.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_type_that_changed_between_the_versions_is_highlighted_and_said()
    {
        // "next" changes E_AlarmSeverity's file; ST_AlarmSettings' Severity member is of that
        // type, so the struct's meaning changed even in rows whose text did not.
        var view = await Compare(Settings);

        Assert.Contains("mt-ref-changed\" data-path=\"Severity\"", view.Html, StringComparison.Ordinal);
        Assert.Contains(ViewService.TypeChangedNotice, view.Notices);
    }

    [Fact]
    public async Task A_data_type_is_its_member_table()
    {
        var view = await View(Settings);

        Assert.Equal("table", view.Kind);
        Assert.Equal("ST_AlarmSettings", view.Title);
        Assert.Contains("data-kind=\"datatype\"", view.Html, StringComparison.Ordinal);
        Assert.Contains("data-path=\"Enable\"", view.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_is_its_interface_with_its_code_body_underneath()
    {
        var view = await View(Simulation);

        Assert.Equal("table", view.Kind);
        Assert.Contains("data-kind=\"interface\"", view.Html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"cd-page\"", view.Html, StringComparison.Ordinal);
        Assert.Contains("T1(IN:= Glob_Var.Compressor", System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(view.Html, "<[^>]+>", "")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ladder_block_is_its_interface_with_its_networks_underneath()
    {
        var view = await View(Signals);

        Assert.Equal("table", view.Kind);
        Assert.Contains("<div class=\"ld-page\"", view.Html, StringComparison.Ordinal);
        Assert.Contains("<svg ", view.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_layer_chip_shows_that_layer_and_the_tree_opens_the_readable_one()
    {
        // An object with two layers. Asking for the native .xml must show the .xml, which the
        // first version never did; asking for the object opens its readable .st, as the desktop does.
        const string Gvl = "fixtures/projects/MachineWithMembers/Device/Plc Logic/Application/Glob_Var.gvl";

        var xml = await View(Gvl + ".xml", PaneView.Raw);
        var st = await View(Gvl + ".st", PaneView.Raw);

        Assert.Equal(Gvl + ".xml", xml.Path);
        Assert.Contains("&lt;", xml.Html, StringComparison.Ordinal);
        Assert.Equal(Gvl + ".st", st.Path);
        Assert.Contains(Gvl + ".st", xml.Layers);
        Assert.Contains(Gvl + ".xml", xml.Layers);
    }

    [Fact]
    public async Task A_readme_is_a_page_and_raw_is_the_file()
    {
        var rendered = await View("README.md");
        var raw = await View("README.md", PaneView.Raw);

        Assert.Equal("markdown", rendered.Kind);
        Assert.Contains("<div class=\"md-page\">", rendered.Html, StringComparison.Ordinal);
        Assert.Equal("source", raw.Kind);
        Assert.Contains("data-line=\"1\"", raw.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_changed_member_shows_old_then_new_in_the_pages_own_words()
    {
        var view = await Compare(Settings);

        Assert.Equal("table", view.Kind);
        Assert.Contains("data-path=\"Enable\" data-change=\"modified\"", view.Html, StringComparison.Ordinal);
        Assert.Contains("<del class=\"mt-was\">TRUE</del> <ins class=\"mt-now\">FALSE</ins>", view.Html, StringComparison.Ordinal);
        Assert.Contains("Since the older version", view.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Since the last commit", view.Html, StringComparison.Ordinal);
        Assert.Equal([ViewService.TypeChangedNotice], view.Notices);
    }

    [Fact]
    public async Task No_view_speaks_of_the_last_commit_or_uncommitted_changes()
    {
        // The desktop compares with the last commit; this page shows one commit or two chosen
        // ones. Seen in the browser: the ladder said "No changes since the last commit" and the
        // table spoke of uncommitted changes.
        var pages = new[] { await View(Signals), await View(Settings), await Compare(Settings), await Compare(Simulation) };

        Assert.All(pages, p => Assert.DoesNotContain("last commit", p.Html, StringComparison.OrdinalIgnoreCase));
        Assert.All(pages, p => Assert.DoesNotContain("uncommitted", p.Html, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("The block interface.", pages[0].Html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_phrase_reworded_is_still_the_one_Core_writes()
    {
        // If Core rewords its summary, Reword stops matching and the desktop's "last commit"
        // reaches the page; this fails first.
        var compared = MemberTableDiff.Compare(
            new MemberTable { Name = "T", Members = [new MemberRow { Name = "A", Path = "A", Datatype = "INT", StartValue = "1" }] },
            new MemberTable { Name = "T", Members = [new MemberRow { Name = "A", Path = "A", Datatype = "INT", StartValue = "2" }] });

        Assert.StartsWith("Since the last commit", compared.ChangeSummary, StringComparison.Ordinal);
        Assert.StartsWith("Since the older version", ViewService.Reword(compared).ChangeSummary, StringComparison.Ordinal);
        Assert.StartsWith("At the last commit", compared.Members[0].Was, StringComparison.Ordinal);
        Assert.StartsWith("In the older version", ViewService.Reword(compared).Members[0].Was, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_the_drawing_leaves_out_is_still_announced()
    {
        // Security rule 10: the attribute line changed, no member did. The table alone would
        // read as untouched.
        var view = await Compare(Severity);

        Assert.Equal("table", view.Kind);
        Assert.DoesNotContain("data-change=\"modified\"", view.Html, StringComparison.Ordinal);
        Assert.Contains(ViewService.NothingDrawnChanged, view.Notices);
    }

    [Fact]
    public async Task A_code_change_under_an_interface_is_marked_and_needs_no_notice()
    {
        var view = await Compare(Simulation);

        Assert.Contains("cd-added", view.Html, StringComparison.Ordinal);
        Assert.Contains("cd-removed", view.Html, StringComparison.Ordinal);
        Assert.Empty(view.Notices);
    }

    [Fact]
    public async Task Raw_compare_is_the_text_diff_and_side_by_side_agrees_with_unified()
    {
        var unified = await Compare(Settings, PaneView.Raw);
        var split = await Compare(Settings, PaneView.Raw, sideBySide: true);

        Assert.Equal("diff", unified.Kind);
        Assert.Contains("data-added=\"1\" data-removed=\"1\"", unified.Html, StringComparison.Ordinal);
        Assert.Contains("data-view=\"side-by-side\"", split.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_file_finds_the_same_node_through_the_index_as_by_walking_the_tree()
    {
        // NodeFor became an index (a comparison asked once per changed file and walked the tree
        // each time). Same answers as the walk it replaced, for every file in the commit.
        var (_, models) = Services();
        var model = await Model(models, "main");
        var nodes = RepoModel.Flatten(model.Nodes).ToList();

        foreach (var path in model.Files.Keys)
        {
            var walked = nodes.FirstOrDefault(n => n.Kind == ProjectNodeKind.Object && n.Files.Contains(path))
                         ?? nodes.FirstOrDefault(n => n.Files.Contains(path) || (n.Path == path && n.Kind == ProjectNodeKind.File));
            var indexed = model.NodeFor(path);
            Assert.NotNull(indexed);
            if (walked is not null)
            {
                Assert.Same(walked, indexed);
            }
        }
    }

    [Fact]
    public async Task Changed_paths_are_exactly_the_edited_files()
    {
        var (_, models) = Services();
        var changes = ViewService.ChangedPaths(await Model(models, "main"), await Model(models, "next"));

        Assert.Equal([(Simulation, "modified"), (Severity, "modified"), (Settings, "modified")], changes);
    }

    [Fact]
    public async Task A_file_over_the_draw_limit_is_plain_text_and_says_why()
    {
        _options.MaxDrawBytes = 10;

        var view = await View(Settings);

        Assert.Equal("source", view.Kind);
        Assert.Contains(ViewService.TooLargeToDrawNotice, view.Notices);
        Assert.DoesNotContain("cd-t-keyword", view.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_over_the_fetch_limit_is_never_downloaded()
    {
        _options.MaxBlobBytes = 10;

        var view = await View(Settings);

        Assert.Equal("too-large", view.Kind);
        Assert.DoesNotContain(_github.Reads, r => r.What.StartsWith("blob ", StringComparison.Ordinal) && r.What.Contains(FakeGitHub.BlobSha(File.ReadAllBytes(Path.Combine(RepoRoot.Path, Settings))), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_render_past_its_budget_is_plain_text_and_says_why()
    {
        _options.RenderBudget = TimeSpan.Zero;

        var view = await View(Simulation);

        Assert.Contains(ViewService.OverBudgetNotice, view.Notices);
        Assert.Contains("data-line=\"1\"", view.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_binary_file_is_named_not_drawn()
    {
        var files = new Dictionary<string, byte[]> { ["logo.png"] = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01] };
        _github.Commit(_repo, "binary", files);
        var (views, models) = Services();

        var result = await views.ViewAsync(RepoAccess.Anonymous, await Model(models, "binary"), "logo.png", PaneView.Rendered, false, CancellationToken.None);

        Assert.Equal("binary", result.Value!.Kind);
    }
}
