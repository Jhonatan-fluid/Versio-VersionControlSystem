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
    /// <para>
    /// Two commands, because <c>checkout --ours</c> writes the working-tree file and leaves the
    /// index entry unmerged. A file that looks resolved on disk and is still conflicted to git
    /// is the state that makes somebody think the button did nothing.
    /// </para>
    /// <para>
    /// <b>Unless the chosen side deleted the file</b> — then there is no version to check out.
    /// In a modify/delete conflict git keeps the surviving side at stage 2 or 3 and the
    /// deleting side at no stage at all, and <c>checkout --ours</c> for the side with no stage
    /// exits 1 with "does not have our version". The first version ran exactly that and
    /// reported the failure: the conflict stayed, the file stayed, and choosing the deletion
    /// was impossible from the panel (the 2026-10-25 review, P2). The resolution that side
    /// asks for is <c>git rm</c>, which takes the file out of the working tree and stages its
    /// removal in one step. Not <c>rm --cached</c>: that would resolve the index and leave the
    /// other side's content on disk as an untracked file, which is the deletion half-done.
    /// Note that <c>rm</c> without <c>-f</c> is <i>not</i> a safeguard here: on an unmerged
    /// path git has no clean version to compare against and removes the working file even if
    /// it was edited by hand after the conflict (checked against git 2.55). That matches the
    /// other side — <c>checkout --ours</c> overwrites a hand edit just the same — so either
    /// button means "that side, wholesale", and the panel offers nothing in between.
    /// </para>
    /// </remarks>
    public async Task<GitResult> ResolveWithSideAsync(string path, bool ours, CancellationToken ct)
    {
        var side = ours ? "--ours" : "--theirs";
        var stages = await UnmergedStagesAsync(path, ct).ConfigureAwait(false);

        // Stage 2 is ours, stage 3 is theirs. A path that is unmerged and has no entry for the
        // side being taken was deleted on that side. A path with no unmerged entry at all falls
        // through to the checkout, which then fails in git's words rather than being guessed at
        // here.
        if (stages.Count > 0 && !stages.Contains(ours ? 2 : 3))
        {
            _log.Info(LogCategory.Git, $"resolving {path} with {side}, which deleted it: git rm");
            return await RunAsync([LiteralPathspecs, "rm", "--", path], null, ct,
                                  TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }

        _log.Info(LogCategory.Git, $"resolving {path} with {side}");

        var taken = await RunAsync([LiteralPathspecs, "checkout", side, "--", path], null, ct,
                                   TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        if (!taken.Success)
        {
            return taken;
        }

        return await RunAsync([LiteralPathspecs, "add", "--", path], null, ct, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
    }

    /// <summary>Which index stages an unmerged path has: 1 base, 2 ours, 3 theirs.</summary>
    /// <remarks>
    /// Empty when the path is not unmerged, and also when git could not answer — a resolution
    /// then proceeds the way it always did and fails, if it fails, with git's own message
    /// rather than a guess about stages. <c>-z</c> as every other path listing here, so a
    /// non-ASCII or space-bearing name is never quoted; the stage is read from the metadata
    /// column and the path field is not matched at all, because the pathspec already did that.
    /// </remarks>
    private async Task<IReadOnlySet<int>> UnmergedStagesAsync(string path, CancellationToken ct)
    {
        var listing = await RunAsync([LiteralPathspecs, "ls-files", "-u", "-z", "--", path], null, ct,
                                     TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        if (!listing.Success)
        {
            _log.Warn(LogCategory.Git,
                      $"could not read the unmerged stages of {path}: {listing.FailureReason}");
            return new HashSet<int>();
        }

        // Each record is "<mode> <object> <stage>\t<path>"; the stage is the last token before
        // the tab, and the path field is not read at all. The runner's one appended newline is
        // dropped by length as the other -z listings do it (IncomingPathsAsync says why never
        // by trimming); here it only keeps the last record from carrying a stray newline.
        var text = listing.StdOut.EndsWith('\n') ? listing.StdOut[..^1] : listing.StdOut;
        var stages = new HashSet<int>();

        foreach (var record in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            var meta = tab < 0 ? record : record[..tab];
            var fields = meta.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length >= 3 && int.TryParse(fields[2], out var stage))
            {
                stages.Add(stage);
            }
        }

        return stages;
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
    /// <remarks>
    /// <b>Null when the question could not be answered, never an empty list.</b> The first
    /// version returned <c>[]</c> on failure, which is indistinguishable from a genuinely empty
    /// diff — so a branch git cannot resolve at all (a remote-only name with no local ref, the
    /// common case) was reported to the preflight as "brings nothing" and refused with
    /// <i>already in this branch</i>. Wrong, and confidently so.
    /// </remarks>
    public async Task<IReadOnlyList<string>?> IncomingPathsAsync(
        string branch, CancellationToken ct)
    {
        // Three dots: what THAT branch has done since the two parted, and not what this one
        // has. Two dots here would report this branch's own work as incoming and refuse a
        // merge over changes that are already present.
        //
        // -z, as every other path listing in this client has: without it git's default
        // core.quotePath writes any non-ASCII name as "Pr\303\274fung.project", quotes and
        // octal included, and that string equals no project path and starts with no mirror
        // prefix. The merge guard then found no affected binding and the import protection
        // was skipped for exactly the merges that touched a project with an umlaut in its
        // name (the 2026-10-24 review's item 15). NUL-separated output is never quoted.
        var result = await RunAsync(
            ["diff", "--name-only", "-z", $"HEAD...{branch}"], null, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            _log.Warn(LogCategory.Git,
                      $"could not list what {branch} would bring: {result.FailureReason}");
            return null;
        }

        // The runner appends one newline to stdout, and that one newline is all that is
        // removed — by length, never by trimming. The first -z version split with
        // TrimEntries to be rid of it, which also took the spaces off every field: a project
        // under " plc/" came back as "plc/…", matched no tracked root, and the merge guard
        // was bypassed for exactly the names -z was added to carry (the second-round review's
        // item 2). A path is whatever git said it is, spaces included.
        var listing = result.StdOut.EndsWith('\n') ? result.StdOut[..^1] : result.StdOut;
        return [.. listing.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
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
