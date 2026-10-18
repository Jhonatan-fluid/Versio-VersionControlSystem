using System.Net;
using System.Text.Json;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// The pulls and releases tabs' API (docs/web/PLAN.md, "The product shape" item 5): the lists
/// carry meta only, one body is rendered when picked, and every name the page shows is spelled.
/// </summary>
public sealed class PullsReleasesTests
{
    private const string Rlo = "‮";

    private static readonly DateTimeOffset When = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    private static FakeGitHub GitHub(out FakeGitHub.FakeRepo plant)
    {
        var github = new FakeGitHub();
        plant = github.Add("acme", "plant");
        github.Commit(plant, "main", FakeGitHub.FromDisk("README.md"));
        plant.Pulls.Add((new PullInfo(12, $"Fix{Rlo}interlock", "jo", When, "main", "fix", new string('d', 40), Draft: true), "## Why\n\nThe delay was wrong."));
        plant.Releases.Add((new ReleaseInfo("v2.0", $"Line{Rlo}3", "jo", When, Prerelease: true), "## Notes\n\nFirst."));
        plant.Releases.Add((new ReleaseInfo("v1.0", "One", "jo", When, Prerelease: false), ""));
        return github;
    }

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    [Fact]
    public async Task The_pulls_list_carries_the_meta_spelled_and_the_view_renders_the_body()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _));

        var list = await host.GetAsync("/api/repos/acme/plant/pulls");
        list.AssertStatus(HttpStatusCode.OK);
        var pull = Json(await list.Body()).GetProperty("pulls").EnumerateArray().Single();
        Assert.Equal(12, pull.GetProperty("number").GetInt32());
        Assert.Equal("FixU+202Einterlock", pull.GetProperty("title").GetString());
        Assert.Equal(new string('d', 40), pull.GetProperty("headSha").GetString());
        Assert.True(pull.GetProperty("draft").GetBoolean());

        var view = await host.GetAsync("/api/repos/acme/plant/pulls/view?number=12");
        view.AssertStatus(HttpStatusCode.OK);
        var shown = Json(await view.Body());
        Assert.Equal("FixU+202Einterlock", shown.GetProperty("title").GetString());
        Assert.Equal("main", shown.GetProperty("base").GetString());
        var html = shown.GetProperty("html").GetString()!;
        Assert.Contains("md-page", html, StringComparison.Ordinal);
        Assert.Contains("<h2", html, StringComparison.Ordinal);
        Assert.Contains("The delay was wrong.", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// GitHub's diff is three-dot. Until 2026-10-17 the page compared the base branch's tip with the
    /// head, so commits merged to main after the branch was cut showed as reverted by the pull
    /// request (FINDINGS 2026-10-17). The view now names the merge-base of the base's tip and the
    /// head, and the page compares from it.
    /// </summary>
    [Fact]
    public async Task The_view_names_the_merge_base_of_the_base_branchs_tip_and_the_head()
    {
        var github = GitHub(out var plant);
        var mergeBase = new string('b', 40);
        plant.MergeBases[(plant.Refs["main"], new string('d', 40))] = mergeBase;
        await using var host = await TestHost.StartAsync(github);

        var shown = Json(await (await host.GetAsync("/api/repos/acme/plant/pulls/view?number=12")).Body());

        Assert.Equal(mergeBase, shown.GetProperty("mergeBase").GetString());
        Assert.Contains(github.Reads, r => r.What == $"merge-base {plant.Refs["main"]}...{new string('d', 40)}");
    }

    /// <summary>
    /// No merge-base is null, never the tip: falling back would bring back exactly the wrong
    /// comparison. The description still shows.
    /// </summary>
    [Fact]
    public async Task Without_a_merge_base_the_view_says_null_and_still_shows_the_description()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _));

        var response = await host.GetAsync("/api/repos/acme/plant/pulls/view?number=12");

        response.AssertStatus(HttpStatusCode.OK);
        var shown = Json(await response.Body());
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("mergeBase").ValueKind);
        Assert.Contains("The delay was wrong.", shown.GetProperty("html").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_releases_list_carries_the_meta_spelled_and_an_empty_body_is_an_empty_fragment()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _));

        var list = await host.GetAsync("/api/repos/acme/plant/releases");
        list.AssertStatus(HttpStatusCode.OK);
        var releases = Json(await list.Body()).GetProperty("releases").EnumerateArray().ToList();
        Assert.Equal(["v2.0", "v1.0"], releases.Select(r => r.GetProperty("tag").GetString()));
        Assert.Equal("LineU+202E3", releases[0].GetProperty("name").GetString());
        Assert.True(releases[0].GetProperty("prerelease").GetBoolean());

        var noted = Json(await (await host.GetAsync("/api/repos/acme/plant/releases/view?tag=v2.0")).Body());
        Assert.Contains("<h2", noted.GetProperty("html").GetString()!, StringComparison.Ordinal);

        var bare = Json(await (await host.GetAsync("/api/repos/acme/plant/releases/view?tag=v1.0")).Body());
        Assert.Equal("", bare.GetProperty("html").GetString());
    }

    [Theory]
    [InlineData("/api/repos/acme/plant/pulls/view")]
    [InlineData("/api/repos/acme/plant/pulls/view?number=0")]
    [InlineData("/api/repos/acme/plant/pulls/view?number=-3")]
    [InlineData("/api/repos/acme/plant/pulls/view?number=99")]
    [InlineData("/api/repos/acme/plant/releases/view")]
    [InlineData("/api/repos/acme/plant/releases/view?tag=a..b")]
    [InlineData("/api/repos/acme/plant/releases/view?tag=none")]
    public async Task A_missing_or_malformed_pick_is_not_found(string path)
    {
        await using var host = await TestHost.StartAsync(GitHub(out _));

        (await host.GetAsync(path)).AssertStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_private_repositorys_pulls_and_releases_are_not_found_anonymously()
    {
        var github = GitHub(out _);
        var secret = github.Add("acme", "secret", isPrivate: true);
        github.Commit(secret, "main", FakeGitHub.FromDisk("README.md"));
        secret.Pulls.Add((new PullInfo(1, "hidden", "jo", When, "main", "fix", new string('e', 40), false), "secret body"));
        await using var host = await TestHost.StartAsync(github);

        foreach (var path in new[] { "/pulls", "/pulls/view?number=1", "/releases" })
        {
            var response = await host.GetAsync("/api/repos/acme/secret" + path);
            response.AssertStatus(HttpStatusCode.NotFound);
            Assert.DoesNotContain("secret body", await response.Body(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_cut_list_says_so_and_shows_the_limit()
    {
        var github = GitHub(out var plant);
        plant.Pulls.Clear();
        for (var i = 1; i <= 51; i++)
        {
            plant.Pulls.Add((new PullInfo(i, $"p{i}", "jo", When, "main", "fix", new string('d', 40), false), ""));
        }

        await using var host = await TestHost.StartAsync(github);

        var json = Json(await (await host.GetAsync("/api/repos/acme/plant/pulls")).Body());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Equal(50, json.GetProperty("pulls").GetArrayLength());
    }
}
