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

    /// <summary>
    /// This file is the destination of a rename or move, so its content is already in the
    /// project under the object's old name and creating it would duplicate the object.
    /// </summary>
    /// <remarks>
    /// The pairing lives in <see cref="ImportPlan.Renames"/>. Skipped rather than removed from
    /// the plan, because the script warns about any mirror file the plan does not mention —
    /// <c>NOT_IN_PLAN</c> — and a file silently absent from a plan is exactly the ambiguity
    /// that walk was deleted to end.
    /// </remarks>
    RenameTarget,
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
/// <param name="NormalizedHash">
/// For a native form, the hash of the normalised payload — the only value a re-export can be
/// compared against, because the raw bytes churn. Empty on a pre-schema-5 mirror, which is what
/// makes such a deletion unverifiable rather than assumed unchanged.
/// </param>
/// <param name="ImplementationHash">
/// For a full ST body, the hash of its implementation alone — the half a rename does not touch,
/// and therefore the only thing that can pair a renamed POU with the file it became. Empty on a
/// pre-schema-6 mirror, and for every other form.
/// </param>
/// <param name="LineSketch">
/// For a full ST body, a bounded fingerprint of its lines — what pairs a renamed-and-edited
/// file when the mirror was never committed and git therefore has no old copy. Empty before
/// schema 7. An estimate, and second choice to git's actual bytes.
/// </param>
public sealed record DeletedFile(
    string Path, string Hash, string Form, string NormalizedHash = "",
    string ImplementationHash = "", string LineSketch = "");

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

    /// <summary>
    /// Null when the executing side can check this object before removing it; otherwise the
    /// sentence saying why it cannot, and therefore why the object will be kept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what makes the confirmation dialog able to tell the truth about two
    /// different outcomes.</b> Every deletion here has lost every file it was exported to, but
    /// that is not the same question as whether the object will actually go: the executing
    /// side refuses any deletion it cannot check against the last export, because a missing
    /// file says somebody deleted it and says nothing about whether it was edited in the IDE
    /// since. A dialog that named both kinds together would be wrong about one of them
    /// whichever tense it chose.
    /// </para>
    /// <para>
    /// <b>A sentence rather than a code, and nullable rather than a bool beside a reason.</b>
    /// The planner is the only place that knows what a form means and the narrative must not,
    /// so the vendor knowledge is spent here and what crosses is prose. A bool and a string
    /// would admit a state where the two disagree, and nothing would notice until it reached
    /// somebody deciding whether to press Import.
    /// </para>
    /// <para>
    /// <b>The planner's answer is a prediction, not the decision.</b> The script re-derives it
    /// from the live object, because only it can see the project — see <c>why_not_delete</c>
    /// in <c>versio_import.py</c>. The two are kept in step by
    /// <c>PythonFileNameParityTests</c>: a planner that promised removal the script then
    /// refused would put this dialog back to lying, which is the defect this field exists for.
    /// </para>
    /// </remarks>
    public string? CannotVerify { get; init; }
}

/// <summary>
/// An object whose file moved or changed name in the mirror, to be carried out as a rename
/// and/or a move on the object rather than as a deletion and a create.
/// </summary>
/// <remarks>
/// <para>
/// <b>One record covers both operations, because a person can do both in one commit.</b>
/// Dragging a file to another folder and renaming it is one edit to them and two calls to the
/// toolchain; splitting it into two plan entries would make the second depend on the first
/// having already happened, and a half-applied pair leaves an object nobody can find.
/// </para>
/// <para>
/// <b>No content check, and that is a real difference from a deletion.</b> A deletion has one
/// because being wrong destroys work; a rename moves an object and destroys nothing, so an
/// object edited in the IDE since the export keeps its edit and gains its new name. What a
/// rename does need is the same certainty about <i>which</i> object it is — so it is built
/// only from deletion candidates, which have already passed the recorded-owner-chain rule.
/// </para>
/// </remarks>
public sealed record PlannedRename
{
    /// <summary>Manifest identity — the whole point of doing this rather than delete-plus-create.</summary>
    public required string Guid { get; init; }

    /// <summary>Real object names from the project root to the object, as it is <i>now</i>.</summary>
    public IReadOnlyList<string> Resolve { get; init; } = [];

    /// <summary>The object's current name, from the manifest.</summary>
    public required string OldName { get; init; }

    /// <summary>What it should be called, from the file that appeared.</summary>
    public required string NewName { get; init; }

    /// <summary>
    /// Real object names from the project root to the object's new owner. Equal to
    /// <see cref="Resolve"/> minus its last element when only the name changed.
    /// </summary>
    public IReadOnlyList<string> NewParent { get; init; } = [];

    /// <summary>The mirror path that went, and the one that appeared — for the log and the dialog.</summary>
    public required string OldPath { get; init; }

    /// <summary>See <see cref="OldPath"/>.</summary>
    public required string NewPath { get; init; }

    /// <summary>True when the object changes owner, not merely name.</summary>
    public bool IsMove => !NewParent.SequenceEqual(Resolve.Take(Resolve.Count - 1));

    /// <summary>True when the object changes name, not merely owner.</summary>
    public bool IsRename => !string.Equals(OldName, NewName, StringComparison.Ordinal);
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
/// Updates, creates, skips and <see cref="Deletions"/>. A deletion is a <b>proposal</b>, but
/// no longer only that: it crosses the wire as the job's <c>deletions</c> key and
/// <c>versio_import.py</c> executes it, so what this plan says about deletions is what a
/// project loses. The executing side still confirms the object is there and still matches
/// before removing anything. Renames and moves are Phase 3.
/// </para>
/// <para>
/// <b>This paragraph said "no wire carries one yet and nothing executes one" for the whole of
/// the session that shipped deleting.</b> It was not the only place: the confirmation dialog
/// went on telling people their objects <i>will stay</i> while the script removed them, which
/// is the one lie this entire chain of gates exists to prevent, and it survived a full green
/// suite because the test pinning the wording asserted the old sentence — a tripwire that
/// could only fire if somebody fixed it. When behaviour crosses the seam, the sentences on
/// both sides of it move in the same commit.
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
    /// Objects that moved or were renamed in the mirror, to be carried out on the object
    /// itself so its identity survives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Each one replaces a deletion and a create.</b> A rename in the mirror looks exactly
    /// like a file disappearing and an unrelated file appearing, and treating it that way
    /// destroys the object and builds a new one — losing the GUID, and with it every
    /// reference, task assignment and visualisation binding that pointed at it. Detecting the
    /// pair is what makes the difference between a rename and a rebuild.
    /// </para>
    /// <para>
    /// <b>Empty whenever <see cref="StructuralChangesUnavailable"/> is set</b>, for the same
    /// reason <see cref="Deletions"/> is: the match needs a manifest that can be believed.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PlannedRename> Renames { get; init; } = [];

    /// <summary>
    /// Why deletes, renames and moves cannot be planned from this mirror — a missing or
    /// pre-reporting-schema manifest — or null when the manifest can be believed. Never
    /// guessed at: a file absent from a mirror whose manifest is stale is not evidence of a
    /// deletion.
    /// </summary>
    public string? StructuralChangesUnavailable { get; init; }
}
