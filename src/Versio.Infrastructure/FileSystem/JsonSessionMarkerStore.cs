using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Versio.Core.Locking;
using Versio.Core.Logging;

namespace Versio.Infrastructure.FileSystem;

/// <summary>
/// Records which Versio has a repository open, in <c>.versio/local/session.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Safeguards §5.G. Beside <c>state.json</c> and gitignored for the same reason: who has a
/// repository open on one machine is not something to commit and hand to everyone who clones
/// it. <see cref="RepositoryPresence"/> decides what a marker means; this only reads, writes
/// and removes it.
/// </para>
/// <para>
/// <b>Every failure here is survivable and none of them stops the app.</b> A missing marker
/// reads as "free", which is the state the application has been in for its whole life; an
/// unwritable one costs the guard on that repository and nothing else. Refusing to open a
/// repository because a presence marker could not be written would be the feature causing the
/// outage it exists to prevent.
/// </para>
/// </remarks>
public sealed class JsonSessionMarkerStore(string repositoryRoot, IVersioLogger? logger = null)
{
    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path => System.IO.Path.Combine(
        repositoryRoot,
        RepositoryPresence.MarkerPath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>What is recorded, or null when nothing is.</summary>
    public SessionMarker? Read()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SessionMarker>(
                File.ReadAllText(Path, JsonFile.Utf8NoBom), Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable marker reads as "nobody has it". The alternative -
            // refusing to open - would turn one bad file into a repository nobody can use.
            _log.Debug(LogCategory.Lock, $"could not read {Path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Claim the repository for this process.</summary>
    public void Write(SessionMarker marker)
    {
        try
        {
            // Temp-then-replace, like state.json beside it: a torn marker would be read back
            // as null, which silently disables the guard rather than announcing itself.
            JsonFile.Write(Path, marker, Options);

            _log.Debug(LogCategory.Lock,
                $"claimed this repository for pid {marker.ProcessId} in {Path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only checkout, or a share that refuses. The guard is a courtesy; the
            // repository still opens.
            _log.Debug(LogCategory.Lock, $"could not write {Path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Give the repository up.
    /// </summary>
    /// <remarks>
    /// <b>Only when the marker is this process's own.</b> A crashed instance's marker is
    /// cleared by the next opener finding its process gone — not by whoever happens to close a
    /// window next, which would hand the repository to a second writer while the first is
    /// still in it.
    /// </remarks>
    public void Release(string sessionId)
    {
        try
        {
            if (Read() is { } marker &&
                !string.Equals(marker.SessionId, sessionId, StringComparison.Ordinal))
            {
                _log.Debug(LogCategory.Lock,
                    $"not releasing {Path}: it belongs to session {marker.SessionId}");
                return;
            }

            File.Delete(Path);

            // Said out loud, because until this line the log recorded the claim and never the
            // handing back: a clean shutdown and a crash left the same trace in the file for
            // the run that ended. The only evidence of the difference was in the NEXT run's
            // log, where the opener reports finding a marker whose process is gone - which is
            // no use at all to somebody reading the log of the session that misbehaved.
            //
            // The two failure paths here have always logged. It was the successful one - the
            // one that runs on every ordinary exit - that was silent.
            _log.Debug(LogCategory.Lock, $"released this repository in {Path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind, and the next opener will find the process gone and take it anyway.
            _log.Debug(LogCategory.Lock, $"could not remove {Path}: {ex.Message}");
        }
    }
}
