namespace Versio.Core.Git;

/// <summary>How far a deletion reaches.</summary>
public enum DeleteScope
{
    /// <summary>This machine only. Anybody else's clone is untouched.</summary>
    Local,

    /// <summary>This machine and the remote.</summary>
    LocalAndRemote,
}

/// <summary>One branch, and whether it may be deleted.</summary>
/// <remarks>
/// <b><see cref="IsMerged"/> is the single most useful fact in the dialog.</b> It is the
/// difference between deleting a tidy-up and deleting somebody's afternoon, and git will refuse
/// an unmerged branch on its own account — a refusal this surfaces per branch rather than
/// escalating past.
/// </remarks>
public sealed record BranchCandidate
{
    public required string Name { get; init; }

    public required BranchExistence Existence { get; init; }

    /// <summary>Whether every commit on it is already on the default branch.</summary>
    public bool IsMerged { get; init; }

    public bool IsCurrent { get; init; }

    /// <summary>True when this is the branch the default-branch resolution named.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Why this branch cannot be ticked at all, or empty when it can.</summary>
    /// <remarks>
    /// The default branch is excluded outright: it is the fallback the current-branch case
    /// checks out, and deleting it has no recovery path inside the application.
    /// </remarks>
    public string Blocked =>
        IsDefault ? "This is the repository's main branch." : string.Empty;

    public bool CanSelect => Blocked.Length == 0;

    /// <summary>True when a remote copy exists to delete.</summary>
    public bool HasRemote => Existence is BranchExistence.Both or BranchExistence.RemoteOnly;

    /// <summary>True when a local copy exists to delete.</summary>
    public bool HasLocal => Existence is BranchExistence.Both or BranchExistence.LocalOnly;
}

/// <summary>
/// What a delete is about to do, in a sentence, before it does it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A confirmation that says "12 branches" without saying which is not a confirmation.</b>
/// This is what the dialog shows while the selection changes, and what the confirmation repeats
/// when the remote is involved.
/// </para>
/// <para>
/// Pure, so every wording is asserted without a repository — which matters more here than
/// almost anywhere else in this application, because the wording is the last thing standing
/// between somebody and a branch that is gone.
/// </para>
/// </remarks>
public sealed record DeletePlan
{
    public required IReadOnlyList<BranchCandidate> Selected { get; init; }

    public required DeleteScope Scope { get; init; }

    /// <summary>Whether the user has acknowledged deleting work that is not merged.</summary>
    public bool ForceAcknowledged { get; init; }

    public int Count => Selected.Count;

    /// <summary>Branches carrying commits the default branch does not have.</summary>
    public IReadOnlyList<BranchCandidate> Unmerged =>
        [.. Selected.Where(b => !b.IsMerged)];

    public bool HasUnmerged => Unmerged.Count > 0;

    /// <summary>True when the branch being deleted is the one checked out.</summary>
    public bool IncludesCurrent => Selected.Any(b => b.IsCurrent);

    /// <summary>True when anything selected has a remote copy the scope would reach.</summary>
    public bool TouchesRemote =>
        Scope == DeleteScope.LocalAndRemote && Selected.Any(b => b.HasRemote);

    /// <summary>
    /// Whether the delete may proceed.
    /// </summary>
    /// <remarks>
    /// <b>Force is a separate, deliberate second press.</b> Git refuses to delete an unmerged
    /// branch, and that refusal is information — so the dialog surfaces it and requires an
    /// acknowledgement rather than quietly escalating <c>-d</c> to <c>-D</c>.
    /// </remarks>
    public bool CanProceed =>
        Count > 0 && (!HasUnmerged || ForceAcknowledged);

    public string BlockedReason =>
        Count == 0 ? "Choose at least one branch."
        : HasUnmerged && !ForceAcknowledged
            ? $"{Describe(Unmerged)} not fully merged. Confirm that you want to lose that work."
            : string.Empty;

    /// <summary>The live summary, as the selection changes.</summary>
    public string Summary
    {
        get
        {
            if (Count == 0)
            {
                return string.Empty;
            }

            var both = Selected.Count(b => b.Existence == BranchExistence.Both);
            var localOnly = Selected.Count(b => b.Existence == BranchExistence.LocalOnly);
            var remoteOnly = Selected.Count(b => b.Existence == BranchExistence.RemoteOnly);

            var parts = new List<string>();

            if (both > 0)
            {
                parts.Add($"{both} local + remote");
            }

            if (localOnly > 0)
            {
                parts.Add($"{localOnly} local only");
            }

            if (remoteOnly > 0)
            {
                parts.Add($"{remoteOnly} remote only");
            }

            var head = Count == 1 ? "1 branch selected" : $"{Count} branches selected";
            var tail = parts.Count > 0 ? $" — {string.Join(", ", parts)}." : ".";
            var warn = HasUnmerged ? $" {Describe(Unmerged)} not merged." : string.Empty;

            return head + tail + warn;
        }
    }

    /// <summary>
    /// The confirmation for a delete that reaches the remote.
    /// </summary>
    /// <remarks>
    /// <b>Names every branch, up to five, then counts the rest.</b> And it says what it means
    /// for other people, because a remote delete is the one operation here whose consequences
    /// land on somebody who is not in the room.
    /// </remarks>
    public string RemoteConfirmation(string remote)
    {
        var names = Selected.Where(b => b.HasRemote).Select(b => b.Name).ToList();
        var shown = string.Join("\n", names.Take(5).Select(n => $"  • {n}"));
        var more = names.Count > 5 ? $"\n  …and {names.Count - 5} more" : string.Empty;

        return $"{shown}{more}\n\n" +
               $"These are deleted from {remote} as well as from this machine.\n\n" +
               "Anybody who has not fetched will keep a stale copy; anybody who has will lose " +
               "theirs.";
    }

    private static string Describe(IReadOnlyList<BranchCandidate> branches) =>
        branches.Count == 1
            ? $"{branches[0].Name} is"
            : $"{branches.Count} of them are";
}

/// <summary>What happened to one branch, so a partial failure can be reported per branch.</summary>
/// <remarks>
/// <b>A single "some branches could not be deleted" is useless.</b> Deleting five where the
/// third fails must say which three succeeded, which failed, and why — otherwise the user has
/// to go and look, which is the trip to the terminal this panel exists to remove.
/// </remarks>
public sealed record DeleteOutcome
{
    public required string Branch { get; init; }

    public bool LocalDeleted { get; init; }

    public bool RemoteDeleted { get; init; }

    /// <summary>Git's own words when something refused. Empty on success.</summary>
    public string Failure { get; init; } = string.Empty;

    public bool Succeeded => Failure.Length == 0;
}
