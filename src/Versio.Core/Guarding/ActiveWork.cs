namespace Versio.Core.Guarding;

/// <summary>What kind of work is in flight.</summary>
/// <remarks>
/// <para>
/// Coarser than "which button was pressed" on purpose. The guard never needs to know that a
/// run was a fetch rather than a status — it needs to know whether killing it halfway through
/// costs anything, and that question has the same answer for both.
/// </para>
/// <para>
/// Vendor-neutral, like everything outside <c>Plc/</c>: an export is an export whether CODESYS
/// or TwinCAT is doing it.
/// </para>
/// </remarks>
public enum WorkKind
{
    /// <summary>Status, diff, log, fetch, <c>ls-remote</c>. Reads, or writes only remote refs.</summary>
    GitRead,

    /// <summary>Stage, commit, checkout, discard, stash, pull. Moves the index or the tree.</summary>
    GitWrite,

    /// <summary>Push. Local state is safe; the remote is what a half-run leaves uncertain.</summary>
    GitPush,

    /// <summary>Clone. Unbounded, and writes only into a directory nothing depends on yet.</summary>
    Clone,

    /// <summary>A toolchain export: project → mirror.</summary>
    Export,

    /// <summary>A toolchain import: mirror → project.</summary>
    Import,

    /// <summary>Repository scan, toolchain probe, staleness evaluation.</summary>
    Scan,
}

/// <summary>Whether work of a given kind may be cut short.</summary>
public enum Interruption
{
    /// <summary>Killing it costs time and nothing else.</summary>
    Cancellable,

    /// <summary>It must be allowed to finish. Cancelling risks leaving something half-written.</summary>
    MustFinish,
}

/// <summary>
/// Which kinds of work may be interrupted, and why.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived, never passed in.</b> A caller that had to state the interruption alongside the
/// kind would eventually state the wrong one, and the failure would be silent in the worst
/// direction — an import cancelled because somebody typed <c>Cancellable</c> at a call site.
/// One function decides, so the answer cannot vary by who is asking.
/// </para>
/// <para>
/// The asymmetry between export and import is the whole point of this type, and it is a
/// property of what the two operations write, not of how long they take:
/// </para>
/// <list type="bullet">
///   <item><b>Export</b> stages into <c>&lt;mirror&gt;.versio_staging</c> and swaps only once
///     the run completed. A killed export leaves the previous mirror exactly as it was.</item>
///   <item><b>Import</b> writes into the binary project itself. A killed import can leave a
    ///     file the IDE will not open. Branch switch still refuses; Stop and closing the app
    ///     may kill it — the backup taken before import is the recovery.</item>
/// </list>
/// </remarks>
public static class WorkKinds
{
    public static Interruption InterruptionOf(WorkKind kind) => kind switch
    {
        WorkKind.GitRead => Interruption.Cancellable,
        WorkKind.Clone => Interruption.Cancellable,
        WorkKind.Export => Interruption.Cancellable,
        WorkKind.Scan => Interruption.Cancellable,

        WorkKind.GitWrite => Interruption.MustFinish,
        WorkKind.GitPush => Interruption.MustFinish,
        WorkKind.Import => Interruption.MustFinish,

        // Deliberately the safe answer. A kind added later is uninterruptible until somebody
        // decides otherwise, so forgetting this switch costs a refusal rather than a
        // half-written project.
        _ => Interruption.MustFinish,
    };
}

/// <summary>One operation in flight, as the guard sees it.</summary>
/// <param name="Kind">What sort of work it is.</param>
/// <param name="Description">The caption already shown beside the progress bar, reused so the
/// dialog and the bar name the same thing in the same words.</param>
/// <param name="Elapsed">How long it has been running. Shown to the user, because "wait" is a
/// reasonable choice only when they can see what they are waiting for.</param>
public readonly record struct ActiveWork(WorkKind Kind, string Description, TimeSpan Elapsed)
{
    public Interruption Interruption => WorkKinds.InterruptionOf(Kind);

    public bool MustFinish => Interruption == Interruption.MustFinish;
}

/// <summary>Everything running at the moment the user asked for something.</summary>
/// <remarks>
/// A snapshot rather than a live view. The guard is pure and must give the same answer twice
/// for the same input; reading a mutable tracker mid-decision would make that untrue.
/// </remarks>
public sealed record WorkSnapshot(IReadOnlyList<ActiveWork> Running)
{
    public static WorkSnapshot Idle { get; } = new([]);

    public static WorkSnapshot Of(params ActiveWork[] running) => new(running);

    public bool IsIdle => Running.Count == 0;

    /// <summary>Work that must be allowed to finish, longest-running first.</summary>
    /// <remarks>
    /// Ordered so the sentence names the operation the user has been waiting on rather than
    /// whichever one happened to start last. The status bar's caption uses the opposite order
    /// on purpose — it answers "what did I just do", and this answers "what is holding me up".
    /// </remarks>
    public IReadOnlyList<ActiveWork> Uninterruptible =>
        [.. Running.Where(w => w.MustFinish).OrderByDescending(w => w.Elapsed)];

    public IReadOnlyList<ActiveWork> Cancellable =>
        [.. Running.Where(w => !w.MustFinish).OrderByDescending(w => w.Elapsed)];

    public bool HasUninterruptible => Running.Any(w => w.MustFinish);
}
