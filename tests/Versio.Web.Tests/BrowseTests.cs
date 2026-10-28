using System.Net;
using System.Text;
using System.Text.Json;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// Repository browsing in GitHub's shape with the desktop's marks (docs/web/FINISH.md, Step 8):
/// the tree marked with a comparison, a commit's page, the tags, and "set up for Versio" meaning
/// a project to draw rather than a repo.json.
/// </summary>
public sealed class BrowseTests
{
    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    private static FakeGitHub Build(out FakeGitHub.FakeRepo repo, out string start, out string head)
    {
        var github = new FakeGitHub();
        repo = github.Add("acme", "plant");
        var files = new Dictionary<string, byte[]>
        {
            ["README.md"] = Encoding.UTF8.GetBytes("# Plant\n"),
            ["src/Pump.st"] = Encoding.UTF8.GetBytes("PROGRAM Pump\nEND_PROGRAM\n"),
            ["src/Old.st"] = Encoding.UTF8.GetBytes("PROGRAM Old\nEND_PROGRAM\n"),
            ["docs/notes.md"] = Encoding.UTF8.GetBytes("notes\n"),
        };
        start = github.Commit(repo, "main", files);
        var next = new Dictionary<string, byte[]>(files, StringComparer.Ordinal) { ["src/Pump.st"] = Encoding.UTF8.GetBytes("PROGRAM Pump\n(* changed *)\nEND_PROGRAM\n"), ["src/New.st"] = Encoding.UTF8.GetBytes("PROGRAM New\nEND_PROGRAM\n") };
        next.Remove("src/Old.st");
        head = github.Commit(repo, "fix", next);
        repo.Parents[head] = [start];
        repo.CommitMessages[head] = "Change the pump\n\nIt was slow.\n";
        repo.Tags["v1.0"] = start;
        return github;
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement nodes) =>
        nodes.EnumerateArray().SelectMany(n => new[] { n }.Concat(Flatten(n.GetProperty("children"))));

