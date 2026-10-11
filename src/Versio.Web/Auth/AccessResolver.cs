using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Web.Sources;

namespace Versio.Web.Auth;

/// <summary>
/// Which credential reads a repository for this request. The one place that decides it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security rule 1, in code.</b> A signed-in request always reads with the person's own
/// token, never the App's: what they see is then exactly what GitHub lets them see, intersected
/// with where the App is installed. The App's installation token could read every repository it
/// is installed on, so it is used only when nobody is signed in, and only after GitHub, asked
/// with that token, says the repository is <b>public</b>. A private repository asked for
/// anonymously is "not found", the same answer as one that does not exist.
/// </para>
/// <para>
/// <b>Visibility is re-checked every five minutes</b> (security rule 11), so a repository made
/// private stops being served anonymously within minutes, not when a cache happens to expire.
/// </para>
/// </remarks>
public sealed class AccessResolver(
    SessionStore sessions,
    GitHubOAuth oauth,
    IInstallationTokens appTokens,
    IRepoSource source,
    IMemoryCache cache,
    IClock clock,
    IOptions<WebOptions> options,
    IVersioLogger log)
{
    public const string SessionCookie = "__Host-versio";

    private static readonly TimeSpan VisibilityRecheck = TimeSpan.FromMinutes(5);

    /// <summary>The signed-in session for this request, or null; refreshed when its token is close to expiry.</summary>
    public async Task<Session?> SessionAsync(HttpContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (sessions.Find(context.Request.Cookies[SessionCookie]) is not { } session)
        {
            return null;
        }

        if (session.AccessExpires - clock.UtcNow > TimeSpan.FromMinutes(5))
        {
            return session;
        }

        // Close to its eight hours: renew quietly. A refresh that fails ends the session, and the
        // page sends the person to sign in again (the sign-in flow, step 4).
        //
        // One renewal per session at a time, and whoever waited re-reads first: GitHub's refresh
        // tokens are single use and the page asks for four things at once on every load, so
        // without the gate the second, third and fourth renewal failed and signed the person out
        // (found reviewing; SecurityTests pins it).
        var gate = sessions.RefreshGate(session.Id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = sessions.Peek(session.Id);
            if (current is null)
            {
                return null;
            }

            if (current.AccessExpires - clock.UtcNow > TimeSpan.FromMinutes(5))
            {
                return current;
            }

            if (sessions.RefreshToken(current) is { } refresh && current.RefreshExpires > clock.UtcNow
                && await oauth.RefreshAsync(refresh, ct).ConfigureAwait(false) is { } renewed)
            {
                log.Info(LogCategory.Auth, $"Renewed the token for {current.Login} (user {current.GitHubUserId})");
                return sessions.UpdateTokens(current, renewed.AccessToken, renewed.AccessExpires, renewed.RefreshToken, renewed.RefreshExpires);
            }

            log.Info(LogCategory.Auth, $"Session for {current.Login} ended: its token expired and could not be renewed");
            sessions.Remove(current.Id);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The credential for reading <paramref name="owner"/>/<paramref name="repo"/> in this request.</summary>
    public async Task<RepoResult<RepoAccess>> ResolveAsync(HttpContext context, string owner, string repo, CancellationToken ct)
    {
        if (await SessionAsync(context, ct).ConfigureAwait(false) is { } session)
        {
            return RepoResult<RepoAccess>.Of(new RepoAccess(AccessKind.User, sessions.AccessToken(session), session.Login));
        }

        if (!options.Value.AnonymousAccess)
        {
            return RepoResult<RepoAccess>.Fail(RepoFailure.Unauthorized, "anonymous access is switched off");
        }

        // The token is asked for on every request (AppTokens keeps it, and renews it with five
        // minutes to spare); only the verdict "public" is cached here. The first version cached
        // the access itself, token included, for five minutes, so a token minted with five
        // minutes left could be served after it expired (found reviewing).
        var token = await appTokens.ForRepositoryAsync(owner, repo, ct).ConfigureAwait(false);
        var access = token is null ? RepoAccess.Anonymous : new RepoAccess(AccessKind.Installation, token, "anonymous");
        var key = $"public:{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}";
        if (cache.TryGetValue(key, out bool isPublic) && isPublic)
        {
            return RepoResult<RepoAccess>.Of(access);
        }

        var info = await source.GetRepoAsync(access, owner, repo, ct).ConfigureAwait(false);
        if (!info.Ok)
        {
            return info.As<RepoAccess>();
        }

        if (info.Value!.Private)
        {
            // The App can see it; an anonymous visitor may not. Not found, as GitHub would say.
            log.Info(LogCategory.Web, $"Anonymous request for private {owner}/{repo} refused as not found (security rule 1)");
            return RepoResult<RepoAccess>.Fail(RepoFailure.NotFound);
        }

        cache.Set(key, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = VisibilityRecheck });
        return RepoResult<RepoAccess>.Of(access);
    }
}
