using Versio.Core.Git;

namespace Versio.Core.Abstractions;

/// <summary>
/// Everything Versio needs from git.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design revision (Rev 9): implemented entirely over <c>git.exe</c>, not LibGit2Sharp.</b>
/// The plan originally specified a hybrid — LibGit2Sharp for reads, the CLI for network
/// operations. Building it, three things argued against the split:
/// </para>
/// <list type="number">
///   <item><b>It removes R19 outright.</b> LibGit2Sharp carries native <c>git2-*.dll</c>
///     binaries, which are the documented way <c>PublishSingleFile</c> breaks. A pure-CLI
///     client has no native payload at all.</item>
///   <item><b>git.exe is already a hard dependency.</b> PortableGit is vendored for
///     credentials, LFS and network operations regardless, so the CLI costs nothing extra
///     while LibGit2Sharp adds a second thing to keep in step with it.</item>
///   <item><b>Porcelain v2 is a stable machine format</b>, explicitly guaranteed across
///     versions. Parsing it is a bounded, testable problem — and it is now covered by
///     unit tests over real output shapes, which is more verification than the
///     LibGit2Sharp path would have had.</item>
/// </list>
/// <para>
/// The cost is a process spawn per status refresh (~20–50 ms). That is irrelevant next to
/// a 17–31 s CODESYS export, and the debounced refresh means it does not run in a loop.
/// If it ever becomes a problem, this interface is the seam to swap behind.
/// </para>
/// </remarks>
public interface IGitClient
{
    Task<bool> IsRepositoryAsync(CancellationToken ct);

    Task<GitStatus> StatusAsync(CancellationToken ct);

    Task<IReadOnlyList<BranchInfo>> BranchesAsync(CancellationToken ct);

    Task<GitResult> StageAsync(IReadOnlyList<string> paths, CancellationToken ct);

    Task<GitResult> UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct);

    Task<GitResult> CommitAsync(string message, CancellationToken ct);

    Task<GitResult> DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct);

    Task<GitResult> CreateBranchAsync(string name, bool checkout, CancellationToken ct);

    Task<GitResult> CheckoutAsync(string branch, CancellationToken ct);

    /// <summary>Verify a remote is reachable without cloning it.</summary>
    Task<GitResult> VerifyRemoteAsync(string url, CancellationToken ct);

    Task<GitResult> FetchAsync(IProgress<GitProgress>? progress, CancellationToken ct);

    Task<GitResult> PullAsync(IProgress<GitProgress>? progress, CancellationToken ct);

    Task<GitResult> PushAsync(IProgress<GitProgress>? progress, CancellationToken ct);

    /// <summary>Unified diff for one path. <paramref name="staged"/> selects the index.</summary>
    Task<string> DiffAsync(string path, bool staged, CancellationToken ct);

    /// <summary>Renames git detected, old path → new path. Used to re-link a binding whose
    /// project was renamed rather than treating it as delete + add.</summary>
    Task<IReadOnlyDictionary<string, string>> DetectRenamesAsync(CancellationToken ct);

    /// <summary>
    /// Whether <c>git lfs</c> is installed and usable in this repository.
    /// </summary>
    /// <remarks>
    /// Asked before writing an LFS rule into <c>.gitattributes</c>, never assumed. A
    /// <c>filter=lfs</c> rule in a repository without LFS fails every later checkout and
    /// commit, and the teammate who hits it did not choose LFS and cannot see where the rule
    /// came from (plan R10).
    /// </remarks>
    Task<bool> IsLfsAvailableAsync(CancellationToken ct);
}
