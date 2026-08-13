using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Versio.Core.Abstractions;
using Versio.Core.Git;
using Versio.Core.Logging;

namespace Versio.Infrastructure.Git;

/// <summary>
/// <see cref="IGitClient"/> over the real <c>git</c> executable.
/// </summary>
/// <remarks>
/// <para>
/// Using the CLI rather than a library is what makes credentials work: Git Credential
/// Manager, SSH agents, 2FA, corporate proxies and SSO all behave exactly as they do in the
/// engineer's terminal, because it <i>is</i> the engineer's terminal client.
/// </para>
/// <para>
/// <b>Every invocation is logged with its outcome</b>, not just its command line. A git
/// command that fails silently is indistinguishable from one that was never run, and the
/// difference matters when the question is why a commit did not appear. Remote URLs are
/// redacted first — see <see cref="Redact"/>.
/// </para>
/// </remarks>
public sealed partial class GitCliClient(
    string repositoryPath,
    string gitExecutable = "git",
    IVersioLogger? logger = null) : IGitClient
{
    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    private bool? _lfsAvailable;

    /// <summary>Working tree root that every command runs against.</summary>
    public string RepositoryPath => repositoryPath;

    /// <summary>
    /// Ask git itself whether it will operate here.
    /// </summary>
    /// <remarks>
    /// The authoritative check. <c>RepositoryLocator</c> inspects the filesystem and can
    /// only see whether <c>.git</c> <i>looks</i> right; a corrupt or partially-cloned
    /// repository passes that inspection and still fails every real command.
    /// </remarks>
    public async Task<bool> IsRepositoryAsync(CancellationToken ct)
    {
        var result = await RunAsync(["rev-parse", "--is-inside-work-tree"], null, ct)
            .ConfigureAwait(false);
        return result.Success && result.StdOut.Trim() == "true";
    }

    /// <summary>The version of the git that will actually run.</summary>
    /// <remarks>
    /// Never throws for a missing git: this is called to diagnose an environment, and the
    /// environment being broken is the answer rather than an error to raise.
    /// </remarks>
    public async Task<string> VersionAsync(CancellationToken ct)
    {
        var result = await RunAsync(["--version"], null, ct, TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        return result.Success
            ? result.StdOut.Trim()
            : result.FailureReason ?? "git could not be run";
    }

    /// <summary>Current branch, divergence counts, and every changed path.</summary>
    /// <remarks>Porcelain v2 is git's stable machine format; the human output is localised
    /// and reflows between versions.</remarks>
    public async Task<GitStatus> StatusAsync(CancellationToken ct)
    {
        // -z because git otherwise quotes and escapes paths with spaces or non-ASCII, and
        // CODESYS object names routinely contain both.
        var result = await RunAsync(
            ["status", "--porcelain=v2", "--branch", "--untracked-files=all", "-z"],
            null, ct).ConfigureAwait(false);

        return PorcelainV2Parser.Parse(result.StdOut);
    }

    /// <summary>Local branches with their upstream and divergence counts.</summary>
    /// <remarks>Ahead/behind needs a second command per branch: <c>for-each-ref</c> cannot
    /// produce both counts in one pass consistently across git versions.</remarks>
    public async Task<IReadOnlyList<BranchInfo>> BranchesAsync(CancellationToken ct)
    {
        // %(if)... would be neater but is fussy across git versions; a fixed field layout
        // with a separator that cannot appear in a ref name is more robust.
        var result = await RunAsync(
        [
            "for-each-ref", "--format=%(refname:short)%1F%(upstream:short)%1F%(HEAD)",
            "refs/heads",
        ], null, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            return [];
        }

        var branches = new List<BranchInfo>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('');
            if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            var (ahead, behind) = string.IsNullOrWhiteSpace(parts[1])
                ? (0, 0)
                : await AheadBehindAsync(parts[0], parts[1], ct).ConfigureAwait(false);

            branches.Add(new BranchInfo
            {
                Name = parts[0],
                Upstream = string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1],
                IsCurrent = parts[2].Trim() == "*",
                Ahead = ahead,
                Behind = behind,
            });
        }

        return branches;
    }

    private async Task<(int Ahead, int Behind)> AheadBehindAsync(
        string branch, string upstream, CancellationToken ct)
    {
        var result = await RunAsync(
            ["rev-list", "--left-right", "--count", $"{upstream}...{branch}"], null, ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return (0, 0);
        }

        var parts = result.StdOut.Trim().Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var behind) &&
               int.TryParse(parts[1], out var ahead)
            ? (ahead, behind)
            : (0, 0);
    }

    /// <summary>Stage the given paths. An empty list is a no-op, not an error — the caller
    /// usually got it from a selection that happened to be empty.</summary>
    /// <remarks>The <c>--</c> separator matters: without it a path that looks like a flag,
    /// or matches a branch name, is interpreted as one.</remarks>
    public Task<GitResult> StageAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        paths.Count == 0
            ? Task.FromResult(NoOp())
            : RunAsync(["add", "--", .. paths], null, ct);

    /// <summary>Remove the given paths from the index, leaving the working tree alone.</summary>
    public Task<GitResult> UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        paths.Count == 0
            ? Task.FromResult(NoOp())
            : RunAsync(["restore", "--staged", "--", .. paths], null, ct);

    /// <summary>Commit whatever is staged.</summary>
    /// <remarks>An empty message is refused here rather than by git, so the user gets a
    /// sentence instead of git's exit code and an editor that never opens under a
    /// redirected console.</remarks>
    public Task<GitResult> CommitAsync(string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _log.Warn(LogCategory.Git, "commit refused: a commit message is required");
            return Task.FromResult(new GitResult
            {
                Success = false, ExitCode = -1, StdErr = "a commit message is required",
            });
        }

        // The subject line only. A commit message can be arbitrarily long, and the log wants
        // enough to identify the commit, not a copy of it.
        _log.Info(LogCategory.Git,
            $"committing: {message.Split('\n')[0].TrimEnd('\r')}");

        return RunAsync(["commit", "-m", message], null, ct);
    }

    /// <summary>Throw away working-tree changes to the given paths.</summary>
    /// <remarks><b>Destructive and unrecoverable</b> — the content is not in git anywhere.
    /// Callers must confirm first. Also releases the direction lock (plan §6.5).</remarks>
    public Task<GitResult> DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0)
        {
            return Task.FromResult(NoOp());
        }

        // Warning, and every path by name. This is the one operation here that destroys
        // work no other copy of exists — if someone comes back asking where their edits
        // went, this line is the answer.
        _log.Warn(LogCategory.Git,
            $"discarding working-tree changes to {paths.Count} path(s): {string.Join(", ", paths)}");

        return RunAsync(["restore", "--worktree", "--", .. paths], null, ct);
    }

    /// <summary>Create a branch, optionally checking it out.</summary>
    public Task<GitResult> CreateBranchAsync(string name, bool checkout, CancellationToken ct)
    {
        _log.Info(LogCategory.Git,
            checkout ? $"creating branch {name} and switching to it" : $"creating branch {name}");
        return RunAsync(checkout ? ["checkout", "-b", name] : ["branch", name], null, ct);
    }

    /// <summary>Switch branches.</summary>
    /// <remarks>Fails on a dirty working tree, which is git's behaviour and the right one:
    /// switching under unsynced CODESYS work is how it gets lost.</remarks>
    public Task<GitResult> CheckoutAsync(string branch, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, $"switching to branch {branch}");
        return RunAsync(["checkout", branch], null, ct);
    }

    /// <summary>Check a remote URL is reachable and the credentials work.</summary>
    /// <remarks><c>ls-remote</c> is a real reachability <i>and</i> auth check that transfers
    /// no objects, so the clone dialog can validate before committing to a long download.</remarks>
    /// <summary>Where this repository was cloned from, or empty.</summary>
    /// <remarks>
    /// <c>config --get</c> rather than <c>remote get-url</c>: both read the same value, and
    /// the config form exits 1 for "no such key" where the remote form writes an error to
    /// stderr for a repository that simply has no remote. This is asked of every repository
    /// that is opened, so the no-remote case has to be quiet.
    /// </remarks>
    public async Task<string> RemoteUrlAsync(string remote, CancellationToken ct)
    {
        var result = await RunAsync(
            ["config", "--get", $"remote.{remote}.url"], null, ct, TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        return result.Success ? result.StdOut.Trim() : string.Empty;
    }

    public Task<GitResult> VerifyRemoteAsync(string url, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, $"verifying remote {Redact(url)}");
        return RunAsync(["ls-remote", "--heads", url], null, ct, TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// Check a remote is reachable when no repository is open yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The clone dialog's Verify button, which by definition runs before there is a repository
    /// to run inside. <c>ls-remote</c> needs no repository — only a working directory that
    /// exists — so the temp folder is used and nothing is written to it.
    /// </para>
    /// <para>
    /// The same check, deliberately, as the instance method: reachability <i>and</i>
    /// authentication, transferring no objects. Verifying with anything weaker would let a
    /// dialog say "reachable" and then fail minutes into a download.
    /// </para>
    /// </remarks>
    public static Task<GitResult> VerifyRemoteAsync(
        string url, string gitExecutable, IVersioLogger? logger, CancellationToken ct) =>
        new GitCliClient(Path.GetTempPath(), gitExecutable, logger).VerifyRemoteAsync(url, ct);

    /// <summary>
    /// Clone a repository into <paramref name="targetDirectory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Static, and deliberately not on <see cref="IGitClient"/>.</b> Every other operation
    /// here runs <i>inside</i> a repository, which is what the interface models and what every
    /// implementation of it can assume. A clone has no repository yet: it runs in the parent
    /// directory and creates one. Putting it on the seam would force a repo-less special case
    /// into every future implementation for the sake of one call made once per repository.
    /// </para>
    /// <para>
    /// The parent is where git is run, and the target's own name is passed as the argument, so
    /// git creates the folder itself. Running in the target and cloning into <c>.</c> would
    /// require the folder to exist first and would silently accept a non-empty one.
    /// </para>
    /// <para>
    /// <c>--progress</c> because git only emits counters when it believes it is talking to a
    /// terminal, and this is exactly the operation somebody watches: a clone of a repository
    /// full of PLC binaries is minutes of apparently nothing.
    /// </para>
    /// </remarks>
    public static Task<GitResult> CloneAsync(
        string url, string targetDirectory, string gitExecutable,
        IProgress<GitProgress>? progress, IVersioLogger? logger, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(targetDirectory));
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(targetDirectory));

        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
        {
            // A path with no parent is a drive root, and cloning into one is never what was
            // meant. Refused with a reason rather than handed to git, which would clone the
            // repository into the root of the disk.
            //
            // The reason travels in StdErr because GitResult derives FailureReason from it —
            // git's own words when there are any, and this refusal happens before git runs.
            return Task.FromResult(new GitResult
            {
                Success = false,
                ExitCode = -1,
                StdErr = $"'{targetDirectory}' is not a folder a repository can be cloned into.",
            });
        }

        // The parent must exist before git runs, because it is the working directory of the
        // process rather than something git creates.
        Directory.CreateDirectory(parent);

        var client = new GitCliClient(parent, gitExecutable, logger);
        client._log.Info(LogCategory.Git, $"cloning {Redact(url)} into {targetDirectory}");

        return client.RunAsync(["clone", "--progress", url, name], progress, ct,
                               TimeSpan.FromMinutes(30));
    }

    /// <summary>Fetch from the remote without touching the working tree.</summary>
    public Task<GitResult> FetchAsync(IProgress<GitProgress>? progress, CancellationToken ct)
    {
        // The three network operations get a start line each, because they are the ones that
        // can sit for minutes behind an auth prompt that will never appear.
        _log.Info(LogCategory.Git, "fetching");
        return RunAsync(["fetch", "--progress"], progress, ct, TimeSpan.FromMinutes(10));
    }

    /// <summary>Fast-forward the current branch from its upstream.</summary>
    /// <remarks><c>--ff-only</c> is deliberate: a divergence is surfaced rather than
    /// auto-merged. Automatically merging native CODESYS XML would produce a file nobody
    /// reviewed, in a format nobody can review afterwards.</remarks>
    public Task<GitResult> PullAsync(IProgress<GitProgress>? progress, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, "pulling (fast-forward only)");
        return RunAsync(["pull", "--ff-only", "--progress"], progress, ct, TimeSpan.FromMinutes(10));
    }

    /// <summary>Push the current branch.</summary>
    /// <remarks>Success is what releases the direction lock (plan §6.5): until work is
    /// pushed nobody else can see it, so the direction is still meaningfully owned.</remarks>
    public Task<GitResult> PushAsync(IProgress<GitProgress>? progress, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, "pushing");
        return RunAsync(["push", "--progress"], progress, ct, TimeSpan.FromMinutes(10));
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

    private static GitResult NoOp() =>
        new() { Success = true, ExitCode = 0 };

    /// <param name="stdin">
    /// Written to the child's standard input and then closed, for the commands that read a
    /// patch rather than take one as an argument.
    /// </param>
    private async Task<GitResult> RunAsync(
        string[] args, IProgress<GitProgress>? progress, CancellationToken ct,
        TimeSpan? timeout = null, string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = gitExecutable,
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,

            // git emits UTF-8. Without this, .NET decodes using the console codepage
            // (CP1252 on a typical Windows box) and a path like
            // "Kühlschrank Regelung.st" arrives mangled - the parser then reports a path
            // that does not exist on disk. Same class of bug as the UTF-8 BOM that broke
            // IronPython's json.load: an encoding assumption at a process boundary.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),

            // And the same on the way in. A patch carries whatever was in the file - a
            // German object name, a comment with an accent - and encoding it with the console
            // codepage would hand git a patch that no longer matches the file it came from.
            StandardInputEncoding = stdin is null ? null : new UTF8Encoding(false),
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // Never let git open an interactive prompt: under a redirected console it would
        // hang forever with nothing on screen to explain why.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var gate = new object();
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (gate)
            {
                stdout.Append(e.Data).Append('\n');
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (gate)
            {
                stderr.Append(e.Data).Append('\n');
            }

            // git writes PROGRESS to stderr. Treating stderr as an error channel would
            // report every successful push as a failure.
            if (progress is not null)
            {
                foreach (var p in GitProgressParser.Parse(e.Data))
                {
                    progress.Report(p);
                }
            }
        };

        var display = $"git {string.Join(' ', args.Select(Redact))}";

        // Announced only if it is still running when the reader would start to wonder.
        //
        // This used to log unconditionally before the process started, so every git call
        // cost two lines - the command, then the same command again with a duration. On the
        // status backstop, which runs every 20 s forever, that is the bulk of an idle log.
        // The pair only ever earned its keep for a command that hangs or never returns, so
        // that is now the only case that gets it, and the completion lines below are
        // unchanged: a command that finished says so once, with its duration.
        using var announce = new CancellationTokenSource();
        var announced = AnnounceIfSlowAsync(display, announce.Token);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            // Written before the wait, and closed rather than left open: git reads a patch
            // from "-" until end of file, so a stream that is never closed is a process that
            // never exits and a UI that waits out the two-minute timeout for nothing.
            await process.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited between the check and the kill.
            }

            if (!timedOut)
            {
                throw;
            }
        }

        await Task.Run(() => process.WaitForExit(), CancellationToken.None).ConfigureAwait(false);
        stopwatch.Stop();

        // Whatever happened, the command is no longer in flight, so the announcement is
        // cancelled before any outcome is logged - otherwise a command finishing at the
        // threshold could report itself as still running after its own result.
        await announce.CancelAsync().ConfigureAwait(false);
        await announced.ConfigureAwait(false);

        string outText, errText;
        lock (gate)
        {
            outText = stdout.ToString();
            errText = stderr.ToString();
        }

        var exitCode = timedOut ? -1 : SafeExitCode(process);

        if (timedOut)
        {
            _log.Error(LogCategory.Git,
                $"{display} timed out after {stopwatch.Elapsed.TotalSeconds:F1}s and was killed");
        }
        else if (exitCode != 0)
        {
            // The stderr tail is the whole diagnosis for a failed git command — "not a git
            // repository", "rejected, non-fast-forward", an auth prompt that could not open.
            _log.Warn(LogCategory.Git,
                $"{display} exited {exitCode} in {stopwatch.Elapsed.TotalSeconds:F1}s" +
                $"{Tail(errText)}");
        }
        else
        {
            _log.Debug(LogCategory.Git,
                $"{display} ok in {stopwatch.Elapsed.TotalSeconds:F1}s");
        }

        return new GitResult
        {
            Success = !timedOut && exitCode == 0,
            ExitCode = exitCode,
            StdOut = outText,
            StdErr = errText,
            Duration = stopwatch.Elapsed,
        };
    }

    /// <summary>
    /// Say what is running, but only once it has run long enough to be worth saying.
    /// </summary>
    /// <remarks>
    /// One second is the threshold because it is roughly where a person stops reading a
    /// pause as instant. Every routine git call in this app finishes in tens of
    /// milliseconds, so in normal use this writes nothing at all; a fetch against an
    /// unreachable remote, an auth prompt that cannot open, or a hung index lock all cross
    /// it, and those are precisely the cases where knowing which command is outstanding is
    /// the whole diagnosis.
    /// </remarks>
    private async Task AnnounceIfSlowAsync(string display, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            _log.Debug(LogCategory.Git, $"{display} — still running");
        }
        catch (OperationCanceledException)
        {
            // Finished before the threshold. The completion line covers it.
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Remove credentials from anything that looks like a remote URL.
    /// </summary>
    /// <remarks>
    /// <c>https://user:token@host/repo.git</c> is an ordinary remote, and a log file is
    /// something users attach to bug reports and paste into chat. The userinfo section is
    /// dropped rather than masked in place so that no part of the token's length or shape
    /// survives. Applied to every argument rather than only to <c>ls-remote</c>, because the
    /// point of a redaction is that it does not depend on remembering to apply it.
    /// </remarks>
    internal static string Redact(string argument) =>
        CredentialsInUrl().Replace(argument, "$1");

    /// <summary>Matches the <c>user:password@</c> in a URL, capturing the scheme so the
    /// replacement can keep it.</summary>
    [GeneratedRegex(@"\b([a-zA-Z][a-zA-Z0-9+.\-]*://)[^/\s:@]+(?::[^/\s@]*)?@")]
    private static partial Regex CredentialsInUrl();

    /// <summary>
    /// The last few lines of a stream, for appending to a one-line log entry.
    /// </summary>
    /// <remarks>
    /// The tail rather than the head: git puts the reason a command failed at the end, after
    /// whatever progress it had already printed. Capped because a failed fetch can emit
    /// megabytes and the log file is not the place to keep all of it.
    /// </remarks>
    private static string Tail(string text, int lines = 3, int maxChars = 500)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var kept = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                       .Select(l => l.TrimEnd('\r'))
                       .Where(l => l.Length > 0)
                       .TakeLast(lines);

        var joined = Redact(string.Join(" | ", kept));
        return $": {(joined.Length > maxChars ? joined[..maxChars] + "…" : joined)}";
    }
}
