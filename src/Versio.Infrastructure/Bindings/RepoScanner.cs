using System.Text.Json;
using Versio.Core.Abstractions;
using Versio.Core.Bindings;
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
/// The walk skips <c>.git</c> and any export directory. A CODESYS export contains no
/// <c>.project</c> files, but scanning megabytes of visualisation XML on every refresh is
/// waste, and the whole scan runs on a background thread for the same reason.
/// </para>
/// </remarks>
public sealed class RepoScanner(string repositoryRoot, IPlcProjectFormat format)
{
    private static readonly string[] SkipDirectories =
        [".git", ".vs", "bin", "obj", "node_modules", ".versio"];

    public async Task<RepoSnapshot> ScanAsync(
        IGitClient? git, CancellationToken ct)
    {
        var projects = new List<string>();
        var exportDirs = new List<string>();
        var manifestIds = new Dictionary<string, string>(StringComparer.Ordinal);

        await Task.Run(() => Walk(repositoryRoot, projects, exportDirs, manifestIds, ct), ct)
            .ConfigureAwait(false);

        // git's own rename detection is the strongest signal available for re-linking a
        // binding whose project was renamed, and it costs one command.
        var renames = git is null
            ? new Dictionary<string, string>()
            : await git.DetectRenamesAsync(ct).ConfigureAwait(false);

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
            if (format.ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
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

    private static string? TryReadBindingId(string manifestPath)
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
