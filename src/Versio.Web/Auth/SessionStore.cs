using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Versio.Core.Abstractions;

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

    public required DateTimeOffset Created { get; init; }

    public required DateTimeOffset LastSeen { get; init; }

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
/// short of rotating keys for everyone; here logout deletes the row, and the epoch kill switch
/// (security rule 17) ends every row at once.
/// </para>
/// <para>
/// <b>Tokens are encrypted at rest</b> with ASP.NET Data Protection (security rule 7), so a dump
/// of this store, or of whatever replaces it, is not a list of GitHub tokens. In production the
/// key ring itself is protected by the host's key vault; that is configuration, not code.
/// </para>
/// <para>
/// <b>In memory for the proof of concept.</b> Sessions end when the process restarts, which
/// costs a sign-in and nothing else. A second instance needs a shared store (Redis or the
/// database), behind this same class.
/// </para>
/// </remarks>
public sealed class SessionStore(IDataProtectionProvider protection, IClock clock, IOptions<WebOptions> options)
{
    private readonly IDataProtector _tokens = protection.CreateProtector("Versio.Web.SessionTokens.v1");
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates = new(StringComparer.Ordinal);

    private WebOptions O => options.Value;

    /// <summary>A new session with a new random id: never one carried over from before sign-in (security rule 14).</summary>
    /// <remarks>
    /// Sweeps expired sessions first. Until then, one was removed only if its cookie came back,
    /// so every abandoned session stayed in memory for the life of the process (found reviewing).
    /// </remarks>
    public Session Create(long userId, string login, string name, string avatar, string accessToken, DateTimeOffset accessExpires,
        string? refreshToken, DateTimeOffset refreshExpires)
    {
        var now = clock.UtcNow;
        foreach (var (id, old) in _sessions)
        {
            if (Expired(old, now))
            {
                _sessions.TryRemove(id, out _);
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
            LastSeen = now,
            Epoch = O.SessionEpoch,
        };
        _sessions[session.Id] = session;
        return session;
    }

    /// <summary>The live session for <paramref name="id"/>, touched; null when absent, idle, too old or from another epoch.</summary>
    public Session? Find(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        var now = clock.UtcNow;
        if (Expired(session, now))
        {
            _sessions.TryRemove(id, out _);
            _refreshGates.TryRemove(id, out _);
            return null;
        }

        var touched = session with { LastSeen = now };
        _sessions[id] = touched;
        return touched;
    }

    /// <summary>Replaces a session's tokens after a refresh.</summary>
    public Session UpdateTokens(Session session, string accessToken, DateTimeOffset accessExpires, string? refreshToken, DateTimeOffset refreshExpires)
    {
        ArgumentNullException.ThrowIfNull(session);
        var updated = session with
        {
            ProtectedAccessToken = _tokens.Protect(accessToken),
            AccessExpires = accessExpires,
            ProtectedRefreshToken = string.IsNullOrEmpty(refreshToken) ? session.ProtectedRefreshToken : _tokens.Protect(refreshToken),
            RefreshExpires = string.IsNullOrEmpty(refreshToken) ? session.RefreshExpires : refreshExpires,
        };
        _sessions[session.Id] = updated;
        return updated;
    }

    public string AccessToken(Session session) => _tokens.Unprotect(session.ProtectedAccessToken);

    public string? RefreshToken(Session session) =>
        session.ProtectedRefreshToken.Length == 0 ? null : _tokens.Unprotect(session.ProtectedRefreshToken);

    public void Remove(string id)
    {
        _sessions.TryRemove(id, out _);
        _refreshGates.TryRemove(id, out _);
    }

    /// <summary>
    /// The one gate a session's token renewal goes through. GitHub's refresh tokens are single
    /// use, and the page sends several requests at once, so without it every request near expiry
    /// renewed in parallel, all but the first failed, and the failure signed the person out
    /// (found reviewing). <see cref="AccessResolver.SessionAsync"/> waits here and re-reads.
    /// </summary>
    public SemaphoreSlim RefreshGate(string id) => _refreshGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    /// <summary>The session as stored now, without touching it; null when gone.</summary>
    public Session? Peek(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    private bool Expired(Session session, DateTimeOffset now) =>
        session.Epoch != O.SessionEpoch || now - session.LastSeen > O.SessionIdle || now - session.Created > O.SessionLifetime;

    public int Count => _sessions.Count;

    private static string NewId() => Base64Url(RandomNumberGenerator.GetBytes(32));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
