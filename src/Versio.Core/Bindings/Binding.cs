namespace Versio.Core.Bindings;

/// <summary>
/// The stored relationship between one .project and its export directory.
/// </summary>
/// <remarks>
/// <b><see cref="Id"/> is the identity; the paths are attributes.</b> That is what makes a
/// rename survivable: the binding keeps its history, baseline and lock while its path
/// changes. Deriving the relationship purely by convention — as codescribe does — breaks
/// silently the moment somebody renames the project.
/// </remarks>
public sealed record ProjectBinding
{
    /// <summary>Generated once, never changes.</summary>
    public required string Id { get; init; }

    /// <summary>Repo-relative, forward slashes.</summary>
    public required string ProjectPath { get; init; }

    public string? TemplatePath { get; init; }

    /// <summary>Repo-relative export root. Defaults to the convention.</summary>
    public required string ExportPath { get; init; }

    /// <summary>Pinned per project, not per repo: two projects in one repo can legitimately
    /// target different CODESYS service packs.</summary>
    public string? Profile { get; init; }

    /// <summary>
    /// Which toolchain owns this project.
    /// </summary>
    /// <remarks>
    /// On the binding rather than the repository, because a systems integrator's repository
    /// can hold a CODESYS machine beside a TwinCAT one. Defaults to CODESYS so bindings
    /// written before this field are read as what they were, rather than as an unknown vendor
    /// nothing can drive.
    /// </remarks>
    public Plc.PlcVendor Vendor { get; init; } = Plc.PlcVendor.Codesys;

    public bool Enabled { get; init; } = true;
}

/// <summary>Contents of <c>.versio/repo.json</c> (committed).</summary>
public sealed record RepoConfig
{
    public int Schema { get; init; } = 1;
    public IReadOnlyList<ProjectBinding> Bindings { get; init; } = [];

    /// <summary>Glob patterns never offered for tracking.</summary>
    public IReadOnlyList<string> Ignore { get; init; } = [];

    public bool AutoAdoptNewProjects { get; init; }
}

/// <summary>The names and layout a binding assumes when nothing overrides them.</summary>
public static class BindingConventions
{
    public const string ProjectExtension = ".project";
    public const string TemplateSuffix = "_template.project";
    public const string ManifestFileName = "_manifest.json";

    /// <summary>
    /// Versio's own machine-specific directory, which must never reach git.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Locks, session state, project backups and the scratch copies taken when the IDE has a
    /// project open. A lock that got committed and pulled onto a colleague's machine would be
    /// a bug factory (plan §5), and the backups are megabytes of binary project snapshots.
    /// </para>
    /// <para>
    /// A constant rather than a string in three comments, because it was a string in three
    /// comments: they each said it "is gitignored" while nothing wrote the rule. This
    /// repository had one because a person typed it; a repository somebody else adopts a
    /// project in did not.
    /// </para>
    /// </remarks>
    public const string LocalIgnorePattern = ".versio/local/";

    /// <summary>codescribe's convention, kept as the default: the export folder is a
    /// sibling named after the project.</summary>
    public static string ConventionalExportPath(string projectPath)
    {
        var normalized = projectPath.Replace('\\', '/');
        return normalized.EndsWith(ProjectExtension, StringComparison.OrdinalIgnoreCase)
            ? normalized[..^ProjectExtension.Length]
            : normalized + ".export";
    }

    public static string ConventionalTemplatePath(string projectPath)
    {
        var normalized = projectPath.Replace('\\', '/');
        return normalized.EndsWith(ProjectExtension, StringComparison.OrdinalIgnoreCase)
            ? normalized[..^ProjectExtension.Length] + TemplateSuffix
            : normalized + TemplateSuffix;
    }

    /// <summary>
    /// A template is a .project file and a naive glob would happily offer to track it,
    /// creating a binding that exports the template into its own tree. Two independent
    /// guards, because this one is genuinely confusing to unpick after the fact.
    /// </summary>
    public static bool LooksLikeTemplate(string path) =>
        path.Replace('\\', '/').EndsWith(TemplateSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a repo-relative path lies inside a repo-relative directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Segment-aware, not a prefix match.</b> <c>plc/Machine</c> does not contain
    /// <c>plc/Machine2/Main.st</c>, and it does not contain <c>plc/Machine.project</c> either —
    /// which is the shape of a bug this codebase has already had once, in a fake filesystem
    /// whose <c>EnumerateFiles</c> was a bare <c>StartsWith</c>. A test written against that
    /// would have proved the opposite of what it claimed.
    /// </para>
    /// <para>
    /// The directory itself is not "inside" itself: the callers are asking which binding owns
    /// a <i>file</i>, and a path equal to the export root is not one.
    /// </para>
    /// </remarks>
    public static bool IsUnder(string path, string directory)
    {
        var p = path.Replace('\\', '/').Trim('/');
        var d = directory.Replace('\\', '/').Trim('/');

        return d.Length > 0 &&
               p.Length > d.Length + 1 &&
               p[d.Length] == '/' &&
               p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The binding whose export tree contains a repo-relative path, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used to decide whether an action on one file concerns a tracked project at all —
    /// discarding a file inside a mirror releases that binding's direction lock (plan §6.5),
    /// while discarding an unrelated README must release nothing.
    /// </para>
    /// <para>
    /// <b>Longest match wins</b>, so a binding nested inside another's tree is answered
    /// correctly rather than by whichever happens to come first in the config. Disabled
    /// bindings are skipped: they are not syncing, so they hold no lock to release.
    /// </para>
    /// </remarks>
    public static ProjectBinding? OwnerOf(IEnumerable<ProjectBinding> bindings, string path) =>
        bindings
            .Where(b => b.Enabled)
            .Where(b => IsUnder(path, b.ExportPath))
            .OrderByDescending(b => b.ExportPath.Length)
            .FirstOrDefault();
}
