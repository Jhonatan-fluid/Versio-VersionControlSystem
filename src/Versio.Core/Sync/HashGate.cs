namespace Versio.Core.Sync;

/// <summary>
/// Loop defence #1: nothing is acted on unless the canonical content hash actually differs
/// from the recorded baseline.
/// </summary>
/// <remarks>
/// This is the strongest of the three loop defences because it is <b>stateless and derived
/// from content</b>. It works correctly after a crash, a reboot, or a file copied in by
/// hand, with no memory of what happened before. It is also what stops the export writing
/// 58 unchanged files and generating 58 spurious watcher events.
/// </remarks>
public sealed class HashGate
{
    private readonly Dictionary<string, string> _baseline = new(StringComparer.Ordinal);

    public HashGate() { }

    public HashGate(IEnumerable<KeyValuePair<string, string>> baseline)
    {
        foreach (var (path, hash) in baseline)
        {
            _baseline[path] = hash;
        }
    }

    public int Count => _baseline.Count;

    /// <summary>True when this content differs from the baseline and should be written.</summary>
    public bool ShouldWrite(string path, string contentHash) =>
        !_baseline.TryGetValue(path, out var known) || known != contentHash;

    /// <summary>Record what was actually written.</summary>
    public void Accept(string path, string contentHash) => _baseline[path] = contentHash;

    public void Forget(string path) => _baseline.Remove(path);

    public string? BaselineFor(string path) =>
        _baseline.TryGetValue(path, out var h) ? h : null;

    /// <summary>Split a candidate set into what genuinely changed and what did not.</summary>
    public GateResult Evaluate(IReadOnlyDictionary<string, string> candidates)
    {
        var changed = new List<string>();
        var unchanged = new List<string>();
        foreach (var (path, hash) in candidates)
        {
            if (ShouldWrite(path, hash))
            {
                changed.Add(path);
            }
            else
            {
                unchanged.Add(path);
            }
        }

        var removed = _baseline.Keys.Where(p => !candidates.ContainsKey(p)).ToList();
        changed.Sort(StringComparer.Ordinal);
        unchanged.Sort(StringComparer.Ordinal);
        removed.Sort(StringComparer.Ordinal);
        return new GateResult(changed, unchanged, removed);
    }

    public IReadOnlyDictionary<string, string> Snapshot() =>
        new Dictionary<string, string>(_baseline, StringComparer.Ordinal);
}

public sealed record GateResult(
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> Removed)
{
    public bool AnyChange => Changed.Count > 0 || Removed.Count > 0;
}
