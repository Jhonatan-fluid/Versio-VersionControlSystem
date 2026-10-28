using System.Text;
using System.Text.Json;
using Versio.Core.Bindings;
using Versio.Core.Logging;

namespace Versio.Infrastructure.Bindings;

/// <summary>
/// Reads and writes <c>.versio/repo.json</c> — the committed record of which project maps
/// to which export directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Committed, unlike the lock state.</b> The bindings are shared configuration: a
/// colleague cloning the repository must get the same mapping, or their first export would
/// land somewhere else and produce a spurious full-tree diff.
/// </para>
/// <para>
/// Written via a temp file and an atomic replace, for the same reason as the lock store: a
/// half-written repo.json would be read back as a plausible but wrong set of bindings.
/// </para>
/// </remarks>
public sealed class RepoConfigStore(string repositoryRoot, IVersioLogger? logger = null)
{
    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    // The options, and the enum-by-name reasoning that goes with them, live in Core's
    // RepoConfigJson since 2026-10-27: this store writes with them, and three readers (this
    // store, the web viewer, the History panel's commit snapshot) read with them.
    private static JsonSerializerOptions Options => RepoConfigJson.Options;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string Path => System.IO.Path.Combine(repositoryRoot, ".versio", "repo.json");

    public bool Exists => File.Exists(Path);

    /// <summary>Load the configuration, or an empty one when the repo has never been set up.</summary>
    /// <remarks>
    /// A corrupt file returns empty rather than throwing. Versio must still open the
    /// repository so the user can see the problem and re-adopt their projects; refusing to
    /// start over a malformed config would leave them with no way in.
    /// <para>
    /// <b>The recovery is silent only in what it does, not in what it says.</b> Returning
    /// empty looks identical to "this repository has never been adopted", which is a
    /// lost-work class of bug if nothing in the log names the file that was unreadable.
    /// The lock store logs the same shape as Error, because discarding a direction lock
    /// is a safety loss. Bindings can be re-adopted, so this is a Warning — still loud
    /// enough that an empty project list after a bad edit has a trail.
    /// </para>
    /// <para>
    /// "Corrupt" covers the shape as well as the syntax — see <see cref="Parse"/>. A file whose
    /// JSON parses but puts <c>null</c> where a binding or a path must be lands here too, with
    /// the exception naming the element, so the one line a user reads says what to fix.
    /// </para>
    /// </remarks>
    public async Task<RepoConfig> LoadAsync(CancellationToken ct)
    {
        if (!Exists)
        {
            return new RepoConfig();
        }

        try
        {
            var json = await File.ReadAllTextAsync(Path, Utf8NoBom, ct).ConfigureAwait(false);
            var config = Parse(json, out var repairs);

            // One line per default filled in, in the voice of the corrupt-file line below: a
            // null bindings list opens the repository with no projects, which looks exactly like
            // one never adopted unless the log says why.
            foreach (var repair in repairs)
            {
                _log.Warn(LogCategory.App, $"repo.json at {Path}: {repair}");
            }

            return config;
        }
        catch (JsonException ex)
        {
            _log.Log(LogLevel.Warning, LogCategory.App,
                $"repo.json at {Path} is corrupt and was ignored; this repository will " +
                "open with no bindings until the file is repaired or the projects are " +
                "adopted again",
                ex);
            return new RepoConfig();
        }
    }

    /// <summary>
    /// The configuration in <paramref name="json"/>: Core's <see cref="RepoConfigJson.Parse"/>,
    /// which holds the shape check and its reasoning. Kept here so the desktop's callers read
    /// through the store they always did.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="LoadAsync"/> so a reader that does not have the file on disk —
    /// the web viewer reading GitHub, the History panel reading a commit — reads it exactly as
    /// the desktop does; that is why the body moved to Core, where both can reach it.
    /// </remarks>
    public static RepoConfig Parse(string json, out IReadOnlyList<string> repairs) =>
        RepoConfigJson.Parse(json, out repairs);

    public async Task SaveAsync(RepoConfig config, CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        // LF, whatever the platform. System.Text.Json indents with Environment.NewLine, so
        // this file was written CRLF on Windows while .gitattributes stores it LF - which made
        // repo.json show as modified immediately after every clone, and again every time
        // Versio touched it. A committed file has to be byte-stable across the machines that
        // share it, and this one is shared by definition: it is how a teammate gets the same
        // project mapping.
        var json = JsonSerializer.Serialize(config, Options).Replace("\r\n", "\n");

        var temp = Path + ".tmp";
        await File.WriteAllTextAsync(temp, json, Utf8NoBom, ct).ConfigureAwait(false);

        File.Move(temp, Path, overwrite: true);
    }

    /// <summary>Add a binding and persist. Used when the user adopts a discovered project.</summary>
    public async Task<RepoConfig> AddBindingAsync(ProjectBinding binding, CancellationToken ct)
    {
        var config = await LoadAsync(ct).ConfigureAwait(false);
        var updated = config with { Bindings = [.. config.Bindings, binding] };
        await SaveAsync(updated, ct).ConfigureAwait(false);
        return updated;
    }
}
