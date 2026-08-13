using Versio.Core.Sync;

namespace Versio.Core.Guarding;

/// <summary>A direction lock currently held by one binding.</summary>
/// <param name="ProjectName">For the sentence. A binding id means nothing to the person reading it.</param>
public readonly record struct HeldLock(string BindingId, string ProjectName, SyncDirection Direction);

/// <summary>The repository state the guard reasons about.</summary>
/// <remarks>
/// Deliberately three lists and nothing else. Everything the guard needs to know about a
/// repository is "what would this action disturb", and that is answered by uncommitted work,
/// unmerged paths, and locks that say a sync is half-finished.
/// </remarks>
public sealed record WorkingTreeSnapshot
{
    public static WorkingTreeSnapshot Clean { get; } = new();

    /// <summary>Repo-relative paths with uncommitted changes, staged or not.</summary>
    public IReadOnlyList<string> DirtyPaths { get; init; } = [];

    /// <summary>Repo-relative paths git reports as unmerged.</summary>
    public IReadOnlyList<string> ConflictedPaths { get; init; } = [];

    /// <summary>Bindings whose direction lock is currently held.</summary>
    public IReadOnlyList<HeldLock> HeldLocks { get; init; } = [];

    public bool HasConflicts => ConflictedPaths.Count > 0;

    public bool IsDirty => DirtyPaths.Count > 0;

    public bool HasHeldLocks => HeldLocks.Count > 0;
}
