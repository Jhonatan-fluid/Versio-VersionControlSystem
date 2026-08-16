namespace Versio.Core.Plc;

/// <summary>One file in the mirror, as handed to a planner.</summary>
/// <remarks>
/// <see cref="Text"/> is loaded by the caller, for exactly the files the format asked for
/// through <see cref="IPlcProjectFormat.NeedsTextToPlan"/>. The planner is pure and cannot
/// read a disk; the caller can, but does not know which suffixes matter to a vendor. Splitting
/// the question that way keeps the IO on one side of the seam and the vendor knowledge on the
/// other.
/// </remarks>
public sealed record MirrorFile
{
    /// <summary>Mirror-relative path, forward slashes.</summary>
    public required string Path { get; init; }

    /// <summary>File content, when the format asked for it. Null otherwise.</summary>
    public string? Text { get; init; }
}

/// <summary>What the import will do with one file.</summary>
public enum PlannedAction
{
    /// <summary>Replace the object's text in place — declaration, implementation, or both.</summary>
    ApplyText,

    /// <summary>Apply through the vendor's typed API — a <c>.tasks.yaml</c> today.</summary>
    ApplyTyped,

    /// <summary>Hand to the vendor's native importer, which replaces in place or creates.</summary>
    ApplyNative,

    /// <summary>Create an object that does not exist in the export's record.</summary>
    Create,

    /// <summary>Deliberately not applied. <see cref="PlannedFile.Skip"/> says why.</summary>
    Skip,
}

/// <summary>Why a planned file is skipped.</summary>
public enum PlannedSkip
{
    /// <summary>Not skipped at all — the action is one of the apply kinds.</summary>
    NotSkipped,

    /// <summary>Versio's own bookkeeping: describes the export, is not in the project.</summary>
    Bookkeeping,

    /// <summary>Real project state, exported for review, with no import path that can be
    /// trusted. <see cref="PlannedFile.ImportableSibling"/> names the editable half when
    /// one exists.</summary>
    ExportOnly,

    /// <summary>Not an object, not bookkeeping — a file the export never wrote. Never handed
    /// to a toolchain (G6): CODESYS was once given markdown as native XML.</summary>
    Stray,

    /// <summary>The manifest records a form this planner does not know. A newer exporter
    /// wrote it; refusing is safer than guessing which importer it belongs to.</summary>
    UnknownForm,
}

/// <summary>One file's planned treatment.</summary>
public sealed record PlannedFile
{
    /// <summary>Mirror-relative path, forward slashes.</summary>
    public required string Path { get; init; }

    public required PlannedAction Action { get; init; }

    public PlannedSkip Skip { get; init; } = PlannedSkip.NotSkipped;

    /// <summary>
    /// The object's real name — the manifest's verbatim record for an existing object, or the
    /// declaration's own statement for a create.
    /// </summary>
    /// <remarks>
    /// This field is the fix for G3: the importer used to name a created object from its
    /// file name, so <c>PROGRAM Foo</c> in <c>Bar.st</c> created <c>Bar</c>. The plan carries
    /// the parsed name and the script stops deriving one.
    /// </remarks>
    public string? Name { get; init; }

    /// <summary>Manifest identity, empty for a create or a skip.</summary>
    public string Guid { get; init; } = string.Empty;

    /// <summary>
    /// Real object names from the project root to the object itself, decoded — never file
    /// stems. The last element is the object; everything before it is where it lives.
    /// </summary>
    /// <remarks>
    /// This is the fix for the resolution half of G4: the importer used to compare
    /// percent-encoded file stems against raw object names, so any name that needed encoding
    /// could not be found. The plan decodes C#-side, where the mapper lives and is tested.
    /// </remarks>
    public IReadOnlyList<string> Resolve { get; init; } = [];

    /// <summary>For an export-only skip: the file to edit instead, or null when the change
    /// belongs in the IDE.</summary>
    public string? ImportableSibling { get; init; }
}

/// <summary>One file the export wrote for an object that is now gone from the mirror.</summary>
/// <param name="Path">Mirror-relative path, forward slashes.</param>
/// <param name="Hash">
/// What the export recorded for these bytes. The executing side compares the <i>live</i>
/// object against it before removing anything: a mismatch means the object changed in the IDE
/// since the export, so its absence from the mirror is not permission to destroy that work.
/// </param>
/// <param name="Form">The export form, so the executing side knows how to compare — a textual
/// file byte for byte, a native one through an equivalence normaliser.</param>
public sealed record DeletedFile(string Path, string Hash, string Form);

