using Versio.Core.Logging;
using Versio.Plc.TiaPortal;

namespace Versio.Plc.TiaPortal.Infrastructure;

/// <summary>One TIA Portal installation, as found on disk.</summary>
/// <remarks>
/// Version is the folder name (<c>V21</c>), not a file version: that is what the project
/// file's extension already claimed, and guessing a newer PublicAPI than the install
/// ships is how OpenWithUpgrade happens by accident.
/// </remarks>
public sealed record TiaPortalInstall(
    string InstallFolder,
    string Version,
    string ApiFolder,
    string? PortalExecutable);

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
                    _log.Debug(TiaPortalLog.Category,
                        $"skipped {folder}: no PublicAPI net48 folder");
                    continue;
                }

                var exe = Path.Combine(folder, "Bin", "Siemens.Automation.Portal.exe");
                if (!File.Exists(exe))
                {
                    exe = Directory.EnumerateFiles(folder, "*Portal*.exe", SearchOption.AllDirectories)
                        .FirstOrDefault() ?? string.Empty;
                }

                found.Add(new TiaPortalInstall(
                    folder, version, api, File.Exists(exe) ? exe : null));
                _log.Info(TiaPortalLog.Category,
                    $"found TIA Portal {version} at {folder} (API {api})");
            }
        }

        return found
            .OrderByDescending(i => i.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FindApiFolder(string installFolder, string version)
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
            return preferred;
        }

        foreach (var candidate in Directory.EnumerateDirectories(publicApi)
                     .Select(d => Path.Combine(d, "net48"))
                     .Where(Directory.Exists))
        {
            if (File.Exists(Path.Combine(candidate, "Siemens.Engineering.Base.dll")))
            {
                return candidate;
            }
        }

        return null;
    }
}
