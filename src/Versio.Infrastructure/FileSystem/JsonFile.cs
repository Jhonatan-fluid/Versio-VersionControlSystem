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

    /// <summary>Serialize <paramref name="value"/> and replace <paramref name="path"/> atomically.</summary>
    public static async Task WriteAsync<T>(
        string path, T value, JsonSerializerOptions options, CancellationToken ct)
    {
        EnsureFolder(path);

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, options), Utf8NoBom, ct)
            .ConfigureAwait(false);

        File.Move(temp, path, overwrite: true);
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

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, options), Utf8NoBom);

        File.Move(temp, path, overwrite: true);
    }
}
