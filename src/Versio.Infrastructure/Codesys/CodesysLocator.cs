using System.Diagnostics;
using Versio.Core.Abstractions;
using Versio.Core.Codesys;

namespace Versio.Infrastructure.Codesys;

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
public sealed class CodesysLocator(IEnumerable<string>? searchRoots = null) : ICodesysLocator
{
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

        foreach (var root in _roots.Where(Directory.Exists))
        {
            foreach (var dir in SafeDirectories(root, "CODESYS*"))
            {
                var exe = Path.Combine(dir, "CODESYS", "Common", "CODESYS.exe");
                if (!File.Exists(exe))
                {
                    continue;
                }

                installs.Add(new CodesysInstall
                {
                    InstallFolder = Path.GetFileName(dir),
                    ExecutablePath = exe,
                    Profiles = ProfilesIn(Path.Combine(dir, "CODESYS", "Profiles")),
                    FileVersion = SafeVersion(exe),
                });
            }
        }

        return installs;
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
