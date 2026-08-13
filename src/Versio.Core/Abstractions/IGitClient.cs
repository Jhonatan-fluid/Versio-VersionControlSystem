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

    /// <summary>The version of git that will actually run, in its own words.</summary>
    /// <remarks>
    /// Asked of git rather than read from a setting, because the one on PATH is the one every
    /// other call here will use - and "which git is it using" is a question that has caused
    /// real confusion on machines carrying several.
    /// </remarks>
    Task<string> VersionAsync(CancellationToken ct);

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

    /// <summary>
    /// Unified diff for one path. <paramref name="staged"/> selects the index.
    /// </summary>
    /// <param name="contextLines">
    /// Unchanged lines to show either side of each change. Raise it to
    /// <see cref="GitDiff.FullFileContext"/> to render the whole file with its changes still
    /// marked — which is what a reviewer wants when three lines of context is not enough to
    /// tell whether an edit to a POU is safe.
    /// </param>
    Task<string> DiffAsync(string path, bool staged, CancellationToken ct, int contextLines = 3);

    /// <summary>Renames git detected, old path → new path. Used to re-link a binding whose
    /// project was renamed rather than treating it as delete + add.</summary>
    Task<IReadOnlyDictionary<string, string>> DetectRenamesAsync(CancellationToken ct);

    /// <summary>
    /// Commit id at HEAD, or empty on an unborn branch.
    /// </summary>
    /// <remarks>
    /// Recorded as the baseline when a push releases the direction locks: the lock records
    /// what the world has seen, and "what the world has seen" is a commit, not a moment.
    /// </remarks>
    Task<string> HeadCommitAsync(CancellationToken ct);

    /// <summary>Newest commits first, subject and author only.</summary>
    /// <param name="count">How many to read. The panel shows a page, not the history.</param>
    /// <param name="skip">
    /// How many newer commits to step over first, so the panel can ask for the next page
    /// rather than re-reading a longer prefix of the same log each time.
    /// </param>
    Task<IReadOnlyList<CommitEntry>> RecentCommitsAsync(int count, int skip, CancellationToken ct);

    /// <summary>The full patch of one commit, for click-to-diff.</summary>
    /// <remarks>
    /// A merge commit yields nothing here, because a diff against several parents is not one
    /// patch. Saying so beats picking a parent on the user's behalf and showing them a diff
    /// that is true against one side of a merge and misleading about the merge itself.
    /// </remarks>
    Task<string> CommitPatchAsync(string commit, CancellationToken ct,
                                  int contextLines = 3);

    /// <summary>
    /// Which files one commit touched, and what it did to each.
    /// </summary>
    /// <remarks>
    /// Asked separately from the patch, and only when somebody expands that commit. The list
    /// is what the history panel shows; the patches behind it are the expensive part, and a
    /// commit that touches an exported project tree can carry hundreds of them.
    /// <para>
    /// A merge yields nothing, for the same reason <see cref="CommitPatchAsync"/> does — git
    /// suppresses the combined diff, and inventing one by picking a parent would describe a
    /// change that nobody made.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<CommitFile>> CommitFilesAsync(string commit, CancellationToken ct);

    /// <summary>
    /// The patch one commit applied to one file.
    /// </summary>
    /// <param name="contextLines">
    /// As <see cref="DiffAsync"/>: raise it to <see cref="GitDiff.FullFileContext"/> to read
    /// the whole file with the commit's changes still marked.
    /// </param>
    Task<string> CommitFileDiffAsync(string commit, string path, CancellationToken ct,
                                     int contextLines = 3);

    /// <summary>Everything currently stashed, newest first.</summary>
    Task<IReadOnlyList<StashEntry>> StashListAsync(CancellationToken ct);

    /// <summary>
    /// Stash the working tree.
    /// </summary>
    /// <param name="includeUntracked">
    /// Also stash files git has never seen. Off by default because it sweeps up build output
    /// and scratch files that were never meant to be kept, and they come back on the pop.
    /// </param>
    Task<GitResult> StashPushAsync(string? message, bool includeUntracked, CancellationToken ct);

    /// <summary>
    /// Restore a stash, optionally dropping it afterwards.
    /// </summary>
    /// <remarks>
    /// Apply and pop are one call with a flag rather than two methods, because they are the
    /// same operation and differ only in whether the entry survives it — which is exactly the
    /// decision the caller is making.
    /// </remarks>
    Task<GitResult> StashApplyAsync(string stashRef, bool drop, CancellationToken ct);

    /// <summary>
    /// Delete a stash without restoring it.
    /// </summary>
    /// <remarks>
    /// <b>Destructive.</b> The work is unreachable afterwards by any ordinary means, so
    /// callers confirm first.
    /// </remarks>
    Task<GitResult> StashDropAsync(string stashRef, CancellationToken ct);

    /// <summary>
    /// Apply a patch to the index only, leaving the working tree untouched.
    /// </summary>
    /// <param name="reverse">
    /// Take the change back out of the index rather than putting it in — which is what
    /// unstaging one hunk is.
    /// </param>
    /// <remarks>
    /// The patch arrives on standard input rather than through a temporary file. A temp file
    /// is one more thing to fail to clean up, and one more place a fragment of somebody's
    /// source could be left readable.
    /// </remarks>
    Task<GitResult> ApplyToIndexAsync(string patch, bool reverse, CancellationToken ct);

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
