using Versio.Core.Abstractions;
using Versio.Core.Logging;

namespace Versio.Core.Plc;

/// <summary>
/// The IO half of planning an import: read the manifest, walk the mirror, and hand both to
/// the vendor's pure planner.
/// </summary>
/// <remarks>
/// <para>
/// <b>Split this way because the two halves have different constraints.</b> The rules are
/// vendor knowledge and must be pure — <see cref="IPlcProjectFormat.PlanImport"/>, unit-tested
/// in milliseconds. The walk is neither: it is the same walk for every vendor, and it needs a
/// disk. Putting the walk here, on <see cref="IFileSystem"/>, is what lets it live in
/// <c>Versio.Core</c> and be tested without one.
/// </para>
/// <para>
/// <b>It exists as a shared class because there were nearly two of it.</b> The orchestrator
/// needs a plan before the confirmation dialog; the CODESYS runner needs one when a caller
/// supplies none. Those are different callers in different assemblies with different loggers,
/// and writing the walk twice is precisely the arrangement <c>CODESYS.md</c> §9 counts four
/// drifts in — where each copy was right for the caller in front of it and wrong for the next
/// one. The walk is subtle enough to be worth the class: get the path convention backwards and
/// every file in the mirror plans as a Create.
/// </para>
/// <para>
/// Nothing here knows a vendor. It knows a mirror has a manifest at a fixed name, that some
/// files need their text loaded, and that the format decides which — no suffixes, no forms,
/// no GUIDs.
/// </para>
/// </remarks>
public static class ImportPlanReader
{
    /// <summary>Versio's own record of what the last export wrote. Not a vendor's file.</summary>
    public const string ManifestName = "_manifest.json";

