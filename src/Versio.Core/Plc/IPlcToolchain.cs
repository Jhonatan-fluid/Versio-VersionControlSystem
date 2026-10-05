namespace Versio.Core.Plc;

/// <summary>What a vendor's toolchain can actually do.</summary>
/// <remarks>
/// Capabilities are declared rather than assumed because they differ sharply. CODESYS can
/// export a readable Structured Text body for a textual POU; a vendor whose only export is
/// a proprietary blob cannot, and the UI must say so instead of showing an empty diff.
/// </remarks>
public sealed record PlcCapabilities
{
    /// <summary>Objects can be exported as reviewable IEC 61131-3 text.</summary>
    public bool TextualExport { get; init; }

    /// <summary>Edited text can be applied back into the project.</summary>
    public bool TextualImport { get; init; }

    /// <summary>Objects absent from the project can be created during import.</summary>
    public bool ObjectCreation { get; init; }

    /// <summary>The toolchain runs headlessly, without a visible IDE.</summary>
    public bool Headless { get; init; }

    /// <summary>
    /// Whether an export may read a copy of the project while the IDE holds the original.
    /// </summary>
    /// <remarks>
    /// Default is true: the first vendor's occupancy is a sidecar on the path, so a copy
    /// at another path is unencumbered and exports byte-identically. A format that opens
    /// the payload exclusively cannot use that trick — copying a directory project to
    /// discover the open will fail is the expensive way to wait. Asked of the format so
    /// Sync never learns which vendors are which.
    /// </remarks>
    public bool ExportHeldViaCopy { get; init; } = true;

    /// <summary>Typical cost of one toolchain invocation. Drives the sync-mode default:
    /// CODESYS measures 17–31 s, which is why export-on-save is not the default.</summary>
    public TimeSpan TypicalStartupCost { get; init; }
}

/// <summary>Identifies a vendor's project files so a repository can be scanned for them.</summary>
public interface IPlcProjectFormat
{
    PlcVendor Vendor { get; }

    /// <summary>Display name, e.g. "CODESYS 3.5".</summary>
    string DisplayName { get; }

    /// <summary>
    /// How this vendor's runner labels its own log lines.
    /// </summary>
    /// <remarks>
    /// Default is <see cref="PlcVendor.Name"/>, which is what every shipping pack emits.
    /// Declared here so the log filter can list a chip per registered vendor without
    /// importing any pack — adding a vendor is still one <c>Register()</c> call, not a
    /// fourth edit in the shell. Empty is skipped, never shown as a blank row.
    /// </remarks>
    string LogCategory => Vendor.Name;

    PlcCapabilities Capabilities { get; }

    /// <summary>Extensions identifying a project, e.g. <c>.project</c>.</summary>
    IReadOnlyList<string> ProjectExtensions { get; }

    /// <summary>
    /// True when a path is a template rather than a working project.
    /// </summary>
    /// <remarks>
    /// Vendor-specific because the convention is. A CODESYS template is
    /// <c>*_template.project</c> and adopting one by mistake creates a binding that exports
    /// a template into its own tree — nonsense, and confusing to unpick afterwards.
    /// </remarks>
    bool IsTemplate(string path);

    /// <summary>Conventional export directory for a project path.</summary>
    string ConventionalExportPath(string projectPath);

    /// <summary>
    /// Sibling template path for a working project, or null when this vendor has none.
    /// </summary>
    /// <remarks>
    /// Default is none. The first vendor names a sibling <c>*_template.project</c>; the
    /// others treat archives as templates of the file itself (<see cref="IsTemplate"/>)
    /// and have no sibling to record. Inventing <c>_template.project</c> for every
    /// adopt is how a TwinCAT binding stored <c>foo.tsproj_template.project</c>.
    /// </remarks>
    string? ConventionalTemplatePath(string projectPath) => null;