/// <summary>
/// An object the mirror no longer holds any file for.
/// </summary>
/// <remarks>
/// <para>
/// <b>A proposal, not an instruction.</b> The planner knows what the last export recorded and
/// what the mirror holds now; it cannot know what the project holds, so the executing side
/// still confirms the object is there and still matches before removing it.
/// </para>
/// <para>
/// <b>Every precondition here is load-bearing rather than defensive.</b> <c>remove()</c> was
/// measured to delete a referenced object with no exception, no cascade and no warning (P3),
/// so nothing downstream will object to a mistake made here.
/// </para>
/// </remarks>
public sealed record PlannedDeletion
{
    /// <summary>Manifest identity. Never empty — an object without one is not deletable,
    /// because identity is the whole basis for believing the absence means anything.</summary>
    public required string Guid { get; init; }

    /// <summary>The manifest's verbatim name.</summary>
    public required string Name { get; init; }

    /// <summary>Real decoded names from the project root to the object itself.</summary>
    public IReadOnlyList<string> Resolve { get; init; } = [];

    /// <summary>
    /// Every file the export wrote for this object, all of them now absent.
    /// </summary>
    /// <remarks>
    /// Plural because one object can be several files: a GVL is a <c>.gvl.st</c> and a
    /// <c>.gvl.xml</c> under one GUID. <b>All of them must be gone.</b> One half deleted is
    /// somebody tidying, mis-editing, or a partial checkout — not a decision to destroy the
    /// object, and acting on it would take the surviving half with it.
    /// </remarks>
    public required IReadOnlyList<DeletedFile> Files { get; init; }
}

/// <summary>
/// What an import would do, decided before the toolchain starts.
/// </summary>
/// <remarks>
/// <para>
/// The importer used to walk the mirror inside the script and infer everything from file
/// names — nine of the import plan's eleven gaps shared that one root cause. This is the
/// walk moved to the C# side, as a pure function over the manifest and a directory listing:
/// unit-testable in milliseconds, and available <i>before</i> CODESYS starts, which is the
/// only place a confirmation dialog can use it. A plan discovered inside the script arrives
/// 17–31 s too late to be agreed to.
/// </para>
/// <para>
/// Updates, creates, skips and <see cref="Deletions"/>. A deletion is a <b>proposal</b>: no
/// wire carries one yet and nothing executes one, so the plan is read C#-side — the log today,
/// the confirmation dialog next. Renames and moves are Phase 3.
/// </para>
/// </remarks>
public sealed record ImportPlan
{
    /// <summary>Every mirror file, classified. Sorted by path for determinism.</summary>
    public required IReadOnlyList<PlannedFile> Files { get; init; }

    /// <summary>Sentences a person should see before or after the import runs.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Manifest paths whose file is gone from the mirror — the raw input to
    /// <see cref="Deletions"/>, not the answer.
    /// </summary>
    /// <remarks>
    /// Kept beside the vetted set because the two differ, and the difference is the point: a
    /// GVL whose <c>.st</c> was deleted while its <c>.xml</c> survives appears here and is
    /// <b>not</b> a deletion. A count from this list is honest as "files that have gone"; it
    /// is not a count of objects that would be removed. Meaningless when
    /// <see cref="StructuralChangesUnavailable"/> is set.
    /// </remarks>
    public IReadOnlyList<string> MissingFromMirror { get; init; } = [];

    /// <summary>
    /// Objects the mirror no longer holds any file for, having passed every precondition the
    /// planner can check.
    /// </summary>
    /// <remarks>
    /// <b>Empty whenever <see cref="StructuralChangesUnavailable"/> is set</b>, and that is
    /// not a formality: what a stale manifest does not list cannot be told apart from what it
    /// never recorded, so every absence would look like a deletion.
    /// </remarks>
    public IReadOnlyList<PlannedDeletion> Deletions { get; init; } = [];

    /// <summary>
    /// Why deletes, renames and moves cannot be planned from this mirror — a missing or
    /// pre-reporting-schema manifest — or null when the manifest can be believed. Never
    /// guessed at: a file absent from a mirror whose manifest is stale is not evidence of a
    /// deletion.
    /// </summary>
    public string? StructuralChangesUnavailable { get; init; }
}
