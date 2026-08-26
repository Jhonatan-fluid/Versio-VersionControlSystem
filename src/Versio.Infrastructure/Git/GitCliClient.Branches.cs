using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Versio.Core.Abstractions;
using Versio.Core.Git;
using Versio.Core.Logging;

namespace Versio.Infrastructure.Git;

/// <summary>
/// Listing, creating, checking out and deleting branches.
/// </summary>
/// <remarks>A part of <see cref="GitCliClient"/>. See GitCliClient.Credentials.cs.</remarks>
public sealed partial class GitCliClient
{
    // ------------------------------------------------------------------- branches

    /// <summary>Every remote-tracking ref, as <c>origin/branch</c>.</summary>
    public async Task<IReadOnlyList<string>> RemoteBranchesAsync(CancellationToken ct)
    {
        var result = await RunAsync(
            ["for-each-ref", "--format=%(refname:short)", "refs/remotes"], null, ct)
            .ConfigureAwait(false);

        return result.Success
            ? [.. result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                               .Select(l => l.Trim())
                               .Where(l => l.Length > 0)]
            : [];
    }

    /// <summary>Branches already merged into another.</summary>
    /// <remarks>
    /// The leading <c>*</c> git puts on the current branch is stripped: it is decoration in a
    /// list that is otherwise names, and leaving it would make the current branch fail every
    /// comparison against itself.
    /// </remarks>
    public async Task<IReadOnlyList<string>> MergedBranchesAsync(string into, CancellationToken ct)
    {
        var result = await RunAsync(["branch", "--merged", into], null, ct)
            .ConfigureAwait(false);

        return result.Success
            ? [.. result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                               .Select(l => l.TrimStart('*', ' ').Trim())
                               .Where(l => l.Length > 0)]
            : [];
    }

    /// <summary>What the remote says its default branch is.</summary>
    /// <remarks>
    /// Not every clone has one: <c>origin/HEAD</c> is written at clone time, and a repository
    /// that gained its remote afterwards may never have had it. Empty is the answer rather than
    /// an error, and the caller falls through to convention.
    /// </remarks>
    public async Task<string> RemoteHeadAsync(string remote, CancellationToken ct)
    {
        var result = await RunAsync(
            ["symbolic-ref", "--quiet", $"refs/remotes/{remote}/HEAD"], null, ct,
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        return result.Success ? result.StdOut.Trim() : string.Empty;
    }

    /// <summary>Delete a local branch.</summary>
    public Task<GitResult> DeleteBranchAsync(string name, bool force, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, $"deleting branch {name}{(force ? " (forced)" : "")}");
        return RunAsync(["branch", force ? "-D" : "-d", "--", name], null, ct,
                        TimeSpan.FromSeconds(30));
    }

