using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Enums by NAME, not by ordinal.
        //
        // repo.json is committed and shared, so the alternative is bad in a way that does not
        // announce itself: a reviewer reading "health": 0 in a diff learns nothing.
        //
        // This used to carry a second and sharper argument, about inserting a vendor into
        // PlcVendor - alphabetically, say - silently repointing every existing binding at a
        // different toolchain, first noticed as an export driven by the wrong tool. That hazard
        // is GONE rather than mitigated: PlcVendor stopped being an enum on 2026-08-16, so there
        // is no ordinal left to shift. The name is the identity. It carries its own converter on
        // the type, which is why it does not depend on this line any more.
        Converters = { new JsonStringEnumConverter() },
    };

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
            return JsonSerializer.Deserialize<RepoConfig>(json, Options) ?? new RepoConfig();
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
