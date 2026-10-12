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

    /// <summary>How long a refused renewal waits for another instance's, in looks.</summary>
    internal const int RenewalLooks = 5;

    internal static readonly TimeSpan RenewalLookEvery = TimeSpan.FromMilliseconds(200);

    /// <summary>The signed-in session for this request, or null; refreshed when its token is close to expiry.</summary>
    public async Task<Session?> SessionAsync(HttpContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (await sessions.FindAsync(context.Request.Cookies[SessionCookie], ct).ConfigureAwait(false) is not { } session)
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
            var current = await sessions.PeekAsync(session.Id, ct).ConfigureAwait(false);
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
                // Signed out while GitHub was answering: nobody holds this id any more, and writing
                // the session back would leave a live, unrevoked token in the store until it
                // expired (found in review). There is no compare-and-swap, so this narrows the
                // window rather than closing it; THREAT-MODEL records the rest.
                if (await sessions.PeekAsync(current.Id, ct).ConfigureAwait(false) is null)
                {
                    await oauth.RevokeAsync(renewed.AccessToken, ct).ConfigureAwait(false);
                    log.Info(LogCategory.Auth, $"Renewed token for {current.Login} revoked: the session ended while it was being renewed");
                    return null;
                }

                log.Info(LogCategory.Auth, $"Renewed the token for {current.Login} (user {current.GitHubUserId})");
                return await sessions.UpdateTokensAsync(current, renewed.AccessToken, renewed.AccessExpires, renewed.RefreshToken, renewed.RefreshExpires, ct).ConfigureAwait(false);
            }

            // The gate holds within one process only. Another instance sharing the store may have
            // renewed with the same single-use refresh token, so this refusal may be that renewal
            // winning, and its write may land a moment after this one's refusal: look for it for a
            // second before answering.
            //
            // And never delete the session here. The first version removed it after one look, and
            // when the winner's write landed between that look and the remove, the remove deleted
            // the renewal and the next request was signed out (found in review; it needs two
            // instances, Web:Redis). Deleting bought nothing: this request is answered as signed
            // out either way, a new sign-in replaces the browser's session, and a renewal that
            // failed because GitHub was down no longer signs anyone out for good.
            for (var look = 0; look < RenewalLooks; look++)
            {
                if (await sessions.PeekAsync(session.Id, ct).ConfigureAwait(false) is { } after
                    && after.AccessExpires - clock.UtcNow > TimeSpan.FromMinutes(5))
                {
                    return after;
                }

                await Task.Delay(RenewalLookEvery, ct).ConfigureAwait(false);
            }

            log.Info(LogCategory.Auth, $"Session for {current.Login} not used: its token expired and could not be renewed");
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

        // The page's simultaneous requests share one check (measured: four at once, each asking).
        var verdict = await _checking.RunAsync(key, async () =>
        {
            var info = await source.GetRepoAsync(access, owner, repo, CancellationToken.None).ConfigureAwait(false);
            if (!info.Ok)
            {
                return info.As<bool>();
            }

            if (info.Value!.Private)
            {
                // The App can see it; an anonymous visitor may not. Not found, as GitHub would say.
                log.Info(LogCategory.Web, $"Anonymous request for private {owner}/{repo} refused as not found (security rule 1)");
                return RepoResult<bool>.Fail(RepoFailure.NotFound);
            }

            cache.Set(key, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = VisibilityRecheck });
            return RepoResult<bool>.Of(true);
        }).WaitAsync(ct).ConfigureAwait(false);

        return verdict.Ok ? RepoResult<RepoAccess>.Of(access) : verdict.As<RepoAccess>();
    }

    private readonly SingleFlight<RepoResult<bool>> _checking = new();
}
