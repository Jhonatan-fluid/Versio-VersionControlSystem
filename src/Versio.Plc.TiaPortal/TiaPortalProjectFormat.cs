using Versio.Core.Plc;
using Versio.Core.Projects;

namespace Versio.Plc.TiaPortal;

/// <summary>
/// TIA Portal's project-file conventions.
/// </summary>
/// <remarks>
/// The second concrete <see cref="IPlcProjectFormat"/>. Everything here is this vendor's:
/// the extensions, the directory payload, the object-type names, and the planner. Sync
/// never reads this type.
/// </remarks>
public sealed class TiaPortalProjectFormat : IPlcProjectFormat
{
    public static readonly TiaPortalProjectFormat Instance = new();

    public PlcVendor Vendor => PlcVendor.TiaPortal;

    public string DisplayName => "TIA Portal";

    /// <remarks>
    /// Startup cost is a floor until Phase 0 measures a cold start on this machine.
    /// The host still uses a twenty-minute timeout because a 488 MB project export is
    /// not the first vendor's 20-second session.
    /// </remarks>
    public PlcCapabilities Capabilities => new()
    {
        TextualExport = true,
        TextualImport = true,
        ObjectCreation = true,
        Headless = true,
        TypicalStartupCost = TimeSpan.FromMinutes(2),
    };

    /// <remarks>
    /// <c>.ap17</c> through <c>.ap21</c> are working projects. <c>.alXX</c> archives are
    /// seen and refused rather than imported — same shape as a <c>.library</c> on the
    /// first vendor: detection without a drive path is the useful answer.
    /// </remarks>
    public IReadOnlyList<string> ProjectExtensions =>
        [".ap17", ".ap18", ".ap19", ".ap20", ".ap21", ".al17", ".al18", ".al19", ".al20", ".al21"];

    public bool IsTemplate(string path)
    {
        // Archives (.alXX) are this vendor's packed project. Adopting one would bind
        // Versio to a file TIA will not open as a working tree. Treated as a template
        // so the scanner sees them and the adoption path refuses, the same bargain
        // *_template.project makes on the first vendor.
        var ext = System.IO.Path.GetExtension(path);
        return ext.StartsWith(".al", StringComparison.OrdinalIgnoreCase);
    }

    public string ConventionalExportPath(string projectPath)
    {
        var normalised = projectPath.Replace('\\', '/');
        var ext = System.IO.Path.GetExtension(normalised);
        return string.IsNullOrEmpty(ext)
            ? normalised + ".export"
            : normalised[..^ext.Length];
    }

    /// <inheritdoc />
    /// <remarks>
    /// The <c>.apXX</c> file is a 150 KB pointer. The program lives in the directory
    /// beside it, and TIA cannot open a copy of the pointer alone. Hashing, backing up
    /// or exporting that file would be the first vendor's copy pipeline applied to the
    /// wrong thing.
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
    /// <c>Logs/</c> and <c>tmp/</c> churn while the program does not. <c>Vci/</c> is
    /// Siemens's own git exporter sitting inside the tree — hashing it would make
    /// somebody else's workspace look like a program change. <c>System/</c> is the
    /// program — skipping it would make a 150 KB pointer the whole hash of a
    /// half-gigabyte project.
    /// </remarks>
    public bool CountsTowardProjectHash(string relativePath)
    {
        var first = relativePath.Replace('\\', '/').Split('/')[0];
        return !string.Equals(first, "Logs", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(first, "tmp", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(first, "Vci", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// No sidecar has been measured. A null marker reads as free; the host's open is
    /// the backstop, and an exclusive open that fails is reported rather than guessed.
    /// </summary>
    public string? OccupancyMarkerFor(string projectPath) => null;

    public IReadOnlyList<string> TransientPatternsFor(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
        {
            return [];
        }

        var dir = PayloadPathFor(projectPath.Replace('\\', '/')).Replace('\\', '/').TrimEnd('/');
        return [dir + "/Logs/", dir + "/tmp/", dir + "/Vci/"];
    }

    public ProjectOccupancy ParseOccupancy(string markerContents) => ProjectOccupancy.Free;

    public bool NeedsTextToPlan(string relativePath) =>
        TiaPortalImportPlanner.NeedsText(relativePath);

    public ImportPlan PlanImport(
        ProjectManifest? manifest,
        IReadOnlyList<MirrorFile> mirror,
        IReadOnlyDictionary<string, string> departed) =>
        TiaPortalImportPlanner.Plan(manifest, mirror, departed);

    public ObjectTypeName? DisplayNameFor(string type) =>
        TiaPortalObjectTypeNames.TryFor(type);
}
