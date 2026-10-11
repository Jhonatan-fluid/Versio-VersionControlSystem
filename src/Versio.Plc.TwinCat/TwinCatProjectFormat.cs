using Versio.Core.Plc;
using Versio.Core.Projects;

namespace Versio.Plc.TwinCat;

/// <summary>
/// TwinCAT 3's project-file conventions.
/// </summary>
/// <remarks>
/// The third concrete <see cref="IPlcProjectFormat"/>. Everything here is this vendor's:
/// the extensions, the directory payload, the object-type names, and the planner. Sync
/// never reads this type.
/// </remarks>
public sealed class TwinCatProjectFormat : IPlcProjectFormat
{
    public static readonly TwinCatProjectFormat Instance = new();

    public PlcVendor Vendor => PlcVendor.TwinCat;

    public string DisplayName => "TwinCAT 3";

    public string LogCategory => TwinCatLog.Category;

    /// <remarks>
    /// DTE create was 2.6 s on this machine (2026-08-19) and then rejected the next
    /// call without an IOleMessageFilter — that is why the host registers one before
    /// touching SuppressUI. A TEMP-copy probe then cost ~19 s and an export ~22 s;
    /// TypicalStartupCost follows the export, not the public 30–60 s guess.
    /// </remarks>
    public PlcCapabilities Capabilities => new()
    {
        TextualExport = true,
        TextualImport = true,
        ObjectCreation = true,
        Headless = true,
        // Occupancy is a sidecar on the path (sibling .~u), measured 2026-08-21
        // against a live TcXaeShell window. A copy at another path is therefore
        // unencumbered the same way the first vendor's copy is: the lock is the
        // original's marker, not a machine-wide hold on the tree. Sync already
        // copies a directory payload (CopyOpenProjectAsync); this flag is what
        // lets it. Import still waits — the IDE holds the original in memory.
        ExportHeldViaCopy = true,
        TypicalStartupCost = TimeSpan.FromSeconds(22),
    };

    /// <remarks>
    /// <c>.tsproj</c> is the working TwinCAT project. <c>.tszip</c> is a packed
    /// archive and <c>.tspproj</c> is a safety project — seen and refused rather
    /// than imported, the same bargain <c>.alXX</c> makes on TIA.
    /// </remarks>
    public IReadOnlyList<string> ProjectExtensions => [".tsproj", ".tszip", ".tspproj"];

    public bool IsTemplate(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".tszip", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".tspproj", StringComparison.OrdinalIgnoreCase);
    }

    public string ConventionalExportPath(string projectPath)
    {
        var normalised = projectPath.Replace('\\', '/');
        var ext = System.IO.Path.GetExtension(normalised);
        return string.IsNullOrEmpty(ext)
            ? normalised + ".export"
            : normalised[..^ext.Length];
    }

    public string? ConventionalTemplatePath(string projectPath) => null;

    /// <inheritdoc />
    /// <remarks>
    /// The <c>.tsproj</c> is a pointer. The PLC lives in folders beside it
    /// (<c>LibraryDocumentationSample/</c> on the gate fixture). Hashing or
    /// backing up only the pointer would leave the program behind.
    /// </remarks>
    public string PayloadPathFor(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
        {
            return projectPath;
        }

        var dir = System.IO.Path.GetDirectoryName(projectPath);
        return string.IsNullOrEmpty(dir) ? projectPath : dir;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>_Boot/</c> and <c>_CompileInfo/</c> are generated. <c>.vs/</c>,
    /// <c>*.suo</c> and <c>*.user</c> are the shell. <c>LineIDs.dbg</c> churns
    /// with the editor caret. <c>.TcPOU</c> / <c>.plcproj</c> <b>are</b> the
    /// program — skipping them would make the pointer the whole hash.
    /// </remarks>
    public bool CountsTowardProjectHash(string relativePath)
    {
        var normalised = relativePath.Replace('\\', '/');
        var parts = normalised.Split('/');
        var first = parts[0];
        var file = parts[^1];
        if (string.Equals(first, "_Boot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, "_CompileInfo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, ".vs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The transient pattern below claims "stem.~u*"; this filter must not claim
        // less, or a suffixed marker variant counts toward the project hash and every
        // in-flight export reads as a program change.
        if (file.StartsWith('~')
            || file.EndsWith(".suo", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".user", StringComparison.OrdinalIgnoreCase)
            || file.Equals("LineIDs.dbg", StringComparison.OrdinalIgnoreCase)
            || System.IO.Path.GetExtension(file).StartsWith(".~", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// XAE writes <c>&lt;stem&gt;.~u</c> beside the loaded solution, same stem as
    /// the <c>.tsproj</c> on the gate fixture. Until a successful open records the
    /// body, existence is Held — guessing Free from an unknown format is how two
    /// writers would share a project. Stale PID recovery uses the same line layout
    /// as the first vendor when those lines are present.
    /// </summary>
    public string? OccupancyMarkerFor(string projectPath) =>
        string.IsNullOrEmpty(projectPath)
            ? null
            : System.IO.Path.ChangeExtension(projectPath, ".~u");

    public IReadOnlyList<string> TransientPatternsFor(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
        {
            return [];
        }

        var normalised = projectPath.Replace('\\', '/');
        var dir = PayloadPathFor(normalised).Replace('\\', '/').TrimEnd('/');
        var stem = System.IO.Path.GetFileNameWithoutExtension(normalised);
        return
        [
            dir + "/_Boot/",
            dir + "/_CompileInfo/",
            dir + "/.vs/",
            dir + "/" + stem + ".~u*",
            dir + "/*.suo",
            dir + "/*.user",
            dir + "/**/LineIDs.dbg",
        ];
    }

    public ProjectOccupancy ParseOccupancy(string markerContents)
    {
        var lines = (markerContents ?? string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .ToArray();

        return new ProjectOccupancy
        {
            Verdict = OccupancyVerdict.Held,
            User = lines.Length > 0 ? lines[0] : string.Empty,
            Machine = lines.Length > 1 ? lines[1] : string.Empty,
            ProcessId = lines.Length > 2 && int.TryParse(lines[2], out var pid) && pid > 0
                ? pid
                : null,
        };
    }

    public bool NeedsTextToPlan(string relativePath) =>
        TwinCatImportPlanner.NeedsText(relativePath);

    public ImportPlan PlanImport(
        ProjectManifest? manifest,
        IReadOnlyList<MirrorFile> mirror,
        IReadOnlyDictionary<string, string> departed) =>
        TwinCatImportPlanner.Plan(manifest, mirror, departed);

    public ObjectTypeName? DisplayNameFor(string type) =>
        TwinCatObjectTypeNames.TryFor(type);

    /// <remarks>
    /// The mirror's <c>.st</c> is Versio's own format, so its declaration reads into the same
    /// table a TIA block's interface and a CODESYS block's do, with the code underneath: the
    /// owner asked for one rendering across platforms (2026-10-10).
    /// </remarks>
    public MemberTable? TryParseMemberTable(string relativePath, string content) =>
        Versio.Core.Iec.StDeclarationTable.FromSource(relativePath, content);

    public (int Start, int End)? CodeBody(string relativePath, string content) =>
        relativePath.EndsWith(".st", StringComparison.OrdinalIgnoreCase)
            ? Versio.Core.Iec.StDeclarationTable.SourceCodeBody(content)
            : null;
}
