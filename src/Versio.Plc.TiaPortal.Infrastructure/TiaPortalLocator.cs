using Versio.Core.Logging;
using Versio.Plc.TiaPortal;

namespace Versio.Plc.TiaPortal.Infrastructure;

/// <summary>One TIA Portal installation, as found on disk.</summary>
/// <remarks>
/// <para>
/// Version is the folder name (<c>V21</c>), not a file version: that is what the project
/// file's extension already claimed, and guessing a newer PublicAPI than the install
/// ships is how OpenWithUpgrade happens by accident.
/// </para>
/// <para>
/// <see cref="ApiShape"/> is how that release ships Openness. V21 moved to modular
/// assemblies under <c>PublicAPI\V21\net48\</c> (<c>Siemens.Engineering.Base.dll</c> and
/// friends); every release before it is one <c>Siemens.Engineering.dll</c> directly under
/// <c>PublicAPI\VNN\</c>. The host loads whatever <c>Siemens.Engineering*.dll</c> the folder
/// holds and finds its types by name, so it does not care which — but the locator has to
/// know both, because until 2026-10-29 it knew only the modular one and a machine with
/// Portal V20 and V21 side by side looked like a machine with V21 alone. FINDINGS 2026-10-29.
/// </para>
/// </remarks>
public sealed record TiaPortalInstall(
    string InstallFolder,
    string Version,
    string ApiFolder,
    string? PortalExecutable,
    string ApiShape)
{
    public const string ModularApi = "modular net48 assemblies";
    public const string SingleAssemblyApi = "single Siemens.Engineering.dll";
}

/// <summary>
/// Finds TIA Portal installations by reading the filesystem.
/// </summary>
public sealed class TiaPortalLocator(
    IEnumerable<string>? searchRoots = null,
    IVersioLogger? logger = null)
{
    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    private static readonly string[] DefaultRoots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Siemens", "Automation"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Siemens", "Automation"),
    ];

    private readonly string[] _roots = (searchRoots ?? DefaultRoots)
        .Where(r => !string.IsNullOrWhiteSpace(r))
        .ToArray();

    public IReadOnlyList<TiaPortalInstall> FindInstalls()
    {
        List<TiaPortalInstall> found = [];

        foreach (var root in _roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var folder in Directory.EnumerateDirectories(root, "Portal V*"))
            {
                var name = Path.GetFileName(folder);
                var version = name.StartsWith("Portal ", StringComparison.OrdinalIgnoreCase)
                    ? name["Portal ".Length..].Trim()
                    : name;
                var api = FindApiFolder(folder, version);
                if (api is null)
                {
                    // Info, not debug: this line is the one that explains a missing release
                    // to somebody reading the log, and at debug it was invisible in the panel
                    // while the toolchain reported itself ready with the other one.
                    _log.Info(TiaPortalLog.Category,
                        $"skipped {folder}: no Openness API under PublicAPI\\{version} "
                        + "(neither a net48 folder with Siemens.Engineering.Base.dll nor a Siemens.Engineering.dll)");
                    continue;
                }

                var exe = Path.Combine(folder, "Bin", "Siemens.Automation.Portal.exe");
                if (!File.Exists(exe))
                {
                    exe = Directory.EnumerateFiles(folder, "*Portal*.exe", SearchOption.AllDirectories)
                        .FirstOrDefault() ?? string.Empty;
                }

                found.Add(new TiaPortalInstall(
                    folder, version, api.Value.Folder, File.Exists(exe) ? exe : null, api.Value.Shape));
                _log.Info(TiaPortalLog.Category,
                    $"found TIA Portal {version} at {folder} (API {api.Value.Folder}, {api.Value.Shape})");
            }
        }

        return found
            .OrderByDescending(i => i.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The Openness folder for this install's own release, and its shape — or null.
    /// </summary>
    /// <remarks>
    /// An install's <c>PublicAPI</c> also carries the API of earlier releases (Portal V20
    /// ships <c>V17</c> through <c>V20</c>) so that older Openness clients keep working. Only
    /// the folder named after the install's own release is accepted here: the single-assembly
    /// lookup never falls back to another release's folder, because the API version is what
    /// decides how the project is opened. The modular lookup keeps its older fallback to any
    /// <c>*\net48</c> for the layouts measured before this was understood.
    /// </remarks>
    private static (string Folder, string Shape)? FindApiFolder(string installFolder, string version)
    {
        var publicApi = Path.Combine(installFolder, "PublicAPI");
        if (!Directory.Exists(publicApi))
        {
            return null;
        }

        var preferred = Path.Combine(publicApi, version, "net48");
        if (Directory.Exists(preferred)
            && File.Exists(Path.Combine(preferred, "Siemens.Engineering.Base.dll")))
        {
            return (preferred, TiaPortalInstall.ModularApi);
        }

        foreach (var candidate in Directory.EnumerateDirectories(publicApi)
                     .Select(d => Path.Combine(d, "net48"))
                     .Where(Directory.Exists))
        {
            if (File.Exists(Path.Combine(candidate, "Siemens.Engineering.Base.dll")))
            {
                return (candidate, TiaPortalInstall.ModularApi);
            }
        }

        var single = Path.Combine(publicApi, version);
        if (File.Exists(Path.Combine(single, "Siemens.Engineering.dll")))
        {
            return (single, TiaPortalInstall.SingleAssemblyApi);
        }

        return null;
    }
}
