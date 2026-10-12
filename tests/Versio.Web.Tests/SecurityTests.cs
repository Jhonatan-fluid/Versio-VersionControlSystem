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
        var session = sessions.CreateAsync(42, login, login, "", token, DateTimeOffset.UtcNow.AddHours(8), null, DateTimeOffset.UtcNow.AddMonths(6)).GetAwaiter().GetResult();
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
    public async Task Rule8_a_comparison_lists_at_most_its_cap_and_says_how_many_there_were()
    {
        var github = new FakeGitHub();
        var repo = github.Add("acme", "plant");
        github.Commit(repo, "main", Enumerable.Range(0, 30).ToDictionary(i => $"f{i}.st", i => Encoding.UTF8.GetBytes($"a{i}")));
        github.Commit(repo, "next", Enumerable.Range(0, 30).ToDictionary(i => $"f{i}.st", i => Encoding.UTF8.GetBytes($"b{i}")));
        await using var host = await TestHost.StartAsync(github, new() { ["Web:MaxComparedFiles"] = "10" });

        var body = await (await host.GetAsync("/api/repos/acme/plant/compare?base=main&head=next")).Body();

        Assert.Contains("\"total\":30", body, StringComparison.Ordinal);
        Assert.Contains("\"truncated\":true", body, StringComparison.Ordinal);
        Assert.Equal(10, Regex.Matches(body, "\"change\":").Count);
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

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.0/8")]
    public async Task Rule8_behind_a_trusted_proxy_each_client_has_its_own_rate(string proxy)
    {
        // Found in review: behind a load balancer every request came from the balancer's address,
        // so the per-address limit was one bucket for everyone. The test's own connection stands
        // in for the balancer.
        await using var host = await TestHost.StartAsync(GitHub(out _, out _), new() { ["Web:RequestsPerMinute"] = "2", ["Web:TrustedProxies:0"] = proxy });

        var busy = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            busy.Add((await From(host, "203.0.113.7")).StatusCode);
        }

        var other = await From(host, "198.51.100.9");

        Assert.Equal(HttpStatusCode.TooManyRequests, busy[^1]);
        other.AssertStatus(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("10.0.0.1")]
    public async Task Rule8_a_forwarded_header_from_anyone_not_trusted_chooses_nothing(string? proxy)
    {
        // From anyone but a trusted proxy, X-Forwarded-For is a header the client wrote: believing
        // it would let one client take a new rate bucket per request. With no proxy configured at
        // all, ASP.NET's middleware would believe everyone (see WebHost.ForwardedFrom).
        var settings = new Dictionary<string, string?> { ["Web:RequestsPerMinute"] = "2" };
        if (proxy is not null)
        {
            settings["Web:TrustedProxies:0"] = proxy;
        }

        await using var host = await TestHost.StartAsync(GitHub(out _, out _), settings);

        await From(host, "203.0.113.1");
        await From(host, "203.0.113.2");
        var third = await From(host, "203.0.113.3");

        third.AssertStatus(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public void A_trusted_proxy_that_is_not_an_address_stops_startup()
    {
        Assert.Throws<InvalidOperationException>(() => WebHost.ForwardedFrom(["10.0.0.0/99"]));
        Assert.Throws<InvalidOperationException>(() => WebHost.ForwardedFrom(["proxy.internal"]));
    }

    private static Task<HttpResponseMessage> From(TestHost host, string client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("X-Forwarded-For", client);
        return host.Client.SendAsync(request);
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
    public async Task Only_the_diagram_frame_allows_inline_styles_and_only_this_site_may_frame_it()
    {
        // Mermaid needs inline <style>; the page's policy must not allow it. The frame page alone
        // does, may be framed by this site and no other, and its scripts load into an opaque origin.
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(root);
        // Only when the built page is not there: the first version overwrote the real frame with
        // this stub, and FixturePreview, which serves from the same folder, then drew nothing.
        foreach (var (name, stub) in new[] { ("mermaid-frame.html", "<!doctype html><title>Diagram</title>"), ("mermaid-frame.js", "// frame") })
        {
            if (!File.Exists(Path.Combine(root, name)))
            {
                await File.WriteAllTextAsync(Path.Combine(root, name), stub);
            }
        }
        await using var host = await TestHost.StartAsync(GitHub(out _, out _));

        var frame = await host.GetAsync(SecurityHeaders.MermaidFrame);
        var page = await host.GetAsync("/acme/plant");
        var mermaid = await host.GetAsync("/assets/mermaid.js");
        var glue = await host.GetAsync("/mermaid-frame.js");

        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, SecurityHeaders.FrameCsp, host.Address.GetLeftPart(UriPartial.Authority)), frame.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("'self'; style", frame.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("SAMEORIGIN", frame.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.DoesNotContain("style-src 'self' 'unsafe-inline'", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        mermaid.AssertStatus(HttpStatusCode.OK);
        Assert.True((await mermaid.Content.ReadAsByteArrayAsync()).Length > 1_000_000, "the vendored mermaid.min.js");
        Assert.Equal("cross-origin", mermaid.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Equal("cross-origin", glue.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Equal("same-origin", page.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
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

        var session = await sessions.CreateAsync(1, "alice", "Alice", "", "ghu_plaintext_token", DateTimeOffset.UtcNow.AddHours(1), "ghr_refresh", DateTimeOffset.UtcNow.AddDays(1));

        Assert.DoesNotContain("ghu_plaintext_token", session.ProtectedAccessToken, StringComparison.Ordinal);
        Assert.DoesNotContain("ghr_refresh", session.ProtectedRefreshToken, StringComparison.Ordinal);
        Assert.Equal("ghu_plaintext_token", sessions.AccessToken(session));
    }

    [Fact]
    public async Task Rule14_each_sign_in_gets_a_new_unguessable_session_id()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _, out _));
        var sessions = host.Services.GetRequiredService<SessionStore>();

        var ids = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            ids.Add((await sessions.CreateAsync(1, "a", "a", "", "t", DateTimeOffset.UtcNow.AddHours(1), null, default)).Id);
        }

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
        var session = await sessions.CreateAsync(42, "alice", "Alice", "", "alice-token", DateTimeOffset.UtcNow.AddMinutes(1), "alice-refresh", DateTimeOffset.UtcNow.AddDays(30));
        var cookie = $"{AccessResolver.SessionCookie}={session.Id}";

        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.GetAsync("/api/repos/acme/secret/tree", cookie)));

        Assert.All(answers, a => a.AssertStatus(HttpStatusCode.OK));
        Assert.Single(oauth.Exchanges, e => e.GetValueOrDefault("grant_type") == "refresh_token");
        Assert.NotNull(await sessions.PeekAsync(session.Id));
    }

    [Fact]
    public async Task A_renewal_refused_because_another_instance_won_finds_that_instances_write()
    {
        // Found in review: across instances, the loser of a renewal peeked once and then deleted
        // the session; when the winner's write landed between the two, the delete removed the
        // renewal. Here the refresh token was already spent elsewhere, and "the other instance"
        // writes its renewal a moment after this one is refused.
        await using var oauth = await FakeOAuth.StartAsync();
        await using var host = await TestHost.StartAsync(GitHub(out _, out _), oauth.Settings());
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = await sessions.CreateAsync(42, "alice", "Alice", "", "alice-token", DateTimeOffset.UtcNow.AddMinutes(1), "spent-refresh", DateTimeOffset.UtcNow.AddDays(30));

        var request = host.GetAsync("/api/repos/acme/secret/tree", $"{AccessResolver.SessionCookie}={session.Id}");
        await Task.Delay(AccessResolver.RenewalLookEvery * 2);
        await sessions.UpdateTokensAsync(session, "alice-token", DateTimeOffset.UtcNow.AddHours(8), "fresh-refresh", DateTimeOffset.UtcNow.AddDays(30));

        (await request).AssertStatus(HttpStatusCode.OK);
        Assert.Equal("fresh-refresh", sessions.RefreshToken((await sessions.PeekAsync(session.Id))!));
    }

    [Fact]
    public async Task A_renewal_nobody_won_answers_signed_out_and_deletes_nothing()
    {
        // Deleting bought nothing and cost a renewal (above); a refused renewal now leaves the
        // entry, which a new sign-in replaces and the store's own expiry ends.
        await using var oauth = await FakeOAuth.StartAsync();
        await using var host = await TestHost.StartAsync(GitHub(out _, out _), oauth.Settings());
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = await sessions.CreateAsync(42, "alice", "Alice", "", "alice-token", DateTimeOffset.UtcNow.AddMinutes(1), "spent-refresh", DateTimeOffset.UtcNow.AddDays(30));

        (await host.GetAsync("/api/repos/acme/secret/tree", $"{AccessResolver.SessionCookie}={session.Id}")).AssertStatus(HttpStatusCode.NotFound);
        Assert.NotNull(await sessions.PeekAsync(session.Id));
    }

    [Fact]
    public async Task A_sign_out_during_a_renewal_revokes_the_new_token_and_writes_nothing_back()
    {
        // Found in review: a renewal already talking to GitHub when the person signed out wrote
        // the session back with fresh tokens nobody held and nobody would revoke.
        await using var oauth = await FakeOAuth.StartAsync();
        oauth.LiveRefreshTokens["alice-refresh"] = true;
        oauth.HoldRefresh = new TaskCompletionSource();
        await using var host = await TestHost.StartAsync(GitHub(out _, out _), oauth.Settings());
        var sessions = host.Services.GetRequiredService<SessionStore>();
        var session = await sessions.CreateAsync(42, "alice", "Alice", "", "alice-token", DateTimeOffset.UtcNow.AddMinutes(1), "alice-refresh", DateTimeOffset.UtcNow.AddDays(30));

        var request = host.GetAsync("/api/repos/acme/secret/tree", $"{AccessResolver.SessionCookie}={session.Id}");
        for (var i = 0; i < 100 && !oauth.Exchanges.Any(e => e.GetValueOrDefault("grant_type") == "refresh_token"); i++)
        {
            await Task.Delay(20);
        }

        await sessions.RemoveAsync(session.Id);
        oauth.HoldRefresh.SetResult();

        (await request).AssertStatus(HttpStatusCode.NotFound);
        Assert.Null(await sessions.PeekAsync(session.Id));
        Assert.Single(oauth.Revoked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_sessions_without_a_shared_key_ring_are_warned_about_at_startup(bool keys)
    {
        // Found in review: Redis without Web:KeysDirectory shares sessions but not the keys that
        // seal them, so people are signed out at random. Startup says so; with a ring, it does not.
        var settings = new Dictionary<string, string?> { ["Web:Redis"] = "127.0.0.1:1" };
        if (keys)
        {
            settings["Web:KeysDirectory"] = Path.Combine(Path.GetTempPath(), "versio-web-keys", Guid.NewGuid().ToString("N"));
        }

        string log;
        await using (var host = await TestHost.StartAsync(GitHub(out _, out _), settings))
        {
            log = string.Concat(Directory.GetFiles(host.LogDirectory).Select(f =>
            {
                using var stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return new StreamReader(stream).ReadToEnd();
            }));
        }

        Assert.Equal(!keys, log.Contains("Web:Redis is set but Web:KeysDirectory is not", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_token_GitHub_gave_no_expiry_is_not_given_one()
    {
        // An App with expiry turned off answers with no expires_in and no refresh token. The first
        // version guessed eight hours, and with nothing to renew with, the session ended there
        // while GitHub still honoured the token (found in review).
        await using var oauth = await FakeOAuth.StartAsync();
        oauth.NonExpiring = true;
        await using var host = await TestHost.StartAsync(GitHub(out _, out _), oauth.Settings());
        var login = await host.GetAsync("/auth/login");
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(login.Headers.Location!.Query)["state"].ToString();
        var callback = await host.GetAsync($"/auth/callback?code=good&state={Uri.EscapeDataString(state)}", $"{AuthEndpoints.LoginCookie}={TestHost.SetCookie(login, AuthEndpoints.LoginCookie)}");
        var id = TestHost.SetCookie(callback, AccessResolver.SessionCookie)!;

        var sessions = host.Services.GetRequiredService<SessionStore>();
        var stored = await sessions.PeekAsync(id);

        Assert.NotNull(stored);
        Assert.Null(sessions.RefreshToken(stored!));
        Assert.Equal(DateTimeOffset.MaxValue, stored!.AccessExpires);
        (await host.GetAsync("/api/repos/acme/secret/tree", $"{AccessResolver.SessionCookie}={id}")).AssertStatus(HttpStatusCode.OK);
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
    public async Task A_session_made_on_one_instance_works_on_another_sharing_the_store_and_keys()
    {
        // Two servers behind one address: the session store (Redis in production, one shared
        // in-memory store here) and the Data Protection key ring are what they must share, or a
        // sign-in on one is unknown, or undecryptable, on the other.
        var store = new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions()));
        var keys = Path.Combine(Path.GetTempPath(), "versio-web-keys", Guid.NewGuid().ToString("N"));
        var settings = new Dictionary<string, string?> { ["Web:KeysDirectory"] = keys };
        void Shared(IServiceCollection s) => s.AddSingleton<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(store);
        var github = GitHub(out _, out _);

        await using var first = await TestHost.StartAsync(github, settings, Shared);
        await using var second = await TestHost.StartAsync(github, settings, Shared);
        var cookie = SignIn(first);

        (await second.GetAsync("/api/repos/acme/secret/tree", cookie)).AssertStatus(HttpStatusCode.OK);
        Assert.Contains("\"login\":\"alice\"", await (await second.GetAsync("/api/me", cookie)).Body(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rule13_a_view_is_audited_and_no_secret_ever_reaches_the_log()
    {
        // The audit line names who viewed what at which commit; nothing that could become a
        // credential is ever written: not the token, the refresh token, the client secret, the
        // one-time code or the PKCE verifier. Read from the file the host actually wrote.
        await using var oauth = await FakeOAuth.StartAsync();
        var github = GitHub(out _, out _);
        await using (var host = await TestHost.StartAsync(github, oauth.Settings()))
        {
            var login = await host.GetAsync("/auth/login");
            var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(login.Headers.Location!.Query)["state"].ToString();
            var callback = await host.GetAsync($"/auth/callback?code=good&state={Uri.EscapeDataString(state)}", $"{AuthEndpoints.LoginCookie}={TestHost.SetCookie(login, AuthEndpoints.LoginCookie)}");
            var session = $"{AccessResolver.SessionCookie}={TestHost.SetCookie(callback, AccessResolver.SessionCookie)}";
            (await host.GetAsync($"/api/repos/acme/secret/view?path={Uri.EscapeDataString(Settings)}", session)).AssertStatus(HttpStatusCode.OK);
            var logout = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
            logout.Headers.Add("Cookie", session);
            logout.Headers.Add(AuthEndpoints.CsrfHeader, "1");
            await host.Client.SendAsync(logout);

            var verifier = oauth.Exchanges.First(e => e.ContainsKey("code_verifier"))["code_verifier"];
            var log = string.Concat(Directory.GetFiles(host.LogDirectory).Select(f =>
            {
                using var stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return new StreamReader(stream).ReadToEnd();
            }));

            Assert.Contains("Signed in alice", log, StringComparison.Ordinal);
            Assert.Contains($"view alice (User) acme/secret@", log, StringComparison.Ordinal);
            Assert.Contains(Settings, log, StringComparison.Ordinal);
            foreach (var secret in new[] { "alice-token", "alice-refresh", "client-secret", "code=good", verifier, state })
            {
                Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Sign_in_is_not_offered_when_no_App_is_configured()
    {
        await using var host = await TestHost.StartAsync(GitHub(out _, out _));

        (await host.GetAsync("/auth/login")).AssertStatus(HttpStatusCode.NotFound);
        Assert.Contains("\"signInAvailable\":false", await (await host.GetAsync("/api/me")).Body(), StringComparison.Ordinal);
    }
}
