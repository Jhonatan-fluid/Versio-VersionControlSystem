using Versio.Infrastructure.FileSystem;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// User state must not live in the Velopack install directory.
/// </summary>
public sealed class AppUserPathsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "versio-userpaths-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public void User_state_is_not_inside_the_packId_folder()
    {
        // Setup.exe installs to %LOCALAPPDATA%\Versio. Settings used to share that
        // name; the first install deleted the recent-repository list.
        Assert.Equal("Versio", AppUserPaths.LegacyFolderName);
        Assert.Equal("VersioUser", AppUserPaths.UserFolderName);
        Assert.NotEqual(AppUserPaths.Root, AppUserPaths.LegacyRoot, StringComparer.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("VersioUser", "settings.json"), AppUserPaths.SettingsFile,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("VersioUser", "logs"), AppUserPaths.LogsDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Migrate_copies_settings_and_logs_once_and_does_not_overwrite()
    {
        var legacy = Path.Combine(_root, "legacy");
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(Path.Combine(legacy, "logs"));
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"recent\":[]}");
        File.WriteAllText(Path.Combine(legacy, "logs", "versio-2026-08-24.log"), "old log");

        AppUserPaths.Migrate(legacy, user);

        var settings = Path.Combine(user, "settings.json");
        var log = Path.Combine(user, "logs", "versio-2026-08-24.log");
        Assert.True(File.Exists(settings));
        Assert.Equal("{\"recent\":[]}", File.ReadAllText(settings));
        Assert.True(File.Exists(log));
        Assert.Equal("old log", File.ReadAllText(log));

        File.WriteAllText(settings, "already migrated");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "newer leftover");
        AppUserPaths.Migrate(legacy, user);
        Assert.Equal("already migrated", File.ReadAllText(settings));
    }

    [Fact]
    public void Migrate_is_a_no_op_when_there_is_nothing_to_copy()
    {
        var user = Path.Combine(_root, "empty-user");
        AppUserPaths.Migrate(Path.Combine(_root, "missing-legacy"), user);
        Assert.True(Directory.Exists(user));
        Assert.False(File.Exists(Path.Combine(user, "settings.json")));
    }
}