    /// <summary>
    /// Plan an import of the mirror at <paramref name="root"/>.
    /// </summary>
    /// <param name="label">
    /// Prefix for every log line — a binding id where there is one. The planner's warnings are
    /// about a specific project's mirror, and a warning that does not say whose is a warning
    /// somebody has to go and correlate by hand.
    /// </param>
    /// <remarks>
    /// Never throws for an unreadable manifest or an unreadable file: a mirror somebody
    /// hand-built, or a file being written as this reads it, still plans. What it cannot know
    /// it declines to guess at — the plan says structural changes are unavailable, and the
    /// script reports a file it still cannot read when it gets there.
    /// </remarks>
    public static async Task<ImportPlan> ReadAsync(
        IFileSystem fs,
        IPlcProjectFormat format,
        string root,
        IVersioLogger log,
        string category,
        string label,
        CancellationToken ct)
    {
        var prefix = string.IsNullOrEmpty(label) ? string.Empty : label + ": ";

        // Said out loud, because the plan cannot say it. An absent mirror and an empty one
        // both produce a plan with no files, and a plan with no files is a valid instruction
        // meaning "touch nothing" — so the difference has to be reported here or it is lost.
        // Refusing on it belongs to the caller: SyncOrchestrator.ImportAsync does, and a
        // caller that only wants to know what an import WOULD do is entitled to ask about a
        // mirror that has gone.
        if (!fs.DirectoryExists(root))
        {
            log.Warn(category,
                $"{prefix}there is no exported text at {root}; the plan is empty because there "
                + "is nothing there, not because there is nothing to do");
        }

        var manifest = await ReadManifestAsync(fs, root, log, category, prefix, ct)
            .ConfigureAwait(false);

        List<MirrorFile> mirror = [];
        foreach (var absolute in fs.EnumerateFiles(root))
        {
            ct.ThrowIfCancellationRequested();

            // EnumerateFiles hands back ABSOLUTE paths; the planner keys everything on
            // mirror-relative ones taken from the manifest. Getting this backwards matches
            // nothing against the manifest and plans a Create for every file in the mirror —
            // a total misplan that looks like a working import until it runs. The interface's
            // own comment claimed the opposite until 2026-08-15; see IFileSystem.
            var relative = System.IO.Path.GetRelativePath(root, absolute).Replace('\\', '/');

            string? text = null;
            if (format.NeedsTextToPlan(relative))
            {
                try
                {
                    text = await fs.ReadAllTextAsync(absolute, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Planned from its name alone. If it is still unreadable when the script
                    // reaches it, the script reports that per file.
                    log.Debug(category,
                        $"{prefix}could not read {relative} while planning: {ex.Message}");
                }
            }

            mirror.Add(new MirrorFile { Path = relative, Text = text });
        }

        var plan = format.PlanImport(manifest, mirror);

        foreach (var warning in plan.Warnings)
        {
            // Warn, because these are decisions a person may disagree with: a created object
            // taking its declaration's name over its file's, a stray file refused.
            log.Warn(category, $"{prefix}{warning}");
        }

        if (plan.StructuralChangesUnavailable is { } why)
        {
            log.Debug(category, $"{prefix}{why}");
        }

        if (plan.MissingFromMirror.Count > 0)
        {
            // The RAW input, and the deletion lines below are the vetted answer. The two differ
            // whenever a precondition held an object back, and every one of those differences
            // is a warning already logged above — so this count is only ever a starting point.
            //
            // THIS LINE USED TO SAY deleting "is not applied by this version, so they remain
            // in the project". It went on saying it after the import learned to delete, which
            // is the same stale-sentence defect that had the confirmation dialog promising
            // objects would stay while they were removed. A log that describes last version's
            // behaviour is worse than silence: it is read as evidence.
            log.Info(category,
                $"{prefix}{plan.MissingFromMirror.Count} exported file(s) are no longer in "
                + "the mirror");
        }

        // The deletions, named and split by what will actually happen to them.
        //
        // AT INFO AND NEVER COLLAPSED. This is the one decision in a plan that takes work out
        // of a project, it is taken ~20 s before CODESYS starts, and until now it was the only
        // part of a plan that reached no log line at all — the script logged the removals it
        // performed, but nothing recorded what had been decided or what a person agreed to.
        // "Quiet when idle, loud when working" cuts the other way here: a run that deletes
        // something is the definition of working.
        var removing = plan.Deletions.Where(d => d.CannotVerify is null).ToList();
        if (removing.Count > 0)
        {
            log.Info(category,
                $"{prefix}{removing.Count} object(s) will be removed from the project: "
                + string.Join(", ", removing.Select(d => d.Name)));
        }

        // Grouped by reason, matching the dialog, so the log and the sentence somebody agreed
        // to can be read against each other rather than guessed at.
        foreach (var group in plan.Deletions
                     .Where(d => d.CannotVerify is not null)
                     .GroupBy(d => d.CannotVerify!, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // Info rather than Warn: refusing to destroy something Versio cannot check is the
            // system working, not a fault. The executing side warns if it disagrees.
            log.Info(category,
                $"{prefix}{group.Count()} object(s) have lost every exported file but will be "
                + $"kept: {string.Join(", ", group.Select(d => d.Name))} — {group.Key}");
        }

        return plan;
    }

    private static async Task<Projects.ProjectManifest?> ReadManifestAsync(
        IFileSystem fs, string root, IVersioLogger log, string category, string prefix,
        CancellationToken ct)
    {
        var path = System.IO.Path.Combine(root, ManifestName);
        if (!fs.FileExists(path))
        {
            return null;
        }

        try
        {
            // The reporting overload, because Parse never throws: garbage comes back as Empty
            // at schema 1, which the planner correctly refuses to trust — but silently, and a
            // manifest that rotted into "plan everything from file names" is a downgrade
            // somebody should be able to find in the log.
            var manifest = Projects.ProjectManifest.Parse(
                await fs.ReadAllTextAsync(path, ct).ConfigureAwait(false), out var problems);

            foreach (var problem in problems)
            {
                log.Warn(category, $"{prefix}{ManifestName}: {problem}");
            }

            return manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn(category,
                $"{prefix}the mirror's manifest could not be read ({ex.Message}) — planning "
                + "the import without it");
            return null;
        }
    }
}
