using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Versio.Core.Abstractions;

namespace Versio.Web.Auth;

/// <summary>Tokens GitHub issued for a user.</summary>
public sealed record UserTokens(string AccessToken, DateTimeOffset AccessExpires, string? RefreshToken, DateTimeOffset RefreshExpires);

/// <summary>What a token request came to: tokens, or GitHub's refusal, or no answer.</summary>
public sealed record Renewal(UserTokens? Tokens, bool Refused)
{
    public static readonly Renewal Denied = new(null, true);

    public static readonly Renewal Unavailable = new(null, false);
}

/// <summary>Who a token belongs to.</summary>
public sealed record GitHubUser(long Id, string Login, string Name, string AvatarUrl);

/// <summary>
/// The GitHub App's user sign-in: the authorize redirect, the code exchange, refresh, revoke.
/// </summary>
/// <remarks>
/// <para>
/// <b>The exchange happens here, on the server</b>, because it needs the client secret, and
/// GitHub's token endpoint accepts no browser request anyway. PKCE is added on top, so a code
/// intercepted on its way back is useless without the verifier this server kept.
/// </para>
/// <para>
/// No token, code, verifier or secret is ever logged; a failure is reported by its status alone.
/// </para>
/// </remarks>
public sealed class GitHubOAuth(IHttpClientFactory clients, IOptions<WebOptions> options, IClock clock)
{
    private GitHubOptions G => options.Value.GitHub;

    /// <summary>A PKCE verifier and its S256 challenge.</summary>
    public static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = SessionStore.Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = SessionStore.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public Uri AuthorizeUrl(string state, string challenge, string redirect) =>
        new($"{G.WebBaseUrl.TrimEnd('/')}/login/oauth/authorize?client_id={E(G.ClientId)}&redirect_uri={E(redirect)}"
            + $"&state={E(state)}&code_challenge={E(challenge)}&code_challenge_method=S256&allow_signup=false");

    public async Task<UserTokens?> ExchangeAsync(string code, string verifier, string redirect, CancellationToken ct) =>
        (await TokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = G.ClientId,
            ["client_secret"] = G.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
        }, ct).ConfigureAwait(false)).Tokens;

    /// <summary>A renewal: new tokens, or whether GitHub refused it (the refresh token is spent or revoked) rather than failing to answer.</summary>
    /// <remarks>
    /// The caller ends a session GitHub refused and keeps one GitHub could not be asked about: a
    /// refusal is final, an outage is not. Both were one null at first, and a network error was
    /// not caught at all, so it reached the reader as a 500 (found in review).
    /// </remarks>
    public Task<Renewal> RefreshAsync(string refreshToken, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = G.ClientId,
            ["client_secret"] = G.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);

    public async Task<GitHubUser?> UserAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{G.ApiBaseUrl.TrimEnd('/')}/user");
        request.Headers.UserAgent.ParseAdd("Versio-Web");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await clients.CreateClient(Sources.GitHubRepoSource.ApiClient).SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var r = doc.RootElement;
        return new GitHubUser(
            r.GetProperty("id").GetInt64(),
            r.GetProperty("login").GetString() ?? "",
            r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "",
            r.TryGetProperty("avatar_url", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : "");
    }

    /// <summary>Asks GitHub to revoke a user token. Best effort: a failure still ends the session here.</summary>
    public async Task<bool> RevokeAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{G.ApiBaseUrl.TrimEnd('/')}/applications/{E(G.ClientId)}/token")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { access_token = accessToken }), Encoding.UTF8, "application/json"),
        };
        request.Headers.UserAgent.ParseAdd("Versio-Web");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{G.ClientId}:{G.ClientSecret}")));
        using var response = await clients.CreateClient(Sources.GitHubRepoSource.ApiClient).SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private async Task<Renewal> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{G.WebBaseUrl.TrimEnd('/')}/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("Versio-Web");
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(Sources.GitHubRepoSource.ApiClient).SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return Renewal.Unavailable;
        }

        using var answered = response;
        if (!response.IsSuccessStatusCode)
        {
            // A 5xx is GitHub not answering, and so are a rate limit (429) and a request timeout
            // (408): the refresh token was not spent, and the same request may succeed later.
            // The first version called every status under 500 a refusal, and a refusal ends the
            // session, so a rate-limited renewal signed the person out (found in review).
            var status = (int)response.StatusCode;
            return status >= 500 || status is 408 or 429 ? Renewal.Unavailable : Renewal.Denied;
        }

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Renewal.Unavailable;
        }

        using var parsed = doc;
        var r = doc.RootElement;

        // GitHub reports a bad code with 200 and an "error" field, not a status.
        if (r.TryGetProperty("error", out _) || !r.TryGetProperty("access_token", out var token) || token.GetString() is not { Length: > 0 } access)
        {
            return Renewal.Denied;
        }

        var now = clock.UtcNow;
        // No expires_in means an App with token expiry turned off: the token does not expire. The
        // first version guessed eight hours, and with no refresh token to renew it the session
        // ended at the guess while GitHub still honoured the token (found in review). The session's
        // own idle and absolute limits still end it.
        var expires = r.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var es) ? now.AddSeconds(es) : DateTimeOffset.MaxValue;
        var refresh = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var refreshExpires = r.TryGetProperty("refresh_token_expires_in", out var re) && re.TryGetInt64(out var res) ? now.AddSeconds(res) : now.AddMonths(6);
        return new Renewal(new UserTokens(access, expires, refresh, refreshExpires), false);
    }

    private static string E(string value) => Uri.EscapeDataString(value);
}

