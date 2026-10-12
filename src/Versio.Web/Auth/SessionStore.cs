using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Versio.Core.Abstractions;
using Versio.Core.Logging;

namespace Versio.Web.Auth;

/// <summary>A signed-in person, as the server holds them. The tokens are encrypted; see <see cref="SessionStore"/>.</summary>
public sealed record Session
{
    public required string Id { get; init; }

    public required long GitHubUserId { get; init; }

    public required string Login { get; init; }

    public string Name { get; init; } = string.Empty;

    public string AvatarUrl { get; init; } = string.Empty;

    /// <summary>The user's GitHub token, protected. Read only through <see cref="SessionStore.AccessToken"/>.</summary>
    public required string ProtectedAccessToken { get; init; }

    public required DateTimeOffset AccessExpires { get; init; }

    /// <summary>The refresh token, protected; empty when GitHub issued none (an App with expiry turned off).</summary>
    public string ProtectedRefreshToken { get; init; } = string.Empty;

    public DateTimeOffset RefreshExpires { get; init; }

    /// <summary>
    /// When the session was made, for the absolute lifetime. There is no "last seen": activity is
    /// the store's sliding expiry, because writing it here let a read overwrite a token renewal
    /// (see <see cref="SessionStore.FindAsync"/>). An entry stored with that field still reads.
    /// </summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>The <see cref="WebOptions.SessionEpoch"/> it was created under; a different epoch ends it.</summary>
    public required int Epoch { get; init; }
}

