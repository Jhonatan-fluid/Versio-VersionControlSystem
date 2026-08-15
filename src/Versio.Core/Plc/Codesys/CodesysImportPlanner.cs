using Versio.Core.Iec;
using Versio.Core.Projects;
using Versio.Core.Serialization;

namespace Versio.Core.Plc.Codesys;

/// <summary>
/// Decides what an import of a CODESYS mirror would do — pure, from the manifest and a
/// directory listing, before CODESYS is started.
/// </summary>
/// <remarks>
/// <para>
/// <b>This replaces the walk inside <c>versio_import.py</c> as the place decisions are
/// made.</b> The script used to infer everything from file names: the object's name from the
/// stem (G3 — <c>PROGRAM Foo</c> in <c>Bar.st</c> created <c>Bar</c>), its location by
/// comparing percent-encoded stems against raw object names (G4 — an encoded name could
/// never be found), the owner split from the last dot (G5), and "everything else must be
/// native XML" (G6 — a <c>README.md</c> was handed to CODESYS as native XML). The manifest
/// has carried the verbatim name, the form, the identity and the owner of every exported
/// file all along; this is the first code to read it on the import side.
/// </para>
/// <para>
/// Pure and vendor-owned: the suffix tables and the stem codec are CODESYS knowledge, so the
/// planner lives behind the seam and the sync engine sees only
/// <see cref="ImportPlan"/> through <see cref="IPlcProjectFormat.PlanImport"/>.
/// </para>
/// <para>
/// Phase 1 plans updates, creates and skips. Deletes, renames and moves are phases 2 and 3
/// of <c>docs/CODESYS-IMPORT-PLAN.md</c>; their raw material
/// (<see cref="ImportPlan.MissingFromMirror"/>) is recorded here and acted on by nothing.
/// </para>
/// </remarks>
public static class CodesysImportPlanner
{
    /// <summary>
    /// Suffixes recognised as textual sources, longest first so <c>.gvl.st</c> wins over
    /// <c>.st</c>. Must stay in step with <c>SUFFIXES</c> in <c>versio_import.py</c>.
    /// </summary>
    private static readonly string[] TextualSuffixes =
        [".gvl.st", ".action.st", ".transition.st", ".st"];

    private static readonly string[] NativeSuffixes = [".gvl.xml", ".vis.xml", ".xml"];

    /// <summary>Forms applied by replacing text in place.</summary>
    private static readonly string[] TextualForms = ["ST_FULL", "ST_DECL", "ST_IMPL"];

    /// <summary>Forms handed to <c>import_native</c>.</summary>
    private static readonly string[] NativeForms = ["NATIVE", "NATIVE_R", "NATIVE_WITH_CHILDREN"];

    /// <summary>The form applied through the typed task API.</summary>
    private const string TypedTaskForm = "TASK_BOTH";

    /// <summary>Versio's own bookkeeping. Must match <c>VERSIO_FILES</c> in
    /// <c>versio_types.py</c>.</summary>
    private static readonly string[] BookkeepingFiles = ["_manifest.json", "_coverage.md"];

    /// <summary>True when the planner wants this file's content — see
    /// <see cref="IPlcProjectFormat.NeedsTextToPlan"/>.</summary>
    /// <remarks>
    /// Every textual suffix, not only the manifest-absent ones, because the caller cannot
    /// know which files the manifest covers without doing the planner's job first. The cost
    /// is reading a handful of small source files that were about to be imported anyway.
    /// </remarks>
    public static bool NeedsText(string relativePath) =>
        relativePath.EndsWith(".st", StringComparison.OrdinalIgnoreCase);

    public static ImportPlan Plan(ProjectManifest? manifest, IReadOnlyList<MirrorFile> mirror)
    {
        // A manifest below the reporting schema is still believed about the files it names —
        // a recorded name is a recorded name — but not about what it does NOT name, which is
        // exactly the half deletes would need. ExportCoverage.ReportingSchema is the same
        // threshold the coverage report uses for the same reason.
        var trusted = manifest is not null && manifest.Schema >= ExportCoverage.ReportingSchema;
        var unavailable = manifest is null
            ? "the mirror has no readable manifest, so deletes, renames and moves cannot be "
              + "planned — export the project again to record one"
            : manifest.Schema < ExportCoverage.ReportingSchema
                ? $"the manifest is schema {manifest.Schema}, below {ExportCoverage.ReportingSchema}"
                  + " — what it does not list cannot be told apart from what it did not record,"
                  + " so deletes, renames and moves are unavailable until the next export"
                : null;

        var byPath = new Dictionary<string, ProjectObject>(StringComparer.Ordinal);
        var byGuid = new Dictionary<string, List<ProjectObject>>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in manifest?.Objects ?? [])
        {
            byPath.TryAdd(obj.Path, obj);
            if (!string.IsNullOrEmpty(obj.Guid))
            {
                (byGuid.TryGetValue(obj.Guid, out var list)
                    ? list
                    : byGuid[obj.Guid] = []).Add(obj);
            }
        }

        List<PlannedFile> files = [];
        List<string> warnings = [];

