using Versio.Core.Bindings;

namespace Versio.Core.Sync;

public enum GateDecision
{
    /// <summary>Nothing stale; the commit may proceed untouched.</summary>
    Proceed,

    /// <summary>One or more bindings are stale; a blocking export must run first.</summary>
    ExportRequired,

    /// <summary>Something is wrong that an export will not fix.</summary>
    Blocked,
}

public sealed record CommitGateResult
{
    public required GateDecision Decision { get; init; }
    public IReadOnlyList<string> StaleBindings { get; init; } = [];
    public required string Explanation { get; init; }

    public bool CanCommit => Decision == GateDecision.Proceed;
}

/// <summary>Everything the gate needs to know about one binding, supplied by the caller.</summary>
public sealed record BindingFreshness
{
    public required string BindingId { get; init; }

    /// <summary>Hash of the .project as it is on disk right now.</summary>
    public required string CurrentProjectHash { get; init; }

    /// <summary>Hash recorded when the export tree was last written.</summary>
    public required string ExportedProjectHash { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Set when the last sync failed; an export will not clear that by itself.</summary>
    public bool InFailedState { get; init; }
}

/// <summary>
/// Refuses a commit while any binding's export is known-stale.
/// </summary>
/// <remarks>
/// <para>
/// This is what converts change-detection latency from a <b>correctness</b> problem into a
/// <b>UX</b> problem. Watchers drop events — the buffer overflows, network and
/// cloud-synced paths misbehave, antivirus interferes — so nothing in the design may
/// depend on having seen every save. The worst case of a completely broken watcher becomes
/// "the diff appears when I commit rather than when I saved": annoying, never wrong.
/// </para>
/// <para>
/// It matters more since <c>On git action</c> became the default sync mode: with no
/// export-on-save, this is not a safety net but the <b>primary</b> path by which the export
/// tree gets written at all.
/// </para>
/// <para>
/// It checks <b>every enabled binding</b>, not just the one last looked at. Committing with
/// project B stale while looking at project A is exactly the mistake it exists to prevent.
/// </para>
/// </remarks>
public static class CommitGate
{
    public static CommitGateResult Evaluate(IReadOnlyList<BindingFreshness> bindings)
    {
        var enabled = bindings.Where(b => b.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return new CommitGateResult
            {
                Decision = GateDecision.Proceed,
                Explanation = "no CODESYS projects are tracked in this repository",
            };
        }

        var failed = enabled.Where(b => b.InFailedState).Select(b => b.BindingId).ToList();
        if (failed.Count > 0)
        {
            // Re-running an export will not clear a failure that needs acknowledging, so
            // offering to do so would just loop.
            return new CommitGateResult
            {
                Decision = GateDecision.Blocked,
                StaleBindings = failed,
                Explanation = failed.Count == 1
                    ? "the last sync for this project failed; acknowledge the failure before committing"
                    : $"{failed.Count} projects have a failed sync; acknowledge them before committing",
            };
        }

        var stale = enabled
            .Where(b => !string.Equals(b.CurrentProjectHash, b.ExportedProjectHash, StringComparison.Ordinal))
            .Select(b => b.BindingId)
            .ToList();

        if (stale.Count == 0)
        {
            return new CommitGateResult
            {
                Decision = GateDecision.Proceed,
                Explanation = "every tracked project is exported and up to date",
            };
        }

        return new CommitGateResult
        {
            Decision = GateDecision.ExportRequired,
            StaleBindings = stale,
            Explanation = stale.Count == 1
                ? "the project has changed since it was last exported; exporting before commit"
                : $"{stale.Count} projects have changed since they were last exported; exporting before commit",
        };
    }

    /// <summary>Convenience overload for the common single-binding case.</summary>
    public static CommitGateResult Evaluate(ProjectBinding binding, string currentHash, string exportedHash) =>
        Evaluate([
            new BindingFreshness
            {
                BindingId = binding.Id,
                CurrentProjectHash = currentHash,
                ExportedProjectHash = exportedHash,
                Enabled = binding.Enabled,
            },
        ]);
}