    /// <summary>
    /// Review, 2026-10-25 (P2): the commit page's comparison took no old path, so a file GitHub
    /// reported renamed (and edited) was drawn as wholly added — here two additions and no removal,
    /// where the pull request's view, told the old path, draws the one line that changed. The route
    /// now takes <c>from</c> as the pull request's does; an old path that is not a path is refused,
    /// not read.
    /// </summary>
    [Fact]
    public async Task A_renamed_file_compared_from_its_old_path_shows_the_one_line_that_changed()
    {
        await using var host = await TestHost.StartAsync(Build(out _, out var start, out var head));
        var route = $"/api/repos/acme/plant/compare/view?base={start}&head={head}&path=src%2FNew.st&view=raw";

        var cold = Json(await (await host.GetAsync(route)).Body()).GetProperty("html").GetString()!;
        var toldResponse = await host.GetAsync(route + "&from=src%2FOld.st");
        toldResponse.AssertStatus(HttpStatusCode.OK);
        var told = Json(await toldResponse.Body()).GetProperty("html").GetString()!;

        static int Count(string html, string mark) => html.Split(mark).Length - 1;
        Assert.Equal((2, 0), (Count(cold, "cd-added"), Count(cold, "cd-removed")));
        Assert.Equal((1, 1), (Count(told, "cd-added"), Count(told, "cd-removed")));
        // The removed row is the old path's text (one row per line; the keyword is highlighted apart from the name).
        Assert.Matches("cd-removed[^\n]*Old", told);
        Assert.Matches("cd-added[^\n]*New", told);
        (await host.GetAsync(route + "&from=..%2Fsrc%2FOld.st")).AssertStatus(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Review, 2026-10-25 (P2, after the commit page learnt <c>from</c>): the old path reached the
    /// renderer, which then paired the older layers to the newer by everything after the file name's
    /// first dot, so a rename that changed the extension (old.txt to new.md) or dropped a dotted part
    /// of the name (Old.Name.st to New.st) lost its old side all the same and drew additions only.
    /// The pair the caller names decides; here each draws the one line that changed.
    /// </summary>
    [Theory]
    [InlineData("notes/old.txt", "notes/new.md")]
    [InlineData("src/Old.Name.st", "src/New.st")]
    public async Task A_renamed_file_whose_extension_changed_is_still_compared_from_its_old_path(string oldPath, string newPath)
    {
        var github = new FakeGitHub();
        var repo = github.Add("acme", "plant");
        var start = github.Commit(repo, "main", new Dictionary<string, byte[]> { [oldPath] = Encoding.UTF8.GetBytes("alpha\nbeta\n") });
        var head = github.Commit(repo, "fix", new Dictionary<string, byte[]> { [newPath] = Encoding.UTF8.GetBytes("alpha\ngamma\n") });
        await using var host = await TestHost.StartAsync(github);
        var route = $"/api/repos/acme/plant/compare/view?base={start}&head={head}&path={Uri.EscapeDataString(newPath)}&view=raw";

        var cold = Json(await (await host.GetAsync(route)).Body()).GetProperty("html").GetString()!;
        var told = Json(await (await host.GetAsync(route + "&from=" + Uri.EscapeDataString(oldPath))).Body()).GetProperty("html").GetString()!;

        static int Count(string html, string mark) => html.Split(mark).Length - 1;
        Assert.Equal((2, 0), (Count(cold, "cd-added"), Count(cold, "cd-removed")));
        Assert.Equal((1, 1), (Count(told, "cd-added"), Count(told, "cd-removed")));
        Assert.Matches("cd-removed[^\n]*beta", told);
        Assert.Matches("cd-added[^\n]*gamma", told);
    }

    [Fact]
    public async Task The_tree_compared_with_a_base_marks_each_row_rolls_folders_up_and_lists_the_deleted_file_struck()
    {
        await using var host = await TestHost.StartAsync(Build(out _, out var start, out var head));

        var marked = Json(await (await host.GetAsync("/api/repos/acme/plant/tree?ref=fix&base=main")).Body());
        var plain = Json(await (await host.GetAsync("/api/repos/acme/plant/tree?ref=fix")).Body());
        var changedOnly = Json(await (await host.GetAsync("/api/repos/acme/plant/tree?ref=fix&base=main&changed=1")).Body());

        Assert.Equal((head, start), (marked.GetProperty("commit").GetString(), marked.GetProperty("base").GetString()));
        var status = (JsonElement tree, string path) => Flatten(tree.GetProperty("nodes")).Single(n => n.GetProperty("path").GetString() == path);
        Assert.Equal("modified", status(marked, "src/Pump.st").GetProperty("status").GetString());
        Assert.Equal("added", status(marked, "src/New.st").GetProperty("status").GetString());
        Assert.Equal("deleted", status(marked, "src/Old.st").GetProperty("status").GetString());
        Assert.Equal((3, "deleted"), (status(marked, "src").GetProperty("changedDescendants").GetInt32(), status(marked, "src").GetProperty("rollUp").GetString()));
        Assert.Equal("unmodified", status(plain, "src/Pump.st").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("base").ValueKind);
        Assert.Equal(["src", "src/New.st", "src/Old.st", "src/Pump.st"], Flatten(changedOnly.GetProperty("nodes")).Select(n => n.GetProperty("path").GetString()).Order());
    }

    [Fact]
    public async Task A_commits_page_carries_its_message_people_parents_stats_and_files_and_a_bad_sha_is_not_found()
    {
        await using var host = await TestHost.StartAsync(Build(out _, out var start, out var head));

        var commit = Json(await (await host.GetAsync($"/api/repos/acme/plant/commit?sha={head}")).Body());

        Assert.Equal(("Change the pump", "It was slow.", "Tester", "tester"), (commit.GetProperty("headline").GetString(), commit.GetProperty("body").GetString(), commit.GetProperty("author").GetString(), commit.GetProperty("login").GetString()));
        Assert.Equal([start], commit.GetProperty("parents").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal((2, 2), (commit.GetProperty("additions").GetInt32(), commit.GetProperty("deletions").GetInt32()));
        Assert.Equal(["src/New.st:added", "src/Old.st:removed", "src/Pump.st:modified"], commit.GetProperty("files").EnumerateArray().Select(f => $"{f.GetProperty("path").GetString()}:{f.GetProperty("status").GetString()}"));
        (await host.GetAsync("/api/repos/acme/plant/commit?sha=not-a-sha")).AssertStatus(HttpStatusCode.NotFound);
        (await host.GetAsync("/api/repos/acme/plant/commit?sha=" + new string('0', 40))).AssertStatus(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A Versio repository with one CODESYS export, then a commit that edits only the native XML
    /// layer of a two-layer GVL, removes a POU whole, and adds a document outside any export.
    /// </summary>
    private static FakeGitHub BuildVersio(out FakeGitHub.FakeRepo repo, out string head)
    {
        var github = new FakeGitHub();
        repo = github.Add("acme", "plant");
        var files = FakeGitHub.FromDisk(".versio/repo.json", "fixtures/projects/MachineWithMembers");
        var start = github.Commit(repo, "main", files);
        var next = new Dictionary<string, byte[]>(files, StringComparer.Ordinal)
        {
            [GlobVarXml] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files[GlobVarXml]).Replace("TempActual", "TempMeasured", StringComparison.Ordinal)),
            ["docs/x.md"] = Encoding.UTF8.GetBytes("# x\n"),
        };
        // Removed as an export removes an object: the file and its manifest entry together. With
        // the entry left in, the commit's own index still names the object, and the parent-side
        // fallback in ObjectFor is never what finds it (the first version of this test left the
        // entry in, and the fallback could be deleted with the test staying green).
        next.Remove(Simulation);
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(files[Manifest]))!.AsObject();
        var objects = manifest["objects"]!.AsArray();
        var entry = objects.Single(o => o!["path"]!.GetValue<string>().EndsWith("/Simulation.st", StringComparison.Ordinal));
        objects.Remove(entry);
        manifest["objectCount"] = objects.Count;
        next[Manifest] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        head = github.Commit(repo, "edit", next);
        repo.Parents[head] = [start];
        return github;
    }

    private const string Application = "fixtures/projects/MachineWithMembers/Device/Plc Logic/Application";
    private const string GlobVarXml = Application + "/Glob_Var.gvl.xml";
    private const string GlobVarSt = Application + "/Glob_Var.gvl.st";
    private const string Simulation = Application + "/Simulation.st";
    private const string Manifest = "fixtures/projects/MachineWithMembers/_manifest.json";

    /// <summary>
    /// The commit page drew one section per layer file until 2026-10-27: a GVL edited in its
    /// native XML was a section headed by an .xml path, with no name, chip or pill, and the tree
    /// beside it drew the object once. `objects` groups GitHub's list the way the tree does: one
    /// entry per object with every layer (the touched ones flagged), drawn from the layer the
    /// commit touched, with the row's status and the project's pill; a file outside any export is
    /// a plain entry. The removed POU is still an object — found at the parent, where its node
    /// still is — and not a nameless file.
    /// </summary>
    [Fact]
    public async Task A_commits_files_are_grouped_by_object_with_every_layer_the_touched_one_drawn_and_the_tree_rows_status_and_pill()
    {
        await using var host = await TestHost.StartAsync(BuildVersio(out _, out var head));

        var commit = Json(await (await host.GetAsync($"/api/repos/acme/plant/commit?sha={head}")).Body());

        Assert.Equal(JsonValueKind.Null, commit.GetProperty("objectsUnavailable").ValueKind);
        Assert.Equal(4, commit.GetProperty("files").GetArrayLength());
        var objects = commit.GetProperty("objects").EnumerateArray().ToList();
        // GitHub's order: an entry sits where its first file was listed, and the manifest, inside
        // the export but no object's layer, is a plain file like the document outside it.
        Assert.Equal(["x.md:file", "Glob_Var:object", "Simulation:object", "_manifest.json:file"], objects.Select(o => $"{o.GetProperty("name").GetString()}:{o.GetProperty("kind").GetString()}"));

        var gvl = objects[1];
        Assert.Equal((GlobVarXml, "modified", "GVL"), (gvl.GetProperty("path").GetString(), gvl.GetProperty("status").GetString(), gvl.GetProperty("type").GetString()));
        Assert.False(string.IsNullOrEmpty(gvl.GetProperty("typeName").GetString()));
        Assert.False(string.IsNullOrEmpty(gvl.GetProperty("toolchain").GetString()));
        Assert.NotEqual(GlobVarXml, gvl.GetProperty("key").GetString());
        Assert.Equal([(Application + "/Glob_Var.gvl.st", "unmodified", false), (GlobVarXml, "modified", true)],
            gvl.GetProperty("layers").EnumerateArray().Select(l => (l.GetProperty("path").GetString(), l.GetProperty("status").GetString(), l.GetProperty("changed").GetBoolean())));
        Assert.Equal((1, 1), (gvl.GetProperty("additions").GetInt32(), gvl.GetProperty("deletions").GetInt32()));

        var doc = objects[0];
        Assert.Equal(("docs/x.md", "docs/x.md", "added", ""), (doc.GetProperty("key").GetString(), doc.GetProperty("path").GetString(), doc.GetProperty("status").GetString(), doc.GetProperty("toolchain").GetString()));
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("type").ValueKind);

        var pou = objects[2];
        Assert.Equal(("deleted", Simulation, "POU"), (pou.GetProperty("status").GetString(), pou.GetProperty("path").GetString(), pou.GetProperty("type").GetString()));

        // The page scrolls a tree pick to the section whose id is the row's path: every object
        // entry's key is a row of the marked tree the page draws beside it, with the same status.
        var tree = Json(await (await host.GetAsync($"/api/repos/acme/plant/tree?ref={head}&base=main&changed=1")).Body());
        var rows = Flatten(tree.GetProperty("nodes")).ToDictionary(n => n.GetProperty("path").GetString()!, n => n.GetProperty("status").GetString());
        foreach (var o in objects.Where(o => o.GetProperty("kind").GetString() == "object"))
        {
            Assert.Equal(o.GetProperty("status").GetString(), rows[o.GetProperty("key").GetString()!]);
        }
    }

    /// <summary>
    /// The grouping needs the two project trees, and a rate limit on those must not cost the
    /// commit: the card and GitHub's file list are answered, the grouping is absent and says why.
    /// </summary>
    [Fact]
    public async Task A_commit_whose_project_trees_cannot_be_read_keeps_its_card_and_files_and_says_why_the_objects_are_missing()
    {
        var github = BuildVersio(out _, out var head);
        github.BeforeTree = _ => Task.FromResult(RepoFailure.RateLimited);
        string log;
        await using (var host = await TestHost.StartAsync(github))
        {
            var response = await host.GetAsync($"/api/repos/acme/plant/commit?sha={head}");
            response.AssertStatus(HttpStatusCode.OK);
            var commit = Json(await response.Body());

            Assert.Equal(4, commit.GetProperty("files").GetArrayLength());
            Assert.Equal(0, commit.GetProperty("objects").GetArrayLength());
            Assert.Contains("rate limit", commit.GetProperty("objectsUnavailable").GetString(), StringComparison.Ordinal);
            log = string.Concat(Directory.GetFiles(host.LogDirectory).Select(f =>
            {
                using var stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return new StreamReader(stream).ReadToEnd();
            }));
        }

        Assert.Contains("could not be read (RateLimited); 4 files answered ungrouped", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// An object's history covered one layer until 2026-10-27: GitHub's list-commits takes one
    /// path, and the page sent the readable file's, so a commit that edited the native XML alone
    /// was not in the GVL's history. Now every layer is listed and merged: the XML-only commit is
    /// there, naming the layer it touched, and the commit that edited both names both.
    /// </summary>
    [Fact]
    public async Task An_objects_history_covers_every_layer_and_says_which_each_commit_touched()
    {
        var github = BuildVersio(out var repo, out var xmlOnly);
        // A third commit on top, editing both layers of the GVL.
        var files = new Dictionary<string, byte[]>(repo.Commits[xmlOnly], StringComparer.Ordinal);
        files[GlobVarSt] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files[GlobVarSt]).Replace("TempActual", "TempMeasured", StringComparison.Ordinal));
        files[GlobVarXml] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files[GlobVarXml]) + "\n");
        var both = github.Commit(repo, "both", files);
        repo.Parents[both] = [xmlOnly];
        await using var host = await TestHost.StartAsync(github);

