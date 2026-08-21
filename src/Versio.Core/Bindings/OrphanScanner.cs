using Versio.Core.Plc;

namespace Versio.Core.Bindings;

/// <summary>What the scanner concluded about one project or export tree.</summary>
public enum BindingHealth
{
    /// <summary>Binding exists; project and export both present; manifest agrees.</summary>
    Bound,

    /// <summary>A .project with no binding. Offer to track it.</summary>
    OrphanedProject,

    /// <summary>An export tree with no matching project.</summary>
    OrphanedExport,

    /// <summary>repo.json references a path that no longer exists.</summary>
    BrokenBinding,

    /// <summary>The manifest's bindingId disagrees with repo.json.</summary>
    ManifestMismatch,

    /// <summary>Casing differs between disk and the committed path.</summary>
    CasingMismatch,
}

/// <summary>One conclusion, carrying the sentence the UI shows for it.</summary>
public sealed record ScanFinding(
    BindingHealth Health,
    string Path,
    string? BindingId,
    string Explanation)
{
    public bool NeedsAttention => Health != BindingHealth.Bound;
}

/// <summary>What the scanner observes about the repo. Supplied by the caller so the scanner
/// itself stays pure and fully unit testable.</summary>
public sealed record RepoSnapshot
{
    /// <summary>Every *.project found, repo-relative, forward slashes.</summary>
    public IReadOnlyList<string> ProjectFiles { get; init; } = [];

    /// <summary>Directories that contain a _manifest.json, repo-relative.</summary>
    public IReadOnlyList<string> ExportDirectories { get; init; } = [];

