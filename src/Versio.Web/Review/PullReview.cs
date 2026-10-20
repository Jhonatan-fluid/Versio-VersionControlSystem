using Versio.Core.Logging;
using Versio.Web.Rendering;
using Versio.Web.Repo;
using Versio.Web.Sources;

namespace Versio.Web.Review;

/// <summary>Whether the pull request is still a snapshot's revision; <paramref name="Failure"/> says why it could not be told.</summary>
public sealed record RevisionCheck(bool Same, RepoFailure Failure);

/// <summary>Why a changed file cannot take a comment on a line, or null when it can.</summary>
public static class InlineUnavailable
{
    /// <summary>GitHub listed the file but gave no patch: binary, too large, or past its diff limits.</summary>
    public const string NoPatch = "no-patch";

    /// <summary>GitHub's list stopped before this file (3,000 for a pull request, 300 for a compare).</summary>
    public const string ListCut = "list-cut";

    /// <summary>GitHub's diff does not name this file although the trees differ (a mode change, an LFS pointer swap).</summary>
    public const string NotInGitHubDiff = "not-in-github-diff";

    /// <summary>GitHub's file list could not be read at all, or only for another revision than this snapshot's.</summary>
    public const string Unavailable = "github-unavailable";
}

/// <summary>One changed file of a snapshot: what Versio's tree diff found, and what GitHub's diff allows.</summary>
/// <param name="Change">added, modified, deleted, or renamed (when GitHub reports the rename).</param>
/// <param name="PreviousPath">A renamed file's old path, from GitHub; Versio never guesses a rename.</param>
/// <param name="Patch">GitHub's hunks for this file in this snapshot, which bound where a line comment may go.</param>
/// <param name="InlineUnavailable">Null when line comments can go here; otherwise an <see cref="Review.InlineUnavailable"/> reason.</param>
public sealed record SnapshotFile(string Path, string Change, string? PreviousPath, string? Patch, string? InlineUnavailable);

/// <summary>A snapshot's changed files, and where GitHub's patches came from.</summary>
/// <param name="PatchSource">"pull", "compare", or "none".</param>
public sealed record SnapshotInventory(IReadOnlyList<SnapshotFile> Files, string PatchSource, bool GitHubListCut);

/// <summary>
/// Resolves a pull request to a <see cref="ReviewSnapshot"/> and lists what it changes
/// (docs/web/PLAN.md, *Pull request review*, C1).
/// </summary>
public sealed class PullReview(IRepoSource source, ReviewSnapshots snapshots, IVersioLogger log)
{
    /// <summary>How many times resolution starts over when the pull request moves under it.</summary>
    internal const int ResolveAttempts = 3;

    /// <summary>
    /// The pull request's head, its base branch's tip and their merge-base, read so that all three
    /// describe one moment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read, then read again.</b> The head comes from the pull request, the base tip from the
    /// branch, the merge-base from a compare of the two: three calls, and a push or a merge to the
    /// base can land between them. The pull request and the base are read a second time after the
    /// merge-base; if either moved, the three answers may belong to different moments, and
    /// resolution starts over. A pull request still moving after <see cref="ResolveAttempts"/> is
    /// "upstream": the page offers a reload rather than a snapshot nobody can vouch for.
    /// </para>
    /// <para>
    /// <b>No merge-base, no snapshot.</b> Without it there is no honest comparison (the base tip
    /// would show later base commits as reverted, FINDINGS 2026-10-17).
    /// </para>
    /// </remarks>
    public async Task<RepoResult<(ReviewSnapshot Snapshot, PullDetail Pull)>> ResolveAsync(
        RepoAccess who, string owner, string repo, int number, string viewer, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= ResolveAttempts; attempt++)
        {
            var pull = await source.GetPullAsync(who, owner, repo, number, ct).ConfigureAwait(false);
            if (!pull.Ok)
            {
                return pull.As<(ReviewSnapshot, PullDetail)>();
            }

            var p = pull.Value!.Pull;
            var baseTip = await source.ResolveCommitAsync(who, owner, repo, p.BaseRef, ct).ConfigureAwait(false);
            if (!baseTip.Ok)
            {
                return baseTip.As<(ReviewSnapshot, PullDetail)>();
            }

            var mergeBase = await source.GetMergeBaseAsync(who, owner, repo, baseTip.Value!, p.HeadSha, ct).ConfigureAwait(false);
            if (!mergeBase.Ok)
            {
                return mergeBase.As<(ReviewSnapshot, PullDetail)>();
            }

            var again = await source.GetPullAsync(who, owner, repo, number, ct).ConfigureAwait(false);
            var baseAgain = await source.ResolveCommitAsync(who, owner, repo, p.BaseRef, ct).ConfigureAwait(false);
            if (again.Ok && baseAgain.Ok && again.Value!.Pull.HeadSha == p.HeadSha && again.Value.Pull.BaseRef == p.BaseRef && baseAgain.Value == baseTip.Value)
            {
                var snapshot = new ReviewSnapshot(owner, repo, number, p.BaseRef, baseTip.Value!, mergeBase.Value!, p.HeadSha, snapshots.Now, viewer);
                log.Info(LogCategory.Web, $"review snapshot {who} {owner}/{repo}#{number} base {Short(baseTip.Value!)} merge-base {Short(mergeBase.Value!)} head {Short(p.HeadSha)}");
                return RepoResult<(ReviewSnapshot, PullDetail)>.Of((snapshot, pull.Value));
            }

            log.Info(LogCategory.Web, $"review snapshot {owner}/{repo}#{number}: the pull request moved while it was read (attempt {attempt} of {ResolveAttempts})");
        }