        var history = Json(await (await host.GetAsync($"/api/repos/acme/plant/history?ref=both&path={Uri.EscapeDataString(GlobVarSt)}")).Body());

        Assert.Equal("Glob_Var", history.GetProperty("object").GetString());
        Assert.Equal([GlobVarSt, GlobVarXml], history.GetProperty("layers").EnumerateArray().Select(l => l.GetProperty("path").GetString()));
        Assert.False(history.GetProperty("layersCut").GetBoolean());
        var rows = history.GetProperty("commits").EnumerateArray().ToDictionary(c => c.GetProperty("sha").GetString()!, c => c.GetProperty("touched").EnumerateArray().Select(t => t.GetString()).ToList());
        Assert.Equal([GlobVarSt, GlobVarXml], rows[both]);
        Assert.Equal([GlobVarXml], rows[xmlOnly]);
        // The root commit added both layers, as GitHub lists it for either path.
        Assert.Equal([GlobVarSt, GlobVarXml], rows[repo.Refs["main"]]);
        Assert.Equal(3, rows.Count);

        // A plain file is its own history, with no object; a branch's has no layers and says nothing of what each commit touched.
        var readme = Json(await (await host.GetAsync("/api/repos/acme/plant/history?ref=both&path=.versio%2Frepo.json")).Body());
        Assert.Equal(JsonValueKind.Null, readme.GetProperty("object").ValueKind);
        Assert.Equal([".versio/repo.json"], readme.GetProperty("layers").EnumerateArray().Select(l => l.GetProperty("path").GetString()));
        var branch = Json(await (await host.GetAsync("/api/repos/acme/plant/history?ref=both")).Body());
        Assert.Equal(0, branch.GetProperty("layers").GetArrayLength());
        Assert.All(branch.GetProperty("commits").EnumerateArray(), c => Assert.Equal(0, c.GetProperty("touched").GetArrayLength()));
    }

    /// <summary>A rate limit on the project tree costs the other layers, never the history: the one path is listed, and the log says why.</summary>
    [Fact]
    public async Task An_objects_history_still_answers_for_the_one_path_when_the_project_tree_cannot_be_read()
    {
        var github = BuildVersio(out _, out _);
        github.BeforeTree = _ => Task.FromResult(RepoFailure.RateLimited);
        await using var host = await TestHost.StartAsync(github);

        var response = await host.GetAsync($"/api/repos/acme/plant/history?ref=edit&path={Uri.EscapeDataString(GlobVarXml)}");
        response.AssertStatus(HttpStatusCode.OK);
        var history = Json(await response.Body());

        Assert.Equal(JsonValueKind.Null, history.GetProperty("object").ValueKind);
        Assert.Equal([GlobVarXml], history.GetProperty("layers").EnumerateArray().Select(l => l.GetProperty("path").GetString()));
        // The root commit that added the file and the one that edited it: GitHub's list for the one path, as before.
        Assert.Equal(2, history.GetProperty("commits").GetArrayLength());
    }

    [Fact]
    public async Task The_tags_are_listed_with_their_commits_spelled()
    {
        await using var host = await TestHost.StartAsync(Build(out _, out var start, out _));

        var tags = Json(await (await host.GetAsync("/api/repos/acme/plant/tags")).Body()).GetProperty("tags");

        Assert.Equal(("v1.0", start), (tags[0].GetProperty("label").GetString(), tags[0].GetProperty("commit").GetString()));
    }

    /// <summary>A source project with no repo.json is a Versio project all the same (seen on the real pull request, FINISH.md A.8): the notice is for a repository with nothing to draw.</summary>
    [Fact]
    public async Task A_repository_with_a_source_project_and_no_repo_json_is_set_up_for_Versio_and_one_with_neither_is_not()
    {
        var github = new FakeGitHub();
        var fbs = github.Add("acme", "fbs");
        github.Commit(fbs, "main", FakeGitHub.FromDisk("fixtures/projects/RefrigeratorControl_FBS"));
        var bare = github.Add("acme", "bare");
        github.Commit(bare, "main", new Dictionary<string, byte[]> { ["README.md"] = Encoding.UTF8.GetBytes("# Bare\n") });
        await using var host = await TestHost.StartAsync(github);

        var withProject = Json(await (await host.GetAsync("/api/repos/acme/fbs/tree?ref=main")).Body());
        var without = Json(await (await host.GetAsync("/api/repos/acme/bare/tree?ref=main")).Body());

        Assert.True(withProject.GetProperty("isVersio").GetBoolean());
        Assert.False(without.GetProperty("isVersio").GetBoolean());
    }
}
