using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// A <c>repo.json</c> whose shape is wrong is a problem said on the page, not a request that
/// fails (the 2026-10-25 review's sixth round, P2).
/// </summary>
/// <remarks>
/// The viewer reads <c>repo.json</c> through the desktop's own <c>RepoConfigStore.Parse</c>, so
/// the two readings the store makes — a null list is the default, a null where a value must be
/// refuses the file — arrive here as well. Each used to arrive as a <c>NullReferenceException</c>
/// out of the model builder, which the page showed as a failed load with nothing to say about
/// which file.
/// </remarks>
public sealed class RepoConfigShapeTests
{
    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    private static FakeGitHub Repo(string repoJson)
    {
        var github = new FakeGitHub();
        var repo = github.Add("acme", "plant");
        github.Commit(repo, "main", new Dictionary<string, byte[]>
        {
            [".versio/repo.json"] = Encoding.UTF8.GetBytes(repoJson),
            ["README.md"] = Encoding.UTF8.GetBytes("# Plant\n"),
        });
        return github;
    }

    [Fact]
    public async Task A_null_where_a_binding_belongs_is_a_problem_on_the_page_not_a_failed_request()
    {
        await using var host = await TestHost.StartAsync(Repo("""{ "schema": 1, "bindings": [null] }"""));

        var response = await host.GetAsync("/api/repos/acme/plant/tree?ref=main");

        response.AssertStatus(HttpStatusCode.OK);
        var tree = Json(await response.Body());
        var problem = Assert.Single(tree.GetProperty("problems").EnumerateArray()).GetString()!;
        Assert.Contains(".versio/repo.json", problem, StringComparison.Ordinal);
        Assert.Contains("bindings[0]", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_bindings_list_is_read_as_empty_and_said_on_the_page()
    {
        await using var host = await TestHost.StartAsync(Repo("""{ "schema": 1, "bindings": null }"""));

        var response = await host.GetAsync("/api/repos/acme/plant/tree?ref=main");

        response.AssertStatus(HttpStatusCode.OK);
        var tree = Json(await response.Body());
        // Read, not refused: the file is still what makes this a Versio repository.
        Assert.True(tree.GetProperty("isVersio").GetBoolean());
        var problem = Assert.Single(tree.GetProperty("problems").EnumerateArray()).GetString()!;
        Assert.Contains(".versio/repo.json", problem, StringComparison.Ordinal);
        Assert.Contains("bindings", problem, StringComparison.Ordinal);
    }
}
