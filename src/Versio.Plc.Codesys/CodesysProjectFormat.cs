using Versio.Core.Bindings;
using Versio.Core.Plc;

namespace Versio.Plc.Codesys;

/// <summary>
/// CODESYS 3.5's project-file conventions.
/// </summary>
/// <remarks>
/// The first concrete <see cref="IPlcProjectFormat"/>, and the reference for what a second
/// vendor has to supply. Everything here is genuinely CODESYS-specific: the extensions, the
/// template naming convention, and what the toolchain can do.
/// </remarks>
public sealed class CodesysProjectFormat : IPlcProjectFormat
{
    public static readonly CodesysProjectFormat Instance = new();

    public PlcVendor Vendor => PlcVendor.Codesys;

    public string DisplayName => "CODESYS 3.5";

    public string LogCategory => CodesysLog.Category;

    /// <remarks>
    /// Every capability here was measured against a real project rather than assumed —
    /// see <c>docs/phase1/PHASE0-FINDINGS.md</c>. <see cref="PlcCapabilities.TypicalStartupCost"/>
    /// in particular is why export-on-save is not the default sync mode.
    /// </remarks>
    public PlcCapabilities Capabilities => new()
    {
        TextualExport = true,
        TextualImport = true,
        ObjectCreation = true,
        Headless = true,
        // The occupancy marker is a sidecar on the path. A copy at another path is
        // unencumbered and exports byte-identically — measured against a real IDE
        // holding the original.
        ExportHeldViaCopy = true,
        TypicalStartupCost = TimeSpan.FromSeconds(20),
    };

    /// <remarks>
    /// <c>.library</c> is included because a library project is still a CODESYS project;
    /// importing one is not yet supported, but it must be <i>seen</i> so the user is told
    /// that rather than left wondering why it was ignored.
    /// </remarks>
    public IReadOnlyList<string> ProjectExtensions => [".project", ".library"];

    public bool IsTemplate(string path) => BindingConventions.LooksLikeTemplate(path);

    public string ConventionalExportPath(string projectPath) =>
        BindingConventions.ConventionalExportPath(projectPath);

    public string? ConventionalTemplatePath(string projectPath) =>
        BindingConventions.ConventionalTemplatePath(projectPath);

    /// <summary>
    /// What CODESYS leaves beside a project, measured on 3.5.22.20.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opening <c>Machine.project</c> creates <c>Machine.~u</c> while it is held and removes it
    /// on close; a crash leaves it behind. Exporting or opening also leaves
    /// <c>Machine-AllUsers.opt</c> and <c>Machine-&lt;user&gt;-&lt;machine&gt;.opt</c>, and those
    /// <b>persist</b> — they are per-user settings, not locks.
    /// </para>
    /// <para>
    /// The per-user file is the one that matters: its <i>name</i> carries the username and the
    /// machine name, so committing it writes both into history and guarantees a different file
    /// for every engineer who opens the project.
    /// </para>
    /// <para>
    /// <b>The marker is written through a temporary sibling.</b> While the IDE holds a project
    /// it rewrites the marker every few seconds, and each rewrite goes via
    /// <c>Machine.~u~</c> — trailing tilde — which then disappears. Measured with a watcher on
    /// a real repository: 20 seconds of an idle open project produced 7 events for
    /// <c>.~u~</c> and 4 for <c>.~u</c>. So the pattern is <c>.~u*</c>, not <c>.~u</c>: the
    /// narrower rule left a file git had never been told to ignore appearing and vanishing in
    /// the working tree, and left Versio spending a <c>git status</c> on each one.
    /// </para>
    /// <para>
    /// <b><c>&lt;stem&gt;_project.precompilecache</c> was one of the unobserved ones until
    /// 2026-08-15</b>, and it is the largest of the three — 277 KB of binary beside the
    /// fixture. The dot of <c>.project</c> becomes an underscore, confirmed across six
    /// projects. It went unnoticed because *this* repository has <c>*.precompilecache</c> in
    /// <c>.gitignore</c>, typed there by hand, so the only tree anybody watched was already
    /// covered — the same way the staging rule was present here and missing from every
    /// repository Versio adopted. An adopted repository was leaving it untracked where
    /// <i>Stage all</i> would commit it.
    /// </para>
    /// <para>
    /// Only observed files are listed. CODESYS can be configured to write <c>.bak</c> backups
    /// too, and that is deliberately absent: it has not been seen here, and the rules are
    /// re-checked on every adoption, so it costs one line to add when it is.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> TransientPatternsFor(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
        {
            return [];
        }

        var normalized = projectPath.Replace(Backslash, '/');
        var stem = normalized.EndsWith(BindingConventions.ProjectExtension,
                                       StringComparison.OrdinalIgnoreCase)
            ? normalized[..^BindingConventions.ProjectExtension.Length]
            : normalized;

