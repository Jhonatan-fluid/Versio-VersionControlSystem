using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Versio.Core.Abstractions;
using Versio.Core.Git;
using Versio.Core.Logging;

namespace Versio.Infrastructure.Git;

/// <summary>
/// Unmerged paths, and taking one side of them.
/// </summary>
/// <remarks>A part of <see cref="GitCliClient"/>. See GitCliClient.Credentials.cs.</remarks>
public sealed partial class GitCliClient
{
    // ------------------------------------------------------------------- conflicts

    /// <summary>Take one side of a conflicted file and stage it.</summary>
    /// <remarks>
    /// Two commands, because <c>checkout --ours</c> writes the working-tree file and leaves the
    /// index entry unmerged. A file that looks resolved on disk and is still conflicted to git
    /// is the state that makes somebody think the button did nothing.
    /// </remarks>
    public async Task<GitResult> ResolveWithSideAsync(string path, bool ours, CancellationToken ct)
    {
        var side = ours ? "--ours" : "--theirs";
        _log.Info(LogCategory.Git, $"resolving {path} with {side}");

        var taken = await RunAsync(["checkout", side, "--", path], null, ct,
                                   TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        if (!taken.Success)
        {
            return taken;
        }

        return await RunAsync(["add", "--", path], null, ct, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
    }

    /// <summary>Merge the upstream in, producing conflicts rather than refusing.</summary>
    /// <remarks>
    /// <c>--no-ff</c> is deliberately absent: when the merge <i>can</i> fast-forward there is no
    /// reason to manufacture a merge commit, and the caller only reaches this because
    /// <c>--ff-only</c> already refused.
    /// </remarks>
    public Task<GitResult> MergeUpstreamAsync(
        IProgress<GitProgress>? progress, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, "merging the upstream in");
        return RunAsync(["merge", "--progress", "@{u}"], progress, ct, TimeSpan.FromMinutes(10));
    }

    /// <summary>Merge a named branch into the current one.</summary>
    public Task<GitResult> MergeBranchAsync(
        string branch, IProgress<GitProgress>? progress, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, $"merging {branch} into the current branch");

        // -- before the ref, so a branch named like a flag cannot become one.
        return RunAsync(["merge", "--progress", "--", branch], progress, ct,
                        TimeSpan.FromMinutes(10));
    }

    /// <summary>What merging that branch would bring, from the merge base.</summary>
    public async Task<IReadOnlyList<string>> IncomingPathsAsync(
        string branch, CancellationToken ct)
    {
        // Three dots: what THAT branch has done since the two parted, and not what this one
        // has. Two dots here would report this branch's own work as incoming and refuse a
        // merge over changes that are already present.
        var result = await RunAsync(
            ["diff", "--name-only", $"HEAD...{branch}"], null, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            _log.Warn(LogCategory.Git,
                      $"could not list what {branch} would bring: {result.FailureReason}");
            return [];
        }

        return [.. result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>Replay this branch onto the upstream.</summary>
    public Task<GitResult> RebaseOntoUpstreamAsync(
        IProgress<GitProgress>? progress, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, "rebasing onto the upstream");
        return RunAsync(["rebase", "@{u}"], progress, ct, TimeSpan.FromMinutes(10));
    }

    /// <summary>Undo whichever of the two is part-way through.</summary>
    /// <remarks>
    /// The state is read first rather than trying both and ignoring the failure: <c>git merge
    /// --abort</c> outside a merge is an error, and an error swallowed on the way to a
    /// successful-looking result is how a tool ends up lying about what it did.
    /// </remarks>
    public async Task<GitResult> AbortMergeOrRebaseAsync(CancellationToken ct)
    {
        var state = await MergeStateAsync(ct).ConfigureAwait(false);
        _log.Info(LogCategory.Git, $"aborting the {state.ToString().ToLowerInvariant()}");

        return state switch
        {
            MergeState.Merging => await RunAsync(["merge", "--abort"], null, ct,
                                                 TimeSpan.FromMinutes(2)).ConfigureAwait(false),
            MergeState.Rebasing => await RunAsync(["rebase", "--abort"], null, ct,
                                                  TimeSpan.FromMinutes(2)).ConfigureAwait(false),
            _ => NoOp(),
        };
    }

    /// <summary>Whether a merge or rebase is part-way through.</summary>
    /// <remarks>
    /// From git's own marker files rather than from the presence of unmerged paths. Resolving
    /// every conflict leaves the tree clean while the merge is still in progress and waiting to
    /// be committed, and reading "no conflicts" as "nothing happening" would offer a plain
    /// commit for what is the conclusion of a merge.
    /// </remarks>
    public async Task<MergeState> MergeStateAsync(CancellationToken ct)
    {
        var gitDir = await GitDirectoryAsync(ct).ConfigureAwait(false);

        if (gitDir.Length == 0)
        {
            return MergeState.None;
        }

        // rebase-merge is the interactive machinery, rebase-apply the older patch-based path.
        // Both mean the same thing to everything above this.
        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge"))
            || Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
        {
            return MergeState.Rebasing;
        }

        return File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))
            ? MergeState.Merging
            : MergeState.None;
    }
}
