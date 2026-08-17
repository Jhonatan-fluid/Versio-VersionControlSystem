using Versio.Core.Plc;
using Versio.Plc.Codesys;

namespace Versio.Core.Serialization;

/// <summary>How an object is written to disk. Mirrors <c>versio_types.py</c>.</summary>
public enum ExportForm
{
    /// <summary>Deliberately not exported (service objects, template-only state).</summary>
    Skip = 0,

    /// <summary>Structural node: becomes a directory, children walked.</summary>
    Container,

    /// <summary>Declaration + delimiter + implementation, one .st file.</summary>
    StFull,

    /// <summary>Declaration only (DUTs, GVL declarations).</summary>
    StDeclaration,

    /// <summary>Implementation only — actions and transitions have no declaration.</summary>
    StImplementation,

    /// <summary>Native .gvl.xml for structure plus .gvl.st for a readable diff.</summary>
    GvlBoth,

    /// <summary>Native export, non-recursive.</summary>
    Native,

    /// <summary>Native export, recursive — the object owns children we do not walk separately.</summary>
    NativeRecursive,

    /// <summary>
    /// The object's library references, written as vendor-neutral YAML.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own form rather than a flavour of <see cref="Native"/>, because the native XML of a
    /// library manager is a snapshot of how references <i>resolved</i> on the exporting machine
    /// rather than a list of what the project <i>asks for</i>. Two engineers with different
    /// libraries installed would produce different bytes from the same project.
    /// </para>
    /// <para>
    /// <b>One way.</b> Alone among the forms, this one is exported and never imported: the file
    /// is for review, and libraries are changed in the IDE (plan §3.10, Tier 3). The importer
    /// skips it by suffix, and the coverage report says so out loud, because a file that looks
    /// editable and is silently ignored is its own kind of gap.
    /// </para>
    /// </remarks>
    Libraries,

    /// <summary>
    /// Export the node itself natively (non-recursive) <b>and</b> descend into a
    /// subdirectory for its children.
    /// </summary>
    /// <remarks>
    /// Reserved for unmapped types that turn out to be structural. Probing a real project
    /// found two such nodes — "Plc Logic" and "Application" — that were Unknown before
    /// their GUIDs were added. Treating an unknown container as a plain leaf flattens its
    /// children into the parent's folder and quietly destroys the tree shape; treating it
    /// as a pure container would drop whatever payload the node itself carries. This does
    /// neither.
    /// </remarks>
    NativeWithChildren,

    /// <summary>
    /// Native <c>.xml</c> for fidelity plus <c>.tasks.yaml</c> for review — and the YAML is
    /// the half that imports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shaped after <see cref="GvlBoth"/>: one object, two files, one GUID in the manifest.
    /// The difference is which half is authoritative. A GVL imports from its <c>.st</c>; a
    /// task configuration imports from its <c>.tasks.yaml</c>, and its native XML is
    /// export-only — recorded in the manifest as
    /// <see cref="Versio.Core.Projects.ExportCoverage.TaskNativeForm"/>, which is what makes
    /// that last clause true anywhere but this comment. It was only a comment until
    /// 2026-08-15.
    /// </para>
    /// <para>
    /// <b>That asymmetry is the entire point, and it is not about readability.</b> Handing
    /// task configuration to <c>import_native</c> cost two measured defects: it rewrote
    /// <c>Core2</c> — a task's CPU core affinity — on every import without being asked, and
    /// every import after the first threw on the task objects while the run reported
    /// <c>failed: 0</c>. Both stop when this file stops going through that path.
    /// </para>
    /// </remarks>
    TaskBoth,

    /// <summary>
    /// An external-file object's native export — its one and only file, export-only.
    /// </summary>
    /// <remarks>
    /// Export-only from the day the type first existed, because the correction was measured
    /// before the type ever shipped importable: the first E4 round trip handed one of these
    /// to <c>import_native</c> and CODESYS <b>duplicated</b> the object instead of replacing
    /// it (G13). Unlike <see cref="TaskBoth"/> and <see cref="GvlBoth"/> there is no
    /// importable sibling — external files are IDE-managed entirely, and the payload is
    /// metadata (a file id and a download flag) a mirror edit could not meaningfully change.
    /// Recorded as <see cref="Versio.Core.Projects.ExportCoverage.ExternalNativeForm"/>.
    /// </remarks>
    ExternalNative,
}

