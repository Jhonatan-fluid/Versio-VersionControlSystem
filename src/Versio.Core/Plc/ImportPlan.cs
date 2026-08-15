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
/// Phase 1 plans updates, creates and skips. Deletes, renames and moves are phases 2 and 3;
/// <see cref="MissingFromMirror"/> is their input, recorded here and acted on by nothing.
/// </para>
/// </remarks>
public sealed record ImportPlan
{
    /// <summary>Every mirror file, classified. Sorted by path for determinism.</summary>
    public required IReadOnlyList<PlannedFile> Files { get; init; }

    /// <summary>Sentences a person should see before or after the import runs.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Manifest paths whose file is gone from the mirror. Phase 2's delete candidates —
    /// recorded, not acted on, and meaningless when
    /// <see cref="StructuralChangesUnavailable"/> is set.
    /// </summary>
    public IReadOnlyList<string> MissingFromMirror { get; init; } = [];

    /// <summary>
    /// Why deletes, renames and moves cannot be planned from this mirror — a missing or
    /// pre-reporting-schema manifest — or null when the manifest can be believed. Never
    /// guessed at: a file absent from a mirror whose manifest is stale is not evidence of a
    /// deletion.
    /// </summary>
    public string? StructuralChangesUnavailable { get; init; }
}
