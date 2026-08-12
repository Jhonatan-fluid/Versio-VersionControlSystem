namespace Versio.Core.Plc;

/// <summary>
/// A PLC engineering tool Versio can drive.
/// </summary>
/// <remarks>
/// Versio started CODESYS-only, but nothing above this line is CODESYS-shaped: git, the
/// diff, the direction lock, change detection and the commit gate all work the same way
/// whichever tool produced the project. This enum is the seam where vendor knowledge
/// begins.
/// </remarks>
public enum PlcVendor
{
    /// <summary>CODESYS 3.5 and the many OEM rebrands built on it.</summary>
    Codesys,

    /// <summary>Siemens TIA Portal. Not implemented — listed so the shape of the
    /// abstraction is judged against a real second vendor rather than a hypothetical one.</summary>
    TiaPortal,

    /// <summary>Rockwell Studio 5000. Not implemented.</summary>
    Studio5000,

    /// <summary>Beckhoff TwinCAT. Not implemented. Note it is CODESYS-derived but has
    /// diverged enough to need its own handling.</summary>
    TwinCat,
}

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
