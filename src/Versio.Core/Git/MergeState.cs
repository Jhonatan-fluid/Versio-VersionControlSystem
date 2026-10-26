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

    /// <summary>
    /// Unmerged paths with no merge or rebase behind them: a stash apply (or a cherry-pick
    /// or revert) stopped at a conflict.
    /// </summary>
    /// <remarks>
    /// Not read from a marker file — there is none to read for a stash apply — but from the
    /// status, by the shell, once the marker files have said <see cref="None"/>. The state
    /// exists because without it a conflicted stash apply read as "nothing happening": the
    /// files moved into the conflicted section, the commit button said "Resolve the
    /// conflicts first", and the section with the Keep buttons was hidden behind a
    /// merge-or-rebase check. The 2026-10-24 review's item 30. There is nothing to finish
    /// and nothing to abandon: resolving every file ends it, and git's <c>--ours</c> is this
    /// branch while <c>--theirs</c> is what was being applied.
    /// </remarks>
    Conflicts,
}
