using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Versio.Web.Sources;

namespace Versio.Web.Tests;

/// <summary>
/// The real web host on a free local port, with GitHub replaced: repositories by
/// <see cref="FakeGitHub"/>, sign-in by <see cref="FakeOAuth"/>.
/// </summary>
/// <remarks>
/// Cookies are handled by hand: the session cookie is <c>Secure</c> and <c>__Host-</c>, which a
/// browser honours on http://localhost but HttpClient's cookie jar will not send over plain
/// http. Reading Set-Cookie and sending Cookie keeps the test honest about what was set.
/// </remarks>
internal sealed class TestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    public FakeGitHub GitHub { get; }

    public HttpClient Client { get; }

    public Uri Address { get; }

    /// <summary>Where this host writes its daily log file.</summary>
    public string LogDirectory { get; private init; } = string.Empty;

    public IServiceProvider Services => _app.Services;

    private TestHost(WebApplication app, FakeGitHub github, Uri address)
    {
        _app = app;
        GitHub = github;
        Address = address;
        Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = address };
    }

    public static async Task<TestHost> StartAsync(FakeGitHub github, Dictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        var logs = Path.Combine(Path.GetTempPath(), "versio-web-tests", Guid.NewGuid().ToString("N"));
        var args = new List<string> { "--urls=http://127.0.0.1:0", $"--Web:LogDirectory={logs}", "--environment=Development" };
        args.AddRange((settings ?? []).Select(kv => $"--{kv.Key}={kv.Value}"));

        var app = WebHost.Build([.. args], builder =>
        {
            builder.Services.AddSingleton<IRepoSource>(github);
            services?.Invoke(builder.Services);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TestHost(app, github, new Uri(address)) { LogDirectory = logs };
    }

    /// <summary>A GET with the given cookie header, or none.</summary>
    public Task<HttpResponseMessage> GetAsync(string path, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return Client.SendAsync(request);
    }

    /// <summary>The value a response set for <paramref name="name"/>, or null.</summary>
    public static string? SetCookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).Where(v => v.StartsWith(name + "=", StringComparison.Ordinal)).Select(v => v[(name.Length + 1)..]).LastOrDefault()
            : null;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// GitHub's sign-in endpoints, answering on a local port: the token exchange, the user, revoke.
/// </summary>
internal sealed class FakeOAuth : IAsyncDisposable
{
    private readonly WebApplication _app;

    public Uri Address { get; }

    public ConcurrentQueue<Dictionary<string, string>> Exchanges { get; } = new();

    public ConcurrentQueue<string> Revoked { get; } = new();

    /// <summary>Refresh tokens that have not been used yet.</summary>
    public ConcurrentDictionary<string, bool> LiveRefreshTokens { get; } = new();

    private int _issued;

    private FakeOAuth(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    public static async Task<FakeOAuth> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeOAuth? self = null;

        app.MapPost("/login/oauth/access_token", async (HttpContext http) =>
        {
            var form = (await http.Request.ReadFormAsync()).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            self!.Exchanges.Enqueue(form);
            if (form.GetValueOrDefault("grant_type") == "refresh_token")
            {
                // As GitHub: a refresh token works once, and each renewal issues a new one.
                if (!self.LiveRefreshTokens.TryRemove(form.GetValueOrDefault("refresh_token") ?? "", out _))
                {
                    return Results.Json(new { error = "bad_refresh_token" });
                }

                var next = $"alice-refresh-{Interlocked.Increment(ref self._issued)}";
                self.LiveRefreshTokens[next] = true;
                return Results.Json(new { access_token = "alice-token", expires_in = 28800, refresh_token = next, refresh_token_expires_in = 15897600 });
            }

            if (form.GetValueOrDefault("code") != "good")
            {
                return Results.Json(new { error = "bad_verification_code" });
            }

            self.LiveRefreshTokens["alice-refresh"] = true;
            return Results.Json(new { access_token = "alice-token", expires_in = 28800, refresh_token = "alice-refresh", refresh_token_expires_in = 15897600 });
        });
        app.MapGet("/user", (HttpContext http) =>
            http.Request.Headers.Authorization == "Bearer alice-token"
                ? Results.Json(new { id = 42, login = "alice", name = "Alice", avatar_url = "https://avatars.githubusercontent.com/u/42" })
                : Results.StatusCode(401));
        app.MapDelete("/applications/{client}/token", async (HttpContext http) =>
        {
            self!.Revoked.Enqueue(await new StreamReader(http.Request.Body).ReadToEndAsync());
            return Results.NoContent();
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new FakeOAuth(app, new Uri(address));
        return self;
    }

    public Dictionary<string, string?> Settings() => new()
    {
        ["Web:GitHub:WebBaseUrl"] = Address.ToString().TrimEnd('/'),
        ["Web:GitHub:ApiBaseUrl"] = Address.ToString().TrimEnd('/'),
        ["Web:GitHub:ClientId"] = "client-id",
        ["Web:GitHub:ClientSecret"] = "client-secret",
    };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal static class HttpExtensions
{
    public static async Task<string> Body(this HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    public static void AssertStatus(this HttpResponseMessage response, HttpStatusCode expected) =>
        Xunit.Assert.True(response.StatusCode == expected, $"expected {expected}, got {response.StatusCode}");
}
