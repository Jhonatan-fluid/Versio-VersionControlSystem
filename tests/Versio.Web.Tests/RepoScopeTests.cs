using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Versio.Web.Auth;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// The server's repository scope (docs/web/PLAN.md, "The product shape"): a server configured
/// with <c>Web:Repositories</c> serves those and nothing else, and <c>/api/repos</c> is the list
/// each caller sees — the admin's scope intersected with their own access.
/// </summary>
public sealed class RepoScopeTests
{
    private static FakeGitHub GitHub()
    {
        var github = new FakeGitHub();
        var files = FakeGitHub.FromDisk("README.md");
        github.Commit(github.Add("acme", "plant"), "main", files);
        var secret = github.Add("acme", "secret", isPrivate: true);
        secret.Readers.Add("alice-token");
        github.Commit(secret, "main", files);
        github.Commit(github.Add("acme", "unlisted"), "main", files);
        return github;
    }

    /// <summary>Signs alice in directly, through the store the callback uses, and returns her cookie.</summary>
    private static string SignIn(TestHost host)
    {
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = sessions.CreateAsync(42, "alice", "alice", "", "alice-token", DateTimeOffset.UtcNow.AddHours(8), null, DateTimeOffset.UtcNow.AddMonths(6)).GetAwaiter().GetResult();
        return $"{AccessResolver.SessionCookie}={session.Id}";
    }

    private static Dictionary<string, string?> Scope(params string[] entries) =>
        entries.Select((e, i) => ($"Web:Repositories:{i}", (string?)e)).ToDictionary(kv => kv.Item1, kv => kv.Item2);

    private static List<string> Listed(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("repos").EnumerateArray()
            .Select(r => $"{r.GetProperty("owner").GetString()}/{r.GetProperty("name").GetString()}").ToList();

    [Fact]
    public async Task A_repository_off_the_list_is_not_found_even_to_someone_who_could_read_it()
    {
        await using var host = await TestHost.StartAsync(GitHub(), Scope("acme/plant"));
        var cookie = SignIn(host);

        (await host.GetAsync("/api/repos/acme/plant/tree", cookie)).AssertStatus(HttpStatusCode.OK);
        (await host.GetAsync("/api/repos/acme/unlisted/tree", cookie)).AssertStatus(HttpStatusCode.NotFound);
        (await host.GetAsync("/api/repos/acme/unlisted/tree")).AssertStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_list_is_the_scope_intersected_with_the_callers_own_access()
    {
        await using var host = await TestHost.StartAsync(GitHub(), Scope("acme/plant", "acme/secret"));

        // Anonymous: the private repository is left out, not greyed out.
        var anonymous = await host.GetAsync("/api/repos");
        anonymous.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(["acme/plant"], Listed(await anonymous.Body()));

        // Alice can read it, so she sees it, in the admin's order.
        var alice = await host.GetAsync("/api/repos", SignIn(host));
        alice.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(["acme/plant", "acme/secret"], Listed(await alice.Body()));
    }

    [Fact]
    public async Task An_unscoped_server_says_so_instead_of_listing_anything()
    {
        await using var host = await TestHost.StartAsync(GitHub());

        var response = await host.GetAsync("/api/repos");

        response.AssertStatus(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Body()).RootElement;
        Assert.False(json.GetProperty("scoped").GetBoolean());
        Assert.Empty(json.GetProperty("repos").EnumerateArray());
    }

    [Fact]
    public async Task A_sign_in_only_server_asks_the_signed_out_to_sign_in_and_lists_for_the_signed_in()
    {
        var settings = Scope("acme/plant", "acme/secret");
        settings["Web:AnonymousAccess"] = "false";
        await using var host = await TestHost.StartAsync(GitHub(), settings);

        (await host.GetAsync("/api/repos")).AssertStatus(HttpStatusCode.Unauthorized);

        var alice = await host.GetAsync("/api/repos", SignIn(host));
        alice.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(["acme/plant", "acme/secret"], Listed(await alice.Body()));
    }

    [Theory]
    [InlineData("justaname")]
    [InlineData("acme/")]
    [InlineData("/plant")]
    [InlineData("acme/plant/extra")]
    [InlineData("acme/..")]
    public void A_malformed_scope_entry_stops_startup_rather_than_scoping_wrongly(string entry)
    {
        var error = Assert.Throws<InvalidOperationException>(() => WebHost.ValidateRepositories([entry]));
        Assert.Contains(entry, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_well_formed_scope_passes_validation()
    {
        WebHost.ValidateRepositories(["acme/plant", "Octo-Cat/My.Repo_2"]);
    }

    [Fact]
    public async Task The_host_itself_refuses_to_start_on_a_malformed_scope()
    {
        // The theory above drives ValidateRepositories directly, so it alone would stay green
        // with the call deleted from WebHost.Build. This one proves the wiring.
        await Assert.ThrowsAsync<InvalidOperationException>(() => TestHost.StartAsync(GitHub(), Scope("justaname")));
    }
}
