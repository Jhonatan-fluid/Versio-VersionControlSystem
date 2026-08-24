using Versio.Core.Diff;
using Versio.Core.Hashing;
using Versio.Core.Plc;
using Versio.Core.Projects;

namespace Versio.Plc.TiaPortal;

/// <summary>
/// Decides what an import of a TIA Portal mirror would do — pure, from the manifest and a
/// directory listing, before the host starts.
/// </summary>
/// <remarks>
/// Same actions as the first vendor's planner, different suffixes and identity. The
/// executing side is the net48 host; this class never names Openness.
/// </remarks>
public static class TiaPortalImportPlanner
{
    private static readonly string[] BookkeepingFiles = ["_manifest.json", "_coverage.md"];

    public static bool NeedsText(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        return path.EndsWith(".scl", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".s7dcl", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".udt", StringComparison.OrdinalIgnoreCase);
    }

    public static ImportPlan Plan(
        ProjectManifest? manifest,
        IReadOnlyList<MirrorFile> mirror,
        IReadOnlyDictionary<string, string>? departed = null)
    {
        var trusted = manifest is not null && manifest.Schema >= ExportCoverage.ReportingSchema;
        var unavailable = manifest is null
            ? "the mirror has no readable manifest, so deletes, renames and moves cannot be "
              + "planned — export the project again to record one"
            : manifest.Schema < ExportCoverage.ReportingSchema
                ? $"the manifest is schema {manifest.Schema}, below {ExportCoverage.ReportingSchema}"
                  + " — deletes, renames and moves are unavailable until the next export"
                : null;

        var byPath = new Dictionary<string, ProjectObject>(StringComparer.Ordinal);
        foreach (var obj in manifest?.Objects ?? [])
        {
            byPath.TryAdd(obj.Path, obj);
        }

        List<PlannedFile> files = [];
        List<string> warnings = [];
        var present = new HashSet<string>(
            mirror.Select(f => f.Path), StringComparer.Ordinal);

        foreach (var file in mirror.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            files.Add(PlanOne(file, byPath, present, warnings));
        }

        var deletions = trusted
            ? PlanDeletions(manifest!, present)
            : new List<PlannedDeletion>();

        // A rename is a file that went and a file that arrived. Matched last so the
        // pair is withdrawn from delete-plus-create — otherwise a renamed SCL block
        // loses its UId and becomes a duplicate. TIA folders are not recorded objects,
        // so the new parent is the arrival's directory chain, not a GUID lookup.
        var renames = trusted
            ? MatchRenames(files, deletions, mirror, departed ?? new Dictionary<string, string>(), warnings)
            : [];

        return new ImportPlan
        {
            Files = files,
            Deletions = deletions,
            Renames = renames,
            Warnings = warnings,
            StructuralChangesUnavailable = unavailable,
        };
    }

