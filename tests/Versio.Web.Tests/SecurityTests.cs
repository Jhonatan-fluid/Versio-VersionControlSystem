using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Versio.Web.Auth;
using Versio.Web.Http;
using Versio.Web.Sources;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// docs/web/PLAN.md's security rules, against the real host: each test names the rule and the
/// attack it stops.
/// </summary>
public sealed class SecurityTests
{
    private const string Settings = "fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/Library/Alarm/DUTs/ST_AlarmSettings.st";

    private static FakeGitHub GitHub(out FakeGitHub.FakeRepo open, out FakeGitHub.FakeRepo secret)
    {
        var github = new FakeGitHub();
        var files = FakeGitHub.FromDisk(".versio/repo.json", "README.md", "fixtures/projects/RefrigeratorControl");
        open = github.Add("acme", "plant");
        github.Commit(open, "main", files);
        secret = github.Add("acme", "secret", isPrivate: true);
        secret.Readers.Add("alice-token");
        github.Commit(secret, "main", files);
        return github;
    }

    /// <summary>Signs alice in directly, through the store the callback uses, and returns her cookie.</summary>
    private static string SignIn(TestHost host, string token = "alice-token", string login = "alice")
    {
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = sessions.Create(42, login, login, "", token, DateTimeOffset.UtcNow.AddHours(8), null, DateTimeOffset.UtcNow.AddMonths(6));
        return $"{AccessResolver.SessionCookie}={session.Id}";
    }

