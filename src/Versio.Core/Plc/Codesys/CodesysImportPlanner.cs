using Versio.Core.Diff;
using Versio.Core.Hashing;
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
/// Plans updates, creates, skips and <b>deletions</b>, and a deletion is executed: it crosses
/// the wire and <c>versio_import.py</c> removes the object. The planner knows what the last
/// export recorded and what the mirror holds now, so it can say which objects have lost every
/// file — but not whether the project still has them, or whether they were edited in the IDE
/// since, which are the executing side's questions. Renames and moves are Phase 3 of
/// <c>docs/CODESYS-IMPORT-PLAN.md</c>.
/// </para>
/// <para>
/// <see cref="PlannedDeletion.CannotVerify"/> is the planner's prediction of the executing
/// side's refusal, and it exists for the dialog: somebody is asked to agree ~20 s before
/// CODESYS is started, so the plan has to say which of these objects will actually go.
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

    public static ImportPlan Plan(
        ProjectManifest? manifest,
        IReadOnlyList<MirrorFile> mirror,
        IReadOnlyDictionary<string, string>? departed = null)
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

        var deletions = trusted
            ? PlanDeletions(manifest!, present, byGuid, warnings)
            : [];

        // Renames are matched LAST, out of the deletions and the creates, because that is what
        // a rename is: a file that went and a file that arrived. Both lists are rewritten by
        // the match — the deletion is withdrawn and the create becomes a skip — so a rename
        // costs the project neither its object nor a duplicate of it.
        var renames = trusted
            ? MatchRenames(files, deletions, byGuid, mirror, departed ?? new Dictionary<string, string>(), warnings)
            : [];

        return new ImportPlan
        {
            Files = files,
            Warnings = warnings,
            MissingFromMirror = missing,
            Deletions = deletions,
            Renames = renames,
            StructuralChangesUnavailable = unavailable,
        };
    }

    /// <summary>
    /// Pair each deletion with a create holding identical content, and turn the pair into a
    /// rename. Rewrites <paramref name="files"/> and <paramref name="deletions"/> in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Exact content match only — step 1 of Phase 3.</b> A file that moved or was renamed
    /// without being edited has the bytes the export wrote, so its hash still equals the one
    /// the manifest recorded for the file that went. That covers rename-without-editing, which
    /// is most renames, and it needs nothing but the manifest and the mirror.
    /// </para>
    /// <para>
    /// <b>Rename-and-edit is deliberately not detected here</b>, because its content no longer
    /// matches anything. It needs the old bytes from <c>git show HEAD:&lt;path&gt;</c> and a
    /// similarity score, and it falls back to delete-plus-create until then — correct, and
    /// lossy in the one way this whole pass exists to avoid, so it is worth saying out loud
    /// rather than leaving as a silent gap.
    /// </para>
    /// <para>
    /// <b>Single-file objects only.</b> A GVL is a <c>.gvl.st</c> and a <c>.gvl.xml</c> under
    /// one GUID; renaming it moves both, and pairing them means matching a set rather than a
    /// file. Refused rather than half-done: renaming an object on the strength of one of its
    /// two files is how the two halves end up disagreeing about what the object is called.
    /// </para>
    /// <para>
    /// <b>Ambiguity is refused, not guessed.</b> Two deleted files with identical content —
    /// two empty actions, say — cannot be told apart from the two creates that replaced them,
    /// and picking either pairing has a 50% chance of renaming the wrong object. Both stay a
    /// delete and a create, which is lossy but never wrong.
    /// </para>
    /// </remarks>
    private static List<PlannedRename> MatchRenames(
        List<PlannedFile> files,
        List<PlannedDeletion> deletions,
        Dictionary<string, List<ProjectObject>> byGuid,
        IReadOnlyList<MirrorFile> mirror,
        IReadOnlyDictionary<string, string> departed,
        List<string> warnings)
    {
        // Content lives on the MirrorFile, not on the plan: the plan says what to DO with a
        // file and deliberately does not carry its bytes across the wire.
        var text = mirror
            .Where(m => m.Text is not null)
            .ToDictionary(m => m.Path, m => m.Text!, StringComparer.Ordinal);

        // A create the planner has the content of. Text is loaded only for the suffixes the
        // format asked for, so a create whose text was never read simply does not match — it
        // cannot, and pretending otherwise would need bytes nobody has.
        var creates = files
            .Select((f, i) => (File: f, Index: i))
            .Where(p => p.File.Action == PlannedAction.Create && text.ContainsKey(p.File.Path))
            .ToList();

        if (creates.Count == 0 || deletions.Count == 0)
        {
            return [];
        }

        // Hash once per side, then pair. Content is the key on both sides: the deletion knows
        // what the export wrote, and the create is what is on disk now.
        var createsByHash = creates
            .GroupBy(p => ContentHash.Of(text[p.File.Path]), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        List<PlannedRename> renames = [];
        List<PlannedDeletion> matched = [];

        // Ambiguity has TWO sides and only one of them is obvious. Two arrivals holding one
        // departure's content is the case that comes to mind; two DEPARTURES sharing content
        // with one arrival is the same coin toss seen from the other end, and it does not
        // announce itself — the first deletion in iteration order simply wins, silently, and
        // the second is left as a plain delete. Counted up front so both are refused alike.
        var deletionsByHash = deletions
            .Where(d => d.Files.Count == 1)
            .GroupBy(d => d.Files[0].Hash, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var deletion in deletions.Where(d => d.Files.Count == 1))
        {
            var gone = deletion.Files[0];
            if (!createsByHash.TryGetValue(gone.Hash, out var candidates))
            {
                continue;
            }

            // One deleted file, one arrived file, one meaning. Anything else is a guess.
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
                // The declaration did not name it, so there is nothing to rename it TO. G3's
                // rule pointed the other way: a created object takes its declaration's name,
                // never its file's, and a rename must not reintroduce the file-stem guess.
                continue;
            }

            var newParent = ParentChainOf(arrival.File, deletion, byGuid);
            if (newParent is null)
            {
                warnings.Add(
                    $"{deletion.Name}: it looks renamed to {arrival.File.Path}, but the folder "
                    + "it moved into is not an object this mirror records, so where to move it "
                    + "can only be guessed at — treated as a deletion and a create instead");
                continue;
            }

            var proposed = new PlannedRename
            {
                Guid = deletion.Guid,
                Resolve = deletion.Resolve,
                OldName = deletion.Name,
                NewName = arrival.File.Name,
                NewParent = newParent,
                OldPath = gone.Path,
                NewPath = arrival.File.Path,
            };

            // A PAIRING THAT CHANGES NOTHING IS NOT A RENAME, and emitting one anyway is how
            // Versio reports work it did not do.
            //
            // This is the ordinary case for a POU, not an edge case. An ST_FULL file opens
            // `PROGRAM Simulation`, so the object's name lives in the CONTENT — rename the
            // file alone and the declaration still says what the object is called, the plan
            // reads that name, and old and new come out equal. The script would then perform
            // neither operation, find the object exactly where it left it, and count a
            // rename. The whole pair is withdrawn instead: no delete, no create, no rename.
            //
            // Withdrawing rather than leaving it as a delete-and-create matters. The
            // declaration is what names an object; treating the file name as authoritative
            // would destroy `Simulation` and build `Renamed` from a file that still declares
            // itself `Simulation`, which is G3's defect wearing different clothes.
            if (!proposed.IsRename && !proposed.IsMove)
            {
                warnings.Add(
                    $"{deletion.Name}: {gone.Path} was renamed to {arrival.File.Path}, but the "
                    + $"declaration inside it still says {arrival.File.Name}, so the object "
                    + "keeps that name and nothing is renamed — rename the object in the IDE, "
                    + "or edit the declaration, and the file name will follow on the next "
                    + "export");

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

            // The create must not also run: the object is already there under its old name,
            // and creating it would leave the project with two.
            files[arrival.Index] = arrival.File with
            {
                Action = PlannedAction.Skip,
                Skip = PlannedSkip.RenameTarget,
            };

            // Belt and braces, and honestly labelled as such: the deletionsByHash guard above
            // already means at most one deletion per hash reaches here, so nothing can pair
            // with this arrival twice. Kept because pairing an arrival with two objects would
            // rename one of them onto the other, and the cost of the line is nothing.
            createsByHash.Remove(gone.Hash);
        }

        foreach (var done in matched)
        {
            deletions.Remove(done);
        }

        MatchRenamedPous(files, deletions, byGuid, text, renames, warnings);
        MatchBySimilarity(files, deletions, byGuid, text, departed, renames, warnings);

        return [.. renames.OrderBy(r => r.OldName, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Pair a departed POU with an arrived one holding the same <b>implementation</b>, which is
    /// the half a rename does not touch. Step 2 of Phase 3, for <c>ST_FULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Exact content matching cannot see a POU rename at all</b>, and that is why this
    /// exists. A POU's file opens <c>PROGRAM Simulation</c>, so renaming the object rewrites the
    /// file and no whole-file hash matches. The body does not change, and manifest schema 6
    /// records it — <see cref="ProjectObject.ImplementationHash"/>.
    /// </para>
    /// <para>
    /// <b>The arrival is APPLIED here, not skipped</b>, and that is the difference from the
    /// exact-content pass above. There the two files were byte-identical, so writing one over
    /// the other was pointless; here the declaration genuinely differs — it carries the new name
    /// at the very least — so the object is renamed and then the file is written into it. Skip
    /// it and the object would wear its new name over its old declaration.
    /// </para>
    /// <para>
    /// <b>A wrong pairing here does not destroy anything</b>, which is what makes the weaker
    /// signal acceptable. Two unrelated POUs sharing a body would be paired as a rename: the
    /// project ends with the same set of objects it would have had either way, and only the GUID
    /// lineage differs. Compare a wrong pairing in <see cref="PlanDeletions"/>, which loses
    /// work. The ambiguity rules are still applied, so it takes a body shared by exactly one
    /// departure and exactly one arrival.
    /// </para>
    /// <para>
    /// <b>An empty body is shared by half the POUs in a project</b>, and that is handled by the
    /// ambiguity rule rather than by a special case: two empty bodies are two candidates, and
    /// two candidates are refused.
    /// </para>
    /// </remarks>
    private static void MatchRenamedPous(
        List<PlannedFile> files,
        List<PlannedDeletion> deletions,
        Dictionary<string, List<ProjectObject>> byGuid,
        Dictionary<string, string> text,
        List<PlannedRename> renames,
        List<string> warnings)
    {
        // Only ST_FULL carries an implementation hash, and only a create the planner has the
        // text of can have its own computed.
        var creates = files
            .Select((f, i) => (File: f, Index: i))
            .Where(p => p.File.Action == PlannedAction.Create
                        && !string.IsNullOrEmpty(p.File.Name)
                        && text.ContainsKey(p.File.Path))
            .Select(p => (p.File, p.Index,
                          IHash: ContentHash.Of(StTextCodec.Split(text[p.File.Path]).Implementation)))
            .ToList();

        var candidates = deletions
            .Where(d => d.Files.Count == 1
                        && string.Equals(d.Files[0].Form, "ST_FULL", StringComparison.Ordinal)
                        && d.Files[0].ImplementationHash.Length > 0)
            .ToList();

        if (creates.Count == 0 || candidates.Count == 0)
        {
            return;
        }

        List<PlannedDeletion> matched = [];

        foreach (var deletion in candidates)
        {
            var key = deletion.Files[0].ImplementationHash;

            // Both sides of the ambiguity, exactly as the exact-content pass does them.
            var arrivals = creates.Where(c => c.IHash == key).ToList();
            var departures = candidates.Count(d => d.Files[0].ImplementationHash == key);

            if (arrivals.Count != 1 || departures != 1)
            {
                warnings.Add(
                    $"{deletion.Name}: {departures} POUs lost a file with exactly this body and "
                    + $"{arrivals.Count} new files hold it, so which is the rename cannot be "
                    + "told — they are treated as deletions and separate creates instead");
                continue;
            }

            var arrival = arrivals[0];
            var newParent = ParentChainOf(arrival.File, deletion, byGuid);
            if (newParent is null)
            {
                continue;
            }

            var proposed = new PlannedRename
            {
                Guid = deletion.Guid,
                Resolve = deletion.Resolve,
                OldName = deletion.Name,
                NewName = arrival.File.Name!,
                NewParent = newParent,
                OldPath = deletion.Files[0].Path,
                NewPath = arrival.File.Path,
            };

            if (!proposed.IsRename && !proposed.IsMove)
            {
                // Same body, same name, same owner, and yet the whole-file hash did not match:
                // the DECLARATION changed and the file's STEM changed, without the object's
                // name changing. `Old.st` gone and `Old_2.st` arrived, both declaring
                // `PROGRAM Old`.
                //
                // There is nothing structural to do — but leaving it here would be the worst
                // of the three outcomes. `continue` would let the deletion stand AND the create
                // stand, destroying the object and rebuilding it under the same name with a new
                // GUID, for an edit that never moved or renamed anything. This comment used to
                // say the file was "left to be applied as the update it is", which is what the
                // code should have done and did not.
                //
                // So the deletion is withdrawn and the arrival is applied to the object where
                // it already lives.
                matched.Add(deletion);
                files[arrival.Index] = arrival.File with
                {
                    Action = PlannedAction.ApplyText,
                    Guid = deletion.Guid,
                    Resolve = deletion.Resolve,
                };
                continue;
            }

            renames.Add(proposed);
            matched.Add(deletion);

            // APPLIED, not skipped. The declaration differs — it carries the new name — so the
            // object is renamed first and then written into, and the resolve chain has to name
            // it where the rename will have put it.
            files[arrival.Index] = arrival.File with
            {
                Action = PlannedAction.ApplyText,
                Guid = deletion.Guid,
                Resolve = [.. newParent, arrival.File.Name!],
            };
        }

        foreach (var done in matched)
        {
            deletions.Remove(done);
        }
    }

    /// <summary>
    /// How alike a departed file and an arrived one must be before they are called the same
    /// object. Nothing below this is ever paired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>0.6, and the number is a judgement rather than a measurement.</b> What it has to
    /// separate is "renamed and edited" from "deleted one thing and wrote another", and those
    /// are not cleanly separable — a rename that rewrites two thirds of a POU genuinely does
    /// look like a new object. Set high enough that unrelated ST files, which share their
    /// keyword lines and little else, stay well under.
    /// </para>
    /// <para>
    /// <b>Being wrong here costs a GUID, not any work.</b> Too low and two unrelated objects are
    /// paired: the project ends with the same set of objects either way and only the identity
    /// lineage differs. Too high and a real rename becomes a delete and a create, which is
    /// exactly what happened before this pass existed. Neither destroys anything, which is why
    /// a judgement is acceptable here and would not be in the deletion set.
    /// </para>
    /// </remarks>
    private const double SimilarityFloor = 0.6;

    /// <summary>
    /// Pair a departed file with an arrived one on how alike their content is — the last
    /// matching pass, and the only one that can see a rename that also edited the file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It runs last because it is the only inexact one.</b> Exact content, then the
    /// implementation hash, then this: each earlier pass is certain, so anything reaching here
    /// has already failed every test that could have answered without a judgement.
    /// </para>
    /// <para>
    /// <b>It needs the old bytes, and only git has them.</b> The manifest records one hash per
    /// file, and hashes cannot be compared for similarity — that is what a hash is for. So
    /// <paramref name="departed"/> is what HEAD held, supplied by the caller, and an uncommitted
    /// mirror simply has none: the pass then does nothing and the rename falls back to a delete
    /// and a create, which is what it did before.
    /// </para>
    /// <para>
    /// <b>Best match, and it must be clearly best.</b> Two candidates scoring within a hair of
    /// each other is the same ambiguity the exact passes refuse outright, and here it is worse
    /// because neither is certain. A runner-up within <see cref="SimilarityMargin"/> refuses the
    /// pairing.
    /// </para>
    /// </remarks>
    private static void MatchBySimilarity(
        List<PlannedFile> files,
        List<PlannedDeletion> deletions,
        Dictionary<string, List<ProjectObject>> byGuid,
        Dictionary<string, string> text,
        IReadOnlyDictionary<string, string> departed,
        List<PlannedRename> renames,
        List<string> warnings)
    {
        if (departed.Count == 0 || deletions.Count == 0)
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
            if (!departed.TryGetValue(deletion.Files[0].Path, out var old)
                || string.IsNullOrEmpty(old))
            {
                continue;
            }

            var scored = creates
                .Select(c => (c.File, c.Index, Score: TextSimilarity.Of(old, text[c.File.Path])))
                .Where(c => c.Score >= SimilarityFloor)
                .OrderByDescending(c => c.Score)

                // Ties broken by path so two identical scores do not depend on directory
                // enumeration order — the runner-up check below is what actually refuses them,
                // and this only keeps the message stable.
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
                    + $"about as similar to its exported file ({scored[0].Score:P0} and "
                    + $"{scored[1].Score:P0}), so which is the rename cannot be told — it is "
                    + "treated as a deletion and separate creates instead");
                continue;
            }

            var arrival = scored[0];
            var newParent = ParentChainOf(arrival.File, deletion, byGuid);
            if (newParent is null)
            {
                continue;
            }

            var proposed = new PlannedRename
            {
                Guid = deletion.Guid,
                Resolve = deletion.Resolve,
                OldName = deletion.Name,
                NewName = arrival.File.Name!,
                NewParent = newParent,
                OldPath = deletion.Files[0].Path,
                NewPath = arrival.File.Path,
            };

            matched.Add(deletion);

            // Applied either way, as in the implementation-hash pass: the content differs by
            // definition here, so the object has to be written into after it is renamed.
            files[arrival.Index] = arrival.File with
            {
                Action = PlannedAction.ApplyText,
                Guid = deletion.Guid,
                Resolve = [.. newParent, arrival.File.Name!],
            };

            if (proposed.IsRename || proposed.IsMove)
            {
                renames.Add(proposed);
            }

            warnings.Add(
                $"{deletion.Name}: matched to {arrival.File.Path} on content similarity "
                + $"({arrival.Score:P0}), so it is renamed rather than removed and recreated — "
                + "no hash could pair them, because the file was edited as well as renamed");

            // Consumed: an arrival cannot be two objects.
            creates.RemoveAll(c => c.Index == arrival.Index);
        }

        foreach (var done in matched)
        {
            deletions.Remove(done);
        }
    }

    /// <summary>
    /// How far clear the best similarity match must be of the runner-up.
    /// </summary>
    /// <remarks>
    /// Without it, two files scoring 0.71 and 0.70 would pair on a hundredth — a difference
    /// with no meaning, decided by one line. The exact passes refuse ambiguity outright; this
    /// is the same rule expressed for a score rather than an equality.
    /// </remarks>
    private const double SimilarityMargin = 0.1;

    /// <summary>
    /// The chain to the object that should own the arrived file, or null when it cannot be
    /// read from the manifest.
    /// </summary>
    /// <remarks>
    /// Directory segments are decoded object names — the same convention <see cref="ResolveKnown"/>
    /// builds on — so an unchanged directory yields the old owner chain unchanged and the
    /// planner can tell a pure rename from a move without a special case.
    /// </remarks>
    private static IReadOnlyList<string>? ParentChainOf(
        PlannedFile arrival,
        PlannedDeletion deletion,
        Dictionary<string, List<ProjectObject>> byGuid)
    {
        var slash = arrival.Path.LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : arrival.Path[..slash];

        var oldParent = deletion.Resolve.Take(deletion.Resolve.Count - 1).ToList();

        var oldSlash = deletion.Files[0].Path.LastIndexOf('/');
        var oldDirectory = oldSlash < 0 ? string.Empty : deletion.Files[0].Path[..oldSlash];

        // Same folder: the owner has not changed, whatever its name decodes to. Taking the
        // recorded chain rather than re-deriving it from the path is the point — the recorded
        // one is names, and the path is stems.
        if (string.Equals(directory, oldDirectory, StringComparison.Ordinal))
        {
            return oldParent;
        }

        // A different folder. Every segment has to decode to a real name, and the resulting
        // chain has to be one the mirror actually records — otherwise this is a move into a
        // folder that exists on disk and nowhere in the project.
        var segments = directory.Length == 0
            ? []
            : directory.Split('/').Select(PathMapper.DecodeStem).ToList();

        var known = byGuid.Values
            .SelectMany(records => records)
            .Any(o => o.Path.StartsWith(
                directory.Length == 0 ? string.Empty : directory + "/", StringComparison.Ordinal));

        return known || directory.Length == 0 ? segments : null;
    }

    /// <summary>
    /// Which recorded objects the mirror no longer holds <b>any</b> file for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Grouped by GUID rather than walked per file, because the unit being deleted is an
    /// object and an object can be several files. A GVL is a <c>.gvl.st</c> and a
    /// <c>.gvl.xml</c> under one identity; a per-file rule would see the <c>.st</c> gone and
    /// remove the object the surviving <c>.xml</c> still describes.
    /// </para>
    /// <para>
    /// <b>Every rule below is the only thing standing between a stale mirror and destroyed
    /// work.</b> P3 measured what <c>remove()</c> does to an object others reference: it
    /// removes it, saves cleanly, and warns about nothing. There is no toolchain backstop, so
    /// a rule that is merely probably right here is a rule that silently breaks projects.
    /// </para>
    /// </remarks>
    private static List<PlannedDeletion> PlanDeletions(
        ProjectManifest manifest,
        HashSet<string> present,
        Dictionary<string, List<ProjectObject>> byGuid,
        List<string> warnings)
    {
        // Ancestors of everything still in the mirror, by GUID, naming what depends on them.
        //
        // TRANSITIVE, and it took a pre-existing test to notice. Checking direct children only
        // lets a grandparent go: a class whose property is gone from the mirror but whose
        // property ACCESSOR is still there would have been proposed for deletion, taking both
        // with it. Ownership chains are two and three deep in a real project — a property owns
        // its Get and Set — so the question is never "does this have a surviving child" but
        // "does anything surviving live inside it, at any depth".
        //
        // Export-only records cannot vote either way: their files are rewritten by every
        // export and applied by nothing, so their presence is not evidence an object lives.
        var dependants = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in manifest.Objects)
        {
            if (ExportCoverage.IsExportOnly(obj.Form) || !present.Contains(obj.Path))
            {
                continue;
            }

            var parent = obj.Parent;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrEmpty(parent) && seen.Add(parent))
            {
                (dependants.TryGetValue(parent, out var names)
                    ? names
                    : dependants[parent] = []).Add(obj.Name);

                parent = byGuid.TryGetValue(parent, out var owner) && owner.Count > 0
                    ? owner[0].Parent
                    : string.Empty;
            }
        }

        List<PlannedDeletion> deletions = [];

        foreach (var (guid, records) in byGuid.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var importable = records.Where(o => !ExportCoverage.IsExportOnly(o.Form)).ToList();

            // An object recorded only as export-only files has nothing whose absence means
            // anything. Never deletable, and nothing to say about it.
            if (importable.Count == 0)
            {
                continue;
            }

            // The record this object is described BY — its name, its owner, and the path its
            // resolve chain is read from. The first IMPORTABLE one rather than the first of
            // the group, so nothing here depends on the order the manifest happens to list
            // one object's files in.
            //
            // <b>Defensive, and measured to be exactly that.</b> Swapping this back to
            // records[0] turns no test red, and cannot: two records under one GUID agree on
            // name and owner by definition, and the paths that differ resolve the same because
            // StemOf answers null for a .tasks.yaml and so never reaches the dotted-stem
            // split. It is kept because a deletion resolving to the wrong object is
            // unrecoverable and manifest order is not something this file should depend on —
            // but it is not standing in front of a defect anybody has seen.
            var identity = importable[0];

            var gone = importable.Where(o => !present.Contains(o.Path)).ToList();
            if (gone.Count == 0)
            {
                continue;
            }

            // THE TWO-FILE RULE. Some gone, some still here: not a decision to destroy the
            // object. Said out loud rather than passed over, because "I deleted the .st and
            // the POU is still there" is otherwise unanswerable — and because acting on it
            // would take the surviving half with it.
            if (gone.Count != importable.Count)
            {
                warnings.Add(
                    $"{identity.Name}: {Files(gone.Count)} gone from the mirror but "
                    + $"{string.Join(", ", importable.Except(gone).Select(o => o.Path))} "
                    + "still there — the object is left alone, because one file of several is "
                    + "not a decision to delete it");
                continue;
            }

            // THE ORPHAN RULE, which is P3 written as a precondition. Removing an object other
            // objects reference dangles them silently — no exception, no cascade, no warning.
            // The planner cannot see every reference a project holds, but it can see the one
            // kind the manifest records: ownership, at any depth.
            if (dependants.TryGetValue(guid, out var living))
            {
                var orphaned = living.Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();

                warnings.Add(
                    $"{identity.Name}: every file for it has gone from the mirror, but "
                    + $"{string.Join(", ", orphaned)} still {(orphaned.Count == 1 ? "lives" : "live")} "
                    + "inside it — removing it would take them with it, so it is left alone");
                continue;
            }

            // THE GUESSED-CHAIN RULE. ResolveKnown falls back to splitting the file stem when
            // an owner is not in the manifest, and for an update that is a reasonable guess —
            // the worst case is text applied to the wrong object, which git can undo. For a
            // DELETION the same guess resolves to some other object and destroys it, and
            // nothing downstream objects (P3). A chain that had to be guessed is not a chain
            // to delete by.
            if (!OwnerChainIsRecorded(identity, byGuid))
            {
                warnings.Add(
                    $"{identity.Name}: every file for it has gone from the mirror, but its "
                    + "owner is not in the manifest, so where it lives can only be guessed at "
                    + "— it is left alone rather than resolved by file name and deleted");
                continue;
            }

            deletions.Add(new PlannedDeletion
            {
                Guid = guid,
                Name = identity.Name,

                // From the recorded path, because there is no file to read one from — which is
                // exactly why the chain is built from the manifest's names rather than stems.
                Resolve = ResolveKnown(identity.Path, identity, byGuid, warnings),
                Files = [.. gone
                    .OrderBy(o => o.Path, StringComparer.Ordinal)
                    .Select(o => new DeletedFile(
                        o.Path, o.Hash, o.Form, o.NormalizedHash, o.ImplementationHash))],
                CannotVerify = WhyUnverifiable(gone),
            });
        }

        return [.. deletions.OrderBy(d => d.Name, StringComparer.Ordinal)];
    }

    private static string Files(int n) => n == 1 ? "1 file" : $"{n} files";

    /// <summary>
    /// Why the script will refuse to remove this object, or null when it can check it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The planner's copy of a decision the script makes for itself</b>, and it exists
    /// because the dialog is asked the question ~20 s before CODESYS is. The script re-derives
    /// the same answer from the live object and its answer is the one that governs; this one
    /// only has to agree, and <c>PythonFileNameParityTests</c> is what says it does.
    /// </para>
    /// <para>
    /// <b>Every file, not any file.</b> A GVL is a <c>.gvl.st</c> and a <c>.gvl.xml</c> under
    /// one GUID, and the script checks each in turn and refuses on the first it cannot rebuild.
    /// One unverifiable file therefore keeps the whole object, so the predicate here has to be
    /// the same shape or the dialog would promise removal for an object held back by its
    /// second file.
    /// </para>
    /// <para>
    /// <b>Textual and native are checked against different things.</b> A textual file is
    /// rebuilt and compared byte for byte; a native one is re-exported, normalised, and
    /// compared against <see cref="ProjectObject.NormalizedHash"/>, which exists only from
    /// manifest schema 5. That is the whole of the native condition — the raw hash is over
    /// bytes that churn between exports and cannot stand in for it.
    /// </para>
    /// </remarks>
    private static string? WhyUnverifiable(List<ProjectObject> gone)
    {
        // Ordered, so the sentence a person reads is the same one on two readings of the same
        // repository rather than whichever record the manifest happened to list first.
        foreach (var file in gone.OrderBy(o => o.Path, StringComparer.Ordinal))
        {
            if (TextualForms.Contains(file.Form, StringComparer.Ordinal))
            {
                continue;
            }

            if (NativeForms.Contains(file.Form, StringComparer.Ordinal))
            {
                if (!string.IsNullOrEmpty(file.NormalizedHash))
                {
                    // Schema 5 and later: the manifest carries the normalised payload's hash,
                    // so the script can re-export the live object, normalise it the same way
                    // and compare. This is what closed Phase 2's open half.
                    continue;
                }

                // Exported before Versio recorded one. The raw hash cannot stand in: it is
                // over bytes that churn between exports, so comparing a re-export against it
                // would refuse this deletion for a reason with nothing to do with the object.
                // Says what to do about it, because there is something.
                return "It was exported before Versio recorded a comparable hash for native "
                    + "objects, so whether it changed in the IDE since is unknowable — "
                    + "export the project again and this will resolve itself";
            }

            // TASK_BOTH is the only form left that reaches here, and its .tasks.yaml is
            // rendered through the typed task API rather than read off the object. Refused
            // rather than rebuilt: a task configuration is the one object in a project whose
            // accidental removal takes every task with it.
            return $"Versio cannot rebuild a {file.Form} file from the project, so whether it "
                + "changed in the IDE since the last export is unknowable and Versio will not "
                + "guess";
        }

        return null;
    }

    /// <summary>
    /// True when every owner above this object is recorded, so its location is read rather
    /// than inferred.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="ResolveKnown"/>'s walk deliberately: what this asks is precisely
    /// "would that method have to fall back to splitting the stem". Kept as its own check
    /// rather than a flag threaded through the resolver, because the two callers want opposite
    /// things from the same answer — an update is content to guess, a deletion must not.
    /// </remarks>
    private static bool OwnerChainIsRecorded(
        ProjectObject obj, Dictionary<string, List<ProjectObject>> byGuid)
    {
        var parent = obj.Parent;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!string.IsNullOrEmpty(parent) && seen.Add(parent))
        {
            if (!byGuid.TryGetValue(parent, out var records) || records.Count == 0)
            {
                return false;
            }

            parent = records[0].Parent;
        }

        return true;
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
