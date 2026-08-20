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

    /// <summary>
    /// Read a file's bytes, retrying while it is share-locked.
    /// </summary>
    /// <remarks>
    /// Hashing a directory payload includes the pointer file, and the toolchain that just
    /// exported may still hold it after the child process has exited. Exclusive
    /// <c>File.ReadAllBytes</c> is how that became a failed session rather than a hash.
    /// </remarks>
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

    /// <summary>
    /// Copy a directory tree, creating the destination and retrying share-locked files.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="CopyAsync"/> for a project that is a directory rather
    /// than a file. Without this the orchestrator can only copy one name, and a tree
    /// payload would lose everything but the pointer sitting at the top of it.
    /// </remarks>
    Task CopyTreeAsync(string source, string destination, CancellationToken ct);

    /// <summary>
    /// Immediate child directories of <paramref name="root"/>, as absolute paths.
    /// Empty when the root does not exist.
    /// </summary>
    /// <remarks>
    /// Non-recursive on purpose. The backup folder holds one copy per import, each of
    /// which may itself be a tree; walking files underneath those copies is how a prune
    /// would delete the inside of the newest backup and leave its name looking valid.
    /// </remarks>
    IEnumerable<string> EnumerateDirectories(string root);

    void DeleteFile(string path);

    /// <summary>
    /// Remove a directory and everything beneath it. No-op when it is not there.
    /// </summary>
    /// <remarks>
    /// Added because a tree-payload scratch copy and a tree backup cannot be cleared by
    /// deleting files one at a time without leaving the folders behind, and because the
    /// backup pruner has to drop a whole copy rather than the files inside the newest
    /// one. Nothing outside those two jobs should reach for this.
    /// </remarks>
    void DeleteDirectory(string path);

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
