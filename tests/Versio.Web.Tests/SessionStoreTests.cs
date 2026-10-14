using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Web.Auth;
using Xunit;

namespace Versio.Web.Tests;

/// <summary>
/// The session store's writes: after sign-in only a token renewal writes a session, so nothing
/// can put a consumed refresh token back.
/// </summary>
public sealed class SessionStoreTests
{
    /// <summary>A store that can hand one read an older copy, as a request that read before a renewal holds.</summary>
    private sealed class StaleReadCache(IDistributedCache inner) : IDistributedCache
    {
        public byte[]? NextRead { get; set; }

        public int Writes { get; private set; }

        public byte[]? Get(string key) => inner.Get(key);

        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            if (NextRead is { } stale)
            {
                NextRead = null;
                return stale;
            }

            return await inner.GetAsync(key, token);
        }

        public void Refresh(string key) => inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);

        public void Remove(string key) => inner.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => inner.Set(key, value, options);

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Writes++;
            return inner.SetAsync(key, value, options, token);
        }
    }

    [Fact]
    public async Task A_request_that_read_before_a_renewal_cannot_put_the_old_token_back()
    {
        // Found in review: Find wrote "last seen" by replacing the whole entry, so a request that
        // had read the session before a renewal could land after it and restore the consumed
        // refresh token. The next renewal failed and signed the person out.
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cache = new StaleReadCache(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        var store = new SessionStore(cache, new EphemeralDataProtectionProvider(), clock, Options.Create(new WebOptions()), NullLogger.Instance);
        var session = await store.CreateAsync(1, "alice", "Alice", "", "old-token", clock.UtcNow.AddMinutes(2), "old-refresh", clock.UtcNow.AddDays(30));
        var before = await cache.GetAsync("versio-session:" + session.Id);

        clock.Advance(TimeSpan.FromMinutes(5));
        await store.UpdateTokensAsync(session, "new-token", clock.UtcNow.AddHours(8), "new-refresh", clock.UtcNow.AddDays(30));
        var writesAfterRenewal = cache.Writes;

        cache.NextRead = before;
        var seen = await store.FindAsync(session.Id);

        Assert.NotNull(seen);
        Assert.Equal(writesAfterRenewal, cache.Writes);
        var stored = await store.PeekAsync(session.Id);
        Assert.Equal("new-token", store.AccessToken(stored!));
        Assert.Equal("new-refresh", store.RefreshToken(stored!));
    }

    private sealed class CountingLogger : IVersioLogger
    {
        public int Warnings { get; private set; }

        public string? CurrentOperationId => null;

        public void Log(LogLevel level, string category, string message, Exception? exception = null, bool isHeartbeat = false)
        {
            if (level == LogLevel.Warning)
            {
                Warnings++;
            }
        }

        public IDisposable BeginOperation(string operationId) => new MemoryStream();
    }

    [Fact]
    public async Task A_session_from_another_key_ring_is_warned_about_once_not_on_every_request()
    {
        // Found in review: the browser keeps sending the cookie, and the warning came every time.
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var first = new SessionStore(cache, new EphemeralDataProtectionProvider(), clock, Options.Create(new WebOptions()), NullLogger.Instance);
        var log = new CountingLogger();
        var second = new SessionStore(cache, new EphemeralDataProtectionProvider(), clock, Options.Create(new WebOptions()), log);
        var session = await first.CreateAsync(1, "alice", "Alice", "", "token", clock.UtcNow.AddHours(8), "refresh", clock.UtcNow.AddDays(30));

        for (var i = 0; i < 3; i++)
        {
            Assert.Null(await second.FindAsync(session.Id));
        }

        Assert.Equal(1, log.Warnings);
    }

    [Fact]
    public async Task A_session_sealed_under_another_key_ring_is_signed_out_not_an_error()
    {
        // Found in review: Redis shared the session, but each instance (or each restart) had its
        // own key ring, and decrypting the token threw for every signed-in request: a 500.
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var first = new SessionStore(cache, new EphemeralDataProtectionProvider(), clock, Options.Create(new WebOptions()), NullLogger.Instance);
        var second = new SessionStore(cache, new EphemeralDataProtectionProvider(), clock, Options.Create(new WebOptions()), NullLogger.Instance);
        var session = await first.CreateAsync(1, "alice", "Alice", "", "token", clock.UtcNow.AddHours(8), "refresh", clock.UtcNow.AddDays(30));

        Assert.Null(await second.FindAsync(session.Id));
        Assert.NotNull(await first.FindAsync(session.Id));
    }

    [Fact]
    public async Task A_session_stored_before_last_seen_was_dropped_still_reads()
    {
        // Sessions outlive a deploy in Redis. The field went with the fix above; an entry written
        // by the version before must not become a sign-out.
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new SessionStore(cache, new EphemeralDataProtectionProvider(), clock, Options.Create(new WebOptions()), NullLogger.Instance);
        var session = await store.CreateAsync(1, "alice", "Alice", "", "token", clock.UtcNow.AddHours(8), null, default);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await cache.GetAsync("versio-session:" + session.Id))!.AsObject();
        json["LastSeen"] = clock.UtcNow;
        await cache.SetAsync("versio-session:" + session.Id, System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), new DistributedCacheEntryOptions());

        var found = await store.FindAsync(session.Id);

        Assert.Equal("token", store.AccessToken(found!));
    }
}
