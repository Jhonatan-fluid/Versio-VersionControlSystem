using System.Collections.Concurrent;

namespace Versio.Web;

/// <summary>
/// Concurrent callers asking for the same key share one piece of work instead of each doing it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The page asks for the repository, its branches, its tree and an object all at
/// once. Measured on 2026-10-11, that one page load made 17 GitHub calls: the four requests each
/// checked the repository's visibility, and the tree and the view each loaded the whole model,
/// because both found the cache empty at the same instant. Caches answer the second request;
/// only coalescing answers the simultaneous one.
/// </para>
/// <para>
/// <b>It shares work, never access.</b> Callers pass keys that already carry everything the
/// answer depends on (the repository, the commit, the credential where it matters), so a caller
/// is only ever handed what it would have computed itself. The entry is removed when the work
/// ends, success or failure, so a failure is not served to the next caller. A key that leaves
/// the credential out (the repository model: any caller who resolved the commit may read it) does
/// not share a failure either: the caller who brought another credential reads again with its own
/// (<c>RepoModelLoader.LoadAsync</c>; found in review).
/// </para>
/// </remarks>
public sealed class SingleFlight<T>
{
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> _inFlight = new(StringComparer.Ordinal);

    public async Task<T> RunAsync(string key, Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<T>>(work, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<T>>>(key, lazy));
        }
    }

    /// <summary>How many keys have work running now; for tests.</summary>
    public int Count => _inFlight.Count;
}
