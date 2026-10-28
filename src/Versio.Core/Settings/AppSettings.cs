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
/// One record with a schema field rather than a file per setting, which is what let the theme
/// and the panel sizes join it rather than invent a second store.
/// </para>
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Format version, read for the same reason the manifest's is.</summary>
    [JsonPropertyName("schema")] public int Schema { get; init; } = 1;

    /// <summary>Repositories opened before, newest first.</summary>
    [JsonPropertyName("recent")] public IReadOnlyList<RecentRepository> Recent { get; init; } = [];

    /// <summary>
    /// Which palette to paint with: <c>System</c>, <c>Light</c> or <c>Dark</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This record's own remarks nominated it, and for a while that was all that happened.</b>
    /// The startup path called <c>ThemeManager.Apply(AppTheme.System)</c> unconditionally and
    /// nothing here held a theme, so picking Dark on a light-mode Windows held for the session
    /// and was gone on the next launch — with the settings popup showing "Follow system"
    /// checked, as though the user had chosen it.
    /// </para>
    /// <para>
    /// A string rather than the app's <c>AppTheme</c> enum, because Core does not know about
    /// the WPF layer and an unrecognised value has to mean "follow the system" rather than
    /// throw. A settings file hand-edited to nonsense must cost a preference, never a launch.
    /// </para>
    /// </remarks>
    [JsonPropertyName("theme")] public string Theme { get; init; } = "System";

    /// <summary>Panel sizes the user has dragged.</summary>
    [JsonPropertyName("layout")] public LayoutSettings Layout { get; init; } = new();

    /// <summary>
    /// The update the user answered Later to, so the launch check stops asking about it.
    /// </summary>
    /// <remarks>
    /// One version, not a switch: a newer release asks again (<c>UpdatePolicy</c>). Empty in
    /// every settings file written before updates existed, which reads as "never dismissed".
    /// </remarks>
    [JsonPropertyName("dismissedUpdate")] public string DismissedUpdateVersion { get; init; } = string.Empty;

    public static readonly AppSettings Empty = new();
}

/// <summary>
/// What the user dragged, so they do not drag it again tomorrow.
/// </summary>
/// <remarks>
/// <para>
/// All three of these reset on every launch. The log was worse than reset — it was a hardcoded
/// <c>Height="200"</c> with no splitter at all, which at 20 px a row is exactly ten lines, for
/// a panel whose whole purpose is troubleshooting a CODESYS export that emits far more than
/// that across two categories.
/// </para>
/// <para>
/// <b>Zero means "not set", and that is why they are doubles rather than nullables.</b> The
/// values come from a WPF <c>GridLength</c> and go back into one; a null round-tripping
/// through JSON as an absent property and back would need three states where the view only
/// has two. A pane nobody has resized has no stored size, and zero is not a size.
/// </para>
/// </remarks>
public sealed record LayoutSettings
{
    /// <summary>Height of the log console in pixels, or zero for the default.</summary>
    [JsonPropertyName("logHeight")] public double LogHeight { get; init; }

    /// <summary>Width of the left panel in pixels, or zero for the default.</summary>
    [JsonPropertyName("panelWidth")] public double PanelWidth { get; init; }

    /// <summary>
    /// A size that is safe to restore, or zero.
    /// </summary>
    /// <remarks>
    /// A stored size is only as trustworthy as the display it was stored on. Somebody who
    /// dragged the log to 900 px on a 4K monitor and then opened Versio on a laptop would get
    /// a log console taller than the window, with the panels squeezed to nothing and no
    /// splitter left on screen to drag back. Clamped on the way out rather than on the way in,
    /// because the display can change between the two.
    /// </remarks>
    public static double Restore(double stored, double minimum, double available) =>
        stored <= 0 || available <= 0
            ? 0
            : Math.Clamp(stored, minimum, Math.Max(minimum, available));
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

    /// <summary>
    /// One spelling of a path, so the same repository cannot appear twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Case was not the only way a path gets typed differently, and separators are the way
    /// that actually happened.</b> Git answers <c>rev-parse --show-toplevel</c> with forward
    /// slashes and the Windows folder picker hands back backslashes, so opening one repository
    /// both ways put two entries in the list for it — and the dropdown then showed the same
    /// repository twice, same name, same location, with no way to tell them apart. The comment
    /// above already had the right argument and only applied it to case.
    /// </para>
    /// <para>
    /// Backslashes, because this ships on Windows and that is what a person sees everywhere
    /// else in the operating system. A trailing separator is dropped because <c>C:\repo</c> and
    /// <c>C:\repo\</c> are one directory — but a bare root keeps its own, since <c>C:</c> alone
    /// means "the current directory on C" to Windows and is a different thing entirely.
    /// </para>
    /// <para>
    /// Deliberately no <c>GetFullPath</c>: it consults the process's current directory and can
    /// throw on a path this assembly is only ever comparing, and Core does no IO.
    /// </para>
    /// </remarks>
    public static string Canonical(string? path)
    {
        var text = (path ?? string.Empty).Trim().Replace('/', '\\');

        return text.Length > 3 && text.EndsWith('\\') ? text.TrimEnd('\\') : text;
    }

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
        // Canonical on the way in AND on the way out. Comparing without storing would keep
        // whichever spelling happened to arrive first, so the list would be correct and still
        // look inconsistent to anybody reading settings.json — or to the folder picker, which
        // is handed the stored path as its starting directory.
        var path = Canonical(opened.Path);
        var previous = current.FirstOrDefault(r => PathComparer.Equals(Canonical(r.Path), path));

        var entry = opened.RemoteUrl.Length > 0 || previous is null
            ? opened with { Path = path }
            : opened with { Path = path, RemoteUrl = previous.RemoteUrl };

        return
        [
            entry,
            .. current
                .Where(r => !PathComparer.Equals(Canonical(r.Path), path))
                .Select(r => r with { Path = Canonical(r.Path) })
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
        [.. current.Where(r => !PathComparer.Equals(Canonical(r.Path), Canonical(path)))];
}