    /// <summary>
    /// The bytes that <i>are</i> the project on disk — the file, or the directory that
    /// contains it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default is the project path itself, which is a file for every vendor Versio drove
    /// first. A second vendor's project is a directory that happens to have a small
    /// pointer file at the top: copying, hashing, backing up or restoring only that
    /// pointer leaves the program behind and the next open looking at an empty shell.
    /// </para>
    /// <para>
    /// <b>Asked of the format so Sync never learns which vendors are trees.</b> The
    /// orchestrator copies, hashes and restores whatever this returns. Naming a vendor
    /// in that code would be the leak the seam exists to prevent; a TwinCAT project is
    /// a directory too.
    /// </para>
    /// </remarks>
    string PayloadPathFor(string projectPath) => projectPath;

    /// <summary>
    /// True when this file, relative to <see cref="PayloadPathFor"/>, belongs in the
    /// project hash.
    /// </summary>
    /// <remarks>
    /// Default is everything. A tree payload writes logs and scratch beside the program;
    /// hashing those would make an idle project look changed on every poll. The format
    /// names the noise. Sync still must not skip the program store itself — that is how
    /// a 150 KB pointer file would become the whole hash of a half-gigabyte project.
    /// </remarks>
    bool CountsTowardProjectHash(string relativePath) => true;

    /// <summary>
    /// Where this toolchain records that it has a project open, or null if it records nothing.
    /// </summary>
    /// <remarks>
    /// CODESYS writes a <c>&lt;project&gt;.~u</c> sidecar while the IDE holds a project and
    /// removes it on close. Knowing that before launching is worth 17-31 s per attempt, and
    /// more importantly it is the difference between "wait" and a failure the user has to
    /// clear by hand.
    /// </remarks>
    string? OccupancyMarkerFor(string projectPath);

    /// <summary>
    /// Globs for files this toolchain writes beside a project that git should not keep.
    /// </summary>
    /// <remarks>
    /// Scoped to the project rather than blanket extensions: <c>plc/Machine-*.opt</c>, not
    /// <c>*.opt</c>. A repository is somebody else's too, and ignoring every file of a given
    /// extension repository-wide on the strength of one adopted project is a decision Versio
    /// has no business making.
    /// </remarks>
    IReadOnlyList<string> TransientPatternsFor(string projectPath);

    /// <summary>
    /// Globs for the toolchain's own binary files beside the project pointer, which git must
    /// store as binary — and through LFS where it can — rather than as text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default is none, which is right for a vendor whose project is one file: that file is
    /// already covered by <see cref="ProjectExtensions"/>. A directory project is not. A TIA
    /// Portal <c>.apXX</c> is a 150 KB pointer and the program is hundreds of megabytes in
    /// folders beside it, so a repository whose only rule was <c>*.ap20</c> committed the
    /// program as ordinary git blobs.
    /// </para>
    /// <para>
    /// <b>Scoped to this project, never a bare extension</b> — the bargain
    /// <see cref="TransientPatternsFor"/> already makes. <c>plc/Station/XRef/**</c>, not
    /// <c>*.db</c>: marking every <c>.db</c> or <c>.idx</c> in somebody's repository binary on
    /// the strength of one adopted project is not Versio's decision. Repo-relative, forward
    /// slashes, each ending <c>/**</c>. The planner withholds any pattern that would also cover
    /// a mirror, so a format need not know where the user put its export.
    /// </para>
    /// </remarks>
    IReadOnlyList<string> BinaryPayloadPatternsFor(string projectPath) => [];

    /// <summary>Read a marker's contents. Never throws; unreadable means held.</summary>
    /// <remarks>
    /// Returns <see cref="OccupancyVerdict.Held"/> or <see cref="OccupancyVerdict.Free"/> only.
    /// Whether a marker naming a dead process is <i>stale</i> is not a vendor question, and is
    /// decided by <see cref="ProjectOccupancyProbe.Resolve"/>.
    /// </remarks>
    ProjectOccupancy ParseOccupancy(string markerContents);