/// <summary>
/// The per-type export decision. Pure, table-driven and fully unit tested, because a
/// wrong answer here either loses graphical layout or produces an unreadable diff.
/// </summary>
public static class ExportFormRules
{
    private static readonly Dictionary<CodesysObjectType, ExportForm> Table = new()
    {
        [CodesysObjectType.Folder] = ExportForm.Container,
        [CodesysObjectType.Device] = ExportForm.Container,
        [CodesysObjectType.PlcLogic] = ExportForm.Container,
        [CodesysObjectType.Application] = ExportForm.Container,

        [CodesysObjectType.Pou] = ExportForm.StFull,
        [CodesysObjectType.Dut] = ExportForm.StDeclaration,
        [CodesysObjectType.Interface] = ExportForm.StFull,
        [CodesysObjectType.Gvl] = ExportForm.GvlBoth,
        [CodesysObjectType.GvlPersistent] = ExportForm.GvlBoth,
        [CodesysObjectType.Evc] = ExportForm.Native,
        [CodesysObjectType.Method] = ExportForm.StFull,
        [CodesysObjectType.MethodNoReturn] = ExportForm.StFull,
        [CodesysObjectType.Property] = ExportForm.NativeRecursive,
        [CodesysObjectType.Action] = ExportForm.StImplementation,
        [CodesysObjectType.Transition] = ExportForm.StImplementation,

        [CodesysObjectType.Visualisation] = ExportForm.Native,
        [CodesysObjectType.TaskConfiguration] = ExportForm.TaskBoth,
        [CodesysObjectType.TextList] = ExportForm.Native,
        [CodesysObjectType.GlobalTextList] = ExportForm.Native,
        [CodesysObjectType.ImagePool] = ExportForm.Native,
        [CodesysObjectType.ProjectInformation] = ExportForm.Native,

        // Export-only by measurement, not policy: import_native duplicates these instead of
        // replacing them (G13). See ExportForm.ExternalNative.
        [CodesysObjectType.ExternalFile] = ExportForm.ExternalNative,
        [CodesysObjectType.ExternalFileSource] = ExportForm.ExternalNative,

        // Was Skip, reported as the NOT_IMPLEMENTED gap that _coverage.md exists to name:
        // adding a library or bumping a version changed nothing in the mirror. Its
        // references now go to neutral YAML, which is the one form a second vendor can
        // also produce without inventing a file format of its own.
        [CodesysObjectType.LibraryManager] = ExportForm.Libraries,

        // Exporting the visualisation manager makes every later import raise an
        // interactive overwrite dialog (codescribe's finding). The template carries these.
        [CodesysObjectType.VisualisationManager] = ExportForm.Skip,
        [CodesysObjectType.TargetVisualisation] = ExportForm.Skip,
        [CodesysObjectType.WebVisualisation] = ExportForm.Skip,
        [CodesysObjectType.VisualisationStyle] = ExportForm.Skip,
        [CodesysObjectType.ProjectSettings] = ExportForm.Skip,
        [CodesysObjectType.Task] = ExportForm.Skip,
        [CodesysObjectType.CallToPou] = ExportForm.Skip,
    };

    /// <summary>
    /// Decide the on-disk form for one object.
    /// </summary>
    /// <remarks>
    /// The textual fallbacks matter: a POU declared in ST but drawn in CFC/LD/FBD has no
    /// textual implementation, and writing it as .st would silently discard its graphical
    /// layout. Those objects go to native XML instead.
    /// </remarks>
    public static ExportForm Decide(CodesysObjectType type, bool hasTextualImplementation,
                                    bool hasTextualDeclaration, bool hasChildren = false)
    {
        if (!Table.TryGetValue(type, out var form))
        {
            // Unmapped type (Unknown, or a type a newer CODESYS added).
            // Never silently drop it — native export preserves whatever it is.
            //
            // The type table is incomplete by nature: probing a 25-object toy project found
            // two missing GUIDs, so a real machine project will find more. An unknown node
            // WITH children is most likely structural, and must keep its subtree shape.
            return hasChildren ? ExportForm.NativeWithChildren : ExportForm.Native;
        }

        return form switch
        {
            ExportForm.StFull when !hasTextualImplementation => ExportForm.Native,
            ExportForm.StDeclaration when !hasTextualDeclaration => ExportForm.Native,
            ExportForm.StImplementation when !hasTextualImplementation => ExportForm.Native,
            ExportForm.GvlBoth when !hasTextualDeclaration => ExportForm.Native,
            _ => form,
        };
    }

    /// <summary>True when the form produces an ST layer — text the toolchain round-trips.</summary>
    /// <remarks>
    /// Not simply "is it plain text": <see cref="ExportForm.Libraries"/> writes YAML, which is
    /// perfectly readable in a diff, but it is not an ST layer and there is no
    /// <c>textual_declaration</c> on the other end to replace. Answering true for it would put
    /// a library file through the importer's ST path.
    /// </remarks>
    public static bool IsTextual(ExportForm form) =>
        form is ExportForm.StFull or ExportForm.StDeclaration or ExportForm.StImplementation;

    // An IsExportOnly(ExportForm) lived here until 2026-08-15, documented as "the predicate the
    // UI needs for the Tier 3 lock glyph" and as the place "the next one-way form is declared".
    // It was neither. It had NO caller and NO test in its whole life, because the panel is
    // vendor-neutral and reads a manifest, not this enum — the live path is ObjectIndex asking
    // ExportCoverage.IsExportOnly(string).
    //
    // So when TaskBoth arrived and its own documentation said its native XML was export-only,
    // this predicate still answered "libraries only" and nothing went red, because nothing was
    // asking. It is deleted rather than corrected: a second home for a rule that cannot be
    // reached from the code that needs it is not a safeguard, it is a comment that compiles.
    //
    // One-wayness is a property of the manifest's recorded form, which is why it belongs with
    // the other manifest strings. This enum is the DECISION table — what to write — and
    // TASK_NATIVE is never decided, only recorded.
}
