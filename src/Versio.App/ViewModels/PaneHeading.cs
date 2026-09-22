namespace Versio.App.ViewModels;

/// <summary>What the right-hand pane is showing, by the route that filled it.</summary>
public enum PaneMode
{
    /// <summary>Nothing selected.</summary>
    Empty,

    /// <summary>A working-tree or index diff of one file, with changes in it.</summary>
    Diff,

    /// <summary>One file shown whole: browsed in the explorer, or unchanged.</summary>
    File,

    /// <summary>A tracked project's card.</summary>
    Project,

    /// <summary>A whole commit's patch.</summary>
    Commit,

    /// <summary>What one commit did to one file.</summary>
    CommitFile,
}

/// <summary>
/// The pane header, derived: a small label for the kind of view, the thing's name, where it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>The header used to say DIFF whatever it showed</b>, with the path at the far right
/// trimmed from the END — so a whole unchanged file browsed from the explorer was titled DIFF,
/// and the object's name, the one part of
/// <c>fixtures/projects/RefrigeratorControl/Device/Plc Logic/Application/…/FB_Alarm.st</c> a
/// reader wanted, was the part the ellipsis ate. Seen in the running window on 2026-09-21.
/// </para>
/// <para>
/// Now the name leads and cannot be trimmed away by its folder, the folder follows in the
/// quiet face, and <see cref="IsComparing"/> says whether the diff-only options — side by
/// side, full file — mean anything. They used to sit there over a whole file, a project card
/// and an empty pane, where ticking them did nothing visible.
/// </para>
/// <para>Pure, so every route's heading is tested without a repository.</para>
/// </remarks>
public sealed record PaneHeading(string Label, string Title, string Subtitle, bool IsComparing)
{
    /// <summary>The heading of an empty pane: the kind of thing it will show once asked.</summary>
    public static PaneHeading None { get; } = new("DIFF", string.Empty, string.Empty, false);

    /// <param name="mode">Which route filled the pane.</param>
    /// <param name="shownPath">
    /// What the pane was titled by before this existed: a repo-relative path, or for a whole
    /// commit its caption (<c>"&lt;short id&gt; &lt;subject&gt;"</c>).
    /// </param>
    /// <param name="commitShortId">The commit's short id, for the two commit modes.</param>
    /// <param name="commitFilePath">The file, for <see cref="PaneMode.CommitFile"/>.</param>
    public static PaneHeading Of(
        PaneMode mode, string shownPath, string? commitShortId = null, string? commitFilePath = null)
    {
        if (mode is PaneMode.Empty || shownPath.Length == 0)
        {
            return None;
        }

        switch (mode)
        {
            case PaneMode.Commit:
            {
                // The caption is "<short id> <subject>"; the subject is the name a reader
                // knows the commit by, and the id is where it is.
                var id = commitShortId ?? string.Empty;
                var subject = id.Length > 0 && shownPath.StartsWith(id + " ", StringComparison.Ordinal)
                    ? shownPath[(id.Length + 1)..]
                    : shownPath;
                return new PaneHeading("COMMIT", subject, id, IsComparing: true);
            }

            case PaneMode.CommitFile:
            {
                var path = commitFilePath ?? shownPath;
                var folder = FolderOf(path);
                var where = folder.Length == 0
                    ? $"@ {commitShortId}"
                    : $"{folder} @ {commitShortId}";
                return new PaneHeading("COMMIT", NameOf(path), where, IsComparing: true);
            }

            case PaneMode.Project:
                return new PaneHeading("PROJECT", NameOf(shownPath), FolderOf(shownPath), false);

            case PaneMode.File:
                return new PaneHeading("FILE", NameOf(shownPath), FolderOf(shownPath), false);

            default:
                return new PaneHeading("DIFF", NameOf(shownPath), FolderOf(shownPath), true);
        }
    }

    private static string NameOf(string path)
    {
        var normal = path.Replace('\\', '/').TrimEnd('/');
        var slash = normal.LastIndexOf('/');
        return slash < 0 ? normal : normal[(slash + 1)..];
    }

    private static string FolderOf(string path)
    {
        var normal = path.Replace('\\', '/').TrimEnd('/');
        var slash = normal.LastIndexOf('/');
        return slash < 0 ? string.Empty : normal[..slash];
    }
}
