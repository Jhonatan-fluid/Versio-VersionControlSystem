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

    /// <summary>A real LFS object: its content, and the pointer's oid for it.</summary>
    private static readonly byte[] LfsContent = Encoding.UTF8.GetBytes("<Document>the real file</Document>\n");

    private static readonly string RealOid = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(LfsContent)).ToLowerInvariant();

    /// <summary>The LFS download host, answered in memory: the guard requires https on 443.</summary>
    private sealed class LfsStore(byte[] answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(answer) });
    }

    private WebApplication? _api;
    private Uri? _address;
    private readonly ConcurrentQueue<(string Path, string? Auth)> _requests = new();
    private string _lfsHref = "https://169.254.169.254/latest/meta-data/";
    private readonly ConcurrentQueue<string> _appJwts = new();
    private readonly ConcurrentQueue<string> _mintBodies = new();
    private readonly TaskCompletionSource _releaseHeld = new();

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
        app.MapGet("/repos/acme/plant/commits/main", (HttpContext http) =>
            // GitHub's SHA-only answer: forty characters, no JSON, no patches.
            http.Request.Headers.Accept.ToString() == "application/vnd.github.sha"
                ? Results.Text(new string('c', 40), "application/vnd.github.sha")
                : Results.StatusCode(415));
        app.MapGet("/repos/acme/plant/branches", (int per_page, int page) =>
            // 250 branches, in GitHub's pages.
            Results.Json(Enumerable.Range(0, 250).Skip((page - 1) * per_page).Take(per_page)
                .Select(i => new { name = $"b{i}", commit = new { sha = new string('f', 40) } })));
        app.MapGet("/repos/acme/endless/branches", (int per_page, int page) =>
            Results.Json(Enumerable.Range(0, per_page).Select(i => new { name = $"p{page}-{i}", commit = new { sha = new string('f', 40) } })));
        app.MapGet("/repos/acme/broken", () => Results.Json(new { unexpected = true }));
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
                'f' => Results.Text($"version https://git-lfs.github.com/spec/v1\noid sha256:{RealOid}\nsize {LfsContent.Length}\n", "application/vnd.github.raw"),
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
        app.MapGet("/repos/acme/held/installation", async () =>
        {
            // GitHub slow to answer for one repository.
            await _releaseHeld.Task;
            return Results.Json(new { id = 77 });
        });
        app.MapGet("/repos/acme/plant/installation", (HttpContext http) =>
        {
            _appJwts.Enqueue(http.Request.Headers.Authorization.ToString());
            return Results.Json(new { id = 77 });
        });
        app.MapPost("/app/installations/77/access_tokens", async (HttpContext http) =>
        {
            _appJwts.Enqueue(http.Request.Headers.Authorization.ToString());
            _mintBodies.Enqueue(await new StreamReader(http.Request.Body).ReadToEndAsync());
            return Results.Json(new { token = "installation-token", expires_at = DateTimeOffset.UtcNow.AddHours(1) });
        });

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
    public async Task Every_branch_is_listed_across_GitHubs_pages_up_to_the_cap()
    {
        // Found in review: one page of 100 was read, so branches past it were missing.
        var source = Source();

        var some = await source.GetBranchesAsync(Alice, "acme", "plant", CancellationToken.None);
        var endless = await source.GetBranchesAsync(Alice, "acme", "endless", CancellationToken.None);

        Assert.Equal(250, some.Value!.Branches.Count);
        Assert.Equal("b249", some.Value.Branches[^1].Name);
        Assert.False(some.Value.Truncated);
        Assert.Equal(3, _requests.Count(r => r.Path.StartsWith("/repos/acme/plant/branches", StringComparison.Ordinal)));
        Assert.Equal(GitHubRepoSource.BranchPages * GitHubRepoSource.BranchPageSize, endless.Value!.Branches.Count);
        Assert.True(endless.Value.Truncated);
        // The cap's pages, and one past it to learn whether the list was cut.
        Assert.Equal(GitHubRepoSource.BranchPages + 1, _requests.Count(r => r.Path.StartsWith("/repos/acme/endless/branches", StringComparison.Ordinal)));
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_LFS_download_is_the_file_only_when_it_matches_its_pointer(bool tampered)
    {
        // Found in review: the downloaded bytes were never checked against the pointer's SHA-256,
        // so whatever the download host answered was drawn as this commit's file.
        _lfsHref = "https://lfs.example/media/x";
        var answer = tampered ? Encoding.UTF8.GetBytes("<Document>the fake file</Document>\n") : LfsContent;
        var services = new ServiceCollection();
        services.AddHttpClient(GitHubRepoSource.ApiClient);
        services.AddHttpClient(GitHubRepoSource.LfsClient).ConfigurePrimaryHttpMessageHandler(() => new LfsStore(answer));
        var options = Options.Create(new WebOptions
        {
            GitHub = new GitHubOptions { ApiBaseUrl = _address!.ToString().TrimEnd('/'), WebBaseUrl = _address.ToString().TrimEnd('/'), LfsHosts = ["lfs.example"] },
        });
        var source = new GitHubRepoSource(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), options, NullLogger.Instance);

        var result = await source.GetBlobAsync(Alice, "acme", "plant", new string('f', 40), 1000, CancellationToken.None);

        if (tampered)
        {
            Assert.Equal(RepoFailure.Upstream, result.Failure);
        }
        else
        {
            Assert.Equal(LfsContent, result.Value);
        }
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
    public async Task An_answer_in_the_wrong_shape_is_upstream_not_an_exception()
    {
        var result = await Source().GetRepoAsync(Alice, "acme", "broken", CancellationToken.None);

        Assert.Equal(RepoFailure.Upstream, result.Failure);
    }

    [Fact]
    public async Task An_unreachable_GitHub_is_upstream_for_json_and_for_blobs()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(GitHubRepoSource.ApiClient);
        services.AddHttpClient(GitHubRepoSource.LfsClient);
        var options = Options.Create(new WebOptions { GitHub = new GitHubOptions { ApiBaseUrl = "http://127.0.0.1:1", WebBaseUrl = "http://127.0.0.1:1" } });
        var source = new GitHubRepoSource(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), options, NullLogger.Instance);

        Assert.Equal(RepoFailure.Upstream, (await source.GetRepoAsync(Alice, "acme", "plant", CancellationToken.None)).Failure);
        Assert.Equal(RepoFailure.Upstream, (await source.GetBlobAsync(Alice, "acme", "plant", new string('a', 40), 100, CancellationToken.None)).Failure);
        Assert.Equal(RepoFailure.Upstream, (await source.ResolveCommitAsync(Alice, "acme", "plant", "main", CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task One_repositorys_slow_mint_does_not_hold_up_another_repositorys()
    {
        // Found in review: one lock serialised all minting, held across GitHub's round trips, so
        // one slow answer stalled every anonymous visitor to every repository.
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var options = Options.Create(new WebOptions
        {
            GitHub = new GitHubOptions { ApiBaseUrl = _address!.ToString().TrimEnd('/'), AppId = "123", PrivateKeyPem = rsa.ExportRSAPrivateKeyPem() },
        });
        var services = new ServiceCollection();
        services.AddHttpClient(GitHubRepoSource.ApiClient);
        var tokens = new Versio.Web.Auth.AppTokens(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), options,
            new Versio.Core.Abstractions.FakeClock(DateTimeOffset.UtcNow), new Versio.Web.Auth.PemAppSigner(options));

        var held = tokens.ForRepositoryAsync("acme", "held", CancellationToken.None);
        await Task.Delay(100);
        var other = await tokens.ForRepositoryAsync("acme", "plant", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        _releaseHeld.SetResult();

        Assert.Equal("installation-token", other);
        Assert.Equal("installation-token", await held);
    }

    [Fact]
    public async Task An_installation_token_is_minted_for_one_repository_read_only_with_a_valid_app_jwt()
    {
        // Security rule 1's other half: even the App's own credential reads one repository, read
        // only. And the JWT that asks for it is RS256-signed by the App's key (through IAppSigner,
        // the seam a key vault plugs into) with this App's id, valid minutes, not hours.
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var options = Options.Create(new WebOptions
        {
            GitHub = new GitHubOptions { ApiBaseUrl = _address!.ToString().TrimEnd('/'), AppId = "123", PrivateKeyPem = rsa.ExportRSAPrivateKeyPem() },
        });
        var services = new ServiceCollection();
        services.AddHttpClient(GitHubRepoSource.ApiClient);
        var clock = new Versio.Core.Abstractions.FakeClock(DateTimeOffset.UtcNow);
        var tokens = new Versio.Web.Auth.AppTokens(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), options, clock, new Versio.Web.Auth.PemAppSigner(options));

        var token = await tokens.ForRepositoryAsync("acme", "plant", CancellationToken.None);

        Assert.Equal("installation-token", token);
        var body = System.Text.Json.JsonDocument.Parse(Assert.Single(_mintBodies)).RootElement;
        Assert.Equal(["plant"], body.GetProperty("repositories").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("read", body.GetProperty("permissions").GetProperty("contents").GetString());
        Assert.Equal("read", body.GetProperty("permissions").GetProperty("metadata").GetString());
        Assert.Equal(2, body.GetProperty("permissions").EnumerateObject().Count());

        foreach (var header in _appJwts)
        {
            var parts = header["Bearer ".Length..].Split('.');
            Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]),
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));
            var claims = System.Text.Json.JsonDocument.Parse(FromBase64Url(parts[1])).RootElement;
            Assert.Equal("123", claims.GetProperty("iss").GetString());
            Assert.True(claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64() <= 600);
        }
    }

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
    }

    [Fact]
    public async Task A_name_cannot_reshape_the_url()
    {
        await Source().GetRepoAsync(Alice, "acme", "plant/../../evil?x=", CancellationToken.None);

        Assert.Contains(_requests, r => r.Path.StartsWith("/repos/acme/plant%2F..%2F..%2Fevil%3F", StringComparison.Ordinal));
        Assert.DoesNotContain(_requests, r => r.Path.StartsWith("/evil", StringComparison.Ordinal));
    }
}
