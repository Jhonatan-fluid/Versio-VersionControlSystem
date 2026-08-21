using Versio.Core.Logging;
using Versio.Plc.TwinCat;

namespace Versio.Plc.TwinCat.Infrastructure;

/// <summary>One TwinCAT XAE shell, as found on this machine.</summary>
/// <remarks>
/// Both 17.0 and 15.0 are the same TwinCAT build on the machine this was measured on
/// (2026-08-19, 3.1.4026.25). Preferring 17.0 is not TIA's "several Portal versions →
/// refuse" case: 17.0 is the 64-bit host. The choice is logged, never a silent conversion.
/// </remarks>
public sealed record TwinCatInstall(
    string ProgId,
    string Version,
    string InstallFolder,
    string? ShellExecutable);

/// <summary>
/// Finds TwinCAT XAE by ProgID and the install folder, not by shipping any Beckhoff DLL.
/// </summary>
public sealed class TwinCatLocator(IVersioLogger? logger = null)
{
    public const string PreferredProgId = "TcXaeShell.DTE.17.0";
    public const string FallbackProgId = "TcXaeShell.DTE.15.0";

    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    public TwinCatInstall? FindPreferred()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var progId in new[] { PreferredProgId, FallbackProgId })
        {
            var type = Type.GetTypeFromProgID(progId);
            if (type is null)
            {
                continue;
            }

            var folder = FindInstallFolder();
            var shell = FindShellExecutable();
            var version = ReadTwinCatVersion(folder) ?? "unknown";
            _log.Info(TwinCatLog.Category,
                $"found {progId} TwinCAT {version} at {folder ?? "(no 3.1 folder)"}");
            return new TwinCatInstall(progId, version, folder ?? "", shell);
        }

        _log.Debug(TwinCatLog.Category, "no TcXaeShell.DTE ProgID on this machine");
        return null;
    }

    private static string? FindInstallFolder()
    {
        // TWINCAT3DIR is set machine-wide by the Beckhoff installer and covers layouts
        // the folder candidates below do not. It matters more than it used to: the host
        // now refuses to open a project when the install version is unknown, so a
        // locator that cannot find the folder used to skip the conversion check and now
        // blocks the open instead.
        var fromEnv = Environment.GetEnvironmentVariable("TWINCAT3DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
        {
            return fromEnv;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Beckhoff", "TwinCAT", "3.1"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Beckhoff", "TwinCAT", "3.1"),
            @"C:\TwinCAT\3.1",
        };

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string? FindShellExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Beckhoff", "TcXaeShell", "Common7", "IDE", "TcXaeShell.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Beckhoff", "TcXaeShell", "Common7", "IDE", "TcXaeShell.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// TwinCAT's own build, not the Visual Studio shell's. <c>TcXaeShell.exe</c>
    /// ProductVersion is 15.0.0.0 on the 17.0 host (measured 2026-08-19) — that is
    /// the shell, and comparing it to a project's <c>TcVersion="3.1.4024.4"</c>
    /// refused every open as a conversion.
    /// </summary>
    internal static string? ReadTwinCatVersion(string? installFolder)
    {
        if (installFolder is null || !Directory.Exists(installFolder))
        {
            return null;
        }

        var plc = Path.Combine(installFolder, "Components", "Plc");
        if (Directory.Exists(plc))
        {
            var build = Directory.EnumerateDirectories(plc, "Build_*")
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => n.StartsWith("Build_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(BuildOrdinal)
                .FirstOrDefault();
            if (build is not null)
            {
                return "3.1." + build["Build_".Length..];
            }
        }

        try
        {
            var versionTxt = Path.Combine(installFolder, "Version.txt");
            if (File.Exists(versionTxt))
            {
                return File.ReadAllText(versionTxt).Trim();
            }
        }
        catch (IOException)
        {
            // An unreadable Version.txt must degrade to "unknown", not fault ProbeAsync.
        }

        return null;
    }

    // "Build_4026.25" → (4026, 25). An ordinal string sort would put a five-digit build
    // below a four-digit one. Duplicated in the host's InstallTcVersion, which cannot
    // reference this assembly.
    private static (int Major, int Minor) BuildOrdinal(string name)
    {
        var parts = name["Build_".Length..].Split('.');
        return (int.TryParse(parts[0], out var major) ? major : 0,
                parts.Length > 1 && int.TryParse(parts[1], out var minor) ? minor : 0);
    }
}