/// <summary>
/// The server-side session store: the browser holds only a random id (docs/web/PLAN.md, the
/// sign-in flow, step 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Server-side, so a session can be ended.</b> A token inside the cookie could not be revoked
/// short of rotating keys for everyone; here logout deletes the entry, and the epoch kill switch
/// (security rule 17) ends every entry at once.
/// </para>
/// <para>
/// <b>Tokens are encrypted at rest</b> with ASP.NET Data Protection (security rule 7), so a dump
/// of the store is not a list of GitHub tokens. In production the key ring itself is protected by
/// the host's key vault; that is configuration, not code.
/// </para>
/// <para>
/// <b>Behind <see cref="IDistributedCache"/>, so more than one instance can share it.</b> The
/// first version was a dictionary in one process: a second instance did not know a session the
/// first created, and abandoned sessions stayed until the process ended. In memory by default
/// (one instance, sessions end on restart); Redis when <c>Web:Redis</c> is set (WebHost). The
/// cache's own expiry enforces the idle and absolute limits, so nothing has to be swept.
/// </para>
/// <para>
/// <b>After sign-in, only a token renewal writes a session.</b> Reads refresh the store's sliding
/// expiry and never write the entry back, so no request can overwrite a renewal with the snapshot
/// it read before it (see <see cref="FindAsync"/>).
/// </para>
/// </remarks>
public sealed class SessionStore(IDistributedCache store, IDataProtectionProvider protection, IClock clock, IOptions<WebOptions> options, IVersioLogger log)
{
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);

    private readonly IDataProtector _tokens = protection.CreateProtector("Versio.Web.SessionTokens.v1");
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _touched = new(StringComparer.Ordinal);

    private WebOptions O => options.Value;

    /// <summary>A new session with a new random id: never one carried over from before sign-in (security rule 14).</summary>
    public async Task<Session> CreateAsync(long userId, string login, string name, string avatar, string accessToken, DateTimeOffset accessExpires,
        string? refreshToken, DateTimeOffset refreshExpires, CancellationToken ct = default)
    {
        var now = clock.UtcNow;

        // This instance's touch times for sessions idle past the limit: the store has dropped those
        // entries itself, and the times must not accumulate either.
        foreach (var (id, last) in _touched)
        {
            if (now - last > O.SessionIdle)
            {
                _touched.TryRemove(id, out _);
                _refreshGates.TryRemove(id, out _);
            }
        }

        var session = new Session
        {
            Id = NewId(),
            GitHubUserId = userId,
            Login = login,
            Name = name,
            AvatarUrl = avatar,
            ProtectedAccessToken = _tokens.Protect(accessToken),
            AccessExpires = accessExpires,
            ProtectedRefreshToken = string.IsNullOrEmpty(refreshToken) ? string.Empty : _tokens.Protect(refreshToken),
            RefreshExpires = refreshExpires,
            Created = now,
            Epoch = O.SessionEpoch,
        };
        await WriteAsync(session, ct).ConfigureAwait(false);
        return session;
    }

    /// <summary>The live session for <paramref name="id"/>, touched; null when absent, idle, too old or from another epoch.</summary>
    public async Task<Session?> FindAsync(string? id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id) || await PeekAsync(id, ct).ConfigureAwait(false) is not { } session)
        {
            return null;
        }

        var now = clock.UtcNow;
        if (session.Epoch != O.SessionEpoch || now - session.Created > O.SessionLifetime)
        {
            await RemoveAsync(id, ct).ConfigureAwait(false);
            return null;
        }

        // A read never writes the session back. The first versions wrote "last seen" by replacing
        // the whole entry, and a request that had read the session before a token renewal could
        // land its write after it, restoring the consumed refresh token; the next renewal then
        // failed and signed the person out (found in review). Idleness is the store's sliding
        // expiry instead, kept alive with Refresh, which resets the clock without touching the
        // data; at most once a minute per instance, since with Redis it is a round trip.
        if (!_touched.TryGetValue(id, out var last) || now - last >= TouchEvery)
        {
            _touched[id] = now;
            await store.RefreshAsync(Key(id), ct).ConfigureAwait(false);
        }

        return session;
    }

    /// <summary>The session as stored now, without touching it; null when gone.</summary>
    public async Task<Session?> PeekAsync(string id, CancellationToken ct = default)
    {
        var bytes = await store.GetAsync(Key(id), ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        Session? session;
        try
        {
            session = JsonSerializer.Deserialize<Session>(bytes);
        }
        catch (JsonException)
        {
            // An entry this version cannot read (written by another version, or damaged): a
            // sign-in, not an error page.
            return null;
        }

        // Tokens this instance cannot decrypt: sealed under another key ring. With Web:Redis set
        // and no shared Web:KeysDirectory, every restart (and every second instance) has its own
        // ring, and AccessToken threw for every signed-in request and every sign-out, a 500
        // each (found in review). A sign-in instead, as for an entry this version cannot parse.
        // Not removed: another instance's ring may still read it. WebHost warns at startup.
        if (session is not null && !Readable(session))
        {
            log.Warn(LogCategory.Auth, $"Session for {session.Login} sealed under another key ring; treated as signed out (share Web:KeysDirectory between instances)");
            return null;
        }

        return session;
    }

    private bool Readable(Session session)
    {
        try
        {
            _tokens.Unprotect(session.ProtectedAccessToken);
            if (session.ProtectedRefreshToken.Length > 0)
            {
                _tokens.Unprotect(session.ProtectedRefreshToken);
            }

            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Replaces a session's tokens after a refresh.</summary>
    public async Task<Session> UpdateTokensAsync(Session session, string accessToken, DateTimeOffset accessExpires, string? refreshToken,
        DateTimeOffset refreshExpires, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var updated = session with
        {
            ProtectedAccessToken = _tokens.Protect(accessToken),
            AccessExpires = accessExpires,
            ProtectedRefreshToken = string.IsNullOrEmpty(refreshToken) ? session.ProtectedRefreshToken : _tokens.Protect(refreshToken),
            RefreshExpires = string.IsNullOrEmpty(refreshToken) ? session.RefreshExpires : refreshExpires,
        };
        await WriteAsync(updated, ct).ConfigureAwait(false);
        return updated;
    }

    public string AccessToken(Session session) => _tokens.Unprotect(session.ProtectedAccessToken);

    public string? RefreshToken(Session session) =>
        session.ProtectedRefreshToken.Length == 0 ? null : _tokens.Unprotect(session.ProtectedRefreshToken);

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        await store.RemoveAsync(Key(id), ct).ConfigureAwait(false);
        _refreshGates.TryRemove(id, out _);
        _touched.TryRemove(id, out _);
    }

    /// <summary>
    /// The one gate a session's token renewal goes through in this process. GitHub's refresh
    /// tokens are single use, and the page sends several requests at once, so without it every
    /// request near expiry renewed in parallel, all but the first failed, and the failure signed
    /// the person out (found reviewing). <see cref="AccessResolver.SessionAsync"/> waits here and
    /// re-reads; across instances it re-reads before giving up, since another may have renewed.
    /// </summary>
    public SemaphoreSlim RefreshGate(string id) => _refreshGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    private Task WriteAsync(Session session, CancellationToken ct)
    {
        // The store forgets an entry idle for SessionIdle or older than SessionLifetime by itself.
        var entry = new DistributedCacheEntryOptions
        {
            SlidingExpiration = O.SessionIdle,
            AbsoluteExpiration = session.Created + O.SessionLifetime,
        };
        return store.SetAsync(Key(session.Id), JsonSerializer.SerializeToUtf8Bytes(session), entry, ct);
    }

    private static string Key(string id) => "versio-session:" + id;

    private static string NewId() => Base64Url(RandomNumberGenerator.GetBytes(32));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
