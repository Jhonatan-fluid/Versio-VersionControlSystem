using System.Diagnostics;
using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.Plc;
using Versio.Plc.Codesys;

namespace Versio.Plc.Codesys.Infrastructure;

/// <summary>
/// Finds CODESYS installations and their usable profiles by reading the filesystem.
/// </summary>
/// <remarks>
/// <para>
/// <b>The profile name is never synthesised from the version.</b> Measured: the install
/// folder is <c>CODESYS 3.5.22.20</c> while the profile is
/// <c>CODESYS V3.5 SP22 Patch 2</c> — "22.20" is SP22 Patch 2, not Patch 20. Building the
/// string from a version number yields a profile that does not exist, and CODESYS then
/// refuses to start.
/// </para>
/// <para>
/// Active profiles are <c>*.profile.xml</c> in <c>CODESYS\Profiles</c>. The
/// <c>Informational</c> subfolder holds historical <c>*.profile</c> files for versions
/// going back to V3.3; those are <b>not</b> usable and are excluded.
/// </para>
/// </remarks>
public sealed class CodesysLocator(
    IEnumerable<string>? searchRoots = null,
    IVersioLogger? logger = null) : ICodesysLocator
{
    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    private static readonly string[] DefaultRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    ];

    private readonly string[] _roots = (searchRoots ?? DefaultRoots)
        .Where(r => !string.IsNullOrWhiteSpace(r))
        .ToArray();

    public IReadOnlyList<CodesysInstall> FindInstalls()
    {
        var installs = new List<CodesysInstall>();

        _log.Debug(CodesysLog.Category,
            $"searching for CODESYS under {string.Join(", ", _roots)}");

        foreach (var root in _roots.Where(Directory.Exists))
        {
            foreach (var dir in SafeDirectories(root, "CODESYS*"))
            {
                var exe = Path.Combine(dir, "CODESYS", "Common", "CODESYS.exe");
                if (!File.Exists(exe))
                {
                    continue;
                }

                var install = new CodesysInstall
                {
                    InstallFolder = Path.GetFileName(dir),
                    ExecutablePath = exe,
                    Profiles = ProfilesIn(Path.Combine(dir, "CODESYS", "Profiles")),
                    FileVersion = SafeVersion(exe),
                };

                installs.Add(install);

                // The profile names in full, because they cannot be guessed from the
                // folder name and the exact string is what --profile= requires.
                _log.Info(CodesysLog.Category,
                    $"found CODESYS {install.FileVersion} at {exe} with " +
                    (install.Profiles.Count == 0
                        ? "no usable profiles"
                        : $"profile(s): {string.Join(", ", install.Profiles)}"));
            }
        }

        if (installs.Count == 0)
        {
            // Not an error here — the app runs without CODESYS and says so — but it is the
            // reason every later export is refused, so it is stated once, plainly.
            _log.Warn(CodesysLog.Category,
                "no CODESYS installation found; export and import will be unavailable");
        }

        return installs;
    }

    /// <summary>
    /// The profiles of the install that owns this executable, read the same way
    /// <see cref="FindInstalls"/> reads them. Empty when the layout is not the usual one.
    /// </summary>
    /// <remarks>
    /// The runner is handed an executable path and nothing else. The install it belongs to is
    /// <c>…\CODESYS\Common\CODESYS.exe</c>, with <c>…\CODESYS\Profiles</c> beside
    /// <c>Common</c>, which is the layout <see cref="FindInstalls"/> requires before it
    /// accepts an install at all.
    /// </remarks>
    public static IReadOnlyList<string> ProfilesBeside(string executablePath)
    {
        var common = Path.GetDirectoryName(executablePath);
        var codesys = common is null ? null : Path.GetDirectoryName(common);
        return codesys is null ? [] : ProfilesIn(Path.Combine(codesys, "Profiles"));
    }

    private static IReadOnlyList<string> ProfilesIn(string profilesDir)
    {
        if (!Directory.Exists(profilesDir))
        {
            return [];
        }

        // TopDirectoryOnly on purpose: it excludes Informational\, whose *.profile files
        // are historical records, not installed profiles.
        var names = Directory
            .EnumerateFiles(profilesDir, "*.profile.xml", SearchOption.TopDirectoryOnly)
            .Select(ProfileResolver.ProfileNameFromFile)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();

        return ProfileResolver.RankNewestFirst(names);
    }

    private static IEnumerable<string> SafeDirectories(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static string? SafeVersion(string exe)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(exe).FileVersion;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
