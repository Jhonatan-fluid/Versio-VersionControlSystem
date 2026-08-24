namespace Versio.Infrastructure.FileSystem;

/// <summary>
/// Machine-local files that belong to the user, not to the installed program.
/// </summary>
/// <remarks>
/// <para>
/// Velopack's per-user Setup.exe installs into <c>%LOCALAPPDATA%\Versio</c> (<c>current\</c>,
/// <c>packages\</c>, <c>Update.exe</c>). Settings and logs used to live in that same folder
/// because it was just a convenient name, years before an installer existed. The first
/// Setup.exe that occupied it deleted <c>settings.json</c> — the recent-repository dropdown
/// went empty — and uninstall of that layout would have taken the logs with it.
/// </para>
/// <para>
/// User state therefore lives in a sibling folder Velopack does not own. Updates replace
/// <c>current\</c>; uninstall deletes the packId directory; neither touches
/// <see cref="Root"/>. Still Local, not Roaming: logs are machine-specific, and a domain
/// that copies <c>%APPDATA%</c> on every logon must not carry them.
/// </para>
/// </remarks>
public static class AppUserPaths
{
    /// <summary>The folder Velopack (and a leftover F5) used for everything.</summary>
    public const string LegacyFolderName = "Versio";

    /// <summary>User state. Not the packId, on purpose.</summary>
    public const string UserFolderName = "VersioUser";

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary><c>%LOCALAPPDATA%\VersioUser</c>.</summary>
    public static string Root => Path.Combine(LocalAppData, UserFolderName);

    /// <summary>Theme, panel sizes, recent repositories.</summary>
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Daily log files.</summary>
    public static string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>The Velopack per-user install root, and the old settings/logs parent.</summary>
    public static string LegacyRoot => Path.Combine(LocalAppData, LegacyFolderName);

    public static string LegacySettingsFile => Path.Combine(LegacyRoot, "settings.json");

    public static string LegacyLogsDirectory => Path.Combine(LegacyRoot, "logs");

    /// <summary>
    /// Copy leftover settings and logs out of the Velopack directory once.
    /// </summary>
    /// <remarks>
    /// Does not overwrite a file that is already in the durable location, and does not
    /// delete the originals — an older build may still be writing there until it is replaced.
    /// </remarks>
    public static void MigrateFromLegacyInstallDirectory() =>
        Migrate(LegacyRoot, Root);

    /// <summary>The same copy, with roots a test can point at a temp tree.</summary>
    /// <remarks>
    /// <b>Best-effort on purpose — a rescue must not become the thing that needs rescuing.</b>
    /// This runs inside <c>OnStartup</c> and two constructors, and two ordinary situations
    /// make a copy fail: an older build still running holds today's log open, and two new
    /// instances launching at once both pass the exists-check before one of them copies.
    /// An exception here is a Versio that will not open, on exactly the machines the
    /// migration exists to rescue — and a file skipped this launch is copied on the next,
    /// because the originals are never deleted.
    /// </remarks>
    public static void Migrate(string legacyRoot, string userRoot)
    {
        if (string.IsNullOrEmpty(legacyRoot) || string.IsNullOrEmpty(userRoot))
        {
            return;
        }

        if (string.Equals(
                Path.GetFullPath(legacyRoot),
                Path.GetFullPath(userRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Not guarded: if the durable root cannot be created, the settings store and the
        // log sink fail on the same path a moment later, and THAT failure should surface.
        Directory.CreateDirectory(userRoot);

        CopyIfMissing(
            Path.Combine(legacyRoot, "settings.json"),
            Path.Combine(userRoot, "settings.json"));

        try
        {
            var legacyLogs = Path.Combine(legacyRoot, "logs");
            var userLogs = Path.Combine(userRoot, "logs");
            if (!Directory.Exists(legacyLogs))
            {
                return;
            }

            Directory.CreateDirectory(userLogs);
            foreach (var file in Directory.EnumerateFiles(legacyLogs, "*.log"))
            {
                CopyIfMissing(file, Path.Combine(userLogs, Path.GetFileName(file)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Enumeration itself can fail on a directory an installer is mid-way through
            // deleting. Old logs are the least valuable thing this copies; startup is not
            // traded for them.
        }
    }

    private static void CopyIfMissing(string source, string destination)
    {
        try
        {
            if (!File.Exists(source) || File.Exists(destination))
            {
                return;
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.Copy(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A source held open by an older running build, or a destination another
            // instance created between the check and the copy. Both self-resolve: the
            // next launch copies what this one could not.
        }
    }
}
