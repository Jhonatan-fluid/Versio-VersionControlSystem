namespace Versio.Core.Git;

/// <summary>What happened to a file, from git's point of view.</summary>
public enum ChangeKind
{
    Unmodified = 0,
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    Untracked,
    Ignored,
    Conflicted,
}

/// <summary>
/// One changed path.
/// </summary>
/// <remarks>
/// Staged and unstaged state are tracked separately because git tracks them separately: a
/// file can be staged-modified and unstaged-modified at once, and collapsing that loses
/// information the user acts on.
/// </remarks>
public sealed record FileChange
{
    public required string Path { get; init; }
    public required ChangeKind Staged { get; init; }
    public required ChangeKind Unstaged { get; init; }

    /// <summary>Previous path for a rename or copy.</summary>
    public string? OriginalPath { get; init; }

    /// <summary>Rename/copy similarity, 0-100.</summary>
    public int? SimilarityScore { get; init; }

    public bool IsStaged => Staged is not (ChangeKind.Unmodified or ChangeKind.Untracked);
    public bool IsConflicted => Staged == ChangeKind.Conflicted || Unstaged == ChangeKind.Conflicted;

    /// <summary>Single glyph for the tree: staged state wins when both are set.</summary>
    public ChangeKind Effective =>
        IsConflicted ? ChangeKind.Conflicted
        : Staged is not ChangeKind.Unmodified ? Staged
        : Unstaged;
}

/// <summary>One branch, with its upstream and how far it has diverged from it.</summary>
public sealed record BranchInfo
{
    public required string Name { get; init; }
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public bool IsDetached { get; init; }
    public bool IsCurrent { get; init; }

    /// <summary>True for a repo with no commits yet, where HEAD points nowhere.</summary>
    public bool IsUnborn { get; init; }

    /// <summary>
    /// Whether this branch is on this machine, on the remote, or both.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="BranchExistence.LocalOnly"/> because that is what a
    /// <c>refs/heads</c> read alone can establish. <see cref="BranchInventory.Combine"/> is what
    /// upgrades it, and until this existed a branch that lived only on the remote was invisible
    /// to the application entirely.
    /// </remarks>
    public BranchExistence Existence { get; init; } = BranchExistence.LocalOnly;

    /// <summary>The remote-tracking ref for a branch not checked out here, e.g.
    /// <c>origin/feature/x</c>. Empty otherwise.</summary>
    public string RemoteRef { get; init; } = string.Empty;

    /// <summary>True when checking this out would create the local branch.</summary>
    public bool IsRemoteOnly => Existence == BranchExistence.RemoteOnly;

    /// <summary>True when nobody else can see this branch.</summary>
    public bool IsLocalOnly => Existence == BranchExistence.LocalOnly;
}

/// <summary>The working tree as porcelain v2 reported it: current branch, plus every change.</summary>
public sealed record GitStatus
{
    public required BranchInfo Branch { get; init; }
    public IReadOnlyList<FileChange> Changes { get; init; } = [];

    public bool IsClean => Changes.Count == 0;
    public bool HasConflicts => Changes.Any(c => c.IsConflicted);
    public bool HasStagedChanges => Changes.Any(c => c.IsStaged);

    public IEnumerable<FileChange> Staged => Changes.Where(c => c.IsStaged);
    public IEnumerable<FileChange> Unstaged =>
        Changes.Where(c => c.Unstaged is not ChangeKind.Unmodified);
}

/// <summary>The raw result of one <c>git</c> invocation.</summary>
public sealed record GitResult
{
    public required bool Success { get; init; }
    public required int ExitCode { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }

    /// <summary>Best available explanation for a failure, preferring git's own words.</summary>
    public string? FailureReason
    {
        get
        {
            if (Success)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(StdErr))
            {
                return $"git exited with code {ExitCode}";
            }

            var lines = StdErr.Trim()
                              .Split('\n')
                              .Select(l => l.Trim())
                              .Where(l => l.Length > 0)
                              .ToList();

            // The last line that is not advice. Git frequently appends `hint:` lines AFTER the
            // error, and taking the last line outright surfaced those instead - so refusing to
            // delete a branch reported as:
            //
            //     hint: Disable this message with "git config advice.forceDeleteBranch false"
            //
            // rather than "the branch 'tidy' is not fully merged". This message goes straight
            // into a notification the user reads, so the wrong line is the whole failure as far
            // as they are concerned.
            //
            // `warning:` is skipped for the same reason: git emits one before the error it is
            // explaining, and the error is the more useful of the two.
            var meaningful = lines.LastOrDefault(
                l => !l.StartsWith("hint:", StringComparison.OrdinalIgnoreCase)
                     && !l.StartsWith("warning:", StringComparison.OrdinalIgnoreCase));

            // Everything git said was advice. Better its own words than an exit code.
            return meaningful ?? lines[^1];
        }
    }
}

/// <summary>One progress report parsed out of git stderr, such as receiving objects at 47%.</summary>
public readonly record struct GitProgress(string Phase, int? Percent, string Raw);

/// <summary>Constants for driving <c>git diff</c>.</summary>
public static class GitDiff
{
    /// <summary>
    /// Context large enough that no real file has more lines than this.
    /// </summary>
    /// <remarks>
    /// Git has no "whole file" switch; the idiom is an unreasonably large <c>-U</c>. A
    /// million lines is far past any plausible exported POU — the largest object in the
    /// fixture project is under two thousand — and asking for more context than a file
    /// contains is not an error, it simply yields the file.
    /// </remarks>
    public const int FullFileContext = 1_000_000;

    /// <summary>What git shows when nobody asks for anything else.</summary>
    public const int DefaultContext = 3;
}
