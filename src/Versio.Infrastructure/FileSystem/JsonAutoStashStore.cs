using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Versio.Core.Abstractions;
using Versio.Core.Guarding;
using Versio.Core.Logging;

namespace Versio.Infrastructure.FileSystem;

/// <summary>
/// Persists the auto-stash record to <c>.versio/local/autostash.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Beside <c>state.json</c> and gitignored with it. <c>.versio/local/</c> is machine-local by
/// design — locks, backups, scratch copies — and a stash is the same kind of thing: it exists
/// in one clone and cannot mean anything in another.
/// </para>
/// <para>
/// Written via a temp file and an atomic replace, exactly as <see cref="JsonLockStore"/> is. A
/// torn record here is worse than a missing one: a half-written entry reads back as a plausible
/// stash id, and the thing that reads it goes on to offer somebody their work back from a
/// commit that does not exist.
/// </para>
/// </remarks>
public sealed class JsonAutoStashStore(string repositoryRoot, IVersioLogger? logger = null)
    : IAutoStashStore
{
    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The file itself, so a log line can name it.</summary>
    public string Path =>
        System.IO.Path.Combine(repositoryRoot, ".versio", "local", "autostash.json");

    public async Task<AutoStashState> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(Path))
        {
            return AutoStashState.Empty;
        }

        try
        {
            var json = await File.ReadAllTextAsync(Path, Utf8NoBom, ct).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<AutoStashState>(json, Options)
                        ?? AutoStashState.Empty;

            _log.Debug(LogCategory.Lock,
                $"read {state.Entries.Count} auto-stash record(s) from {Path} " +
                $"(schema {state.Schema})");

            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException
                                      or UnauthorizedAccessException)
        {
            // Error, not Warning, and the wording says what was lost.
            //
            // Nobody's work has gone - every stash is still in git and still in `git stash
            // list` - but the record of which of them were Versio's, and which branch each
            // belongs to, has. That is exactly the information that stops the app offering
            // somebody else's stash back, so it is not a shrug.
            _log.Error(LogCategory.Lock,
                $"the auto-stash record at {Path} is unreadable and was ignored; stashes " +
                "Versio took are still in git and can be restored with `git stash list` and " +
                "`git stash apply`, but they will no longer be offered automatically", ex);

            return AutoStashState.Empty;
        }
    }

    public async Task WriteAsync(AutoStashState state, CancellationToken ct)
    {
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);

        var temp = Path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, Options), Utf8NoBom, ct)
            .ConfigureAwait(false);

        // Atomic swap. File.Move with overwrite is atomic on NTFS.
        File.Move(temp, Path, overwrite: true);

        _log.Debug(LogCategory.Lock,
            $"persisted {state.Entries.Count} auto-stash record(s) to {Path}");
    }
}