    private static PlannedFile PlanOne(
        MirrorFile file,
        IReadOnlyDictionary<string, ProjectObject> byPath,
        HashSet<string> present,
        List<string> warnings)
    {
        var name = PathFileName(file.Path);
        if (BookkeepingFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return Skip(file.Path, PlannedSkip.Bookkeeping);
        }

        if (byPath.TryGetValue(file.Path, out var known))
        {
            if (string.Equals(known.Form, ExportCoverage.DeviceCommentForm, StringComparison.Ordinal))
            {
                // Comment SetAttribute only. Missing this file must not delete the
                // live device — PlanDeletions skips IsDeviceStructure. DEVICES (the
                // CODESYS I/O form) stays export-only even when the suffix matches.
                return new PlannedFile
                {
                    Path = file.Path,
                    Action = PlannedAction.ApplyText,
                    Name = known.Name,
                    Guid = known.Guid,
                    Resolve = ResolveOf(known),
                };
            }

            if (ExportCoverage.IsExportOnly(known.Form))
            {
                var sibling = SiblingText(file.Path, present);
                return new PlannedFile
                {
                    Path = file.Path,
                    Action = PlannedAction.Skip,
                    Skip = PlannedSkip.ExportOnly,
                    Name = known.Name,
                    Guid = known.Guid,
                    Resolve = ResolveOf(known),
                    ImportableSibling = sibling,
                };
            }

            if (IsTextForm(known.Form) || NeedsText(file.Path))
            {
                return new PlannedFile
                {
                    Path = file.Path,
                    Action = PlannedAction.ApplyText,
                    Name = known.Name,
                    Guid = known.Guid,
                    Resolve = ResolveOf(known),
                };
            }

            if (string.Equals(known.Form, "NATIVE", StringComparison.Ordinal))
            {
                return new PlannedFile
                {
                    Path = file.Path,
                    Action = PlannedAction.ApplyNative,
                    Name = known.Name,
                    Guid = known.Guid,
                    Resolve = ResolveOf(known),
                };
            }

            warnings.Add($"{file.Path}: form '{known.Form}' is not one this planner applies");
            return new PlannedFile
            {
                Path = file.Path,
                Action = PlannedAction.Skip,
                Skip = PlannedSkip.UnknownForm,
                Name = known.Name,
                Guid = known.Guid,
                Resolve = ResolveOf(known),
            };
        }

        if (NeedsText(file.Path))
        {
            var declared = TiaPortalSclHeader.ObjectName(file.Text);
            if (string.IsNullOrEmpty(declared))
            {
                warnings.Add($"{file.Path}: create refused — no SCL header naming the object");
                return Skip(file.Path, PlannedSkip.Stray);
            }

            return new PlannedFile
            {
                Path = file.Path,
                Action = PlannedAction.Create,
                Name = declared,
                Resolve = [declared],
            };
        }

        return Skip(file.Path, PlannedSkip.Stray);
    }

