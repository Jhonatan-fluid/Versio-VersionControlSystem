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
    /// The configuration in <paramref name="json"/>, read with the same options the store writes
    /// with and checked against the shape <see cref="RepoConfig"/> promises. Throws
    /// <see cref="JsonException"/> on a corrupt file; the caller decides what that means.
    /// </summary>
    /// <param name="repairs">
    /// One sentence per default the file left as <c>null</c> and this read filled in; empty when
    /// the file was read as written. Not optional, and there is no overload without it: a caller
    /// that could not say what was repaired would be a silent recovery, which is the defect class
    /// <see cref="LoadAsync"/>'s remarks are about.
    /// </param>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="LoadAsync"/> so the web viewer, which reads <c>repo.json</c> from
    /// GitHub rather than disk, reads it exactly as the desktop does. Copying the options would
    /// be two readers of one committed file, and the enum-by-name rule above is easy to drop.
    /// </para>
    /// <para>
    /// <b>The model's nullability is not a guarantee the deserializer keeps.</b>
    /// <c>RepoConfig</c>'s lists are non-nullable and <c>ProjectBinding</c>'s paths are
    /// <c>required</c>, and System.Text.Json honours neither against an explicit <c>null</c>:
    /// <c>"bindings": null</c> sets the list to null, <c>[null]</c> puts a null where a binding
    /// must be, and <c>"projectPath": null</c> satisfies <c>required</c> because the property is
    /// present. Every one of those deserialised cleanly until 2026-10-25 and then threw
    /// <c>NullReferenceException</c> from the first reader to touch it — <c>Adoption.Plan</c>
    /// matching the ignore globs, the orphan scanner, the web viewer's model — none of which named
    /// the file. The manifest scanner had the same defect a round earlier
    /// (<c>RepoScanner.TryReadBindingId</c>); this is the configuration reader's copy of the fix,
    /// and it is here rather than in Core's readers because this is the only place a null can get
    /// into a <c>RepoConfig</c>. Core's readers stay written against the type they are given;
    /// teaching each of them to doubt it would scatter one defect's fix across every consumer.
    /// </para>
    /// <para>
    /// <b>Two readings, one rule: a null where the model has a default is the default; a null where
    /// the model requires a value is corruption.</b> <c>null</c> is how many writers spell
    /// "absent", and an absent <c>bindings</c>, <c>ignore</c> or <c>defaultBranch</c> already means
    /// the default, so reading the null the same way loses nothing — it is reported through
    /// <paramref name="repairs"/> because an empty bindings list is the lost-work shape and needs
    /// its trail. A null <i>binding</i>, a null <i>path</i> inside one, or a null <i>glob</i> is
    /// different: there is no default to fall back to, and the alternative — dropping the element and
    /// keeping the rest — would leave a project silently unprotected while the panel looked
    /// healthy, and invite the user to adopt it again under a new id, orphaning its lock and
    /// baseline. Refusing the whole file costs what a syntax error already costs, says so in the
    /// same line, and is what System.Text.Json already does when the same path is <i>absent</i>
    /// rather than null; the null spelling must not cost less than the absent one.
    /// </para>
    /// </remarks>
    public static RepoConfig Parse(string json, out IReadOnlyList<string> repairs)
    {
        var found = new List<string>();
        repairs = found;

        // `null` is valid JSON. It used to read as an empty configuration without a word, which
        // is indistinguishable from a repository that was never adopted.
        var config = JsonSerializer.Deserialize<RepoConfig>(json, Options)
            ?? throw new JsonException("the file holds null, not a JSON object");

        if (config.Bindings is null)
        {
            found.Add("'bindings' is null and was read as an empty list; this repository will " +
                      "open with no bindings until the file is repaired or the projects are " +
                      "adopted again");
            config = config with { Bindings = [] };
        }

        if (config.Ignore is null)
        {
            found.Add("'ignore' is null and was read as an empty list; nothing is excluded " +
                      "from adoption until the file is repaired");
            config = config with { Ignore = [] };
        }

        if (config.DefaultBranch is null)
        {
            found.Add("'defaultBranch' is null and was read as unset; the trunk falls back to " +
                      "what the remote says");
            config = config with { DefaultBranch = string.Empty };
        }

        for (var i = 0; i < config.Bindings.Count; i++)
        {
            var binding = config.Bindings[i];
            if (binding is null)
            {
                throw new JsonException($"bindings[{i}] is null, not a binding");
            }

            // The id is quoted where there is one: it is the one thing a reader can search the
            // file for, since the path that would otherwise identify the binding is what is missing.
            if (binding.Id is null)
            {
                throw new JsonException($"bindings[{i}] has a null id; every binding needs one");
            }

            if (binding.ProjectPath is null)
            {
                throw new JsonException($"bindings[{i}] ('{binding.Id}') has a null projectPath");
            }

            if (binding.ExportPath is null)
            {
                throw new JsonException($"bindings[{i}] ('{binding.Id}') has a null exportPath");
            }
        }

        for (var i = 0; i < config.Ignore.Count; i++)
        {
            if (config.Ignore[i] is null)
            {
                throw new JsonException($"ignore[{i}] is null, not a glob pattern");
            }
        }

        return config;
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
