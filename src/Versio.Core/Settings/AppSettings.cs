using System.Text.Json.Serialization;

namespace Versio.Core.Settings;

/// <summary>One repository the user has opened before.</summary>
/// <remarks>
/// <para>
/// <see cref="RemoteUrl"/> is recorded <b>at open time and not when it is needed</b>, which is
/// the whole reason a vanished clone can be offered back: by the time the folder is gone there
/// is no <c>.git</c> left to read a remote out of. A repository with no remote records none,
/// and the offer to re-clone is simply not made.
/// </para>
/// <para>
/// The path is the identity. Two clones of one remote in two directories are two entries,
/// because they are two working trees with two sets of local work in them.
/// </para>
/// </remarks>
public sealed record RecentRepository
{
    /// <summary>Absolute path of the working tree's root.</summary>
    [JsonPropertyName("path")] public required string Path { get; init; }

    /// <summary><c>origin</c>'s URL as it was when the repository was last opened.</summary>
    [JsonPropertyName("remoteUrl")] public string RemoteUrl { get; init; } = string.Empty;

    [JsonPropertyName("lastOpenedUtc")] public DateTimeOffset LastOpenedUtc { get; init; }
}

/// <summary>Everything Versio remembers about this machine rather than this repository.</summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> <c>repo.json</c>, which is committed and describes bindings every
/// teammate shares. Which repositories this user has opened is machine-local by nature: it
/// names paths that exist on one disk, and pushing it would leak a directory layout into a
/// repository that has no use for it.
/// </para>
/// <para>
/// One record with a schema field rather than a file per setting, so the next preference to
/// need persisting — the theme is the obvious one, and is currently reapplied as
/// <c>System</c> on every start — joins this instead of inventing a second store.
/// </para>
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Format version, read for the same reason the manifest's is.</summary>
    [JsonPropertyName("schema")] public int Schema { get; init; } = 1;

    /// <summary>Repositories opened before, newest first.</summary>
    [JsonPropertyName("recent")] public IReadOnlyList<RecentRepository> Recent { get; init; } = [];

    public static readonly AppSettings Empty = new();
}

/// <summary>
/// The rules the recent-repository list follows.
/// </summary>
/// <remarks>
/// Pure, and in Core, because "newest first, no duplicates, capped, and a path compared the way
/// the platform compares paths" is four decisions and each of them is wrong in a way nobody
/// would notice from a screenshot. The view model holds an <c>ObservableCollection</c>; the
/// part worth testing holds none.
/// </remarks>
public static class RecentRepositories
{
    /// <summary>
    /// How many are kept.
    /// </summary>
    /// <remarks>
    /// A dropdown, not a history. Ten is what fits on screen without scrolling, and an
    /// eleventh entry is a repository somebody has not touched in months — which is exactly
    /// the one whose folder has been moved or deleted underneath it.
    /// </remarks>
    public const int Limit = 10;

    /// <summary>
    /// Paths are compared case-insensitively.
    /// </summary>
    /// <remarks>
    /// Windows is the only platform this ships on and it treats <c>C:\Git\Repo</c> and
    /// <c>c:\git\repo</c> as one directory. An ordinal comparison would let the same
    /// repository appear twice in the list, once per way somebody happened to type it.
    /// </remarks>
    public static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Put one repository at the top, keeping the list unique and capped.</summary>
    /// <remarks>
    /// Promotion rather than insertion: opening a repository already in the list moves it up
    /// and refreshes what is known about it, and never produces a second row for it. The
    /// remote is taken from the new entry when it has one and inherited from the old when it
    /// does not, so opening a repository while offline cannot erase the URL that would let it
    /// be cloned back.
    /// </remarks>
    public static IReadOnlyList<RecentRepository> Promote(
        IReadOnlyList<RecentRepository> current, RecentRepository opened, int limit = Limit)
    {
        var previous = current.FirstOrDefault(r => PathComparer.Equals(r.Path, opened.Path));

        var entry = opened.RemoteUrl.Length > 0 || previous is null
            ? opened
            : opened with { RemoteUrl = previous.RemoteUrl };

        return
        [
            entry,
            .. current
                .Where(r => !PathComparer.Equals(r.Path, opened.Path))
                .Take(Math.Max(0, limit - 1)),
        ];
    }

    /// <summary>Drop one repository from the list.</summary>
    /// <remarks>
    /// Only ever called because the user said so. A repository whose folder has gone missing
    /// is <b>not</b> removed on discovery: a disconnected network drive and a deleted clone
    /// look identical from here, and quietly forgetting the second would also forget the URL
    /// that could restore it.
    /// </remarks>
    public static IReadOnlyList<RecentRepository> Remove(
        IReadOnlyList<RecentRepository> current, string path) =>
        [.. current.Where(r => !PathComparer.Equals(r.Path, path))];
}
