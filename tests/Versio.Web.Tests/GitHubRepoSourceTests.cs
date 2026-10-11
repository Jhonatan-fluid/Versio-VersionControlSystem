using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Versio.Core.Logging;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// The real GitHub client against a local server answering in GitHub's shapes: trees, raw blobs,
/// LFS pointers, and the refusals the page must word correctly.
/// </summary>
public sealed class GitHubRepoSourceTests : IAsyncLifetime
{
    private const string Oid = "1111111111111111111111111111111111111111111111111111111111111111";

    private WebApplication? _api;
    private Uri? _address;
    private readonly ConcurrentQueue<(string Path, string? Auth)> _requests = new();
    private string _lfsHref = "https://169.254.169.254/latest/meta-data/";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Use(async (http, next) =>
        {
            _requests.Enqueue((http.Request.Path + http.Request.QueryString, http.Request.Headers.Authorization.ToString()));
            await next(http);
        });

        app.MapGet("/repos/acme/plant", () => Results.Json(new { name = "plant", @private = false, default_branch = "main", owner = new { login = "acme" } }));
        app.MapGet("/repos/acme/plant/commits/main", () => Results.Json(new { sha = new string('c', 40) }));
        app.MapGet("/repos/acme/plant/git/trees/{sha}", (string sha) => Results.Json(new
        {
            sha,
            truncated = false,
            tree = new object[]
            {
                new { path = "src", type = "tree", sha = new string('d', 40) },
                new { path = "src/Main.st", type = "blob", sha = new string('a', 40), size = 13 },
                new { path = "big.bin", type = "blob", sha = new string('b', 40), size = 99 },
                new { path = "lfs.xml", type = "blob", sha = new string('e', 40), size = 130 },
            },
        }));
        app.MapGet("/repos/acme/plant/git/blobs/{sha}", (string sha, HttpContext http) =>
        {
            Assert.Equal("application/vnd.github.raw", http.Request.Headers.Accept.ToString());
            return sha[0] switch
            {
                'a' => Results.Text("PROGRAM Main\n", "application/vnd.github.raw"),
                'e' => Results.Text($"version https://git-lfs.github.com/spec/v1\noid sha256:{Oid}\nsize 20\n", "application/vnd.github.raw"),
                _ => Results.Text(new string('x', 99), "application/vnd.github.raw"),
            };
        });
        app.MapPost("/acme/plant.git/info/lfs/objects/batch", () =>
            Results.Json(new { objects = new[] { new { oid = Oid, size = 20, actions = new { download = new { href = _lfsHref } } } } }));
        app.MapGet("/repos/acme/sso", (HttpContext http) =>
        {
            http.Response.Headers["X-GitHub-SSO"] = "required; url=https://github.com/orgs/acme/sso";
            return Results.StatusCode(403);
        });
        app.MapGet("/repos/acme/limited", (HttpContext http) =>
        {
            http.Response.Headers["x-ratelimit-remaining"] = "0";
            return Results.StatusCode(403);
        });
        app.MapGet("/repos/acme/forbidden", () => Results.StatusCode(403));

