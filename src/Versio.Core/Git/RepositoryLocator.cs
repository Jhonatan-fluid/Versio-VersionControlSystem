namespace Versio.Core.Git;

/// <summary>What a candidate folder turned out to be.</summary>
public enum RepositoryKind
{
    /// <summary>The folder is the working tree root — it directly contains <c>.git</c>.</summary>
    WorkingTreeRoot,

    /// <summary>The folder sits inside a repository; the root is further up.</summary>
    InsideWorkingTree,

    /// <summary>
    /// A linked worktree or a submodule: <c>.git</c> is a <b>file</b> containing a
    /// <c>gitdir:</c> pointer rather than a directory.
    /// </summary>
    LinkedWorkingTree,

    /// <summary>A bare repository — real git data, but no working tree to diff against.</summary>
    Bare,

    /// <summary>No repository anywhere up the chain.</summary>
    NotARepository,

    /// <summary>The path does not exist or cannot be read.</summary>
    Inaccessible,
}

public sealed record RepositoryLocation
{
    public required RepositoryKind Kind { get; init; }

    /// <summary>Working tree root, when there is one. This — not the folder the user
    /// picked — is what every git command should run against.</summary>
    public string? WorkingTreeRoot { get; init; }

    /// <summary>Path of the <c>.git</c> directory or file that was found.</summary>
    public string? GitPath { get; init; }

    /// <summary>The folder the user actually chose, kept for the message.</summary>
    public required string RequestedPath { get; init; }

    /// <summary>True when Versio can operate here.</summary>
    public bool IsUsable =>
        Kind is RepositoryKind.WorkingTreeRoot
             or RepositoryKind.InsideWorkingTree
             or RepositoryKind.LinkedWorkingTree;

    /// <summary>One sentence a user can act on, never a raw error.</summary>
    public string Explanation => Kind switch
    {
        RepositoryKind.WorkingTreeRoot =>
            "Git repository.",
        RepositoryKind.InsideWorkingTree =>
            $"This folder is inside a repository. Opening its root instead: {WorkingTreeRoot}",
        RepositoryKind.LinkedWorkingTree =>
            "Git worktree.",
        RepositoryKind.Bare =>
            "This is a bare repository. It has no working tree, so there are no files to " +
            "compare — Versio needs a normal clone.",
        RepositoryKind.NotARepository =>
            "This folder is not inside a Git repository. Versio works entirely from Git " +
            "history, so it needs one — clone a repository, or run 'git init' here first.",
        RepositoryKind.Inaccessible =>
            "That folder could not be opened.",
        _ => "Unknown state.",
    };
}

/// <summary>Filesystem probes the locator needs. Abstracted so the walk is unit testable.</summary>
public interface IRepositoryProbe
{
    bool DirectoryExists(string path);

    bool FileExists(string path);

    /// <summary>First line of a file, or null. Used to read a <c>.git</c> pointer file.</summary>
    string? ReadFirstLine(string path);
}

/// <summary>
/// Walks up from a chosen folder to find the Git working tree it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Versio is built entirely on Git.</b> Branches, history, the diff, the direction lock
/// releasing on push — none of it means anything outside a repository. Opening a plain
/// folder would produce an app where every control is present and none of them work, which
/// is worse than refusing.
/// </para>
/// <para>
/// The walk exists because people select the folder they were looking at, not the root.
/// Picking <c>repo/src/plc</c> should open <c>repo</c>, exactly as <c>git status</c> would.
/// </para>
/// <para>
/// Three cases are handled that a naive <c>Directory.Exists(".git")</c> check gets wrong:
/// a <b>linked worktree or submodule</b>, where <c>.git</c> is a file holding a
/// <c>gitdir:</c> pointer; a <b>bare repository</b>, which has real git data but no working
/// tree to diff against; and a <b>subdirectory</b>, which is the common case.
/// </para>
/// </remarks>
public sealed class RepositoryLocator(IRepositoryProbe probe)
{
    /// <summary>Guards against a pathological path; real trees are nowhere near this deep.</summary>
    private const int MaxDepth = 64;

    public RepositoryLocation Locate(string? path)
    {
        // Normalise BEFORE probing. Checking the raw path first means "C:\repo\" is tested
        // as a different string from "C:\repo", which a strict probe reports as missing.
        var requested = string.IsNullOrWhiteSpace(path) ? string.Empty : Normalize(path);

        if (requested.Length == 0 || !probe.DirectoryExists(requested))
        {
            return new RepositoryLocation
            {
                Kind = RepositoryKind.Inaccessible,
                RequestedPath = path ?? string.Empty,
            };
        }

        var current = requested;

        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var dotGit = Path.Combine(current, ".git");

            if (probe.DirectoryExists(dotGit))
            {
                return new RepositoryLocation
                {
                    Kind = current == requested
                        ? RepositoryKind.WorkingTreeRoot
                        : RepositoryKind.InsideWorkingTree,
                    WorkingTreeRoot = current,
                    GitPath = dotGit,
                    RequestedPath = requested,
                };
            }

            // A linked worktree or a submodule stores a pointer file, not a directory.
            if (probe.FileExists(dotGit))
            {
                var first = probe.ReadFirstLine(dotGit);
                if (first?.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new RepositoryLocation
                    {
                        Kind = RepositoryKind.LinkedWorkingTree,
                        WorkingTreeRoot = current,
                        GitPath = first["gitdir:".Length..].Trim(),
                        RequestedPath = requested,
                    };
                }
            }

            // A bare repository has the git internals at the top level and no working tree.
            // It must be reported distinctly: "not a repository" would be a lie, and the
            // user would keep trying.
            if (IsBare(current))
            {
                return new RepositoryLocation
                {
                    Kind = RepositoryKind.Bare,
                    GitPath = current,
                    RequestedPath = requested,
                };
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                break;      // reached a drive root
            }

            current = parent;
        }

        return new RepositoryLocation
        {
            Kind = RepositoryKind.NotARepository,
            RequestedPath = requested,
        };
    }

    private bool IsBare(string path) =>
        probe.FileExists(Path.Combine(path, "HEAD")) &&
        probe.DirectoryExists(Path.Combine(path, "objects")) &&
        probe.DirectoryExists(Path.Combine(path, "refs"));

    /// <summary>Trim trailing separators so a path compares equal to its walked form —
    /// otherwise <c>C:\repo\</c> and <c>C:\repo</c> look like different folders and the
    /// root is misreported as a subdirectory.</summary>
    private static string Normalize(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is { Length: > 0 } t
            ? t
            : path;
}