    /// <summary>
    /// True when <see cref="PlanImport"/> needs this file's content to plan it.
    /// </summary>
    /// <remarks>
    /// The planner is pure and cannot read a disk; the caller can, but which suffixes carry a
    /// parseable declaration is vendor knowledge. So the caller asks per file and loads text
    /// for exactly the ones the format names — today, textual sources whose declaration
    /// header can name an object the manifest does not know yet.
    /// </remarks>
    bool NeedsTextToPlan(string relativePath);

    /// <summary>
    /// Decide what an import of this mirror would do, before the toolchain starts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure: manifest and listing in, plan out. The manifest may be null or stale — the plan
    /// says so in <see cref="ImportPlan.StructuralChangesUnavailable"/> rather than guessing,
    /// and still classifies every file from what can be known without it.
    /// </para>
    /// <para>
    /// This is how the import becomes manifest-driven without the sync engine learning any
    /// vendor's file shapes: the orchestrator walks the directory and reads the manifest, the
    /// format decides what each file means, and the script executes exactly the plan.
    /// </para>
    /// </remarks>
    /// <param name="departed">
    /// Content the mirror no longer has, by mirror-relative path — what HEAD held for files the
    /// manifest records and the working tree has lost. Empty when the caller could not supply
    /// it, which is an ordinary state: an uncommitted mirror has no old copy of anything.
    /// </param>
    /// <remarks>
    /// <b><paramref name="departed"/> exists because a hash cannot answer every question.</b>
    /// Pairing a renamed file with the file it became is exact while the content is unchanged;
    /// once it has been edited too, only the old bytes can settle it, and the manifest keeps a
    /// hash rather than the bytes. Supplying it stays the caller's job so the planner remains
    /// pure — it is handed content, it never fetches any.
    /// </remarks>
    ImportPlan PlanImport(
        Projects.ProjectManifest? manifest,
        IReadOnlyList<MirrorFile> mirror,
        IReadOnlyDictionary<string, string> departed);

    /// <summary>
    /// How this vendor writes one of its own object types on screen, or null if it does not
    /// name that type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type strings in a manifest belong to whoever wrote it. <c>PLC_LOGIC</c>,
    /// <c>CALL_TO_POU</c> and <c>LIBRARY_MANAGER</c> describe how one vendor organises a
    /// project, and a second vendor organising the same machine differently needs its own
    /// answers rather than inherited ones.
    /// </para>
    /// <para>
    /// <b>Null for anything the standard already names.</b>
    /// <see cref="Projects.ObjectTypeNames"/> falls through to
    /// <see cref="Iec.IecObjectTypeNames"/>, so a vendor implements only the words that are its
    /// own — and a vendor that adds nothing to IEC 61131-3 can return null throughout and still
    /// render correctly.
    /// </para>
    /// </remarks>
    Projects.ObjectTypeName? DisplayNameFor(string type);

    /// <summary>
    /// A structured member pane for this file, or null when highlighted source is enough.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The format decides. The pane asks every registered format and takes the first
    /// table; a vendor that has nothing to draw this way keeps the default and the
    /// pane falls through to the file's own text. Default-null is load-bearing: every
    /// stand-in format in the tests inherits it, and adding a vendor must not require
    /// an App edit.
    /// </para>
    /// <para>
    /// Handed the path and the content so the format stays pure — it never fetches.
    /// </para>
    /// </remarks>
    Projects.MemberTable? TryParseMemberTable(string relativePath, string content) => null;

    /// <summary>
    /// This file's ladder networks in the neutral model, or null when the file is not a
    /// ladder block this format can read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same shape as <see cref="TryParseMemberTable"/> and for the same reason: the pane
    /// asks every registered format, and a vendor with no ladder parser keeps the default and
    /// costs nothing. Everything after the parse (structure, layout, diff, drawing) is
    /// <c>Versio.Core.Iec.Ladder</c>, so adding a vendor's ladder is one parser in that
    /// vendor's pack and this override, never an App or Core edit.
    /// </para>
    /// <para>
    /// Null for anything that is not ladder, including other graphical languages: those
    /// keep their current view rather than being drawn as something they are not.
    /// </para>
    /// </remarks>
    Iec.Ladder.LadderDocument? TryParseLadder(string relativePath, string content) => null;

