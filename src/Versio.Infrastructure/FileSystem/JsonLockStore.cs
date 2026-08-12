using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Versio.Core.Abstractions;
using Versio.Core.Locking;
using Versio.Core.Logging;

namespace Versio.Infrastructure.FileSystem;

/// <summary>
/// Persists the direction locks to <c>.versio/local/state.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Gitignored on purpose. A lock committed and then pulled onto a colleague's machine would
/// claim their repository on behalf of a process that never existed there.
/// </para>
/// <para>
/// Written via a temp file and an atomic replace: a torn state.json is worse than a missing
/// one, because recovery would read a plausible-looking half-record and act on it.
/// </para>
/// </remarks>
public sealed class JsonLockStore(string repositoryRoot, IVersioLogger? logger = null) : ILockStore
{
    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string Path => System.IO.Path.Combine(repositoryRoot, ".versio", "local", "state.json");

    public async Task<LockState?> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(Path))
        {
            _log.Debug(LogCategory.Lock, $"no lock state at {Path}");
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(Path, Utf8NoBom, ct).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<LockState>(json, Options);

            _log.Debug(LogCategory.Lock,
                $"read {json.Length} bytes of lock state from {Path} " +
                $"({state?.Bindings.Count ?? 0} binding(s), schema {state?.Schema})");

            return state;
        }
        catch (JsonException ex)
        {
            // A corrupt lock file must not stop the app from opening. Recovery treats a
            // null state as "nothing to recover", which is the safe reading: the hash
            // comparison will re-detect any outstanding work on the next change.
            //
            // Logged as an error even so. Discarding the record of which direction was held
            // is a real loss of safety information, and it must not happen quietly just
            // because the app can carry on.
            _log.Error(LogCategory.Lock,
                $"lock state at {Path} is corrupt and was ignored; the direction lock for " +
                "this repository has been lost and will be re-derived from content hashes", ex);
            return null;
        }
        catch (IOException ex)
        {
            _log.Error(LogCategory.Lock, $"could not read lock state from {Path}", ex);
            return null;
        }
    }

    public async Task WriteAsync(LockState state, CancellationToken ct)
    {
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);

        var temp = Path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, Options), Utf8NoBom, ct)
            .ConfigureAwait(false);

        // Atomic swap. File.Move with overwrite is atomic on NTFS.
        File.Move(temp, Path, overwrite: true);

        _log.Debug(LogCategory.Lock,
            $"persisted {state.Bindings.Count} lock(s) to {Path}");
    }

    public Task ClearAsync(CancellationToken ct)
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
            _log.Info(LogCategory.Lock, $"cleared lock state at {Path}");
        }

        return Task.CompletedTask;
    }
}
