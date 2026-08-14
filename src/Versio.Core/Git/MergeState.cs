namespace Versio.Core.Git;

/// <summary>Whether the repository is part-way through something that can be finished or undone.</summary>
/// <remarks>
/// <para>
/// Read from the marker files git leaves in <c>.git</c> rather than inferred from the presence
/// of unmerged paths. The two are not the same: resolving every conflict leaves the tree clean
/// while the merge is still <i>in progress</i> and waiting for a commit, and a panel that read
/// "no conflicts" as "nothing happening" would offer a plain commit for what is actually the
/// conclusion of a merge.
/// </para>
/// <para>
/// A rebase has two marker directories depending on how it was started — <c>rebase-merge</c> for
/// the interactive machinery, <c>rebase-apply</c> for the older patch-based path. Both mean the
/// same thing to a user and to everything here.
/// </para>
/// </remarks>
public enum MergeState
{
    /// <summary>Nothing in progress.</summary>
    None,

    /// <summary>A merge is waiting to be committed or aborted.</summary>
    Merging,

    /// <summary>A rebase is part-way through.</summary>
    Rebasing,
}