        foreach (var file in mirror.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            files.Add(PlanOne(file, byPath, byGuid, warnings));
        }

        // Phase 2's raw material. Export-only records are excluded: their files are rewritten
        // by every export and applied by nothing, so an absence says someone tidied the
        // mirror, not that an object should die.
        var present = new HashSet<string>(mirror.Select(f => f.Path), StringComparer.Ordinal);
        var missing = trusted
            ? manifest!.Objects
                .Where(o => !present.Contains(o.Path) && !ExportCoverage.IsExportOnly(o.Form))
                .Select(o => o.Path)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList()
            : [];

        return new ImportPlan
        {
            Files = files,
            Warnings = warnings,
            MissingFromMirror = missing,
            StructuralChangesUnavailable = unavailable,
        };
    }

    private static PlannedFile PlanOne(
        MirrorFile file,
        Dictionary<string, ProjectObject> byPath,
        Dictionary<string, List<ProjectObject>> byGuid,
        List<string> warnings)
    {
        var fileName = file.Path[(file.Path.LastIndexOf('/') + 1)..];

        if (BookkeepingFiles.Contains(fileName, StringComparer.Ordinal))
        {
            // Silent: these are present in every mirror, and a warning that always fires is
            // how a real one gets read past.
            return new PlannedFile
            {
                Path = file.Path, Action = PlannedAction.Skip, Skip = PlannedSkip.Bookkeeping,
            };
        }

        if (byPath.TryGetValue(file.Path, out var known))
        {
            return PlanKnown(file, known, byGuid, warnings);
        }

        return PlanUnknown(file, fileName, warnings);
    }

    /// <summary>A file the manifest records: the form says what it is, the name is verbatim.</summary>
    private static PlannedFile PlanKnown(
        MirrorFile file,
        ProjectObject known,
        Dictionary<string, List<ProjectObject>> byGuid,
        List<string> warnings)
    {
        if (ExportCoverage.IsExportOnly(known.Form))
        {
            // The importable half, when one exists, is the same object's other file — the
            // pairing the importer used to rediscover by probing the disk for a sibling.
            // The manifest has carried the answer since TASK_NATIVE was recorded, which is
            // what lets the disk probe go once the script runs from this plan.
            var sibling = byGuid.TryGetValue(known.Guid, out var shared)
                ? shared.FirstOrDefault(o => !ExportCoverage.IsExportOnly(o.Form))?.Path
                : null;

            return new PlannedFile
            {
                Path = file.Path, Action = PlannedAction.Skip, Skip = PlannedSkip.ExportOnly,
                Name = known.Name, Guid = known.Guid, ImportableSibling = sibling,
            };
        }

        var action =
            TextualForms.Contains(known.Form, StringComparer.Ordinal) ? PlannedAction.ApplyText
            : known.Form == TypedTaskForm ? PlannedAction.ApplyTyped
            : NativeForms.Contains(known.Form, StringComparer.Ordinal) ? PlannedAction.ApplyNative
            : PlannedAction.Skip;

        if (action == PlannedAction.Skip)
        {
            // A form this planner has never heard of: a newer exporter wrote the mirror.
            // Guessing which importer it belongs to is how the wrong one gets fed, so refuse
            // and say so — the same rule as skip_reason's default on the export side.
            warnings.Add($"{file.Path}: the manifest records form '{known.Form}', which this "
                         + "version does not know how to import — skipped");
            return new PlannedFile
            {
                Path = file.Path, Action = PlannedAction.Skip, Skip = PlannedSkip.UnknownForm,
                Name = known.Name, Guid = known.Guid,
            };
        }

        return new PlannedFile
        {
            Path = file.Path,
            Action = action,
            Name = known.Name,
            Guid = known.Guid,
            Resolve = ResolveKnown(file.Path, known, byGuid, warnings),
        };
    }

    /// <summary>
    /// Real names from the root to a recorded object: decoded directories, then the owner
    /// chain, then the object's own verbatim name.
    /// </summary>
    private static List<string> ResolveKnown(
        string path,
        ProjectObject known,
        Dictionary<string, List<ProjectObject>> byGuid,
        List<string> warnings)
    {
        var chain = DirectoryNames(path);

        if (!string.IsNullOrEmpty(known.Parent))
        {
            // Members sit BESIDE their owner as `Owner.Member.xml`, so ownership is a name
            // chain, not a directory. The manifest records it by GUID — a chain, because a
            // property owns its accessors — and walking GUIDs is what survives renames and
            // dotted names alike.
            List<string> owners = [];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parent = known.Parent;
            while (!string.IsNullOrEmpty(parent) && seen.Add(parent))
            {
                if (!byGuid.TryGetValue(parent, out var records) || records.Count == 0)
                {
                    warnings.Add($"{path}: owner {parent} is not in the manifest — resolving "
                                 + "by file name instead");
                    return FallBackToStemSplit(path, known, chain);
                }

                owners.Add(records[0].Name);
                parent = records[0].Parent;
            }

            owners.Reverse();
            chain.AddRange(owners);
        }
        else if (StemOf(path) is { } stem && stem.Contains('.')
                 && PathMapper.DecodeStem(stem) != known.Name)
        {
            // No recorded owner, but the stem is dotted and the whole of it is NOT the
            // object's name. That is a member exported before manifest schema 4 recorded
            // parents — the dot is the owner split, exactly as the script always read it.
            // The manifest name is what makes this safe where G5 was not: a genuinely
            // dotted NAME decodes to the full stem and never reaches this branch.
            return FallBackToStemSplit(path, known, chain);
        }

        chain.Add(known.Name);
        return chain;
    }

    private static List<string> FallBackToStemSplit(
        string path, ProjectObject known, List<string> directories)
    {
        var stem = StemOf(path) ?? known.Name;
        var lastDot = stem.LastIndexOf('.');
        if (lastDot > 0)
        {
            directories.Add(PathMapper.DecodeStem(stem[..lastDot]));
        }

        directories.Add(known.Name);
        return directories;
    }

    /// <summary>A file the manifest does not record: a create candidate, or a stray.</summary>
    private static PlannedFile PlanUnknown(
        MirrorFile file, string fileName, List<string> warnings)
    {
        var textualSuffix = TextualSuffixes.FirstOrDefault(
            s => fileName.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        if (textualSuffix is not null)
        {
            return PlanCreate(file, fileName, textualSuffix, warnings);
        }

        var nativeSuffix = NativeSuffixes.FirstOrDefault(
            s => fileName.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        if (nativeSuffix is not null)
        {
            // import_native both replaces and creates, so an unrecorded native file is not
            // suspicious the way an unrecorded YAML is — it is how a teammate's new
            // graphical POU arrives before anyone re-exports.
            var (owner, name) = SplitStem(fileName[..^nativeSuffix.Length]);
            List<string> chain = DirectoryNames(file.Path);
            if (owner is not null)
            {
                chain.Add(owner);
            }

            chain.Add(name);
            return new PlannedFile
            {
                Path = file.Path, Action = PlannedAction.ApplyNative,
                Name = name, Resolve = chain,
            };
        }

        // Not an object, not bookkeeping. This is G6's fix: the old importer's last branch
        // was "hand it to import_native", so a README.md in the mirror was offered to
        // CODESYS as native XML. A YAML the manifest does not record lands here too — the
        // export writes those files, so one it did not write is not Versio's to apply.
        warnings.Add($"{file.Path}: not part of the export — not imported");
        return new PlannedFile
        {
            Path = file.Path, Action = PlannedAction.Skip, Skip = PlannedSkip.Stray,
        };
    }

    private static PlannedFile PlanCreate(
        MirrorFile file, string fileName, string suffix, List<string> warnings)
    {
        var (owner, stemName) = SplitStem(fileName[..^suffix.Length]);
        var name = stemName;

        if (suffix is not ".gvl.st" && !string.IsNullOrEmpty(file.Text))
        {
            // The declaration states the object's real name; the file name is a convenience
            // for humans. When they disagree the declaration wins — that is G3 — and the
            // disagreement is worth a sentence, because the file the user sees and the
            // object the project gains will not match. A GVL body has no header to parse,
            // so its name can only come from the file.
            var parsed = StDeclarationParser.Parse(
                StTextCodec.Split(file.Text).Declaration);
            if (parsed.CanCreate && parsed.Name != stemName)
            {
                warnings.Add($"{file.Path}: the declaration names '{parsed.Name}', not "
                             + $"'{stemName}' — the object will be created as '{parsed.Name}'");
                name = parsed.Name!;
            }
        }

        List<string> chain = DirectoryNames(file.Path);
        if (owner is not null)
        {
            chain.Add(owner);
        }

        chain.Add(name);
        return new PlannedFile
        {
            Path = file.Path, Action = PlannedAction.Create, Name = name, Resolve = chain,
        };
    }

    /// <summary>Decoded directory names for a mirror path, outermost first.</summary>
    /// <remarks>
    /// Decoded because a folder object's name obeys the same encoding as any other stem, and
    /// comparing an encoded segment against raw names is the resolution half of G4. A
    /// shortened stem (digest tail) does not decode back; nothing can recover it from the
    /// path alone, and the object it names will simply not resolve — reported by the script,
    /// not guessed at here.
    /// </remarks>
    private static List<string> DirectoryNames(string path)
    {
        var segments = path.Split('/');
        List<string> names = new(segments.Length);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            names.Add(PathMapper.DecodeStem(segments[i]));
        }

        return names;
    }

    /// <summary>The file stem with its recognised suffix removed, or null when none matches.</summary>
    private static string? StemOf(string path)
    {
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        var suffix = TextualSuffixes.Concat(NativeSuffixes).FirstOrDefault(
            s => fileName.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        return suffix is null ? null : fileName[..^suffix.Length];
    }

    /// <summary>Owner-prefix split on the last dot, both halves decoded.</summary>
    private static (string? Owner, string Name) SplitStem(string stem)
    {
        var lastDot = stem.LastIndexOf('.');
        return lastDot > 0
            ? (PathMapper.DecodeStem(stem[..lastDot]), PathMapper.DecodeStem(stem[(lastDot + 1)..]))
            : (null, PathMapper.DecodeStem(stem));
    }
}
