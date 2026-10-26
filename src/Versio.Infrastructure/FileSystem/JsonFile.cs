using System.Text;
using System.Text.Json;

namespace Versio.Infrastructure.FileSystem;

/// <summary>
/// How this application writes a small JSON file it cannot afford to find torn.
/// </summary>
/// <remarks>
/// <para>
/// Four stores - settings, locks, auto-stashes and the session marker - each carried their own
/// copy of the create-directory, write-temp, move-over dance and their own BOM-less UTF-8.
/// Those really were the same rule four times over, which is four chances for one of them to
/// be corrected and the others not.
/// </para>
/// <para>
/// <b>The temp file and the replace are the point.</b> A half-written state.json is worse than
/// a missing one, because recovery would read a plausible-looking half-record and act on it.
/// <c>File.Move</c> with <c>overwrite</c> is atomic on NTFS, so a reader sees either the whole
/// old file or the whole new one.
/// </para>
/// <para>
/// <b>The serializer options are NOT shared, and that is deliberate.</b> They looked
/// interchangeable and are not: the settings store has no enum converter, because the theme is
/// stored as a string on purpose and Core does not know the app's enum; the lock and session
/// stores have no <c>PropertyNameCaseInsensitive</c>. Folding them together would change what
/// each store accepts off disk and how it writes back - a behaviour change wearing the clothes
/// of a cleanup - so each store still declares what it means and passes it in.
/// </para>
/// <para>
/// Logging and failure handling stay with the callers too. Each says something different: a
/// corrupt lock file must not stop the app opening, a settings file that will not save is
/// worth a warning and nothing more.
/// </para>
/// </remarks>
internal static class JsonFile
{
    /// <summary>UTF-8 with no byte-order mark, for reading as well as writing.</summary>
    /// <remarks>
    /// A BOM on a JSON file is legal and <see cref="JsonSerializer"/> copes, but these files
    /// sit inside a git repository beside the exported text, and a BOM appearing or
    /// disappearing is a diff nobody made.
    /// </remarks>
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Create the folder the file lives in, when it has one.
    /// </summary>
    /// <remarks>
    /// The emptiness check is not decoration. <see cref="Path.GetDirectoryName(string)"/>
    /// returns null for a root and empty for a bare filename, and
    /// <see cref="Directory.CreateDirectory(string)"/> throws on both - out of a caller whose
    /// own catch only expects <see cref="IOException"/> and
    /// <see cref="UnauthorizedAccessException"/>, so it would escape as an unhandled
    /// exception rather than the quiet failure that path is written for.
    /// </remarks>
    private static void EnsureFolder(string path)
    {
        var folder = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }
    }

    /// <summary>
    /// One writer per file at a time, within this process.
    /// </summary>
    /// <remarks>
    /// Two bindings persisting their direction locks at the same moment both wrote
    /// <c>state.json.tmp</c>: the second open failed with a sharing violation, the exception
    /// escaped the sync engine, and that binding's slot was never released (the 2026-10-25
    /// review's seventh round, item 2). The gate serialises the writers; the temp name below
    /// is unique as well, so a writer in another process — a second Versio handing over, a
    /// tool — cannot collide with this one either. Keyed by full path, case-insensitively,
    /// because two spellings of one file are one file on Windows.
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Writers =
        new(StringComparer.OrdinalIgnoreCase);

    private static SemaphoreSlim WriterFor(string path) =>
        Writers.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// A temp name no other writer uses. The last writer still wins the move, which is the
    /// replace-atomically contract this file has always had; what changes is that two writers
    /// no longer fight over one half-written temp. A temp left behind by a crash is an orphan
    /// in a gitignored folder, not a corrupt target.
    /// </summary>
    private static string TempFor(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    /// <summary>
    /// The replace, tried a few times before it is allowed to fail.
    /// </summary>
    /// <remarks>
    /// On Windows a file that was just written is briefly open elsewhere — the virus scanner,
    /// the indexer — and replacing it in that window fails with "access to the path is denied"
    /// or a sharing violation although nothing of ours holds it. Thirty-two writers in a row
    /// hit it about one run in three under a loaded suite, and before the sync engine learned
    /// to survive a throw from its store (the seventh round's item 2) one such miss was a
    /// binding stuck Coalesced. The same remedy <c>WindowsFileSystem</c> uses for its copies:
    /// a handful of short waits, then the real failure with its real message.
    /// </remarks>
    private static async Task MoveWithRetriesAsync(string temp, string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < MoveAttempts && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(MoveDelay(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private static void MoveWithRetries(string temp, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < MoveAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(MoveDelay(attempt));
            }
        }
    }

    private const int MoveAttempts = 6;

    /// <summary>20, 40, 80 ... ms: short enough not to be felt, long enough for a scanner to let go.</summary>
    private static TimeSpan MoveDelay(int attempt) => TimeSpan.FromMilliseconds(20 << (attempt - 1));

    /// <summary>Serialize <paramref name="value"/> and replace <paramref name="path"/> atomically.</summary>
    public static async Task WriteAsync<T>(
        string path, T value, JsonSerializerOptions options, CancellationToken ct)
    {
        EnsureFolder(path);

        var gate = WriterFor(path);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var temp = TempFor(path);
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, options), Utf8NoBom, ct)
                .ConfigureAwait(false);

            await MoveWithRetriesAsync(temp, path, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The same, for the caller that cannot await.
    /// </summary>
    /// <remarks>
    /// The session marker is claimed and released on paths that are already synchronous,
    /// including shutdown, where there is no dispatcher left to resume a continuation on.
    /// </remarks>
    public static void Write<T>(string path, T value, JsonSerializerOptions options)
    {
        EnsureFolder(path);

        var gate = WriterFor(path);
        gate.Wait();
        try
        {
            var temp = TempFor(path);
            File.WriteAllText(temp, JsonSerializer.Serialize(value, options), Utf8NoBom);

            MoveWithRetries(temp, path);
        }
        finally
        {
            gate.Release();
        }
    }
}
