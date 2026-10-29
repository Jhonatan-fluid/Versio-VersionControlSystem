using Versio.Core.Abstractions;
using Versio.Core.Bindings;
using Versio.Core.Locking;

namespace Versio.Core.Plc;

/// <summary>Whether a vendor toolchain currently has a project open.</summary>
public enum OccupancyVerdict
{
    /// <summary>Nobody has it, or the vendor has no way of saying.</summary>
    Free,

    /// <summary>Somebody has it open. Neither direction can run.</summary>
    Held,

    /// <summary>A marker survives from a toolchain that is no longer running.</summary>
    /// <remarks>
    /// Treated as free. The alternative is a crashed IDE blocking every sync until somebody
    /// finds and deletes a file they have never heard of — which is the same reasoning
    /// <see cref="LockRecoveryPolicy"/> applies to Versio's own locks, for the same reason.
    /// </remarks>
    Stale,
}

/// <summary>
/// Who has a project open, if anyone.
/// </summary>
/// <remarks>
/// Vendor-neutral on purpose. CODESYS writes a <c>.~u</c> sidecar carrying user, machine and
/// pid; another toolchain will do something else or nothing at all, and <c>Sync/</c> must not
/// learn the difference.
/// </remarks>
public sealed record ProjectOccupancy
{
    public static readonly ProjectOccupancy Free = new() { Verdict = OccupancyVerdict.Free };

    public required OccupancyVerdict Verdict { get; init; }

    public string User { get; init; } = string.Empty;
    public string Machine { get; init; } = string.Empty;

    /// <summary>Process holding it, when the marker records one.</summary>
    public int? ProcessId { get; init; }

    /// <summary>When the marker was written, when the vendor records that.</summary>
    /// <remarks>
    /// The one fact that lets a person tell a live hold from a leftover. On 2026-10-28 a
    /// TIA Portal marker two days old blocked every export of a project the engineer had
    /// just closed, and the sentence said only "another session" — nothing in it hinted
    /// that the session in question had ended on the 26th.
    /// </remarks>
    public DateTimeOffset? Since { get; init; }

    /// <summary>
    /// Absolute path of the marker that said Held, when a file did. Set by the probe, which
    /// is the only party that knows where it read; the vendor's parser sees contents alone.
    /// </summary>
    /// <remarks>
    /// Named so the refusal can say which file to delete. The stale policy below only
    /// rescues a marker that carries a pid; a vendor whose marker carries none leaves the
    /// person to clear it by hand, and "close it and the export will run" is then advice
    /// that cannot be followed — the project is already closed. The least the sentence can
    /// do is name the file.
    /// </remarks>
    public string? Marker { get; init; }

    /// <summary>True when neither direction can run right now.</summary>
    public bool IsHeld => Verdict == OccupancyVerdict.Held;

    /// <summary>Who has it, for a log line or a panel.</summary>
    public string Owner =>
        (User, Machine) switch
        {
            ("", "") => "another session",
            (var u, "") => u,
            ("", var m) => m,
            var (u, m) => $"{u} on {m}",
        };

    /// <summary><see cref="Owner"/>, and since when where the marker says.</summary>
    public string Holder =>
        Since is { } since
            ? $"{Owner}, since {since.ToUniversalTime():u}"
            : Owner;
}

/// <summary>Asks whether a project is open in its vendor's IDE.</summary>
/// <remarks>
/// An interface so the orchestrator can be handed a stub. The real one reads a file and probes
/// a process, neither of which belongs in a test about the state machine.
/// </remarks>
public interface IProjectOccupancyProbe
{
    /// <param name="binding">Supplies the vendor, which selects the format.</param>
    /// <param name="absoluteProjectPath">The project on disk.</param>
    Task<ProjectOccupancy> CheckAsync(
        ProjectBinding binding, string absoluteProjectPath, CancellationToken ct);
}

/// <summary>
/// Reads a vendor's open-project marker and decides whether to believe it.
/// </summary>
/// <remarks>
/// <para>
/// Split from the format itself because the two halves have different dependencies. Where the
/// marker lives and how to read it is vendor knowledge and belongs behind
/// <see cref="IPlcProjectFormat"/>; whether a marker naming a dead process still counts is a
/// policy every vendor shares, and it needs a clock's worth of outside knowledge — the current
/// machine, and whether a pid is alive.
/// </para>
/// <para>
/// <b>A marker from another machine is always believed.</b> The pid in it means nothing here,
/// and a project on a share really can be open on somebody else's desk. Guessing free would
/// mean two people writing the same project.
/// </para>
/// </remarks>
public sealed class ProjectOccupancyProbe(
    IFileSystem fs,
    IMachineIdentity machine,
    IProcessProbe processes,
    Func<PlcVendor, IPlcProjectFormat?>? formats = null) : IProjectOccupancyProbe
{
    private readonly Func<PlcVendor, IPlcProjectFormat?> _formats =
        formats ?? PlcFormats.ForVendor;

    public async Task<ProjectOccupancy> CheckAsync(
        ProjectBinding binding, string absoluteProjectPath, CancellationToken ct)
    {
        var format = _formats(binding.Vendor);
        if (format?.OccupancyMarkerFor(absoluteProjectPath) is not { } marker)
        {
            // The vendor has no marker, or none is registered. Not knowing is reported as
            // free: refusing to sync a vendor that cannot answer would make the feature a
            // regression for every toolchain but CODESYS.
            return ProjectOccupancy.Free;
        }

        if (!fs.FileExists(marker))
        {
            return ProjectOccupancy.Free;
        }

        string contents;
        try
        {
            contents = await fs.ReadAllTextAsync(marker, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Being written or deleted as we read it — which means an IDE is doing something
            // with this project right now. Held is the safe reading.
            return new ProjectOccupancy { Verdict = OccupancyVerdict.Held, Marker = marker };
        }

        // The file is named on the way out, whatever the vendor made of its contents: the
        // parser does not know where it read from, and the refusal downstream needs to.
        var parsed = format.ParseOccupancy(contents) with { Marker = marker };
        return Resolve(parsed, machine, processes);
    }

    /// <summary>Decide whether a parsed marker still means anything. Pure.</summary>
    public static ProjectOccupancy Resolve(
        ProjectOccupancy parsed, IMachineIdentity machine, IProcessProbe processes)
    {
        if (parsed.Verdict != OccupancyVerdict.Held)
        {
            return parsed;
        }

        // Another machine: the pid is not ours to probe, and the holder is real.
        if (!string.IsNullOrEmpty(parsed.Machine) &&
            !string.Equals(parsed.Machine, machine.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return parsed;
        }

        // No pid recorded, so nothing to check. Believe the marker.
        if (parsed.ProcessId is not { } pid)
        {
            return parsed;
        }

        return processes.IsProcessAlive(pid)
            ? parsed
            : parsed with { Verdict = OccupancyVerdict.Stale };
    }
}