    /// <summary>bindingId read from each export directory's manifest.</summary>
    public IReadOnlyDictionary<string, string> ManifestBindingIds { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Renames git detected, old path → new path.</summary>
    public IReadOnlyDictionary<string, string> GitRenames { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>
/// Classifies everything in the repo so that nothing is silently ignored.
/// </summary>
/// <remarks>
/// Without this, someone adds a project, nothing happens, and they reasonably conclude the
/// tool is broken. Detection is always on; <b>adoption is offered rather than performed</b>,
/// because auto-exporting an unknown project launches a toolchain for 10–60 s unprompted and
/// the file may be a vendor sample, an archive, or a scratch copy nobody wants tracked.
/// </remarks>
public sealed class OrphanScanner(Func<string, IPlcProjectFormat?>? formats = null)
{
    /// <summary>
    /// Which format claims a path. Defaulted rather than required, same shape as the
    /// orchestrator: existing callers are unchanged and a test can hand its own in.
    /// </summary>
    private readonly Func<string, IPlcProjectFormat?> _formats = formats ?? PlcFormats.ForPath;
    public IReadOnlyList<ScanFinding> Scan(RepoConfig config, RepoSnapshot snapshot)
    {
        var findings = new List<ScanFinding>();
        var enabled = config.Bindings.Where(b => b.Enabled).ToList();

        var boundProjects = new HashSet<string>(
            enabled.Select(b => Normalize(b.ProjectPath)), StringComparer.Ordinal);
        var boundTemplates = new HashSet<string>(
            enabled.Where(b => b.TemplatePath is not null)
                   .Select(b => Normalize(b.TemplatePath!)), StringComparer.Ordinal);
        var boundExports = new HashSet<string>(
            enabled.Select(b => Normalize(b.ExportPath)), StringComparer.Ordinal);

        var projectsOnDisk = snapshot.ProjectFiles.Select(Normalize).ToList();
        var exportsOnDisk = snapshot.ExportDirectories.Select(Normalize).ToList();

        // Paths already accounted for by a more specific diagnosis, so they are not also
        // reported as untracked orphans. Reporting one file twice sends someone hunting
        // for a problem that does not exist.
        var alreadyDiagnosed = new HashSet<string>(StringComparer.Ordinal);

        // --- bindings referencing things that are gone ---------------------------
        foreach (var b in enabled)
        {
            var proj = Normalize(b.ProjectPath);
            var exp = Normalize(b.ExportPath);

            var projectExists = projectsOnDisk.Contains(proj, StringComparer.Ordinal);
            var exportExists = exportsOnDisk.Contains(exp, StringComparer.Ordinal);

            if (!projectExists)
            {
                // Case-only difference is a distinct, more actionable diagnosis: Windows
                // treats the paths as one file, git as two entries.
                var caseOnly = projectsOnDisk.FirstOrDefault(
                    p => string.Equals(p, proj, StringComparison.OrdinalIgnoreCase));
                if (caseOnly is not null)
                {
                    alreadyDiagnosed.Add(caseOnly);
                    findings.Add(new ScanFinding(BindingHealth.CasingMismatch, caseOnly, b.Id,
                        $"binding records '{proj}' but the file on disk is '{caseOnly}'"));
                }
                else
                {
                    findings.Add(new ScanFinding(BindingHealth.BrokenBinding, proj, b.Id,
                        "the bound project file no longer exists"));
                }

                continue;
            }

            if (!exportExists)
            {
                findings.Add(new ScanFinding(BindingHealth.BrokenBinding, exp, b.Id,
                    "the bound export directory no longer exists; a full export will recreate it"));
                continue;
            }

            if (snapshot.ManifestBindingIds.TryGetValue(exp, out var manifestId) &&
                !string.IsNullOrEmpty(manifestId) &&
                !string.Equals(manifestId, b.Id, StringComparison.Ordinal))
            {
                findings.Add(new ScanFinding(BindingHealth.ManifestMismatch, exp, b.Id,
                    $"repo.json binds this directory to '{b.Id}' but its manifest claims '{manifestId}'"));
                continue;
            }

            findings.Add(new ScanFinding(BindingHealth.Bound, proj, b.Id, "bound"));
        }

        // --- projects nobody is tracking ------------------------------------------
        var orphanProjects = new List<string>();
        foreach (var proj in projectsOnDisk)
        {
            if (boundProjects.Contains(proj) || boundTemplates.Contains(proj) ||
                alreadyDiagnosed.Contains(proj))
            {
                continue;
            }

            // Guard 1: any path bound as a template. Guard 2: the format's own template
            // convention, which catches templates that have no binding yet. Asked of the
            // format so this scanner never learns a vendor's suffix.
            if (_formats(proj)?.IsTemplate(proj) == true)
            {
                continue;
            }

            if (IsIgnored(proj, config.Ignore))
            {
                continue;
            }

            orphanProjects.Add(proj);
        }

        // --- export trees whose project is gone ------------------------------------
        //
        // THE IGNORE LIST APPLIES HERE TOO, and it did not until 2026-08-17. An ignore rule was
        // filtering orphaned projects and not their mirrors, which is half a rule: the project
        // disappeared from the panel and every one of its export directories stayed, one finding
        // each. Found by opening this repository in the app after adding `phase0/**` — adoption
        // correctly ignored 21 scratch projects and the panel still reported 43 things needing
        // attention, almost all of them the mirrors of the projects just ignored.
        //
        // A directory holding a _manifest.json IS an export tree to the scanner, whatever it is
        // called, so scratch directories full of round-trip experiments qualify.
        var orphanExports = exportsOnDisk
            .Where(e => !boundExports.Contains(e))
            .Where(e => !IsIgnored(e, config.Ignore))
            .ToList();

        // --- rename detection ------------------------------------------------------
        // git already reports 'R old -> new' for a tracked rename; that is the strongest
        // and cheapest signal available.
        var handledOrphans = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (oldPath, newPath) in snapshot.GitRenames)
        {
            var from = Normalize(oldPath);
            var to = Normalize(newPath);
            var binding = enabled.FirstOrDefault(b =>
                string.Equals(Normalize(b.ProjectPath), from, StringComparison.Ordinal));
            if (binding is not null && orphanProjects.Contains(to))
            {
                handledOrphans.Add(to);
                findings.RemoveAll(f => f.BindingId == binding.Id &&
                                        f.Health == BindingHealth.BrokenBinding);
                findings.Add(new ScanFinding(BindingHealth.Bound, to, binding.Id,
                    $"git reports a rename from '{from}'; re-link keeps the binding id, baseline and lock"));
            }
        }

        foreach (var proj in orphanProjects.Where(p => !handledOrphans.Contains(p)))
        {
            // "PLC" rather than the brand. Scan takes no format — it works from a snapshot so
            // it stays pure — so there is nothing here that could name the vendor honestly, and
            // guessing CODESYS is how somebody with a TwinCAT repository gets told about a tool
            // they do not own. Once the formats are injected rather than fetched from a static
            // registry, this sentence can name the real one.
            findings.Add(new ScanFinding(BindingHealth.OrphanedProject, proj, null,
                "untracked PLC project — track it to start exporting"));
        }

        foreach (var exp in orphanExports)
        {
            // The old wording justified never deleting with "because it is committed
            // history" — a claim this method cannot make. The scan is pure and works from a
            // snapshot; whether the tree is committed, gitignored scratch, or somebody's
            // uncommitted afternoon is a question only git can answer, and an export tree
            // under an ignored path is exactly the case where the old sentence was false.
            // The behaviour was always right for a simpler reason: deleting files on a
            // guess about what they are is the failure mode this application exists to
            // prevent, whatever the files turn out to be.
            findings.Add(new ScanFinding(BindingHealth.OrphanedExport, exp, null,
                "export directory with no bound project; never deleted automatically — " +
                "what it is worth is a person's call, and it may be committed history"));
        }

        return findings;
    }

    /// <summary>Propose a binding for an orphaned project, using the conventions.</summary>
    /// <param name="format">Supplies the export-path and template conventions. Optional so
    /// the neutral default stays usable, but the caller should pass it: both layouts are
    /// vendor decisions, and inventing a sibling <c>_template.project</c> without asking
    /// is how a TwinCAT adopt stored a path that vendor does not use.</param>
    public static ProjectBinding ProposeBinding(
        string projectPath, string id, string? profile = null, IPlcProjectFormat? format = null) =>
        new()
        {
            Id = id,
            ProjectPath = Normalize(projectPath),
            TemplatePath = format?.ConventionalTemplatePath(projectPath) is { Length: > 0 } template
            ? Normalize(template)
            : null,
            ExportPath = Normalize(format is null
                ? BindingConventions.ConventionalExportPath(projectPath)
                : format.ConventionalExportPath(projectPath)),
            Profile = profile,
            Vendor = format?.Vendor ?? default,
            Enabled = true,
        };

    private static string Normalize(string p) => p.Replace('\\', '/').TrimStart('/');

    internal static bool IsIgnored(string path, IReadOnlyList<string> globs) =>
        PathGlob.Matches(path, globs);
}
