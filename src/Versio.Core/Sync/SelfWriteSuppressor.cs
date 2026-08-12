using Versio.Core.Abstractions;

namespace Versio.Core.Sync;

/// <summary>
/// Loop defence #2: swallow the watcher events caused by our own writes.
/// </summary>
/// <remarks>
/// Registered entries carry the expected content hash, not just the path — so a genuine
/// user edit landing on the same path in the same instant is <b>not</b> suppressed. Without
/// that check this would be a data-loss bug rather than a noise filter.
/// <para>Entries expire, so a write that never produces a watcher event cannot suppress a
/// later real edit forever.</para>
/// </remarks>
public sealed class SelfWriteSuppressor(IClock clock, TimeSpan? ttl = null)
{
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, (string Hash, DateTimeOffset Expires)> _pending
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Call immediately before writing.</summary>
    public void Register(string path, string contentHash) =>
        _pending[path] = (contentHash, clock.UtcNow + _ttl);

    /// <summary>
    /// True when this event is the echo of a write we just made.
    /// Consumes the registration, so a second event for the same path is not suppressed.
    /// </summary>
    public bool ShouldSuppress(string path, string observedHash)
    {
        Prune();
        if (!_pending.TryGetValue(path, out var entry))
        {
            return false;
        }

        if (entry.Hash != observedHash)
        {
            // Same path, different content: somebody really did edit it. Do not suppress,
            // and drop the stale registration so we do not suppress the next event either.
            _pending.Remove(path);
            return false;
        }

        _pending.Remove(path);
        return true;
    }

    public int PendingCount
    {
        get
        {
            Prune();
            return _pending.Count;
        }
    }

    private void Prune()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var now = clock.UtcNow;
        var expired = _pending.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList();
        foreach (var key in expired)
        {
            _pending.Remove(key);
        }
    }
}
