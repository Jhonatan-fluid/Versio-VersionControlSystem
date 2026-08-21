namespace Versio.Core.Plc;

/// <summary>What a toolchain is being asked to do.</summary>
public enum PlcOperation
{
    /// <summary>Check the toolchain is installed and usable.</summary>
    Probe,

    /// <summary>Write the project out as reviewable text.</summary>
    Export,

    /// <summary>Apply edited text back into the project.</summary>
    Import,
}

/// <summary>One project inside a job.</summary>
public sealed record PlcProjectJob
{
    public required string BindingId { get; init; }

    /// <summary>Absolute path to the project file.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Export destination, or import source, depending on the operation.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// For an import: exactly what it will do, decided before the toolchain starts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null means the runner computes one for itself, which is what every caller did before
    /// this field existed and what a test driving a runner directly still does. It is not
    /// "plan nothing": an import without a plan is refused, because the walk that used to
    /// infer everything from file names is deleted rather than bypassed.
    /// </para>
    /// <para>
    /// <b>It is here rather than on the vendor's job because of the confirmation dialog.</b>
    /// A person has to be told what an import will delete <i>before</i> agreeing to it, and
    /// the only way that promise is worth anything is if the plan they agreed to is the same
    /// object the script executes. The CODESYS job has carried a plan since Phase 1, but a
    /// vendor's job type is below the seam and the orchestrator that builds this one is
    /// above it — so a plan computed for a dialog had no way down.
    /// </para>
    /// <para>
    /// <see cref="ImportPlan"/> is vendor-neutral by construction: the actions are neutral,
    /// the paths are mirror-relative, and every vendor-specific decision was already made by
    /// <see cref="IPlcProjectFormat.PlanImport"/> before one of these exists. Carrying it
    /// here leaks nothing.
    /// </para>
    /// </remarks>
    public ImportPlan? Plan { get; init; }
}

/// <summary>
/// A unit of toolchain work, in terms no vendor owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Carries a list of projects so a runner that can batch is allowed to.</b> Starting an
/// engineering tool is expensive — CODESYS measures 17–31 s — and that cost is a property of
/// engineering tools generally rather than of one brand, so the room to open, process and
/// close several projects in one session belongs here rather than in a vendor's own job type.
/// </para>
/// <para>
/// <b>No runner batches today.</b> The CODESYS one refuses a job carrying anything but a
/// single project, because its scripts read a flat <c>project</c> key and have no loop —
/// see the remarks on <c>CodesysJob</c>. Whether a list is honoured is the runner's answer to
/// give, and callers must not read the plural as a promise it will be.
/// </para>
/// <para>
/// <see cref="Profile"/> is the vendor's notion of which toolchain version to use: a CODESYS
/// profile name, a TIA Portal version, a Studio 5000 revision. Opaque here by design — the
/// orchestrator carries it from the binding to the runner and never interprets it.
/// </para>
/// </remarks>
public sealed record PlcJob
{
    public required PlcOperation Operation { get; init; }

    /// <summary>Vendor-specific toolchain version identifier. Never parsed here.</summary>
    public string Profile { get; init; } = string.Empty;

    public required IReadOnlyList<PlcProjectJob> Projects { get; init; }

    /// <summary>Correlates every log line from this operation, across threads and processes.</summary>
    public string OperationId { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Whether the toolchain should save the project when it is done.</summary>
    public bool Save { get; init; } = true;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// What a toolchain run produced.
/// </summary>
/// <remarks>
/// Deliberately small. Everything the sync engine needs to decide what happens next, and
/// nothing that would require it to understand a vendor's output format — the CODESYS
/// JSON-lines protocol, whatever TIA Portal emits, or the log a future runner scrapes. The
/// adapter parses; this is what survives the boundary.
/// </remarks>
public sealed record PlcResult
{
    public required bool Succeeded { get; init; }

    /// <summary>Why it failed, in a sentence, or null when it did not.</summary>
    public string? FailureReason { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>Files the run actually wrote, relative to the job's path.</summary>
    public IReadOnlyList<string> ChangedPaths { get; init; } = [];

    /// <summary>Anything the toolchain flagged but did not fail on.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static PlcResult Failure(string why) =>
        new() { Succeeded = false, FailureReason = why };
}

/// <summary>Progress during a long run, for the heartbeat.</summary>
public readonly record struct PlcProgress(
    string OperationId,
    string Phase,
    int? Done,
    int? Total,
    string? Detail,
    TimeSpan Elapsed);

/// <summary>
/// Drives one vendor's engineering tool.
/// </summary>
/// <remarks>
/// <para>
/// The whole toolchain surface, behind one method. Stateless and file-in / file-out, so a
/// local implementation spawning a process and a remote one talking to a build agent are
/// interchangeable — which is what keeps the server-hosted option (plan §13.8) free to defer.
/// </para>
/// <para>
/// <b>This is the seam.</b> Adding a vendor means implementing this,
/// <see cref="IPlcProjectFormat"/> and <see cref="IPlcToolchain"/>. Nothing in <c>Sync/</c>,
/// <c>Git/</c>, <c>Locking/</c>, <c>Watching/</c> or <c>Diff/</c> should need to change; if it
/// does, vendor knowledge has leaked past here and the leak is the bug.
/// </para>
/// </remarks>
public interface IPlcRunner
{
    Task<PlcResult> RunAsync(
        PlcJob job,
        IProgress<PlcProgress>? progress,
        CancellationToken cancellationToken);
}
