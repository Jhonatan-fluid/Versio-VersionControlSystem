using Versio.Core.Abstractions;

namespace Versio.Core.Sync;

/// <summary>
/// The seatbelt: a hard counter that halts sync after N operations on one object inside a
/// time window.
/// </summary>
/// <remarks>
/// No matter how confident the state machine is, this is what prevents the one failure mode
/// that could churn a repository. It costs very little and it is the last line of defence
/// when the other two loop guards have both been defeated by something nobody predicted.
/// <para>Tripping is sticky: it stays tripped until a human acknowledges it.</para>
/// </remarks>
public sealed class CircuitBreaker(IClock clock, int threshold = 5, TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, List<DateTimeOffset>> _events = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tripped = new(StringComparer.Ordinal);

    public int Threshold { get; } = threshold;

    public bool IsTripped(string key) => _tripped.Contains(key);

    public bool AnyTripped => _tripped.Count > 0;

    public IReadOnlyCollection<string> TrippedKeys => _tripped;

    /// <summary>Record one sync operation. Returns false when the breaker has tripped and
    /// the caller must not proceed.</summary>
    public bool TryRecord(string key)
    {
        if (_tripped.Contains(key))
        {
            return false;
        }

        var now = clock.UtcNow;
        if (!_events.TryGetValue(key, out var list))
        {
            list = [];
            _events[key] = list;
        }

        list.RemoveAll(t => now - t > _window);
        list.Add(now);

        if (list.Count > Threshold)
        {
            _tripped.Add(key);
            return false;
        }

        return true;
    }

    public int CountInWindow(string key)
    {
        if (!_events.TryGetValue(key, out var list))
        {
            return 0;
        }

        var now = clock.UtcNow;
        list.RemoveAll(t => now - t > _window);
        return list.Count;
    }

    /// <summary>Explicit human acknowledgement. Deliberately not automatic.</summary>
    public void Reset(string key)
    {
        _tripped.Remove(key);
        _events.Remove(key);
    }

    public void ResetAll()
    {
        _tripped.Clear();
        _events.Clear();
    }
}