/// <summary>Where a read-only installation token for one repository comes from.</summary>
/// <remarks>
/// An interface so a test can hand the anonymous path a token that CAN see a private
/// repository, which is the one case <see cref="AccessResolver"/>'s visibility check exists for.
/// Without it the check was unobservable: an anonymous read is refused by GitHub itself first,
/// and a mutation removing the check left every test green.
/// </remarks>
public interface IInstallationTokens
{
    /// <summary>A read-only token for one repository, or null when the App is not installed on it.</summary>
    Task<string?> ForRepositoryAsync(string owner, string repo, CancellationToken ct);
}

/// <summary>
/// The App's own installation tokens, for anonymous reads of public repositories only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each token is minted for one repository and read-only</b> (<c>contents</c> and
/// <c>metadata</c>), so even the App's credential can only ever read the repository being
/// viewed. The installation could read every repository it is installed on; asking for less is
/// free, and it is the difference between one leaked token reading one public repository and
/// reading the organisation (security rule 1).
/// </para>
/// <para>
/// <b>Signed here, with the platform's RSA</b>, so the private key is never handed to a package.
/// Production keeps the key in the vault and signs there (security rule 15); this is the shape
/// for a key given as configuration.
/// </para>
/// </remarks>
public sealed class AppTokens(IHttpClientFactory clients, IOptions<WebOptions> options, IClock clock, IAppSigner signer) : IInstallationTokens
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);

    // One gate per repository: concurrent requests for one repository mint once, and nothing
    // waits on another repository. A single gate, held across GitHub's round trips, made one
    // slow answer stall every anonymous visitor to every repository (found in review).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    private GitHubOptions G => options.Value.GitHub;

    /// <summary>A read-only token for one repository, or null when the App is not installed on it.</summary>
    public async Task<string?> ForRepositoryAsync(string owner, string repo, CancellationToken ct)
    {
        if (!G.AppConfigured)
        {
            return null;
        }

        var key = $"{owner}/{repo}";
        if (_cache.TryGetValue(key, out var fresh) && fresh.Expires - clock.UtcNow > TimeSpan.FromMinutes(5))
        {
            return fresh.Token;
        }

        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out var held) && held.Expires - clock.UtcNow > TimeSpan.FromMinutes(5))
            {
                return held.Token;
            }

            var jwt = await JwtAsync(ct).ConfigureAwait(false);
            using var find = App(HttpMethod.Get, $"repos/{E(owner)}/{E(repo)}/installation", jwt);
            using var found = await clients.CreateClient(Sources.GitHubRepoSource.ApiClient).SendAsync(find, ct).ConfigureAwait(false);
            if (!found.IsSuccessStatusCode)
            {
                return null;
            }

            using var installation = await JsonDocument.ParseAsync(await found.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var id = installation.RootElement.GetProperty("id").GetInt64();

            using var mint = App(HttpMethod.Post, $"app/installations/{id}/access_tokens", jwt);
            mint.Content = new StringContent(
                JsonSerializer.Serialize(new { repositories = new[] { repo }, permissions = new { contents = "read", metadata = "read" } }),
                Encoding.UTF8, "application/json");
            using var minted = await clients.CreateClient(Sources.GitHubRepoSource.ApiClient).SendAsync(mint, ct).ConfigureAwait(false);
            if (!minted.IsSuccessStatusCode)
            {
                return null;
            }

            using var token = await JsonDocument.ParseAsync(await minted.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var value = token.RootElement.GetProperty("token").GetString()!;
            var expires = token.RootElement.TryGetProperty("expires_at", out var e) ? e.GetDateTimeOffset() : clock.UtcNow.AddMinutes(55);
            _cache[key] = (value, expires);
            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The App's JWT: RS256, issued a minute in the past for clock drift, valid nine minutes.</summary>
    internal async Task<string> JwtAsync(CancellationToken ct)
    {
        var now = clock.UtcNow.ToUnixTimeSeconds();
        var header = SessionStore.Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var payload = SessionStore.Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { iat = now - 60, exp = now + 540, iss = G.AppId })));
        var signature = await signer.SignRs256Async(Encoding.ASCII.GetBytes($"{header}.{payload}"), ct).ConfigureAwait(false);
        return $"{header}.{payload}.{SessionStore.Base64Url(signature)}";
    }

    private HttpRequestMessage App(HttpMethod method, string relative, string jwt)
    {
        var request = new HttpRequestMessage(method, $"{G.ApiBaseUrl.TrimEnd('/')}/{relative}");
        request.Headers.UserAgent.ParseAdd("Versio-Web");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return request;
    }

    private static string E(string value) => Uri.EscapeDataString(value);
}
