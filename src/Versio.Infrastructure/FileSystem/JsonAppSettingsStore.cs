using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Versio.Core.Abstractions;
using Versio.Core.Logging;
using Versio.Core.Settings;

namespace Versio.Infrastructure.FileSystem;

/// <summary>
/// Persists this machine's preferences to <c>%LOCALAPPDATA%\Versio\settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Beside the logs, not inside a repository. Every other piece of state Versio writes belongs
/// to one repository and lives under its <c>.versio/</c>; this one is about the application —
/// which repositories have been opened at all — and has no repository to live in. It is also
/// the answer to "where does the first repository come from on a fresh window".
/// </para>
/// <para>
/// Written through a temp file and an atomic replace, exactly as <see cref="JsonLockStore"/>
/// is. The stakes are far lower here, and the reasoning is the same: a torn settings file is
/// worse than a missing one, because it reads back as a plausible half-list.
/// </para>
/// </remarks>
public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private readonly IVersioLogger _log;

    public JsonAppSettingsStore(string? directory = null, IVersioLogger? logger = null)
    {
        _log = logger ?? NullLogger.Instance;
        Path = System.IO.Path.Combine(
            directory ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Versio"),
            "settings.json");
    }

    /// <summary>The file itself, so a log line can name it.</summary>
    public string Path { get; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<AppSettings> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(Path))
        {
            _log.Debug(LogCategory.App, $"no settings at {Path}");
            return AppSettings.Empty;
        }

        try
        {
            var json = await File.ReadAllTextAsync(Path, Utf8NoBom, ct).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options)
                           ?? AppSettings.Empty;

            _log.Debug(LogCategory.App,
                $"read settings from {Path} ({settings.Recent.Count} recent repositor(ies), " +
                $"schema {settings.Schema})");

            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException
                                      or UnauthorizedAccessException)
        {
            // Warn rather than Error: what is lost is a list of paths somebody can retype,
            // and the next successful write replaces the file. Losing a direction lock is an
            // error; losing a dropdown's memory is an inconvenience with a log line.
            _log.Warn(LogCategory.App,
                $"settings at {Path} could not be read and were ignored: {ex.Message}");
            return AppSettings.Empty;
        }
    }

    public async Task WriteAsync(AppSettings settings, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            var temp = Path + ".tmp";
            await File.WriteAllTextAsync(
                temp, JsonSerializer.Serialize(settings, Options), Utf8NoBom, ct)
                .ConfigureAwait(false);

            // Atomic swap. File.Move with overwrite is atomic on NTFS.
            File.Move(temp, Path, overwrite: true);

            _log.Debug(LogCategory.App,
                $"persisted {settings.Recent.Count} recent repositor(ies) to {Path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing the user asked for has failed: they opened a repository and it opened.
            // Only the memory of it did not survive, and saying so in the log is the whole
            // response this deserves.
            _log.Warn(LogCategory.App, $"could not persist settings to {Path}: {ex.Message}");
        }
    }
}
