using System.Globalization;

namespace Versio.Core.Sync;

/// <summary>One copy of a project, taken before an import overwrote it.</summary>
/// <remarks>
/// <see cref="TakenUtc"/> is nullable and that is not defensive padding: the folder is on the
/// engineer's disk, and anything can end up in it. A file whose name this cannot read is still
/// listed — see <see cref="BackupCatalog"/> for why hiding it would be worse.
/// </remarks>
public sealed record ProjectBackup
{
    /// <summary>Repo-relative path of the copy, forward slashes.</summary>
    public required string Path { get; init; }

    /// <summary>When the copy was taken, or null when the name does not say.</summary>
    public DateTimeOffset? TakenUtc { get; init; }

    /// <summary>The project file this is a copy of, as it was named at the time.</summary>
    public required string ProjectFileName { get; init; }

    /// <summary>This is a copy Versio wrote, and its name was readable.</summary>
    public bool IsRecognised => TakenUtc is not null;
}

/// <summary>
/// The copies of a project that survive, newest first.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ten copies per binding have been written before every import since the import path
/// existed, and nothing could reach them.</b> That is the whole of safeguards §5.E: after the
/// one operation these exist to undo, the engineer had an unusable project and no route to the
/// file that would fix it — short of knowing the layout of <c>.versio/local/</c> and copying it
/// back by hand, at a moment when they are already having a bad afternoon.
/// </para>
/// <para>
/// <b>The name is the record.</b> The orchestrator writes
/// <c>yyyyMMdd-HHmmss-&lt;Name&gt;.project</c> and keeps no index beside it, which is
/// deliberate: an index is a second thing that can disagree with the directory, and the
/// directory is the truth. That makes the timestamp readable without opening anything, and it
/// makes the ordinal sort of the filenames the same as the chronological one — which is what
/// the pruner already relies on.
/// </para>
/// <para>
/// <b>A file it cannot read is listed, not hidden.</b> A copy the engineer dropped in by hand,
/// or one written by a future version, is still very possibly the thing they are looking for.
/// Dropping it from the list would be indistinguishable from Versio having lost it — the same
/// rule the project tree applies to a changed file the manifest does not describe.
/// </para>
/// <para>
/// Pure: it takes names and returns records, so the ordering and the parsing are tested without
/// a filesystem.
/// </para>
/// </remarks>
public static class BackupCatalog
{
    /// <summary>The timestamp format the orchestrator writes.</summary>
    /// <remarks>
    /// UTC, and fixed-width so that an ordinal sort of the filename is a chronological sort.
    /// The pruner depends on that too — it keeps the first ten by descending name.
    /// </remarks>
    public const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Read a folder listing into backups, newest first.</summary>
    /// <param name="paths">Repo-relative file paths, in any order.</param>
    public static IReadOnlyList<ProjectBackup> Read(IEnumerable<string>? paths)
    {
        if (paths is null)
        {
            return [];
        }

        return paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Parse)

            // Newest first, and the unreadable ones last rather than first: they have no time
            // to compare, and putting an unknown at the top of a list somebody is scanning for
            // "the one from before lunch" is the wrong end.
            .OrderByDescending(b => b.IsRecognised)
            .ThenByDescending(b => b.TakenUtc ?? DateTimeOffset.MinValue)
            .ThenBy(b => b.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Take one backup file name apart.</summary>
    public static ProjectBackup Parse(string path)
    {
        var normalised = path.Replace('\\', '/');
        var name = normalised.Split('/').LastOrDefault() ?? normalised;

        // "yyyyMMdd-HHmmss-Name.project": the stamp is fixed width, so the split is by
        // position rather than by counting hyphens — a project called "Line-2.project" has
        // hyphens of its own and would break any parser that counted them.
        if (name.Length > StampFormat.Length + 1 &&
            name[StampFormat.Length] == '-' &&
            DateTimeOffset.TryParseExact(
                name[..StampFormat.Length],
                StampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var taken))
        {
            return new ProjectBackup
            {
                Path = normalised,
                TakenUtc = taken,
                ProjectFileName = name[(StampFormat.Length + 1)..],
            };
        }

        return new ProjectBackup
        {
            Path = normalised,
            TakenUtc = null,
            ProjectFileName = name,
        };
    }

    /// <summary>
    /// The suffix of the marker that sits beside a copy while it is being written.
    /// </summary>
    /// <remarks>
    /// Written before the copy's first byte and deleted after its last, so a copy whose marker
    /// is still there did not finish — the process died, the disk filled, a file in the tree
    /// was locked halfway through. Such a copy is not listed and cannot be restored: a restore
    /// puts back exactly the tree that was saved and deletes what the backup does not have
    /// (the 2026-10-24 review's item 21), so restoring half a tree would delete the other half
    /// of the live project and report success — reproduced by the 2026-10-25 review's fourth
    /// round, item 1, with a program file that vanished. A rename on completion would say the
    /// same thing with one file fewer, but the filesystem seam has no rename, and the marker
    /// keeps the directory the only record, as the rest of this class insists.
    /// </remarks>
    public const string PartialMarkerSuffix = ".partial";

    /// <summary>The marker that says the copy at <paramref name="backupPath"/> is still being written, or never finished.</summary>
    public static string PartialMarkerFor(string backupPath) => backupPath + PartialMarkerSuffix;

    /// <summary>Whether <paramref name="path"/> is such a marker rather than a copy.</summary>
    public static bool IsPartialMarker(string path) =>
        path.EndsWith(PartialMarkerSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where one binding's copies live, repo-relative.</summary>
    /// <remarks>
    /// One place that knows the layout, so the writer, the pruner and the restore list cannot
    /// drift apart about where the copies are.
    /// </remarks>
    public static string FolderFor(string bindingId) =>
        $".versio/local/backups/{bindingId}";
}