        await app.StartAsync();
        _api = app;
        _address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    public async Task DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.StopAsync();
            await _api.DisposeAsync();
        }
    }

    private GitHubRepoSource Source()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(GitHubRepoSource.ApiClient);
        services.AddHttpClient(GitHubRepoSource.LfsClient).ConfigurePrimaryHttpMessageHandler(LfsGuard.CreateHandler);
        var options = Options.Create(new WebOptions { GitHub = new GitHubOptions { ApiBaseUrl = _address!.ToString().TrimEnd('/'), WebBaseUrl = _address.ToString().TrimEnd('/') } });
        return new GitHubRepoSource(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), options, NullLogger.Instance);
    }

    private static readonly RepoAccess Alice = new(AccessKind.User, "alice-token", "alice");

    [Fact]
    public async Task A_repository_its_commit_and_its_tree_are_read_from_GitHubs_shapes()
    {
        var source = Source();

        var repo = await source.GetRepoAsync(Alice, "acme", "plant", CancellationToken.None);
        var commit = await source.ResolveCommitAsync(Alice, "acme", "plant", "main", CancellationToken.None);
        var tree = await source.GetTreeAsync(Alice, "acme", "plant", commit.Value!, CancellationToken.None);

        Assert.Equal(new RepoInfo("acme", "plant", false, "main"), repo.Value);
        Assert.Equal(new string('c', 40), commit.Value);
        Assert.Equal(["src/Main.st", "big.bin", "lfs.xml"], tree.Value!.Files.Select(f => f.Path));
        Assert.Equal(13, tree.Value.Files[0].Size);
        Assert.All(_requests, r => Assert.Equal("Bearer alice-token", r.Auth));
    }

    [Fact]
    public async Task A_blob_is_its_raw_bytes_and_one_over_the_cap_is_refused()
    {
        var source = Source();

        var small = await source.GetBlobAsync(Alice, "acme", "plant", new string('a', 40), 1000, CancellationToken.None);
        var big = await source.GetBlobAsync(Alice, "acme", "plant", new string('b', 40), 50, CancellationToken.None);

        Assert.Equal("PROGRAM Main\n", Encoding.UTF8.GetString(small.Value!));
        Assert.Equal(RepoFailure.TooLarge, big.Failure);
    }

    [Fact]
    public async Task An_LFS_download_pointed_at_the_metadata_service_is_refused_before_any_connection()
    {
        // Security rule 4: the LFS server's answer names 169.254.169.254; nothing is fetched.
        var source = Source();

        var result = await source.GetBlobAsync(Alice, "acme", "plant", new string('e', 40), 1000, CancellationToken.None);

        Assert.Equal(RepoFailure.Upstream, result.Failure);
        Assert.Contains(_requests, r => r.Path.Contains("lfs/objects/batch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_LFS_download_to_an_allowlisted_name_that_resolves_internal_is_refused_at_connect()
    {
        // The name passes the allowlist; the address it resolves to does not. localhost stands in
        // for a rebinding answer by giving the allowlisted host's handler a loopback target.
        _lfsHref = "https://localhost/media/x";
        var services = new ServiceCollection();
        services.AddHttpClient(GitHubRepoSource.ApiClient);
        services.AddHttpClient(GitHubRepoSource.LfsClient).ConfigurePrimaryHttpMessageHandler(LfsGuard.CreateHandler);
        var options = Options.Create(new WebOptions
        {
            GitHub = new GitHubOptions { ApiBaseUrl = _address!.ToString().TrimEnd('/'), WebBaseUrl = _address.ToString().TrimEnd('/'), LfsHosts = ["localhost"] },
        });
        var source = new GitHubRepoSource(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), options, NullLogger.Instance);

        var result = await source.GetBlobAsync(Alice, "acme", "plant", new string('e', 40), 1000, CancellationToken.None);

        Assert.Equal(RepoFailure.Upstream, result.Failure);
    }

    [Theory]
    [InlineData("sso", RepoFailure.SsoRequired)]
    [InlineData("limited", RepoFailure.RateLimited)]
    [InlineData("forbidden", RepoFailure.NotFound)]
    [InlineData("missing", RepoFailure.NotFound)]
    public async Task GitHubs_refusals_become_what_the_page_can_say(string repo, RepoFailure expected)
    {
        var result = await Source().GetRepoAsync(Alice, "acme", repo, CancellationToken.None);

        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public async Task A_name_cannot_reshape_the_url()
    {
        await Source().GetRepoAsync(Alice, "acme", "plant/../../evil?x=", CancellationToken.None);

        Assert.Contains(_requests, r => r.Path.StartsWith("/repos/acme/plant%2F..%2F..%2Fevil%3F", StringComparison.Ordinal));
        Assert.DoesNotContain(_requests, r => r.Path.StartsWith("/evil", StringComparison.Ordinal));
    }
}