    private static List<PlannedDeletion> PlanDeletions(
        ProjectManifest manifest, HashSet<string> present)
    {
        return manifest.Objects
            .Where(o => !present.Contains(o.Path)
                        && !ExportCoverage.IsExportOnly(o.Form)
                        && !ExportCoverage.IsDeviceStructure(o.Form))
            .GroupBy(o => o.Guid, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrEmpty(g.Key)
                        && g.All(o => !present.Contains(o.Path)))
            .Select(g =>
            {
                var first = g.First();
                var cannotVerify = string.IsNullOrEmpty(first.NormalizedHash)
                                   && string.IsNullOrEmpty(first.Hash);
                return new PlannedDeletion
                {
                    Guid = first.Guid,
                    Name = first.Name,
                    Resolve = ResolveOf(first),
                    Files = [.. g.Select(o => new DeletedFile(
                        o.Path, o.Hash, o.Form, o.NormalizedHash, o.ImplementationHash, o.LineSketch))],
                    CannotVerify = cannotVerify
                        ? "the last export recorded no hash, so the live object cannot be compared"
                        : null,
                };
            })
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static PlannedFile Skip(string path, PlannedSkip reason) => new()
    {
        Path = path,
        Action = PlannedAction.Skip,
        Skip = reason,
    };

    private static bool IsTextForm(string form) =>
        string.Equals(form, "SCL", StringComparison.Ordinal)
        || string.Equals(form, "SD", StringComparison.Ordinal)
        || string.Equals(form, "ST_FULL", StringComparison.Ordinal)
        || string.Equals(form, "UDT", StringComparison.Ordinal);

    /// <summary>
    /// The other half of a two-file object, when this file is export-only.
    /// </summary>
    /// <remarks>
    /// XML_REVIEW looks for <c>.scl</c> / <c>.udt</c>. SCL_REVIEW is the inverse — the
    /// <c>.scl</c> is the review copy and the importable half is the SimaticML beside
    /// it — so <c>.xml</c> has to be in this list too. Skipping the path we were given
    /// is what keeps a file from naming itself as its own sibling.
    /// </remarks>
    private static string? SiblingText(string xmlPath, HashSet<string> present)
    {
        var slash = xmlPath.Replace('\\', '/');
        foreach (var ext in new[] { ".scl", ".s7dcl", ".udt", ".xml" })
        {
            var candidate = ReplaceExtension(slash, ext);
            if (string.Equals(candidate, slash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (present.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ReplaceExtension(string path, string ext)
    {
        var slash = path.Replace('\\', '/');
        var dot = slash.LastIndexOf('.');
        return dot < 0 ? slash + ext : slash[..dot] + ext;
    }

    private static string PathFileName(string path)
    {
        var slash = path.Replace('\\', '/');
        var i = slash.LastIndexOf('/');
        return i < 0 ? slash : slash[(i + 1)..];
    }

    private static IReadOnlyList<string> ResolveOf(ProjectObject obj)
    {
        if (string.IsNullOrEmpty(obj.Path))
        {
            return [obj.Name];
        }

        var parts = obj.Path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var dot = p.LastIndexOf('.');
                return dot < 0 ? p : p[..dot];
            })
            .ToList();

        if (parts.Count == 0)
        {
            return [obj.Name];
        }

        parts[^1] = obj.Name;
        return parts;
    }

    /// <summary>
    /// Pair each deletion with a create holding identical content, then fall back to
    /// similarity against git's departed bytes. Rewrites <paramref name="files"/> and
    /// <paramref name="deletions"/> in place.
    /// </summary>
    /// <remarks>
    /// Exact content first — a file moved without editing still hashes as the export.
    /// SCL headers quote the object name, so a rename that also edits the declaration
    /// matches no hash; that pass uses <paramref name="departed"/> the same way the
    /// first vendor does. Ambiguity is refused, not guessed: two identical empty
    /// functions cannot be told apart, and picking either pairing has a 50% chance of
    /// renaming the wrong block.
    /// </remarks>
    private static List<PlannedRename> MatchRenames(
        List<PlannedFile> files,
        List<PlannedDeletion> deletions,
        IReadOnlyList<MirrorFile> mirror,
        IReadOnlyDictionary<string, string> departed,
        List<string> warnings)
    {
        var text = mirror
            .Where(m => m.Text is not null)
            .ToDictionary(m => m.Path, m => m.Text!, StringComparer.Ordinal);

        var creates = files
            .Select((f, i) => (File: f, Index: i))
            .Where(p => p.File.Action == PlannedAction.Create && text.ContainsKey(p.File.Path))
            .ToList();

        List<PlannedRename> renames = [];
        if (creates.Count == 0 || deletions.Count == 0)
        {
            return renames;
        }

        var createsByHash = creates
            .GroupBy(p => ContentHash.Of(text[p.File.Path]), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var deletionsByHash = deletions
            .Where(d => d.Files.Count == 1)
            .GroupBy(d => d.Files[0].Hash, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        List<PlannedDeletion> matched = [];

        foreach (var deletion in deletions.Where(d => d.Files.Count == 1))
        {
            var gone = deletion.Files[0];
            if (!createsByHash.TryGetValue(gone.Hash, out var candidates))
            {
                continue;
            }

            if (candidates.Count != 1 || deletionsByHash[gone.Hash] != 1)
            {
                warnings.Add(
                    $"{deletion.Name}: {deletionsByHash[gone.Hash]} objects have lost a file "
                    + $"holding exactly this content and {candidates.Count} new files hold it, "
                    + "so which is the rename cannot be told — they are treated as deletions "
                    + "and separate creates instead");
                continue;
            }

            var arrival = candidates[0];
            if (string.IsNullOrEmpty(arrival.File.Name))
            {
                continue;
            }

            var proposed = RenameOf(deletion, gone.Path, arrival.File);
            if (!proposed.IsRename && !proposed.IsMove)
            {
                // File renamed, declaration unchanged: the object is still called what
                // the SCL header says. Treating the stem as the name would recreate G3.
                warnings.Add(
                    $"{deletion.Name}: {gone.Path} was renamed to {arrival.File.Path}, but the "
                    + $"declaration inside it still says {arrival.File.Name}, so the object "
                    + "keeps that name and nothing is renamed");
                matched.Add(deletion);
                files[arrival.Index] = arrival.File with
                {
                    Action = PlannedAction.Skip,
                    Skip = PlannedSkip.RenameTarget,
                };
                createsByHash.Remove(gone.Hash);
                continue;
            }

            renames.Add(proposed);
            matched.Add(deletion);
            files[arrival.Index] = arrival.File with
            {
                Action = PlannedAction.Skip,
                Skip = PlannedSkip.RenameTarget,
            };
            createsByHash.Remove(gone.Hash);
        }

        foreach (var done in matched)
        {
            deletions.Remove(done);
        }

        MatchBySimilarity(files, deletions, text, departed, renames, warnings);
        return [.. renames.OrderBy(r => r.OldName, StringComparer.Ordinal)];
    }

    private const double SimilarityFloor = 0.6;
    private const double SimilarityMargin = 0.1;
    private const double SketchFloor = 0.55;

    private static void MatchBySimilarity(
        List<PlannedFile> files,
        List<PlannedDeletion> deletions,
        Dictionary<string, string> text,
        IReadOnlyDictionary<string, string> departed,
        List<PlannedRename> renames,
        List<string> warnings)
    {
        if (deletions.Count == 0
            || (departed.Count == 0
                && !deletions.Any(d => d.Files.Count == 1 && d.Files[0].LineSketch.Length > 0)))
        {
            return;
        }

        var creates = files
            .Select((f, i) => (File: f, Index: i))
            .Where(p => p.File.Action == PlannedAction.Create
                        && !string.IsNullOrEmpty(p.File.Name)
                        && text.ContainsKey(p.File.Path))
            .ToList();

        if (creates.Count == 0)
        {
            return;
        }

        List<PlannedDeletion> matched = [];

        foreach (var deletion in deletions.Where(d => d.Files.Count == 1))
        {
            var old = departed.TryGetValue(deletion.Files[0].Path, out var head) ? head : null;
            var sketch = deletion.Files[0].LineSketch;
            var exact = !string.IsNullOrEmpty(old);
            if (!exact && string.IsNullOrEmpty(sketch))
            {
                continue;
            }

            var scored = creates
                .Select(c => (c.File, c.Index, Score: exact
                    ? TextSimilarity.Of(old, text[c.File.Path])
                    : LineSketch.Similarity(sketch, LineSketch.Of(text[c.File.Path]))))
                .Where(c => c.Score >= (exact ? SimilarityFloor : SketchFloor))
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.File.Path, StringComparer.Ordinal)
                .ToList();

            if (scored.Count == 0)
            {
                continue;
            }

            if (scored.Count > 1 && scored[0].Score - scored[1].Score < SimilarityMargin)
            {
                warnings.Add(
                    $"{deletion.Name}: {scored[0].File.Path} and {scored[1].File.Path} are both "
                    + $"about as similar to its exported file, so which is the rename cannot "
                    + "be told — treated as a deletion and separate creates instead");
                continue;
            }

            var arrival = scored[0];
            var proposed = RenameOf(deletion, deletion.Files[0].Path, arrival.File);
            matched.Add(deletion);
            files[arrival.Index] = arrival.File with
            {
                Action = PlannedAction.ApplyText,
                Guid = deletion.Guid,
                Resolve = [.. proposed.NewParent, arrival.File.Name!],
            };

            if (proposed.IsRename || proposed.IsMove)
            {
                renames.Add(proposed);
            }

            warnings.Add(
                $"{deletion.Name}: matched to {arrival.File.Path} on content similarity "
                + $"({arrival.Score:P0}), so it is renamed rather than removed and recreated");
            creates.RemoveAll(c => c.Index == arrival.Index);
        }

        foreach (var done in matched)
        {
            deletions.Remove(done);
        }
    }

    private static PlannedRename RenameOf(PlannedDeletion deletion, string oldPath, PlannedFile arrival) =>
        new()
        {
            Guid = deletion.Guid,
            Resolve = deletion.Resolve,
            OldName = deletion.Name,
            NewName = arrival.Name!,
            NewParent = ParentOfPath(arrival.Path),
            OldPath = oldPath,
            NewPath = arrival.Path,
        };

    private static IReadOnlyList<string> ParentOfPath(string path)
    {
        var parts = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (parts.Count == 0)
        {
            return [];
        }

        parts.RemoveAt(parts.Count - 1);
        return parts;
    }
}
