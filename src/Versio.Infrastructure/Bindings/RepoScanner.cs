using System.Text.Json;
using Versio.Core.Abstractions;
using Versio.Core.Bindings;
using Versio.Core.Logging;
using Versio.Core.Plc;

namespace Versio.Infrastructure.Bindings;

/// <summary>
/// Builds the filesystem picture that <see cref="OrphanScanner"/> reasons about.
/// </summary>
/// <remarks>
/// <para>
/// Split from the scanner on purpose: this half walks the disk and is verified by
/// integration tests, while the classification logic stays pure and is unit tested against
/// an in-memory snapshot. Discovering a project and deciding what to do about it are
/// different problems.
/// </para>
/// <para>
/// The walk skips <c>.git</c> and any export directory. An export contains no project files,
/// but scanning megabytes of visualisation XML on every refresh is waste, and the whole scan
/// runs on a background thread for the same reason.
/// </para>
/// </remarks>
public sealed class RepoScanner(
    string repositoryRoot,
    IReadOnlyList<IPlcProjectFormat>? formats = null,
    IVersioLogger? logger = null)
{
    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    private readonly IReadOnlyList<IPlcProjectFormat> _formats = formats ?? PlcFormats.All;

    /// <summary>Every extension any registered format claims.</summary>
    private IReadOnlyList<string> Extensions =>
        [.. _formats.SelectMany(f => f.ProjectExtensions)
                    .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static readonly string[] SkipDirectories =
        [".git", ".vs", "bin", "obj", "node_modules", ".versio"];

    public async Task<RepoSnapshot> ScanAsync(
        IGitClient? git, CancellationToken ct)
    {
        var projects = new List<string>();
        var exportDirs = new List<string>();
        var manifestIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var started = System.Diagnostics.Stopwatch.StartNew();

        await Task.Run(() => Walk(repositoryRoot, projects, exportDirs, manifestIds, ct), ct)
            .ConfigureAwait(false);

        // git's own rename detection is the strongest signal available for re-linking a
        // binding whose project was renamed, and it costs one command.
        var renames = git is null
            ? new Dictionary<string, string>()
            : await git.DetectRenamesAsync(ct).ConfigureAwait(false);

        started.Stop();

        // What the scan found is the input to every adoption decision the user is about to
        // be offered, so "it did not offer to track my project" is answered here first.
        _log.Info(LogCategory.Fs,
            $"scanned {repositoryRoot} in {started.ElapsedMilliseconds} ms: " +
            $"{projects.Count} project(s), {exportDirs.Count} export tree(s), " +
            $"{renames.Count} rename(s)");

        foreach (var project in projects)
        {
            _log.Debug(LogCategory.Fs, $"found project {project}");
        }

        return new RepoSnapshot
        {
            ProjectFiles = projects,
            ExportDirectories = exportDirs,
            ManifestBindingIds = manifestIds,
            GitRenames = renames,
        };
    }

    private void Walk(string directory, List<string> projects, List<string> exportDirs,
                      Dictionary<string, string> manifestIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string[] entries;
        try
        {
            entries = Directory.GetFiles(directory);
        }
        catch (UnauthorizedAccessException)
        {
            return;     // a folder we cannot read is not a folder we can help with
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        foreach (var file in entries)
        {
            var extension = Path.GetExtension(file);
            if (Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                projects.Add(Relative(file));
            }
            else if (Path.GetFileName(file) == BindingConventions.ManifestFileName)
            {
                // A directory holding a manifest IS an export tree, whatever it is called.
                // Reading the binding id back is what lets a mismatch be detected rather
                // than guessed at (plan §5.1).
                var dir = Relative(directory);
                exportDirs.Add(dir);

                var id = TryReadBindingId(file);
                if (!string.IsNullOrEmpty(id))
                {
                    manifestIds[dir] = id;
                }
            }
        }

        foreach (var child in SafeDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (SkipDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            Walk(child, projects, exportDirs, manifestIds, ct);
        }
    }

    private string? TryReadBindingId(string manifestPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("bindingId", out var id)
                ? id.GetString()
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            // A truncated or mid-write manifest must not abort the whole scan; the binding
            // simply reads as unidentified, which the scanner already handles.
            //
            // Logged even so: an export tree that cannot be identified is offered to the
            // user as an orphan, and "why does it want to adopt something it already
            // tracks" is otherwise a mystery with no trace behind it.
            _log.Warn(LogCategory.Fs,
                $"could not read the binding id from {manifestPath}: {e.Message}");
            return null;
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    /// <summary>Repo-relative with forward slashes — the form git uses and the bindings store.</summary>
    private string Relative(string absolute) =>
        Path.GetRelativePath(repositoryRoot, absolute).Replace('\\', '/');
}