    [Fact]
    public async Task Rule1_a_private_repository_asked_for_anonymously_is_not_found()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _, out _));

        var response = await host.GetAsync("/api/repos/acme/secret/tree");

        response.AssertStatus(HttpStatusCode.NotFound);
        Assert.DoesNotContain("ST_AlarmSettings", await response.Body(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rule1_the_installation_token_seeing_a_private_repository_does_not_let_an_anonymous_visitor_in()
    {
        // The App is installed and its token CAN read the private repository (the fake lets an
        // installation token see it, as GitHub does). The resolver must still refuse, because the
        // visitor is nobody, and the public repository must still open with that token.
        var github = GitHub(out _, out _);
        await using var host = await TestHost.StartAsync(github, services: s => s.AddSingleton<IInstallationTokens>(new InstalledEverywhere()));

        var secret = await host.GetAsync("/api/repos/acme/secret/tree");
        var open = await host.GetAsync("/api/repos/acme/plant/tree");

        secret.AssertStatus(HttpStatusCode.NotFound);
        Assert.Contains(github.Reads, r => r.Repo == "acme/secret" && r.Access.Kind == AccessKind.Installation);
        Assert.DoesNotContain(github.Reads, r => r.Repo == "acme/secret" && r.What.StartsWith("tree", StringComparison.Ordinal));
        open.AssertStatus(HttpStatusCode.OK);
        Assert.Contains(github.Reads, r => r.Repo == "acme/plant" && r.Access.Kind == AccessKind.Installation && r.What.StartsWith("tree", StringComparison.Ordinal));
    }

    private sealed class InstalledEverywhere : IInstallationTokens
    {
        public Task<string?> ForRepositoryAsync(string owner, string repo, CancellationToken ct) => Task.FromResult<string?>(FakeGitHub.InstallationToken);
    }

    [Fact]
    public async Task Rule1_a_signed_in_request_reads_with_the_persons_own_token_and_nothing_else()
    {
        var github = GitHub(out _, out _);
        await using var host = await TestHost.StartAsync(github);
        var cookie = SignIn(host);

        var response = await host.GetAsync("/api/repos/acme/secret/tree", cookie);

        response.AssertStatus(HttpStatusCode.OK);
        var reads = github.Reads.Where(r => r.Repo == "acme/secret").ToList();
        Assert.NotEmpty(reads);
        Assert.All(reads, r => Assert.Equal(AccessKind.User, r.Access.Kind));
        Assert.All(reads, r => Assert.Equal("alice-token", r.Access.Token));
    }

    [Fact]
    public async Task Rule2_what_one_person_loaded_is_not_served_to_another_who_cannot_see_it()
    {
        var github = GitHub(out _, out _);
        await using var host = await TestHost.StartAsync(github);
        var alice = SignIn(host);
        var bob = SignIn(host, token: "bob-token", login: "bob");

        var hers = await host.GetAsync($"/api/repos/acme/secret/view?path={Uri.EscapeDataString(Settings)}", alice);
        var his = await host.GetAsync($"/api/repos/acme/secret/view?path={Uri.EscapeDataString(Settings)}", bob);

        hers.AssertStatus(HttpStatusCode.OK);
        his.AssertStatus(HttpStatusCode.NotFound);
        Assert.DoesNotContain("ST_AlarmSettings", await his.Body(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rule2_api_responses_are_never_stored_by_a_proxy()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _, out _));

        var response = await host.GetAsync("/api/repos/acme/plant/tree");

        Assert.True(response.Headers.CacheControl is { NoStore: true, Private: true }, response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Rule6_every_response_carries_the_csp_and_the_privacy_headers()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _, out _));

        foreach (var path in new[] { "/api/me", "/assets/code.css", "/anything" })
        {
            var response = await host.GetAsync(path);
            Assert.Equal(SecurityHeaders.Csp, response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Contains("script-src 'self';", SecurityHeaders.Csp, StringComparison.Ordinal);
            Assert.DoesNotContain("'unsafe-inline'; img", SecurityHeaders.Csp.Replace("style-src-attr 'unsafe-inline'; ", "", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("noindex, nofollow", response.Headers.GetValues("X-Robots-Tag").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        }
    }

    [Fact]
    public void Rule6_the_policy_allows_no_inline_or_remote_script()
    {
        var script = Regex.Match(SecurityHeaders.Csp, "script-src ([^;]*);").Groups[1].Value;

        Assert.Equal("'self'", script);
    }

    [Fact]
    public async Task Rule8_a_client_over_its_rate_is_refused()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _, out _), new() { ["Web:RequestsPerMinute"] = "3" });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await host.GetAsync("/api/me")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.OK));
    }

    [Fact]
    public async Task Rule14_a_session_cookie_is_not_trusted_after_the_epoch_moves()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _, out _));
        var cookie = SignIn(host);
        host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebOptions>>().Value.SessionEpoch++;

        var response = await host.GetAsync("/api/repos/acme/secret/tree", cookie);

        response.AssertStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rule17_anonymous_access_switched_off_asks_everyone_to_sign_in()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _, out _), new() { ["Web:AnonymousAccess"] = "false" });

        var response = await host.GetAsync("/api/repos/acme/plant/tree");

        response.AssertStatus(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("../.versio/repo.json")]
    [InlineData("fixtures/../README.md")]
    [InlineData("/etc/passwd")]
    [InlineData("a//b")]
    public async Task Hygiene_a_path_that_climbs_or_is_absolute_is_not_found(string path)
    {
        var github = GitHub(out _, out _);
        await using var host = await TestHost.StartAsync(github);

        var response = await host.GetAsync($"/api/repos/acme/plant/view?path={Uri.EscapeDataString(path)}");

        response.AssertStatus(HttpStatusCode.NotFound);

        // Refused before anything reached GitHub. A 404 alone would pass without the check too,
        // because the tree simply has no such path; the first version of this test did exactly that.
        Assert.Empty(github.Reads);
    }

    [Theory]
    [InlineData("acme", "plant%2F..%2Fsecret")]
    [InlineData("ac me", "plant")]
    [InlineData("acme", "..")]
    public async Task Hygiene_an_owner_or_repository_outside_GitHubs_alphabet_is_not_found(string owner, string repo)
    {
        var github = GitHub(out _, out _);
        await using var host = await TestHost.StartAsync(github);

        var response = await host.GetAsync($"/api/repos/{owner}/{repo}/tree");

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, response.StatusCode.ToString());
        Assert.Empty(github.Reads);
    }

    [Fact]
    public async Task Hygiene_an_unknown_api_route_is_404_not_the_page()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _, out _));

        var response = await host.GetAsync("/api/nothing/here");

        response.AssertStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_pasted_link_to_a_file_with_an_extension_opens_the_page()
    {
        // Found in the browser: the default fallback skips file-like paths, so every link to a
        // .st or .xml was a 404 on reload. The page is whatever index.html the host serves.
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(root);
        if (!File.Exists(Path.Combine(root, "index.html")))
        {
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><title>Versio</title>");
        }

        Directory.CreateDirectory(Path.Combine(root, "assets"));
        await File.WriteAllTextAsync(Path.Combine(root, "assets", "probe-test.js"), "// a built asset");

        await using var host = await TestHost.StartAsync(GitHub(out _, out _));

        var file = await host.GetAsync("/acme/plant/blob/main/Device/Plc%20Logic/Signals.xml");
        var compare = await host.GetAsync("/acme/plant/compare/main...next?path=a.st");
        var builtAsset = await host.GetAsync("/assets/probe-test.js");
        var missingAsset = await host.GetAsync("/assets/nothing.js");

        file.AssertStatus(HttpStatusCode.OK);
        Assert.Equal("text/html", file.Content.Headers.ContentType?.MediaType);
        compare.AssertStatus(HttpStatusCode.OK);

        // The page's own script must still be served as itself, not swallowed by a fallback:
        // the first version of this fix 404'd every built asset and this test did not notice.
        builtAsset.AssertStatus(HttpStatusCode.OK);
        Assert.Equal("// a built asset", await builtAsset.Body());
        missingAsset.AssertStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Hygiene_logout_without_the_request_header_is_refused_and_ends_nothing()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _, out _));
        var cookie = SignIn(host);

        var forged = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        forged.Headers.Add("Cookie", cookie);
        var refused = await host.Client.SendAsync(forged);

        refused.AssertStatus(HttpStatusCode.Forbidden);
        (await host.GetAsync("/api/repos/acme/secret/tree", cookie)).AssertStatus(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/acme/plant/blob/main/README.md", "/acme/plant/blob/main/README.md")]
    [InlineData(null, "/")]
    public void SignIn_returns_only_to_this_sites_own_pages(string? asked, string landed)
    {
        Assert.Equal(landed, AuthEndpoints.SafeReturn(asked));
    }

    /// <remarks>
    /// <para>
    /// <b>In its own process, because it cannot be proved in this one.</b> The timeout is a process
    /// default read once, the first time anything touches Regex, and the test runner uses Regex
    /// before this assembly loads. The first version asserted it here and failed for exactly that
    /// reason, which is also the reason Program.cs sets it on its first line.
    /// </para>
    /// <para>
    /// So the built server is started as itself, and its startup log must say it started and must
    /// not carry the warning WebHost writes when the timeout did not take. It is also the smoke
    /// test that the real executable starts at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Rule3_the_real_server_starts_with_the_regex_timeout_in_force()
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var server = Path.Combine(AppContext.BaseDirectory, "Versio.Web.dll");
        var logs = Path.Combine(Path.GetTempPath(), "versio-web-tests", Guid.NewGuid().ToString("N"));
        var start = new System.Diagnostics.ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var arg in new[] { "exec", server, "--urls=http://127.0.0.1:0", $"--Web:LogDirectory={logs}" })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = new StringBuilder();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!timeout.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    break;
                }

                output.AppendLine(line);
                if (line.Contains("Versio Web starting", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        var log = output.ToString();
        Assert.Contains("Versio Web starting", log, StringComparison.Ordinal);
        Assert.Contains("regex timeout 2 s in force", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rule7_tokens_are_encrypted_at_rest_in_the_session_store()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _, out _));
        var sessions = host.Services.GetRequiredService<SessionStore>();

        var session = sessions.Create(1, "alice", "Alice", "", "ghu_plaintext_token", DateTimeOffset.UtcNow.AddHours(1), "ghr_refresh", DateTimeOffset.UtcNow.AddDays(1));

        Assert.DoesNotContain("ghu_plaintext_token", session.ProtectedAccessToken, StringComparison.Ordinal);
        Assert.DoesNotContain("ghr_refresh", session.ProtectedRefreshToken, StringComparison.Ordinal);
        Assert.Equal("ghu_plaintext_token", sessions.AccessToken(session));
    }

    [Fact]
    public async Task Rule14_each_sign_in_gets_a_new_unguessable_session_id()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _, out _));
        var sessions = host.Services.GetRequiredService<SessionStore>();

        var ids = Enumerable.Range(0, 20).Select(_ => sessions.Create(1, "a", "a", "", "t", DateTimeOffset.UtcNow.AddHours(1), null, default).Id).ToList();

        Assert.Equal(20, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.True(id.Length >= 43, id));
    }

    [Fact]
    public async Task The_full_sign_in_round_trip_uses_PKCE_and_a_state_that_must_match()
    {
        await using var oauth = await FakeOAuth.StartAsync();
        var github = GitHub(out _, out _);
        await using var host = await TestHost.StartAsync(github, oauth.Settings());

        var login = await host.GetAsync("/auth/login?returnUrl=%2Facme%2Fsecret");
        login.AssertStatus(HttpStatusCode.Redirect);
        var authorize = login.Headers.Location!;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorize.Query);
        var loginCookie = TestHost.SetCookie(login, AuthEndpoints.LoginCookie);
        Assert.NotNull(loginCookie);
        Assert.Equal("S256", query["code_challenge_method"].ToString());

        // A state that is not the one issued is refused, whatever the code.
        var forged = await host.GetAsync($"/auth/callback?code=good&state=forged", $"{AuthEndpoints.LoginCookie}={loginCookie}");
        Assert.Equal("/?signin=failed", forged.Headers.Location!.ToString());

        var login2 = await host.GetAsync("/auth/login?returnUrl=%2Facme%2Fsecret");
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(login2.Headers.Location!.Query)["state"].ToString();
        var challenge = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(login2.Headers.Location!.Query)["code_challenge"].ToString();
        var cookie2 = TestHost.SetCookie(login2, AuthEndpoints.LoginCookie);

        var callback = await host.GetAsync($"/auth/callback?code=good&state={Uri.EscapeDataString(state)}", $"{AuthEndpoints.LoginCookie}={cookie2}");

        callback.AssertStatus(HttpStatusCode.Redirect);
        Assert.Equal("/acme/secret", callback.Headers.Location!.ToString());
        var session = TestHost.SetCookie(callback, AccessResolver.SessionCookie);
        Assert.NotNull(session);
        Assert.Contains(callback.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AccessResolver.SessionCookie, StringComparison.Ordinal)
            && c.Contains("httponly", StringComparison.OrdinalIgnoreCase) && c.Contains("secure", StringComparison.OrdinalIgnoreCase));

        // The verifier GitHub received hashes to the challenge the browser was sent.
        var exchange = oauth.Exchanges.Last(e => e.GetValueOrDefault("code") == "good");
        var hashed = SessionStore.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(exchange["code_verifier"])));
        Assert.Equal(challenge, hashed);
        Assert.Equal("client-secret", exchange["client_secret"]);

        var me = await host.GetAsync("/api/me", $"{AccessResolver.SessionCookie}={session}");
        Assert.Contains("\"login\":\"alice\"", await me.Body(), StringComparison.Ordinal);
        (await host.GetAsync("/api/repos/acme/secret/tree", $"{AccessResolver.SessionCookie}={session}")).AssertStatus(HttpStatusCode.OK);

        // Logout revokes at GitHub and ends the session here.
        var logout = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        logout.Headers.Add("Cookie", $"{AccessResolver.SessionCookie}={session}");
        logout.Headers.Add(AuthEndpoints.CsrfHeader, "1");
        (await host.Client.SendAsync(logout)).AssertStatus(HttpStatusCode.NoContent);
        Assert.Contains(oauth.Revoked, body => body.Contains("alice-token", StringComparison.Ordinal));
        (await host.GetAsync("/api/repos/acme/secret/tree", $"{AccessResolver.SessionCookie}={session}")).AssertStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Four_requests_at_once_near_token_expiry_renew_once_and_nobody_is_signed_out()
    {
        // The page asks for four things at once on every load. With a single-use refresh token,
        // parallel renewals all but the first failed and ended the session (found reviewing).
        await using var oauth = await FakeOAuth.StartAsync();
        await using var host = await TestHost.StartAsync(GitHub(out _, out _), oauth.Settings());
        oauth.LiveRefreshTokens["alice-refresh"] = true;
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = sessions.Create(42, "alice", "Alice", "", "alice-token", DateTimeOffset.UtcNow.AddMinutes(1), "alice-refresh", DateTimeOffset.UtcNow.AddDays(30));
        var cookie = $"{AccessResolver.SessionCookie}={session.Id}";

        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.GetAsync("/api/repos/acme/secret/tree", cookie)));

        Assert.All(answers, a => a.AssertStatus(HttpStatusCode.OK));
        Assert.Single(oauth.Exchanges, e => e.GetValueOrDefault("grant_type") == "refresh_token");
        Assert.NotNull(sessions.Peek(session.Id));
    }

    [Fact]
    public async Task Signing_in_again_ends_the_browsers_earlier_session()
    {
        await using var oauth = await FakeOAuth.StartAsync();
        await using var host = await TestHost.StartAsync(GitHub(out _, out _), oauth.Settings());
        var earlier = SignIn(host);

        var login = await host.GetAsync("/auth/login");
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(login.Headers.Location!.Query)["state"].ToString();
        var loginCookie = TestHost.SetCookie(login, AuthEndpoints.LoginCookie);
        await host.GetAsync($"/auth/callback?code=good&state={Uri.EscapeDataString(state)}", $"{AuthEndpoints.LoginCookie}={loginCookie}; {earlier}");

        (await host.GetAsync("/api/repos/acme/secret/tree", earlier)).AssertStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sign_in_is_not_offered_when_no_App_is_configured()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _, out _));

        (await host.GetAsync("/auth/login")).AssertStatus(HttpStatusCode.NotFound);
        Assert.Contains("\"signInAvailable\":false", await (await host.GetAsync("/api/me")).Body(), StringComparison.Ordinal);
    }
}
