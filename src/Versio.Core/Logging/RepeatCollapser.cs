namespace Versio.Core.Logging;

/// <summary>
/// Turns one thing happening over and over into one line, then one line every few minutes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written for the IDE's open-project marker.</b> While CODESYS has a project open it
/// rewrites a sidecar next to it every five seconds, and the watcher logged every rewrite as a
/// settled file — twice, once from the watcher and once from the sync panel — so an idle
/// window with the IDE open wrote about 1,400 lines an hour, all of them the same path. Seen in
/// the running window on 2026-09-21: the log badge climbed by sixty in a minute while nothing
/// was being done.
/// </para>
/// <para>
/// Keyed per thing rather than globally: an export settles dozens of <i>different</i> files
/// once each, and every one of those is worth its line. What collapses is the same key again
/// inside the window. The first occurrence always prints, so the log still says when the storm
/// began; the next line after the window says how many were folded into it.
/// </para>
/// <para>
/// Sibling to <see cref="QuietRunTracker"/>, which summarises checks that found nothing; this
/// summarises an event that keeps being found. Pure, and clocked by the caller.
/// </para>
/// </remarks>
public sealed class RepeatCollapser(TimeSpan? window = null)
{
    /// <summary>Five minutes, the same punctuation <see cref="QuietRunTracker"/> uses.</summary>
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, (DateTimeOffset Logged, int Folded)> _seen =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The line to log for this occurrence of <paramref name="key"/>, or <c>null</c> when it
    /// has been folded into the next one.
    /// </summary>
    public string? Next(string key, string line, DateTimeOffset now)
    {
        if (_seen.TryGetValue(key, out var seen) && now - seen.Logged < _window)
        {
            _seen[key] = (seen.Logged, seen.Folded + 1);
            return null;
        }

        _seen[key] = (now, 0);

        // Housekeeping on the printing path only, which is rare by construction: without it
        // a long session would remember every file it ever saw settle.
        if (_seen.Count > 256)
        {
            foreach (var stale in _seen.Where(p => now - p.Value.Logged >= _window)
                         .Select(p => p.Key).ToList())
            {
                _seen.Remove(stale);
            }
        }

        return seen.Folded switch
        {
            0 => line,
            1 => $"{line} (once more since the last line)",
            var n => $"{line} ({n} more times since the last line)",
        };
    }

    /// <summary>Forget everything, e.g. when the repository changes.</summary>
    public void Reset() => _seen.Clear();
}
