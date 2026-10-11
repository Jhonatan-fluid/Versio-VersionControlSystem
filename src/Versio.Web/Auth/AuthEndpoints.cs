using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Versio.Core.Logging;

namespace Versio.Web.Auth;

/// <summary>
/// Sign-in, sign-out and "who am I" (docs/web/PLAN.md, the sign-in flow).
/// </summary>
/// <remarks>
/// <para>
/// <b>The person signs in on GitHub's own page, never this one.</b> Their password, second factor
/// and company SSO are GitHub's business; this server only ever receives a code, which it
/// exchanges, with its secret and the PKCE verifier, for tokens it keeps.
/// </para>
/// <para>
/// <b>Logout needs a header a browser will not send cross-site</b> (<see cref="CsrfHeader"/>).
/// A custom header forces a CORS preflight, and this server answers none, so another site cannot
/// sign someone out. Logout is the only request here that changes anything; no GET does.
/// </para>
/// </remarks>
public static class AuthEndpoints
{
    public const string LoginCookie = "__Host-versio-login";
    public const string CsrfHeader = "X-Versio-Request";

    private sealed record LoginState(string State, string Verifier, string ReturnUrl, DateTimeOffset Expires);

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/auth/login", (string? returnUrl, HttpContext http, GitHubOAuth oauth, IDataProtectionProvider protection,
            IOptions<WebOptions> options, IVersioLogger log) =>
        {
            if (!options.Value.GitHub.SignInConfigured)
            {
                return Results.NotFound();
            }

            var state = SessionStore.Base64Url(RandomNumberGenerator.GetBytes(32));
            var (verifier, challenge) = GitHubOAuth.NewPkce();
            var login = new LoginState(state, verifier, SafeReturn(returnUrl), DateTimeOffset.UtcNow.AddMinutes(10));
            var sealedState = protection.CreateProtector("Versio.Web.Login.v1").Protect(JsonSerializer.Serialize(login));
            http.Response.Cookies.Append(LoginCookie, sealedState, Cookie(TimeSpan.FromMinutes(10)));
            log.Info(LogCategory.Auth, "Sign-in started; sent to GitHub");
            return Results.Redirect(oauth.AuthorizeUrl(state, challenge, Callback(http, options.Value)).ToString());
        }).RequireRateLimiting(WebHost.RatePolicy);

        app.MapGet("/auth/callback", async (string? code, string? state, HttpContext http, GitHubOAuth oauth, SessionStore sessions,
            IDataProtectionProvider protection, IOptions<WebOptions> options, IVersioLogger log, CancellationToken ct) =>
        {
            var sealedState = http.Request.Cookies[LoginCookie];
            http.Response.Cookies.Delete(LoginCookie, Cookie(TimeSpan.Zero));
            LoginState? login = null;
            try
            {
                login = sealedState is null ? null : JsonSerializer.Deserialize<LoginState>(protection.CreateProtector("Versio.Web.Login.v1").Unprotect(sealedState));
            }
            catch (CryptographicException)
            {
                // A cookie this server did not seal, or sealed under a key it no longer holds.
            }

            if (login is null || code is null || state is null || login.Expires < DateTimeOffset.UtcNow
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state), Encoding.ASCII.GetBytes(login.State)))
            {
                log.Warn(LogCategory.Auth, "Sign-in callback refused: state missing, expired or not ours");
                return Results.Redirect("/?signin=failed");
            }

            var tokens = await oauth.ExchangeAsync(code, login.Verifier, Callback(http, options.Value), ct).ConfigureAwait(false);
            var user = tokens is null ? null : await oauth.UserAsync(tokens.AccessToken, ct).ConfigureAwait(false);
            if (tokens is null || user is null)
            {
                log.Warn(LogCategory.Auth, "Sign-in callback refused: GitHub did not exchange the code or name the user");
                return Results.Redirect("/?signin=failed");
            }

            var session = sessions.Create(user.Id, user.Login, user.Name, user.AvatarUrl, tokens.AccessToken, tokens.AccessExpires, tokens.RefreshToken, tokens.RefreshExpires);
            http.Response.Cookies.Append(AccessResolver.SessionCookie, session.Id, Cookie(options.Value.SessionLifetime));
            log.Info(LogCategory.Auth, $"Signed in {user.Login} (user {user.Id})");
            return Results.Redirect(login.ReturnUrl);
        }).RequireRateLimiting(WebHost.RatePolicy);

        app.MapPost("/auth/logout", async (HttpContext http, SessionStore sessions, GitHubOAuth oauth, IVersioLogger log, CancellationToken ct) =>
        {
            if (http.Request.Headers[CsrfHeader] != "1")
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (sessions.Find(http.Request.Cookies[AccessResolver.SessionCookie]) is { } session)
            {
                var revoked = await oauth.RevokeAsync(sessions.AccessToken(session), ct).ConfigureAwait(false);
                sessions.Remove(session.Id);
                log.Info(LogCategory.Auth, $"Signed out {session.Login} (user {session.GitHubUserId}); token {(revoked ? "revoked at GitHub" : "not revoked: GitHub refused, the session is ended here regardless")}");
            }

            http.Response.Cookies.Delete(AccessResolver.SessionCookie, Cookie(TimeSpan.Zero));
            return Results.NoContent();
        }).RequireRateLimiting(WebHost.RatePolicy);

        app.MapGet("/api/me", async (HttpContext http, AccessResolver access, IOptions<WebOptions> options, CancellationToken ct) =>
        {
            var session = await access.SessionAsync(http, ct).ConfigureAwait(false);
            var g = options.Value.GitHub;
            return Results.Ok(new
            {
                signedIn = session is not null,
                login = session?.Login,
                name = session?.Name,
                avatarUrl = session?.AvatarUrl,
                signInAvailable = g.SignInConfigured,
                anonymousAccess = options.Value.AnonymousAccess,
                installUrl = g.AppSlug.Length > 0 ? $"{g.WebBaseUrl.TrimEnd('/')}/apps/{Uri.EscapeDataString(g.AppSlug)}/installations/new" : null,
            });
        }).RequireRateLimiting(WebHost.RatePolicy);
    }

    /// <summary>
    /// Where to land after sign-in: one of this site's own pages, or the home page. A full URL,
    /// a protocol-relative <c>//host</c> or a <c>/\host</c> would turn sign-in into a redirect to
    /// anywhere (the sign-in flow, step 3).
    /// </summary>
    internal static string SafeReturn(string? returnUrl) =>
        returnUrl is { Length: > 0 and <= 2048 }
        && returnUrl[0] == '/'
        && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
        && !returnUrl.Any(char.IsControl)
            ? returnUrl
            : "/";

    private static string Callback(HttpContext http, WebOptions options) =>
        (options.GitHub.PublicBaseUrl.Length > 0 ? options.GitHub.PublicBaseUrl.TrimEnd('/') : $"{http.Request.Scheme}://{http.Request.Host}")
        + "/auth/callback";

    /// <summary>
    /// The cookie rules: HttpOnly so no script reads it, Secure and the <c>__Host-</c> name so no
    /// subdomain can set or overwrite it (security rule 14), Lax so it comes back from GitHub's redirect.
    /// </summary>
    private static CookieOptions Cookie(TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,
        MaxAge = lifetime > TimeSpan.Zero ? lifetime : null,
        Expires = lifetime > TimeSpan.Zero ? null : DateTimeOffset.UnixEpoch,
    };
}
