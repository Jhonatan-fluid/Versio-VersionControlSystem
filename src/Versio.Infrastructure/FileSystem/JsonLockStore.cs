using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Versio.Core.Abstractions;
using Versio.Core.Locking;

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
public sealed class JsonLockStore(string repositoryRoot) : ILockStore
{
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
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(Path, Utf8NoBom, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LockState>(json, Options);
        }
        catch (JsonException)
        {
            // A corrupt lock file must not stop the app from opening. Recovery treats a
            // null state as "nothing to recover", which is the safe reading: the hash
            // comparison will re-detect any outstanding work on the next change.
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
    }

    public Task ClearAsync(CancellationToken ct)
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }

        return Task.CompletedTask;
    }
}
