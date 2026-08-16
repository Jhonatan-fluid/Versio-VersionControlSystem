namespace Versio.Core.Abstractions;

/// <summary>
/// Filesystem access, abstracted so the orchestrator's decisions can be tested without
/// touching a disk.
/// </summary>
/// <remarks>
/// Deliberately small. Every method here is one the sync logic genuinely needs; anything
/// broader would tempt callers in <c>Versio.Core</c> into doing IO reasoning that belongs
/// in <c>Versio.Infrastructure</c>.
/// </remarks>
public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct);

    Task<string> ReadAllTextAsync(string path, CancellationToken ct);

    Task WriteAllTextAsync(string path, string content, CancellationToken ct);

    /// <summary>
    /// Enumerate files beneath a root, recursively, as <b>absolute</b> paths in the
    /// platform's own separator. Empty when the root does not exist.
    /// </summary>
    /// <remarks>
    /// <b>This said "repo-relative with forward slashes" until 2026-08-15, and it never was.</b>
    /// The implementation returns what <c>Directory.EnumerateFiles</c> returns, and every
    /// caller in the repository already relativises for itself — <c>Path.GetRelativePath</c>
    /// in the hash walk, <c>Relative</c> in the backup catalogue — or hands the result
    /// straight back to a method that wants an absolute path, which is why nothing was ever
    /// broken by it. The comment was wrong rather than the code.
    /// <para>
    /// It is corrected rather than left because the next caller is a mirror walk that feeds
    /// <c>IPlcProjectFormat.PlanImport</c>, and that planner keys every decision on
    /// <b>mirror-relative</b> paths taken from the manifest. A caller that believed this
    /// comment would hand it absolute ones, match nothing against the manifest, and plan a
    /// Create for every file in the mirror — a silent, total misplan that looks like a
    /// working import right up until it runs.
    /// </para>
    /// </remarks>
    IEnumerable<string> EnumerateFiles(string root, string searchPattern = "*");

    /// <summary>
    /// Copy a file, retrying while it is share-locked.
    /// </summary>
    /// <remarks>
    /// CODESYS holds the <c>.project</c> open while the engineer works, so the export
    /// pipeline copies it rather than reading it in place — and that copy has to survive a
    /// transient lock rather than failing the whole sync.
    /// </remarks>
    Task CopyAsync(string source, string destination, CancellationToken ct);

    void DeleteFile(string path);

    void CreateDirectory(string path);
}

/// <summary>Persistence for the per-binding direction locks.</summary>
/// <remarks>
/// Separate from <see cref="IFileSystem"/> because the lock file is the one piece of state
/// whose read/write semantics are load-bearing: it must survive a crash, a reboot and a
/// second Versio instance, and its shape is versioned.
/// </remarks>
public interface ILockStore
{
    Task<Locking.LockState?> ReadAsync(CancellationToken ct);

    Task WriteAsync(Locking.LockState state, CancellationToken ct);

    Task ClearAsync(CancellationToken ct);
}

/// <summary>
/// Where the record of Versio's own stashes lives.
/// </summary>
/// <remarks>
/// <para>
/// A sidecar beside the lock state, and for a related reason: <c>git stash list</c> cannot
/// carry this. <c>stash@{n}</c> is positional and renumbers, the subject is a human-readable
/// string a user's own stash can imitate exactly, and a stash carries no sync state at all —
/// so what Versio stashed, on which branch, and which direction locks went with it have to be
/// written down separately.
/// </para>
/// <para>
/// <b>Machine-local.</b> A stash never leaves the clone it was taken in, so a record of one has
/// no business travelling either.
/// </para>
/// </remarks>
public interface IAutoStashStore
{
    /// <summary>
    /// Read the record, or <see cref="Guarding.AutoStashState.Empty"/> when there is none.
    /// </summary>
    /// <remarks>
    /// <b>Never throws.</b> A corrupt sidecar costs the <i>offer</i> to restore, and nothing
    /// else: the stashes themselves are still in git and still listed, so the work is not lost
    /// — it just stops being handed back automatically. Taking the window down over it would
    /// turn a lost convenience into a lost session.
    /// </remarks>
    Task<Guarding.AutoStashState> ReadAsync(CancellationToken ct);

    Task WriteAsync(Guarding.AutoStashState state, CancellationToken ct);
}