        return RepoResult<(ReviewSnapshot, PullDetail)>.Fail(RepoFailure.Upstream, "the pull request kept moving");
    }

    /// <summary>
    /// What the snapshot changes: every file Versio's own tree diff finds, each marked with whether
    /// GitHub's diff lets a comment sit on one of its lines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The inventory is Versio's, not GitHub's.</b> GitHub's lists stop — 3,000 files for a pull
    /// request, 300 for a compare — and omit patches for binary and large files. A review that
    /// listed only what GitHub patched would quietly leave files out, and an approval of a list
    /// with files missing is an approval of changes nobody was shown. The tree diff between the
    /// two snapshot commits is complete (up to the server's own comparison cap, which the caller
    /// reports); GitHub's patches only decide where a line comment may go.
    /// </para>
    /// <para>
    /// <b>Patches for exactly this revision, or none.</b> The pull request's file list follows its
    /// LIVE head, so it is accepted only when the head read before and after the paging is the
    /// snapshot's. Otherwise the compare of the snapshot's two commits supplies them, which names
    /// the commits and so cannot drift, but stops at 300 files. A file neither covers is listed,
    /// drawn, and takes file-level comments only, with the reason said.
    /// </para>
    /// <para>
    /// <b>Renames only as GitHub reports them.</b> The tree diff sees a delete and an add; GitHub's
    /// <c>previous_filename</c> joins them into one row. A rename Versio inferred itself could join
    /// two unrelated files, so none is inferred.
    /// </para>
    /// </remarks>
    public async Task<SnapshotInventory> InventoryAsync(RepoAccess who, ReviewSnapshot snapshot, RepoModel mergeBase, RepoModel head, CancellationToken ct)
    {
        var (github, patchSource) = await PatchesAsync(who, snapshot, ct).ConfigureAwait(false);
        var byPath = new Dictionary<string, PullFile>(StringComparer.Ordinal);
        foreach (var f in github?.Files ?? [])
        {
            byPath[f.Path] = f;
        }

        var changes = ViewService.ChangedPaths(mergeBase, head);
        var deleted = changes.Where(c => c.Change == "deleted").Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        var joined = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<SnapshotFile>(changes.Count);
        foreach (var (path, change) in changes)
        {
            byPath.TryGetValue(path, out var gh);
            var previous = change == "added" && gh is { Status: "renamed", PreviousPath: { } was } && deleted.Contains(was) ? was : null;
            if (previous is not null)
            {
                joined.Add(previous);
            }

            files.Add(new SnapshotFile(path, previous is null ? change : "renamed", previous, gh?.Patch, Unavailable(github, gh)));
        }

        // A renamed file's old path is also a "deleted" row of its own, listed before or after the
        // new path depending on ordinal order; it is joined into the rename, so it goes.
        files.RemoveAll(f => f.Change == "deleted" && joined.Contains(f.Path));
        return new SnapshotInventory(files, patchSource, github?.Truncated ?? false);
    }

    /// <summary>
    /// GitHub's patch for one file in this snapshot, read by the same rule as the inventory (this
    /// revision's or none), or null with the reason a line comment cannot go there.
    /// </summary>
    public async Task<(string? Patch, string? Unavailable)> PatchForAsync(RepoAccess who, ReviewSnapshot snapshot, string path, CancellationToken ct)
    {
        var (github, _) = await PatchesAsync(who, snapshot, ct).ConfigureAwait(false);
        var file = github?.Files.FirstOrDefault(f => f.Path == path);
        return (file?.Patch, Unavailable(github, file));
    }

    /// <summary>
    /// Whether the pull request, live, is still exactly the snapshot's revision: the same head, the
    /// same base branch, and that branch at the same commit. GitHub's live answers about a pull
    /// request — its files' patches, its threads' current lines — describe the comparison as it is
    /// NOW, so they may be used for the snapshot only when this holds before and after reading them.
    /// </summary>
    /// <remarks>
    /// The head alone was checked at first. Retargeting the base keeps the head and changes the
    /// comparison, so patches of a different diff were accepted as this one's (found by review,
    /// 2026-10-20). Not knowable counts as no.
    /// </remarks>
    private async Task<bool> IsSnapshotRevisionAsync(RepoAccess who, ReviewSnapshot snapshot, CancellationToken ct) =>
        (await CheckRevisionAsync(who, snapshot, ct).ConfigureAwait(false)).Same;

    /// <summary>
    /// THE check of "is the pull request still this snapshot's revision": the same head, the same base
    /// branch, that branch at the same commit — or why it cannot be told. Writes refuse a stale
    /// snapshot by it (<c>Writes.StaleAsync</c>) and live patches are trusted by it; it lived as two
    /// copies, with different failure handling, until a review found them drifting (2026-10-20) —
    /// which is how a base change once slipped past one of them.
    /// </summary>
    public async Task<RevisionCheck> CheckRevisionAsync(RepoAccess who, ReviewSnapshot snapshot, CancellationToken ct)
    {
        var pull = await source.GetPullAsync(who, snapshot.Owner, snapshot.Repo, snapshot.Number, ct).ConfigureAwait(false);
        if (!pull.Ok)
        {
            return new RevisionCheck(false, pull.Failure);
        }

        if (pull.Value!.Pull.HeadSha != snapshot.HeadSha || pull.Value.Pull.BaseRef != snapshot.BaseRef)
        {
            return new RevisionCheck(false, RepoFailure.None);
        }

        var tip = await source.ResolveCommitAsync(who, snapshot.Owner, snapshot.Repo, snapshot.BaseRef, ct).ConfigureAwait(false);
        return tip.Ok ? new RevisionCheck(tip.Value == snapshot.BaseSha, RepoFailure.None) : new RevisionCheck(false, tip.Failure);
    }

    /// <summary>
    /// The review threads, with GitHub's CURRENT lines kept only when they are this snapshot's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A thread's <c>line</c> is where it sits in the pull request's diff as it is now. After a push
    /// or a base change, that is a line of another revision, and placing it on the snapshot's drawing
    /// or Raw diff put a comment on a different declaration (found by review, 2026-10-20). Then the
    /// current lines are dropped: every thread is listed by the page as not on a shown line, and only
    /// an exact anchor (which names its own revision and content) still places one on a drawing.
    /// </para>
    /// <para>
    /// The revision is read in the same answers as the lines (<see cref="ReviewThreadList.Revision"/>),
    /// not around them: that is both exact — no window between a check and the read — and free. The
    /// first version checked before and after with four extra GitHub calls per file view (found in
    /// review, 2026-10-20).
    /// </para>
    /// </remarks>
    public async Task<RepoResult<ReviewThreadList>> ThreadsAsync(RepoAccess who, ReviewSnapshot snapshot, CancellationToken ct)
    {
        var threads = await source.GetReviewThreadsAsync(who, snapshot.Owner, snapshot.Repo, snapshot.Number, ct).ConfigureAwait(false);
        if (!threads.Ok || threads.Value!.Revision == new PullRevision(snapshot.HeadSha, snapshot.BaseRef, snapshot.BaseSha))
        {
            return threads;
        }

        log.Info(LogCategory.Web, $"review threads {snapshot.Owner}/{snapshot.Repo}#{snapshot.Number}: the pull request is no longer at {Short(snapshot.HeadSha)}; its threads' current lines are not used");
        return RepoResult<ReviewThreadList>.Of(threads.Value! with
        {
            Threads = threads.Value.Threads.Select(t => t with { Line = null, StartLine = null }).ToList(),
        });
    }

    private async Task<(PullFileList? Files, string Source)> PatchesAsync(RepoAccess who, ReviewSnapshot snapshot, CancellationToken ct)
    {
        if (await IsSnapshotRevisionAsync(who, snapshot, ct).ConfigureAwait(false))
        {
            var listed = await source.GetPullFilesAsync(who, snapshot.Owner, snapshot.Repo, snapshot.Number, ct).ConfigureAwait(false);
            if (listed.Ok && await IsSnapshotRevisionAsync(who, snapshot, ct).ConfigureAwait(false))
            {
                return (listed.Value, "pull");
            }
        }

        var compared = await source.GetCompareFilesAsync(who, snapshot.Owner, snapshot.Repo, snapshot.MergeBaseSha, snapshot.HeadSha, ct).ConfigureAwait(false);
        if (compared.Ok)
        {
            return (compared.Value, "compare");
        }

        log.Warn(LogCategory.Web, $"review files {snapshot.Owner}/{snapshot.Repo}#{snapshot.Number}: no GitHub patches for this revision ({compared.Failure}); every file takes file-level comments only");
        return (null, "none");
    }

    private static string? Unavailable(PullFileList? github, PullFile? file) =>
        github is null ? InlineUnavailable.Unavailable
        : file is null ? (github.Truncated ? InlineUnavailable.ListCut : InlineUnavailable.NotInGitHubDiff)
        : file.Patch is null ? InlineUnavailable.NoPatch
        : null;

    private static string Short(string sha) => sha[..Math.Min(8, sha.Length)];
}