    /// <summary>Delete a branch from the remote.</summary>
    /// <remarks>
    /// <c>push --delete</c> rather than the older colon refspec, which is the same operation
    /// spelled in a way nobody reads correctly.
    /// </remarks>
    public Task<GitResult> DeleteRemoteBranchAsync(
        string remote, string name, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, $"deleting {name} from {remote}");
        return RunAsync(["push", remote, "--delete", name], null, ct, TimeSpan.FromMinutes(5));
    }

    // FetchPruneAsync lived here until every fetch learned --prune (FINDINGS 2026-08-23).
    // Two fetch methods whose only difference was honesty about deleted branches meant every
    // caller had to know which one told the truth, and the ordinary one - the Fetch button,
    // the poller's catch-up - was the one that did not.

    /// <summary>Every remote this repository has.</summary>
    /// <remarks>
    /// A repository with none is ordinary, not a failure, so an empty list is the answer rather
    /// than an error — the same reasoning <see cref="RemoteUrlAsync"/> applies.
    /// </remarks>
    public async Task<IReadOnlyList<string>> RemotesAsync(CancellationToken ct)
    {
        var result = await RunAsync(["remote"], null, ct, TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        return result.Success
            ? [.. result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                               .Select(l => l.Trim())
                               .Where(l => l.Length > 0)]
            : [];
    }

    /// <summary>Push a branch that has no upstream, and set one.</summary>
    /// <remarks>
    /// <c>--set-upstream</c> with the remote and branch named explicitly rather than relying on
    /// <c>push.default</c> or <c>push.autoSetupRemote</c>: both are configuration this
    /// application does not own, and a publish that behaved differently on two machines in the
    /// same team would be a genuinely confusing thing to debug.
    /// </remarks>
    public Task<GitResult> PublishBranchAsync(
        string remote, string branch, IProgress<GitProgress>? progress, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, $"publishing {branch} to {remote}");
        return RunAsync(["push", "--set-upstream", remote, branch, "--progress"],
                        progress, ct, TimeSpan.FromMinutes(10));
    }

    /// <summary>Unified diff for one path.</summary>
    /// <param name="staged">Compare the index against HEAD rather than the working tree
    /// against the index — a file can differ on both sides at once.</param>
    public async Task<string> DiffAsync(
        string path, bool staged, CancellationToken ct, int contextLines = GitDiff.DefaultContext)
    {
        var unified = $"-U{Math.Max(0, contextLines)}";
        string[] args = staged
            ? ["diff", unified, "--cached", "--", path]
            : ["diff", unified, "--", path];

        var result = await RunAsync(args, null, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            return result.StdOut;
        }

        // A file git has never seen has nothing to be compared against, so plain `git diff`
        // says nothing about it — which used to surface as "no textual diff for this file".
        // That is the wrong answer to the wrong question: a new file has not got *no* diff,
        // it is *entirely* new, and every line of it is an addition worth reading before it
        // is committed.
        return await UntrackedDiffAsync(path, unified, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The whole of a new file, as an all-additions diff.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--no-index</c> compares two paths outside the index, so pointing it at
    /// <c>/dev/null</c> yields a normal unified diff in which the file is new and every line
    /// is added. Git for Windows understands <c>/dev/null</c> here, and the output carries a
    /// real <c>new file mode</c> header — so the existing parser and the existing green
    /// highlighting handle it with no special case of their own.
    /// </para>
    /// <para>
    /// <b>Only for paths git does not track.</b> Falling back whenever the primary diff came
    /// back empty would render an unmodified tracked file as though every line had just been
    /// added — the same wrong answer in the opposite direction, and a far more alarming one.
    /// </para>
    /// <para>
    /// <c>--no-index</c> exits <b>1</b> when the files differ, which is the successful case
    /// here. Its exit code is deliberately not consulted.
    /// </para>
    /// </remarks>
    private async Task<string> UntrackedDiffAsync(
        string path, string unified, CancellationToken ct)
    {
        // ls-files --error-unmatch fails precisely when the path is not in the index.
        var tracked = await RunAsync(["ls-files", "--error-unmatch", "--", path], null, ct)
            .ConfigureAwait(false);
        if (tracked.Success)
        {
            _log.Debug(LogCategory.Git,
                $"no diff for tracked path {path}; nothing to show");
            return string.Empty;
        }

        var full = System.IO.Path.Combine(repositoryPath, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            // Untracked and gone: a file created and deleted between the status refresh and
            // this click. Nothing to render, and nothing wrong either.
            _log.Debug(LogCategory.Git, $"untracked path {path} no longer exists");
            return string.Empty;
        }

        var result = await RunAsync(
            ["diff", unified, "--no-index", "--", "/dev/null", path], null, ct)
            .ConfigureAwait(false);

        _log.Debug(LogCategory.Git,
            $"untracked diff for {path}: {result.StdOut.Length} char(s), exit {result.ExitCode}");

        return result.StdOut;
    }

    /// <summary>Renames git detected, old path → new path.</summary>
    /// <remarks>
    /// Used to re-link a binding whose <c>.project</c> was renamed. Without it the rename
    /// reads as a delete plus an add: the binding id is lost, the baseline resets, and the
    /// diff shows the whole project as newly added (plan R29).
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> DetectRenamesAsync(CancellationToken ct)
    {
        var result = await RunAsync(
            ["diff", "--name-status", "--find-renames", "-z", "HEAD"], null, ct)
            .ConfigureAwait(false);

        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!result.Success)
        {
            return renames;
        }

        // -z layout for a rename is: "R<score>\0<old>\0<new>\0"
        var fields = result.StdOut.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            if (!fields[i].StartsWith('R') || i + 2 >= fields.Length)
            {
                continue;
            }

            renames[fields[i + 1]] = fields[i + 2];
            i += 2;
        }

        return renames;
    }

    /// <summary>Commit id at HEAD, or empty on an unborn branch.</summary>
    /// <remarks>
    /// Empty rather than an exception on an unborn branch. A repository with no commits yet
    /// is a perfectly ordinary state on the first day of a project, and the caller - a push
    /// releasing the direction locks - has nothing sensible to do with a throw.
    /// </remarks>
    public async Task<string> HeadCommitAsync(CancellationToken ct)
    {
        var result = await RunAsync(["rev-parse", "HEAD"], null, ct).ConfigureAwait(false);
        return result.Success ? result.StdOut.Trim() : string.Empty;
    }

    /// <summary>Newest commits first.</summary>
    /// <remarks>
    /// <c>--no-merges</c> is deliberately absent: a merge is part of the history somebody is
    /// scrolling, and hiding it would make the list disagree with the branch it describes.
    /// Its patch is the thing that cannot be shown, and that is said when it is asked for.
    /// </remarks>
    /// <summary>Newest commits in one range, for the ahead/behind previews.</summary>
    /// <remarks>
    /// A failed run — the upstream ref deleted since the caller read its status — comes back
    /// as an empty list rather than a throw: the preview is a glance, and taking the panel
    /// down over a ref that vanished mid-glance would be the wrong trade.
    /// </remarks>
    public async Task<IReadOnlyList<CommitEntry>> CommitsInRangeAsync(
        string range, int count, CancellationToken ct)
    {
        var result = await RunAsync(
            ["log", $"--max-count={Math.Max(1, count)}", range, GitLogParser.CommitFormat],
            null, ct)
            .ConfigureAwait(false);

        return result.Success ? GitLogParser.ParseCommits(result.StdOut) : [];
    }

    /// <summary>The files one range touches — <c>diff --name-status</c> over it.</summary>
    /// <remarks>
    /// <c>-z</c> for the same reason every name-status caller here uses it: git quotes
    /// non-ASCII paths otherwise, and this repository's own fixtures carry umlauts.
    /// <c>--find-renames</c> so an incoming rename previews as the move it is rather than as
    /// a delete and an add — the same courtesy the status parser extends to the working tree.
    /// </remarks>
    public async Task<IReadOnlyList<CommitFile>> RangeFilesAsync(
        string range, CancellationToken ct)
    {
        var result = await RunAsync(
            ["diff", "--name-status", "--find-renames", "-z", range], null, ct)
            .ConfigureAwait(false);

        return result.Success ? GitNameStatusParser.Parse(result.StdOut) : [];
    }

    public async Task<IReadOnlyList<CommitEntry>> RecentCommitsAsync(
        int count, int skip, CancellationToken ct)
    {
        var result = await RunAsync(
            ["log", $"--max-count={Math.Max(1, count)}", $"--skip={Math.Max(0, skip)}",
             GitLogParser.CommitFormat], null, ct)
            .ConfigureAwait(false);

        // An unborn branch has no HEAD to walk, and git says so on stderr. That is the first
        // day of a repository, not a failure worth reporting.
        return result.Success ? GitLogParser.ParseCommits(result.StdOut) : [];
    }

    /// <summary>How many unpushed commits are looked for before giving up counting.</summary>
    /// <remarks>
    /// A repository with no remote answers this query with its whole history, so there has to
    /// be a bound. Two thousand is far past the point where "how much have I not pushed" is a
    /// question anybody is asking precisely, and it is four hundred pages of the history panel.
    /// </remarks>
    private const int UnpushedCap = 2000;

    public async Task<IReadOnlySet<string>> UnpushedCommitIdsAsync(CancellationToken ct)
    {
        // HEAD --not --remotes: everything reachable from here that no remote-tracking branch
        // can reach. See IGitClient for why this rather than @{upstream}..HEAD.
        var result = await RunAsync(
            ["rev-list", $"--max-count={UnpushedCap}", "HEAD", "--not", "--remotes"], null, ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            // An unborn branch, or a repository mid-clone. Nothing to report, and nothing
            // wrong: the panel simply marks nothing rather than showing an error over a
            // question nobody asked.
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return new HashSet<string>(
            result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries
                                     | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);
    }

    /// <summary>The patch of one commit, with no commit header of its own.</summary>
    /// <remarks>
    /// <c>--format=</c> empties the header so the output is a plain unified diff the existing
    /// parser and the existing highlighting handle unchanged. A merge commit produces nothing
    /// under these switches — that is git's answer, not a failure, and it is reported as such
    /// rather than papered over with <c>-m</c>, which would show one parent's diff as though
    /// it were the merge.
    /// </remarks>
    public async Task<string> CommitPatchAsync(
        string commit, CancellationToken ct, int contextLines = GitDiff.DefaultContext)
    {
        var result = await RunAsync(
            ["show", "--format=", "--patch", $"-U{Math.Max(0, contextLines)}", commit], null, ct)
            .ConfigureAwait(false);

        return result.Success ? result.StdOut : string.Empty;
    }

    /// <summary>Which files one commit touched.</summary>
    /// <remarks>
    /// <para>
    /// <c>-z</c> is load-bearing rather than tidiness. Without it git quotes any path holding
    /// a byte it considers unusual, so <c>Kühlschrank Regelung.st</c> — a real shape in this
    /// repository's own fixtures — comes back wrapped in quotes with its umlaut as octal
    /// escapes, and the panel would then ask git for a diff of a path that does not exist.
    /// </para>
    /// <para>
    /// The NUL-separated stream survives <see cref="RunAsync"/> intact: the reader there
    /// splits on line breaks, of which this output has none, so the whole listing arrives as
    /// a single line with its separators untouched.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<CommitFile>> CommitFilesAsync(
        string commit, CancellationToken ct)
    {
        var result = await RunAsync(
            ["show", "--format=", "--name-status", "-z", commit], null, ct)
            .ConfigureAwait(false);

        return result.Success ? GitNameStatusParser.Parse(result.StdOut) : [];
    }

    /// <summary>The patch one commit applied to one file.</summary>
    /// <remarks>
    /// <c>git show</c> with a pathspec rather than a diff between the commit and its parent.
    /// The two agree everywhere except on a root commit, which has no parent to name — and
    /// the first commit of a repository is exactly the one somebody scrolls to the bottom of
    /// the history to look at.
    /// </remarks>
    public async Task<string> CommitFileDiffAsync(
        string commit, string path, CancellationToken ct,
        int contextLines = GitDiff.DefaultContext)
    {
        var result = await RunAsync(
            ["show", "--format=", "--patch", $"-U{Math.Max(0, contextLines)}",
             commit, "--", path], null, ct)
            .ConfigureAwait(false);

        return result.Success ? result.StdOut : string.Empty;
    }

    /// <summary>The full message of one commit.</summary>
    /// <remarks>
    /// <c>show --no-patch</c> rather than <c>log -1</c>, staying in this file's idiom of one
    /// commit named directly; <c>%B</c> is the raw message, subject and body both, exactly as
    /// recorded — no re-wrapping, no encoding of git's own. Failure is null, not empty:
    /// see <see cref="IGitClient.CommitMessageAsync"/>.
    /// </remarks>
    public async Task<string?> CommitMessageAsync(string commit, CancellationToken ct)
    {
        var result = await RunAsync(
            ["show", "--no-patch", "--format=%B", commit], null, ct)
            .ConfigureAwait(false);

        // Null, not empty: empty is a successful read of a blank message, and the
        // callers have to tell those apart — see IGitClient.CommitMessageAsync.
        return result.Success ? result.StdOut : null;
    }

    public async Task<IReadOnlyList<StashEntry>> StashListAsync(CancellationToken ct)
    {
        var result = await RunAsync(["stash", "list", GitLogParser.StashFormat], null, ct)
            .ConfigureAwait(false);

        return result.Success ? GitLogParser.ParseStashes(result.StdOut) : [];
    }

    /// <summary>Stash the working tree.</summary>
    /// <remarks>
    /// <c>push</c> rather than the older <c>save</c>: <c>save</c> is deprecated and treats its
    /// argument list differently, which is a trap for a message that begins with a dash.
    /// </remarks>
    public Task<GitResult> StashPushAsync(
        string? message, bool includeUntracked, CancellationToken ct)
    {
        List<string> args = ["stash", "push"];

        if (includeUntracked)
        {
            args.Add("--include-untracked");
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("-m");
            args.Add(message);
        }

        _log.Info(LogCategory.Git,
            includeUntracked ? "stashing, including untracked files" : "stashing");

        return RunAsync([.. args], null, ct);
    }

    /// <summary>Restore a stash, optionally dropping it afterwards.</summary>
    /// <remarks>
    /// <b>Pop drops only when the restore succeeded</b> — that is git's own behaviour for
    /// <c>stash pop</c>, and the reason this delegates to it rather than running apply and
    /// then drop. Doing it in two commands here would drop the entry after a restore that
    /// conflicted, which destroys the only copy of the work.
    /// </remarks>
    public Task<GitResult> StashApplyAsync(string stashRef, bool drop, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, $"{(drop ? "popping" : "applying")} {stashRef}");
        return RunAsync(["stash", drop ? "pop" : "apply", stashRef], null, ct);
    }

    /// <summary>Delete a stash without restoring it.</summary>
    /// <remarks>
    /// <b>Destructive.</b> Logged by name at Warn for the same reason discard is: if somebody
    /// comes back asking where their work went, this line is the answer.
    /// </remarks>
    public Task<GitResult> StashDropAsync(string stashRef, CancellationToken ct)
    {
        _log.Warn(LogCategory.Git, $"dropping {stashRef}; its contents are not recoverable");
        return RunAsync(["stash", "drop", stashRef], null, ct);
    }

    /// <summary>Apply a patch to the index alone.</summary>
    /// <remarks>
    /// <para>
    /// <c>--cached</c> is what makes this a staging operation: the index moves and the working
    /// tree does not, so the file on disk still says what the engineer wrote. Applying to the
    /// working tree as well would silently rewrite the file they are looking at.
    /// </para>
    /// <para>
    /// <c>--unidiff-zero</c> is deliberately <b>not</b> passed. It disables the context check,
    /// which is the only thing standing between a hunk built from a stale diff and a patch
    /// applied at the wrong offset.
    /// </para>
    /// </remarks>
    public Task<GitResult> ApplyToIndexAsync(string patch, bool reverse, CancellationToken ct)
    {
        List<string> args = ["apply", "--cached"];
        if (reverse)
        {
            args.Add("--reverse");
        }

        args.Add("-");

        _log.Info(LogCategory.Git,
            $"applying a {patch.Length}-char patch to the index{(reverse ? " in reverse" : "")}");

        return RunAsync([.. args], null, ct, stdin: patch);
    }

    /// <summary>Whether <c>git lfs</c> is installed and usable here.</summary>
    /// <remarks>
    /// <c>lfs version</c> rather than <c>lfs env</c> or a check for the binary on PATH: it
    /// is the cheapest call that proves the subcommand actually resolves through <i>this</i>
    /// git, which is the thing the filter rules will depend on. Cached for the lifetime of
    /// the client because installing LFS mid-session is not a case worth re-probing for on
    /// every adopt.
    /// </remarks>
    public async Task<bool> IsLfsAvailableAsync(CancellationToken ct)
    {
        if (_lfsAvailable is { } known)
        {
            return known;
        }

        var result = await RunAsync(["lfs", "version"], null, ct, TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        _lfsAvailable = result.Success;

        // Logged once, and it matters: without LFS the .gitattributes rules are written
        // differently, so this single probe changes what adoption does to the repository.
        _log.Info(LogCategory.Git, result.Success
            ? $"git-lfs is available: {result.StdOut.Trim()}"
            : "git-lfs is not available; .project files will be marked binary without LFS");

        return result.Success;
    }
}
