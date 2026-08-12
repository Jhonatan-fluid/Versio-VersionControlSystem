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
}
