using System.Collections.Concurrent;
using System.Text.Json;
using StackExchange.Redis;

namespace Versio.Web.Review;

/// <summary>Where a write stands.</summary>
public enum OperationState
{
    /// <summary>Reserved; the call to GitHub is in flight, or its process died before recording the end.</summary>
    Pending,

    Succeeded,

    /// <summary>GitHub refused it: nothing was written.</summary>
    Failed,

    /// <summary>Sent, and nobody knows whether GitHub did it: reconciled before anything else is tried.</summary>
    Unknown,
}

/// <summary>One write's record: what was asked (by hash), where it stands, and what came of it.</summary>
public sealed record OperationEntry(string PayloadHash, OperationState State, string? Result, DateTimeOffset Reserved, DateTimeOffset Updated);

/// <summary>What reserving an operation key found.</summary>
/// <param name="Fresh">True: this request owns the key and makes the call.</param>
/// <param name="Existing">The entry already there when <paramref name="Fresh"/> is false.</param>
/// <param name="Version">The store's own stamp of <paramref name="Existing"/> as read (Redis: the stored text), for <see cref="IOperationLedger.TryTakeOverAsync"/>.</param>
public sealed record Reservation(bool Fresh, OperationEntry? Existing, string? Version = null);

/// <summary>
/// Operation keys, reserved atomically before a write reaches GitHub (docs/web/PLAN.md, C5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomic reservation is the whole point.</b> Two identical requests at once — a double click,
/// a retry racing the original — must produce one call to GitHub. Reserve-if-absent decides which
/// request owns the key; the other sees the entry and returns its outcome, or "in progress".
/// </para>
/// <para>
/// <b>What each store guarantees.</b> <see cref="RedisOperationLedger"/> survives a restart and is
/// shared by every instance, so a write in flight when a process died is still pending afterwards
/// and is reconciled, never re-sent. <see cref="MemoryOperationLedger"/> forgets everything on
/// restart: after one, an in-flight write is recoverable only through the tag its body carries
/// (comments) or by re-reading the state it set (resolve), and only a single instance may use it —
/// WebHost refuses writes otherwise.
/// </para>
/// </remarks>
public interface IOperationLedger
{
    /// <summary>Reserves <paramref name="key"/> as pending unless it exists; the existing entry otherwise.</summary>
    Task<Reservation> ReserveAsync(string key, string payloadHash, DateTimeOffset now, CancellationToken ct);

    /// <summary>Records where the operation ended up. Only the reserving request, or a reconciliation, calls this.</summary>
    Task RecordAsync(string key, OperationEntry entry, CancellationToken ct);

    /// <summary>
    /// Takes over an existing entry — failed, or unknown and found undone, or abandoned — replacing it
    /// with <paramref name="next"/> only if it is still exactly what <paramref name="seen"/> read.
    /// True: this request owns the key and makes the call. False: another request took it first.
    /// </summary>
    /// <remarks>
    /// The first reservation was always atomic; taking over was not. Two retries could both read
    /// "failed", both write "pending" and both send a write that must happen once (found by review,
    /// 2026-10-20). A compare-and-set lets exactly one of them through.
    /// </remarks>
    Task<bool> TryTakeOverAsync(string key, Reservation seen, OperationEntry next, CancellationToken ct);

    /// <summary>The entry under <paramref name="key"/>, or null; reserves nothing. For settling a retry that may not start a write.</summary>
    Task<OperationEntry?> FindAsync(string key, CancellationToken ct);
}

/// <summary>The single-instance ledger. Its limits are on <see cref="IOperationLedger"/>.</summary>
public sealed class MemoryOperationLedger : IOperationLedger
{
    private readonly ConcurrentDictionary<string, OperationEntry> _entries = new(StringComparer.Ordinal);

    public Task<Reservation> ReserveAsync(string key, string payloadHash, DateTimeOffset now, CancellationToken ct)
    {
        Sweep(now);
        var mine = new OperationEntry(payloadHash, OperationState.Pending, null, now, now);
        var held = _entries.GetOrAdd(key, mine);
        return Task.FromResult(ReferenceEquals(held, mine) ? new Reservation(true, null) : new Reservation(false, held));
    }

    public Task RecordAsync(string key, OperationEntry entry, CancellationToken ct)
    {
        _entries[key] = entry;
        return Task.CompletedTask;
    }

    public Task<OperationEntry?> FindAsync(string key, CancellationToken ct) =>
        Task.FromResult(_entries.TryGetValue(key, out var entry) ? entry : null);

    /// <remarks>Entries are records, so "still exactly what was read" is value equality, which TryUpdate compares atomically.</remarks>
    public Task<bool> TryTakeOverAsync(string key, Reservation seen, OperationEntry next, CancellationToken ct) =>
        Task.FromResult(seen.Existing is { } was && _entries.TryUpdate(key, next, was));

    /// <summary>Forgets entries past the key lifetime: their keys are refused by age anyway (ReviewTags.CheckKey).</summary>
    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
        {
            if (now - entry.Reserved > ReviewTags.KeyLifetime)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }
}

/// <summary>
/// The shared, durable ledger: Redis <c>SET NX</c> for the reservation, with the key lifetime as
/// its expiry. Durability across a Redis restart is Redis's persistence (AOF), which is the host's
/// configuration and is stated as a requirement in docs/web/THREAT-MODEL.md.
/// </summary>
public sealed class RedisOperationLedger(IConnectionMultiplexer redis) : IOperationLedger
{
    private const string Prefix = "versio-web:op:";

    public async Task<Reservation> ReserveAsync(string key, string payloadHash, DateTimeOffset now, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var mine = new OperationEntry(payloadHash, OperationState.Pending, null, now, now);
        if (await db.StringSetAsync(Prefix + key, JsonSerializer.Serialize(mine, Json), ReviewTags.KeyLifetime, When.NotExists).ConfigureAwait(false))
        {
            return new Reservation(true, null);
        }

        var held = await db.StringGetAsync(Prefix + key).ConfigureAwait(false);
        return held.HasValue
            ? new Reservation(false, JsonSerializer.Deserialize<OperationEntry>(held.ToString(), Json), held.ToString())
            : new Reservation(false, null);
    }

    /// <summary>Compare-and-set on the stored text itself, in one script: no read-modify-write gap between instances.</summary>
    private const string TakeOverScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
          return 1
        end
        return 0
        """;

    public async Task<OperationEntry?> FindAsync(string key, CancellationToken ct)
    {
        var held = await redis.GetDatabase().StringGetAsync(Prefix + key).ConfigureAwait(false);
        return held.HasValue ? JsonSerializer.Deserialize<OperationEntry>(held.ToString(), Json) : null;
    }

    public async Task<bool> TryTakeOverAsync(string key, Reservation seen, OperationEntry next, CancellationToken ct)
    {
        if (seen.Version is null)
        {
            return false;
        }

        var taken = await redis.GetDatabase().ScriptEvaluateAsync(TakeOverScript, [(RedisKey)(Prefix + key)],
            [seen.Version, JsonSerializer.Serialize(next, Json), (long)ReviewTags.KeyLifetime.TotalMilliseconds]).ConfigureAwait(false);
        return (long)taken == 1;
    }

    public Task RecordAsync(string key, OperationEntry entry, CancellationToken ct) =>
        redis.GetDatabase().StringSetAsync(Prefix + key, JsonSerializer.Serialize(entry, Json), ReviewTags.KeyLifetime);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
