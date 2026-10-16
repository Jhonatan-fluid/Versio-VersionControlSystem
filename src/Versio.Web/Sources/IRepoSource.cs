namespace Versio.Web.Sources;

/// <summary>Whose credential a read is made with.</summary>
public enum AccessKind
{
    /// <summary>No token: GitHub's anonymous access, public repositories only.</summary>
    Anonymous,

    /// <summary>The signed-in user's own token: what they can see, intersected with the App's installation.</summary>
    User,

    /// <summary>The App's installation token, used only anonymously and only for a repository GitHub says is public.</summary>
    Installation,
}

/// <summary>A credential for one request. The token is never logged and never leaves the server.</summary>
public sealed record RepoAccess(AccessKind Kind, string? Token, string Who)
{
    public static readonly RepoAccess Anonymous = new(AccessKind.Anonymous, null, "anonymous");

    /// <summary>What the log may say about this access: who, and how, never the token.</summary>
    public override string ToString() => $"{Who} ({Kind})";
}

/// <summary>Why a read did not return what was asked for.</summary>
public enum RepoFailure
{
    None,

    /// <summary>Missing, or not visible to this credential. GitHub does not say which, and neither do we.</summary>
    NotFound,

    /// <summary>The token was refused: expired or revoked. The page sends the user to sign in.</summary>
    Unauthorized,

    /// <summary>The organisation enforces SAML SSO and this token is not authorised for it.</summary>
    SsoRequired,

    /// <summary>GitHub's rate limit for this credential is spent.</summary>
    RateLimited,

    /// <summary>Too large to fetch at all (<see cref="WebOptions.MaxBlobBytes"/>).</summary>
    TooLarge,

    /// <summary>GitHub failed or could not be reached.</summary>
    Upstream,
}

/// <summary>A read's outcome: a value, or why there is none.</summary>
public sealed record RepoResult<T>(T? Value, RepoFailure Failure, string? Detail = null)
{
    public bool Ok => Failure == RepoFailure.None && Value is not null;

    public static RepoResult<T> Of(T value) => new(value, RepoFailure.None);

    public static RepoResult<T> Fail(RepoFailure failure, string? detail = null) => new(default, failure, detail);

    public RepoResult<TOther> As<TOther>() => new(default, Failure, Detail);
}

public sealed record RepoInfo(string Owner, string Name, bool Private, string DefaultBranch);

public sealed record BranchInfo(string Name, string CommitSha);

/// <summary>A repository's branches, and whether there were more than were listed.</summary>
public sealed record BranchList(IReadOnlyList<BranchInfo> Branches, bool Truncated);

/// <summary>One file in a tree: its path, its blob, its size.</summary>
public sealed record TreeEntry(string Path, string BlobSha, long Size);

/// <summary>A commit's whole tree, flat.</summary>
/// <param name="Truncated">GitHub stopped listing (over 100,000 entries); the page says the tree is partial.</param>
public sealed record TreeSnapshot(string CommitSha, IReadOnlyList<TreeEntry> Files, bool Truncated);

public sealed record CommitInfo(string Sha, string Message, string Author, DateTimeOffset When);

/// <summary>
/// An open pull request as the list shows it. <paramref name="HeadSha"/> rather than a head ref
/// for comparing: the head branch may live in a fork, where its NAME means nothing in this
/// repository, but its commit is reachable here through the pull request itself.
/// </summary>
public sealed record PullInfo(int Number, string Title, string Author, DateTimeOffset Updated, string BaseRef, string HeadRef, string HeadSha, bool Draft);

/// <summary>One pull request with its description (Markdown, possibly empty).</summary>
public sealed record PullDetail(PullInfo Pull, string Body);

/// <summary>A published release as the list shows it. Drafts are never listed.</summary>
public sealed record ReleaseInfo(string Tag, string Name, string Author, DateTimeOffset When, bool Prerelease);

/// <summary>One release with its notes (Markdown, possibly empty).</summary>
public sealed record ReleaseDetail(ReleaseInfo Release, string Body);

/// <summary>
/// Where the viewer reads repositories from. GitHub first; GitHub Enterprise Server is the same
/// implementation with another base URL; GitLab and Azure DevOps would be new implementations,
/// with nothing above this interface changing (docs/web/PLAN.md).
/// </summary>
/// <remarks>
/// Every method takes the credential explicitly. There is no ambient token, so a read cannot
/// quietly fall back to a more powerful credential than the one its caller resolved for this
/// person and this repository (security rule 1).
/// </remarks>
public interface IRepoSource
{
    Task<RepoResult<RepoInfo>> GetRepoAsync(RepoAccess access, string owner, string repo, CancellationToken ct);

    Task<RepoResult<BranchList>> GetBranchesAsync(RepoAccess access, string owner, string repo, CancellationToken ct);

    /// <summary>The commit a branch, tag or SHA names, as this credential sees it.</summary>
    Task<RepoResult<string>> ResolveCommitAsync(RepoAccess access, string owner, string repo, string reference, CancellationToken ct);

    /// <summary>The tree of a commit already resolved with <see cref="ResolveCommitAsync"/>.</summary>
    Task<RepoResult<TreeSnapshot>> GetTreeAsync(RepoAccess access, string owner, string repo, string commitSha, CancellationToken ct);

    /// <summary>A blob's bytes, with Git LFS pointers followed to their content.</summary>
    Task<RepoResult<byte[]>> GetBlobAsync(RepoAccess access, string owner, string repo, string blobSha, long maxBytes, CancellationToken ct);

    Task<RepoResult<IReadOnlyList<CommitInfo>>> GetCommitsAsync(RepoAccess access, string owner, string repo, string reference, string? path, int limit, CancellationToken ct);

    /// <summary>The open pull requests, most recently updated first, up to <paramref name="limit"/>.</summary>
    Task<RepoResult<IReadOnlyList<PullInfo>>> GetPullsAsync(RepoAccess access, string owner, string repo, int limit, CancellationToken ct);

    /// <summary>One pull request, open or not, with its description.</summary>
    Task<RepoResult<PullDetail>> GetPullAsync(RepoAccess access, string owner, string repo, int number, CancellationToken ct);

    /// <summary>The published releases, newest first, up to <paramref name="limit"/>.</summary>
    Task<RepoResult<IReadOnlyList<ReleaseInfo>>> GetReleasesAsync(RepoAccess access, string owner, string repo, int limit, CancellationToken ct);

    /// <summary>One release by its tag, with its notes.</summary>
    Task<RepoResult<ReleaseDetail>> GetReleaseAsync(RepoAccess access, string owner, string repo, string tag, CancellationToken ct);
}
