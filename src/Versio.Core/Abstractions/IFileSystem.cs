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

    /// <summary>Enumerate files beneath a root, repo-relative with forward slashes.</summary>
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