        return [stem + ".~u*", stem + "-*.opt", stem + "_project.precompilecache"];
    }

    /// <summary>
    /// CODESYS writes <c>&lt;project&gt;.~u</c> beside the project while the IDE holds it.
    /// </summary>
    /// <remarks>
    /// The extension is <b>replaced</b>, not appended: <c>Machine.project</c> is locked by
    /// <c>Machine.~u</c>. Measured on 3.5.22.20 by holding a project open and watching the
    /// directory; the file appears on open and is removed on close.
    /// </remarks>
    public string? OccupancyMarkerFor(string projectPath) =>
        string.IsNullOrEmpty(projectPath)
            ? null
            : System.IO.Path.ChangeExtension(projectPath, ".~u");

    /// <summary>
    /// Read a <c>.~u</c>: user, machine, pid, timestamp, one per line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured contents, from a real IDE session:
    /// </para>
    /// <code>
    /// elli_
    /// ROG-JVM
    /// 45812
    /// 639221606143479369
    /// </code>
    /// <para>
    /// Undocumented, so it is parsed defensively: a file that exists at all means somebody has
    /// the project, and every field beyond that is a bonus used only for wording. A future
    /// CODESYS writing a fourth field, or none, must not turn "somebody has this open" into
    /// "nobody does" — which is the one way this could lose work.
    /// </para>
    /// </remarks>
    public ProjectOccupancy ParseOccupancy(string markerContents)
    {
        var lines = (markerContents ?? string.Empty)
            .Split(Lf)
            .Select(l => l.Trim())
            .ToArray();

        return new ProjectOccupancy
        {
            Verdict = OccupancyVerdict.Held,
            User = Field(lines, 0),
            Machine = Field(lines, 1),
            ProcessId = int.TryParse(Field(lines, 2), out var pid) && pid > 0 ? pid : null,
        };
    }

    /// <inheritdoc />
    public bool NeedsTextToPlan(string relativePath) =>
        CodesysImportPlanner.NeedsText(relativePath);

    /// <inheritdoc />
    /// <remarks>Delegates to <see cref="CodesysImportPlanner"/>, where the reasoning lives —
    /// this class is the seam's phone book, not its brain.</remarks>
    public ImportPlan PlanImport(
        Versio.Core.Projects.ProjectManifest? manifest,
        IReadOnlyList<MirrorFile> mirror,
        IReadOnlyDictionary<string, string> departed) =>
        CodesysImportPlanner.Plan(manifest, mirror, departed);

    /// <summary>The line separator, written without an escape on purpose.</summary>
    /// <remarks>
    /// Trim() with no arguments removes the carriage return and any stray whitespace,
    /// so a marker written with either line ending parses the same way.
    /// </remarks>
    /// <summary>Path separator, written without an escape for the same reason as Lf.</summary>
    private const char Backslash = (char)92;

    private const char Lf = (char)10;

    private static string Field(string[] lines, int index) =>
        index < lines.Length ? lines[index] : string.Empty;
    /// <summary>CODESYS's own words for its own tree. Null for anything IEC 61131-3 names.</summary>
    /// <remarks>
    /// Returning null for <c>POU</c> and <c>ACTION</c> is deliberate rather than an omission:
    /// those belong to the standard, <see cref="Versio.Core.Projects.ObjectTypeNames"/> falls through to it,
    /// and a vendor repeating them here would be the first step back towards one table nobody
    /// can partition.
    /// </remarks>
    public Versio.Core.Projects.ObjectTypeName? DisplayNameFor(string type) =>
        CodesysObjectTypeNames.TryFor(type);

    /// <remarks>
    /// CODESYS File-Based Storage (3.5.22, PDE licence): a project saved as a folder of
    /// readable files. Shown and drawn, never exported or imported. See <c>docs/fbs/PLAN.md</c>.
    /// </remarks>
    public IReadOnlyList<string> SourceProjectExtensions => FileBased.CodesysFileBasedLayout.Extensions;

    public Versio.Core.Projects.ProjectManifest? DescribeSourceProject(string projectPath, IReadOnlyList<string> relativeFiles) =>
        FileBased.CodesysFileBasedLayout.Describe(projectPath, relativeFiles);

    /// <remarks>
    /// The LD/FBD network list, from a classic mirror's native <c>.xml</c> or a File-Based Storage
    /// <c>.xml.v3</c>; null for everything else, CFC and SFC included, which keep their raw view.
    /// </remarks>
    public Versio.Core.Iec.Ladder.LadderDocument? TryParseLadder(string relativePath, string content) =>
        Ladder.CodesysLadderParser.TryParse(relativePath, content);

}
