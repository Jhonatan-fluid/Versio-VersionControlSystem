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