    /// <summary>
    /// The zero-based lines of a block's code body in its readable source - from the first
    /// line after the declaration to the line before the block's closing keyword (end
    /// exclusive) - or null when the format cannot say.
    /// </summary>
    /// <remarks>
    /// Asked only once a member table drew the block's interface. Without it the code pane
    /// under the table repeats the whole declaration - the owner's FC on 2026-10-05 showed
    /// its thirteen header lines twice, once as a table and once as text - and then the
    /// owner asked for the body alone: no <c>BEGIN</c>, no <c>END_FUNCTION_BLOCK</c>, the
    /// common indent gone and lines numbered from 1. Where the body starts and ends is the
    /// language's business, so the format answers; null keeps the whole file, the honest
    /// fallback.
    /// </remarks>
    (int Start, int End)? CodeBody(string relativePath, string content) => null;
}

/// <summary>
/// Drives a vendor toolchain to export and import projects.
/// </summary>
/// <remarks>
/// <para>
/// Stateless and file-in / file-out, so a local implementation spawning a process and a
/// remote one talking to a build agent are interchangeable. That is what keeps the
/// server-hosted option (§13.8) free to defer.
/// </para>
/// <para>
/// <b>Adding a vendor means implementing this and <see cref="IPlcProjectFormat"/>.</b>
/// Nothing in <c>Sync/</c>, <c>Git/</c>, <c>Locking/</c>, <c>Watching/</c> or <c>Diff/</c>
/// should need to change — if it does, vendor knowledge has leaked past this seam and the
/// leak is the bug.
/// </para>
/// </remarks>
public interface IPlcToolchain
{
    IPlcProjectFormat Format { get; }

    /// <summary>Check the toolchain is installed and usable before anything depends on it.</summary>
    Task<PlcToolchainStatus> ProbeAsync(CancellationToken ct);

    /// <summary>
    /// A runner for the installation the probe found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Creating the runner belongs to the toolchain because only it knows what its runner
    /// needs — an executable path and a profile name for CODESYS, something else entirely for
    /// the next vendor. The application asks for a runner and gets one; it does not assemble
    /// one out of vendor parts, which is what it used to do.
    /// </para>
    /// <para>
    /// <b>Never null, even when the toolchain is missing.</b> An unavailable toolchain returns
    /// a runner that fails every job with the reason, so the sync engine is still built and
    /// the locks, their persistence and their recovery all keep working on a machine that
    /// cannot export. The alternative is a null check in every caller and a panel that goes
    /// dark for a reason unrelated to what it was showing.
    /// </para>
    /// </remarks>
    IPlcRunner CreateRunner(PlcToolchainStatus status);
}

/// <summary>Whether a toolchain can be used, and why not if it cannot.</summary>
public sealed record PlcToolchainStatus
{
    public required bool Available { get; init; }
    public string? Version { get; init; }
    public string? ExecutablePath { get; init; }

    /// <summary>Actionable sentence when unavailable — never a bare error code. Versio
    /// cannot install a vendor toolchain, so the message has to be enough for the user to
    /// fix it themselves.</summary>
    public string? Explanation { get; init; }

    /// <summary>
    /// The toolchain version identifier to pin on a binding.
    /// </summary>
    /// <remarks>
    /// Opaque above the seam: a CODESYS profile name, a TIA Portal version, a Studio 5000
    /// revision. Carried from here to the binding and back to the runner, and interpreted by
    /// nothing in between.
    /// </remarks>
    public string? Profile { get; init; }

    /// <summary>
    /// Several usable installations were found and none can be chosen safely.
    /// </summary>
    /// <remarks>
    /// Distinct from simply unavailable. Picking one for the user risks silently upgrading
    /// their project to a newer toolchain version, which is not a recoverable mistake — so
    /// this is reported and refused rather than guessed.
    /// </remarks>
    public IReadOnlyList<string> Candidates { get; init; } = [];
}
