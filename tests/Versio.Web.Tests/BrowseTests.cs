using System.Net;
using System.Text;
using System.Text.Json;
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
