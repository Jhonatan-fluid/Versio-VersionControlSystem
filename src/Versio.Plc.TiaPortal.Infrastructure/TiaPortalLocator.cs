using System.Text.RegularExpressions;
using Versio.Core.Logging;
using Versio.Plc.TiaPortal;

namespace Versio.Plc.TiaPortal.Infrastructure;

/// <summary>One TIA Portal installation, as found on disk.</summary>
/// <remarks>
/// <para>
/// Version is the release in the folder name (<c>V21</c>), not a file version: that is what
/// the project file's extension already claimed, and guessing a newer PublicAPI than the
/// install ships is how OpenWithUpgrade happens by accident.
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

    /// <summary>The number in <c>V21</c>; -1 for a version that does not read as one.</summary>
    public int ReleaseNumber => ReleaseNumberOf(Version);

    public static int ReleaseNumberOf(string version) =>
        version.Length > 1
        && (version[0] == 'V' || version[0] == 'v')
        && int.TryParse(version.AsSpan(1), out var n)
            ? n
            : -1;
}

/// <summary>
/// Finds TIA Portal installations by reading the filesystem.
/// </summary>
public sealed partial class TiaPortalLocator(
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

    /// <summary>
    /// Every usable install, newest release first, one per release.
    /// </summary>
    /// <remarks>
    /// The release is the <c>V\d+</c> in the folder name — <c>Portal V21</c>, and also
    /// <c>Portal V21 Update 2</c> should Siemens ever name one so — and the folder under
    /// <c>PublicAPI</c> has to be named after exactly that release; a folder whose name
    /// carries no release is skipped and said so. Ordered by the number, not the string,
    /// so a two-digit release never sorts under a one-digit one. A release found under
    /// both Program Files roots is reported once, from the first root searched, and logged.
    /// </remarks>
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
                var release = ReleaseInName().Match(name);
                if (!release.Success)
                {
                    _log.Info(TiaPortalLog.Category,
                        $"skipped {folder}: no release (V and a number) in the folder name");
                    continue;
                }

                var version = "V" + release.Groups[1].Value;
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

                if (found.FirstOrDefault(i => string.Equals(i.Version, version, StringComparison.OrdinalIgnoreCase))
                    is { } twin)
                {
                    _log.Info(TiaPortalLog.Category,
                        $"skipped {folder}: Portal {version} was already found at {twin.InstallFolder}");
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
            .OrderByDescending(i => i.ReleaseNumber)
            .ToList();
    }

    /// <summary>
    /// The Openness folder for this install's own release, and its shape — or null.
    /// </summary>
    /// <remarks>
    /// An install's <c>PublicAPI</c> also carries the API of earlier releases (Portal V20
    /// ships <c>V17</c> through <c>V20</c>) so that older Openness clients keep working. Only
    /// the folder named after the install's own release is accepted, in either layout: the
    /// API release is what decides how the project is opened, and an install driven through
    /// another release's folder is an upgrade or a refusal, never the install. Until
    /// 2026-10-29 the modular lookup fell back to <i>any</i> <c>*\net48</c> under
    /// <c>PublicAPI</c>, which would have reported a <c>Portal V22</c> carrying only
    /// <c>V21\net48</c> as V22 and driven it through V21; found in review, removed.
    /// </remarks>
    private static (string Folder, string Shape)? FindApiFolder(string installFolder, string version)
    {
        var own = Path.Combine(installFolder, "PublicAPI", version);
        if (!Directory.Exists(own))
        {
            return null;
        }

        var modular = Path.Combine(own, "net48");
        if (File.Exists(Path.Combine(modular, "Siemens.Engineering.Base.dll")))
        {
            return (modular, TiaPortalInstall.ModularApi);
        }

        if (File.Exists(Path.Combine(own, "Siemens.Engineering.dll")))
        {
            return (own, TiaPortalInstall.SingleAssemblyApi);
        }

        return null;
    }

    [GeneratedRegex(@"\bV(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseInName();
}
