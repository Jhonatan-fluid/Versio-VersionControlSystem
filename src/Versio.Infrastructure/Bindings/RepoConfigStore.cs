using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Versio.Core.Bindings;

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
public sealed class RepoConfigStore(string repositoryRoot)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Enums by NAME, not by ordinal.
        //
        // repo.json is committed and shared, so both of the alternatives are bad in ways that
        // do not announce themselves. A reviewer reading "vendor": 0 in a diff learns nothing;
        // worse, inserting a vendor into PlcVendor - alphabetically, say - would silently
        // repoint every existing binding at a different toolchain, and the first sign would be
        // an export driven by the wrong tool.
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
        catch (JsonException)
        {
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
