using Versio.Core.Bindings;

namespace Versio.Core.Sync;

/// <summary>Whether a commit may proceed, and what has to happen first if not.</summary>
public enum GateDecision
{
    /// <summary>Nothing stale; the commit may proceed untouched.</summary>
    Proceed,

    /// <summary>One or more bindings in the include set are stale; a blocking export must run first.</summary>
    ExportRequired,

    /// <summary>Something is wrong that an export will not fix, and nothing else is left to commit.</summary>
    Blocked,

    /// <summary>
    /// A failed project owns some of the included files, but not all of them.
    /// Ask before dropping those files and committing the rest.
    /// </summary>
    ExcludeAndAsk,
}

/// <summary>The gate verdict, carrying the sentence shown to the user.</summary>
public sealed record CommitGateResult
{
    public required GateDecision Decision { get; init; }
    public IReadOnlyList<string> StaleBindings { get; init; } = [];
    public IReadOnlyList<string> FailedBindings { get; init; } = [];
    public IReadOnlyList<string> FailedNames { get; init; } = [];

    /// <summary>
    /// Included paths owned by a failed binding. Empty unless
    /// <see cref="GateDecision.ExcludeAndAsk"/> or a scoped <see cref="GateDecision.Blocked"/>.
    /// </summary>
    public IReadOnlyList<string> BlockedPaths { get; init; } = [];

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

    /// <summary>Repo-relative project file. Empty in callers that have not scoped the gate yet.</summary>
    public string ProjectPath { get; init; } = string.Empty;

    /// <summary>Repo-relative export tree. Empty in callers that have not scoped the gate yet.</summary>
    public string ExportPath { get; init; } = string.Empty;

    /// <summary>What to call this project in a sentence. Binding id if nobody supplied a name.</summary>
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// Refuses a commit while any <i>included</i> binding's export is known-stale.
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
/// A <b>backstop</b>, and deliberately not the trigger. Exports follow a detected change, so
/// the mirror is normally current long before a commit — reviewing a diff that only appears
/// once you have committed is reviewing it too late. What this catches is the residue: a
/// dropped watcher event, a save still inside the throttle window, a project adopted while no
/// toolchain was available.
/// </para>
/// <para>
/// <b>Scoped to the files the commit is about to record.</b> Unscoped — <c>includePaths</c>
/// null — still looks at every enabled binding, which is what the staleness panel and the
/// catch-up need. A commit that names its files must not export, or refuse, a project those
/// files do not belong to: committing a README next to a failed import used to block the
/// whole repository, which is the same over-reach discard already refuses (an unrelated file
/// must not take a project with it).
/// </para>
/// </remarks>
public static class CommitGate
{
    public static CommitGateResult Evaluate(
        IReadOnlyList<BindingFreshness> bindings,
        IReadOnlyList<string>? includePaths = null)
    {
        var enabled = bindings.Where(b => b.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return new CommitGateResult
            {
                Decision = GateDecision.Proceed,
                Explanation = "no PLC projects are tracked in this repository",
            };
        }

        var relevant = includePaths is null
            ? enabled
            : enabled.Where(b => includePaths.Any(p => Owns(b, p))).ToList();

        if (includePaths is not null && relevant.Count == 0)
        {
            return new CommitGateResult
            {
                Decision = GateDecision.Proceed,
                Explanation = "no tracked project is part of this commit",
            };
        }

        var failed = relevant.Where(b => b.InFailedState).ToList();
        if (failed.Count > 0)
        {
            var names = failed.Select(DisplayName).ToList();
            var ids = failed.Select(b => b.BindingId).ToList();

            // Unscoped: the staleness panel, the catch-up. A failed binding still blocks
            // the whole answer because those callers have no include set to shrink.
            if (includePaths is null)
            {
                return new CommitGateResult
                {
                    Decision = GateDecision.Blocked,
                    FailedBindings = ids,
                    FailedNames = names,
                    StaleBindings = ids,
                    Explanation = failed.Count == 1
                        ? "the last sync for this project failed; acknowledge the failure before committing"
                        : $"{failed.Count} projects have a failed sync; acknowledge them before committing",
                };
            }

            var blocked = includePaths.Where(p => failed.Any(b => Owns(b, p))).ToList();
            var remaining = includePaths.Count - blocked.Count;

            if (remaining <= 0)
            {
                return new CommitGateResult
                {
                    Decision = GateDecision.Blocked,
                    FailedBindings = ids,
                    FailedNames = names,
                    BlockedPaths = blocked,
                    StaleBindings = ids,
                    Explanation = failed.Count == 1
                        ? "the last sync for this project failed; acknowledge the failure before committing"
                        : $"{failed.Count} projects have a failed sync; acknowledge them before committing",
                };
            }

            var who = failed.Count == 1
                ? names[0]
                : $"{failed.Count} projects";

            return new CommitGateResult
            {
                Decision = GateDecision.ExcludeAndAsk,
                FailedBindings = ids,
                FailedNames = names,
                BlockedPaths = blocked,
                Explanation = failed.Count == 1
                    ? $"{who} failed to sync; those files can be left out so the rest still commit"
                    : $"{who} failed to sync; those files can be left out so the rest still commit",
            };
        }

        var stale = relevant
            .Where(b => !string.Equals(b.CurrentProjectHash, b.ExportedProjectHash, StringComparison.Ordinal))
            .Select(b => b.BindingId)
            .ToList();

        if (stale.Count == 0)
        {
            return new CommitGateResult
            {
                Decision = GateDecision.Proceed,
                Explanation = includePaths is null
                    ? "every tracked project is exported and up to date"
                    : "every project in this commit is exported and up to date",
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

    /// <summary>
    /// Does this binding's project or mirror contain the given repo-relative path?
    /// </summary>
    /// <remarks>
    /// The project is matched exactly and the mirror by prefix, in that order, and the prefix
    /// carries its separator: <c>plc/A.project</c> sits beside <c>plc/A</c>, and a careless
    /// prefix test would have the mirror claim the project. Same rule the orchestrator uses
    /// when deciding whether a conflict belongs to a binding.
    /// </remarks>
    public static bool Owns(BindingFreshness binding, string repoRelativePath)
    {
        var path = repoRelativePath.Replace('\\', '/');
        var project = binding.ProjectPath.Replace('\\', '/');
        var export = binding.ExportPath.Replace('\\', '/').TrimEnd('/');

        return (project.Length > 0
                && string.Equals(path, project, StringComparison.OrdinalIgnoreCase))
            || (export.Length > 0
                && path.StartsWith(export + "/", StringComparison.OrdinalIgnoreCase));
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
                ProjectPath = binding.ProjectPath,
                ExportPath = binding.ExportPath,
            },
        ]);

    private static string DisplayName(BindingFreshness binding) =>
        binding.Name.Length > 0 ? binding.Name : binding.BindingId;
}
