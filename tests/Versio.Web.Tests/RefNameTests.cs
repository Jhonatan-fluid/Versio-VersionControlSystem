using System.Net;
using System.Text;
using System.Text.Json;
using Versio.Web.Api;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// A ref the page names is checked by git's own rules (<c>git check-ref-format</c>), not by an
/// allow-list of characters. Until 2026-10-25 the gate took ASCII letters, digits, <c>_ . / -</c>
/// and nothing else, so a branch git was happy with — <c>release+hotfix</c>, <c>Prüfung</c> —
/// was "not found" on the history, tree, release and download routes (the 2026-10-24 review's
/// item 11). The shapes git refuses stay refused, and so does a leading dash, which git only
/// refuses for a branch: a name that could be read as an option is not a name the API forwards.
/// </summary>
public sealed class RefNameTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    private static FakeGitHub GitHub()
    {
        var github = new FakeGitHub();
        var plant = github.Add("acme", "plant");
        var files = new Dictionary<string, byte[]>
        {
            ["README.md"] = Encoding.UTF8.GetBytes("# Plant\n"),
            ["docs/manual.pdf"] = Encoding.UTF8.GetBytes("%PDF-1.4\n"),
        };
        github.Commit(plant, "main", files);
        github.Commit(plant, "release+hotfix", files);
        github.Commit(plant, "Prüfung", files);
        plant.Releases.Add((new ReleaseInfo("release+hotfix", "Hotfix", "jo", When, Prerelease: false), "## Notes\n\nPlus."));
        plant.Releases.Add((new ReleaseInfo("Prüfung", "Prüfung", "jo", When, Prerelease: false), "## Notes\n\nUmlaut."));
        return github;
    }

    [Theory]
    [InlineData("release+hotfix")]
    [InlineData("Prüfung")]
    [InlineData("feature/plc-7")]
    [InlineData("v1.0.0")]
    [InlineData("a@b")]
    [InlineData("name{brace}")]
    [InlineData("ü/Ω/日本")]
    [InlineData("a-b")]
    public void Names_git_accepts_pass_the_gate(string name) => Assert.True(ApiEndpoints.ValidRef(name));

    [Theory]
    [InlineData("")]
    [InlineData("-x")] // an option, were it ever to reach a command line
    [InlineData("--")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a\u007fb")]
    [InlineData("a~1")]
    [InlineData("a^b")]
    [InlineData("a:b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a[b")]
    [InlineData("a\\b")]
    [InlineData("a..b")]
    [InlineData("a@{b")]
    [InlineData("@")]
    [InlineData(".hidden")]
    [InlineData("a/.b")]
    [InlineData("a.lock")]
    [InlineData("a.lock/b")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a//b")]
    [InlineData("a.")]
    public void Shapes_git_refuses_are_refused(string name) => Assert.False(ApiEndpoints.ValidRef(name));

    [Fact]
    public void An_absent_ref_means_the_default_branch_and_an_overlong_one_is_refused()
    {
        Assert.True(ApiEndpoints.ValidRef(null));
        Assert.True(ApiEndpoints.ValidRef(new string('a', 250)));
        Assert.False(ApiEndpoints.ValidRef(new string('a', 251)));
    }

    [Theory]
    [InlineData("release+hotfix")]
    [InlineData("Prüfung")]
    public async Task A_branch_git_accepts_is_browsed_read_and_downloaded(string branch)
    {
        await using var host = await TestHost.StartAsync(GitHub());
        var encoded = Uri.EscapeDataString(branch);

        (await host.GetAsync($"/api/repos/acme/plant/tree?ref={encoded}")).AssertStatus(HttpStatusCode.OK);
        (await host.GetAsync($"/api/repos/acme/plant/view?ref={encoded}&path=README.md")).AssertStatus(HttpStatusCode.OK);
        (await host.GetAsync($"/api/repos/acme/plant/raw?ref={encoded}&path=docs/manual.pdf")).AssertStatus(HttpStatusCode.OK);

        var history = await host.GetAsync($"/api/repos/acme/plant/history?ref={encoded}");
        history.AssertStatus(HttpStatusCode.OK);
        Assert.Contains(Json(await history.Body()).GetProperty("commits").EnumerateArray(), c => c.GetProperty("message").GetString() == "commit " + branch);
    }

    [Theory]
    [InlineData("release+hotfix", "Plus.")]
    [InlineData("Prüfung", "Umlaut.")]
    public async Task A_release_whose_tag_git_accepts_is_opened(string tag, string notes)
    {
        await using var host = await TestHost.StartAsync(GitHub());

        var view = await host.GetAsync($"/api/repos/acme/plant/releases/view?tag={Uri.EscapeDataString(tag)}");
        view.AssertStatus(HttpStatusCode.OK);
        var shown = Json(await view.Body());
        Assert.Equal(tag, shown.GetProperty("tag").GetString());
        Assert.Contains(notes, shown.GetProperty("html").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("a..b")]
    [InlineData("a@%7Bb")]
    [InlineData("a.lock")]
    [InlineData("a:b")]
    [InlineData("a%20b")]
    [InlineData("a%7E1")]
    public async Task A_shape_git_refuses_is_not_found_on_every_ref_route(string encoded)
    {
        await using var host = await TestHost.StartAsync(GitHub());

        foreach (var route in new[] { $"/tree?ref={encoded}", $"/history?ref={encoded}", $"/view?ref={encoded}&path=README.md", $"/releases/view?tag={encoded}", $"/raw?ref={encoded}&path=docs/manual.pdf" })
        {
            (await host.GetAsync("/api/repos/acme/plant" + route)).AssertStatus(HttpStatusCode.NotFound);
        }
    }
}
