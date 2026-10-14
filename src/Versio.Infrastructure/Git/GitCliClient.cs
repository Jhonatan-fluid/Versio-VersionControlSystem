using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Versio.Core.Abstractions;
using Versio.Core.Git;
using Versio.Core.Logging;
using Versio.Infrastructure.Platform;

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

    /// <summary>
    /// Tracked and untracked paths, gitignored files excluded.
    /// </summary>
    /// <remarks>
    /// <c>-z</c> for the same reason status uses it: paths with spaces or non-ASCII must
    /// round-trip. Parsed in Core so an empty listing and a quoted name are tested without
    /// spawning git.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ListWorkingTreeAsync(CancellationToken ct)
    {
        var result = await RunAsync(
            ["ls-files", "-z", "-c", "-o", "--exclude-standard"],
            null, ct).ConfigureAwait(false);

        return result.Success
            ? WorkingTreeListParser.Parse(result.StdOut)
            : [];
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

    /// <summary>How far a branch has diverged from its upstream, or (0, 0).</summary>
    /// <remarks>
    /// <b>Quiet on failure, because the ordinary failure here is not a fault.</b>
    /// <c>%(upstream:short)</c> names the upstream a branch is <i>configured</i> with, which
    /// outlives the remote-tracking ref itself: a branch pushed and later pruned, or one
    /// cloned with a config the remote never had, both report an upstream that
    /// <c>rev-list</c> then exits 128 on. This is asked of every local branch on every
    /// refresh, so it produced a <c>WRN</c> per such branch at every launch — two of them on
    /// this repository — for a state nobody can act on. The generic failure line is exactly
    /// right for the commands it was written for and wrong for this one; the same argument
    /// <see cref="RemoteUrlAsync"/> makes by choosing a command form that exits 0.
    /// <para>
    /// Not silenced, demoted: the line is still there at Debug, where somebody asking why a
    /// count reads zero will find it.
    /// </para>
    /// </remarks>
    private async Task<(int Ahead, int Behind)> AheadBehindAsync(
        string branch, string upstream, CancellationToken ct)
    {
        var result = await RunAsync(
            ["rev-list", "--left-right", "--count", $"{upstream}...{branch}"], null, ct,
            quietFailure: true)
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
    public Task<GitResult> StageAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        paths.Count == 0
            ? Task.FromResult(NoOp())
            : RunWithPathspecsAsync(["add"], paths, ct);

    /// <summary>Remove the given paths from the index, leaving the working tree alone.</summary>
    public Task<GitResult> UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        paths.Count == 0
            ? Task.FromResult(NoOp())
            : RunWithPathspecsAsync(["restore", "--staged"], paths, ct);

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

    /// <summary>Rewrite the last commit's message, and nothing else.</summary>
    /// <remarks>
    /// <c>--only</c> with no pathspec is the load-bearing switch: git documents exactly this
    /// combination as "amend the last commit without committing changes that have already
    /// been staged". Without it, a stray <c>git add</c> — or Versio's own background staging
    /// — would ride silently into the rewritten commit. See the interface remarks for why
    /// that would be the worst version of this feature.
    /// </remarks>
    public Task<GitResult> AmendCommitMessageAsync(string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _log.Warn(LogCategory.Git, "amend refused: a commit message is required");
            return Task.FromResult(new GitResult
            {
                Success = false, ExitCode = -1, StdErr = "a commit message is required",
            });
        }

        _log.Info(LogCategory.Git,
            $"amending the last commit's message to: {message.Split('\n')[0].TrimEnd('\r')}");

        return RunAsync(["commit", "--amend", "--only", "-m", message], null, ct);
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

        return RunWithPathspecsAsync(["restore", "--worktree"], paths, ct);
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

    /// <summary>Where git keeps its own state for this working tree.</summary>
    public async Task<string> GitDirectoryAsync(CancellationToken ct)
    {
        var result = await RunAsync(
            ["rev-parse", "--absolute-git-dir"], null, ct, TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        return result.Success ? result.StdOut.Trim() : string.Empty;
    }

    /// <summary>Who git will attribute a commit to, as git itself resolves it.</summary>
    /// <remarks>
    /// Plain <c>--get</c> rather than <c>--local --get</c>: a name inherited from the global or
    /// system config is a working identity, and reporting it as missing would nag every user
    /// who configured git once, years ago, the way everybody is told to.
    /// <para>
    /// Exit 1 means "no such key", which is the ordinary answer on a fresh machine and not a
    /// failure worth logging as one — so both calls fall back to empty rather than throwing.
    /// </para>
    /// </remarks>
    public async Task<GitIdentity> IdentityAsync(CancellationToken ct)
    {
        var name = await RunAsync(["config", "--get", "user.name"], null, ct,
                                  TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var email = await RunAsync(["config", "--get", "user.email"], null, ct,
                                   TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        var identity = GitIdentity.From(
            name.Success ? name.StdOut : string.Empty,
            email.Success ? email.StdOut : string.Empty);

        _log.Debug(LogCategory.Git,
            identity.IsComplete
                ? $"identity: {identity.Name} <{identity.Email}>"
                : $"identity incomplete: {identity.Explanation}");

        return identity;
    }

    /// <summary>Write a name and email into git's configuration.</summary>
    /// <remarks>
    /// Two writes, and the second is attempted even when the first fails, so a half-configured
    /// machine does not stay half-configured because of one bad value. The failure returned is
    /// the first one, since that is the one whose message explains what went wrong.
    /// </remarks>
    public async Task<GitResult> SetIdentityAsync(
        string name, string email, bool global, CancellationToken ct)
    {
        var scope = global ? "--global" : "--local";
        _log.Info(LogCategory.Git, $"setting {scope} identity to {name} <{email}>");

        var wroteName = await RunAsync(["config", scope, "user.name", name], null, ct,
                                       TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var wroteEmail = await RunAsync(["config", scope, "user.email", email], null, ct,
                                        TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        if (!wroteName.Success)
        {
            _log.Error(LogCategory.Git, $"could not write user.name: {wroteName.FailureReason}");
            return wroteName;
        }

        if (!wroteEmail.Success)
        {
            _log.Error(LogCategory.Git, $"could not write user.email: {wroteEmail.FailureReason}");
        }

        return wroteEmail;
    }

    public Task<GitResult> VerifyRemoteAsync(string url, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, $"verifying remote {Redact(url)}");
        return RunAsync(["ls-remote", "--heads", url], null, ct, TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// Ask whether git can be run at all, before any repository is open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Static, for the same reason <see cref="CloneAsync"/> is:</b> it runs where there is no
    /// repository. It runs at launch, so the one machine that needs this answered — the one
    /// without git — gets it before anything else has a chance to fail obscurely.
    /// </para>
    /// <para>
    /// A missing executable surfaces as <see cref="System.ComponentModel.Win32Exception"/> from
    /// the process start, not as a non-zero exit code, so it is caught rather than read off a
    /// <see cref="GitResult"/>. <see cref="IOException"/> comes with it because a PATH entry on
    /// a dead network share throws that instead, and both mean the same thing to a user.
    /// </para>
    /// <para>
    /// The executable's own path is resolved separately and its failure is not the probe's
    /// failure: <c>--version</c> having succeeded already proves git runs, and the path is a
    /// diagnostic nicety for the machines carrying several gits.
    /// </para>
    /// </remarks>
    public static async Task<GitPresence> ProbeAsync(
        string gitExecutable, IVersioLogger? logger, CancellationToken ct)
    {
        var log = logger ?? NullLogger.Instance;

        try
        {
            var client = new GitCliClient(Path.GetTempPath(), gitExecutable, logger);
            var version = await client.VersionAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(version))
            {
                log.Warn(LogCategory.Git, "git ran but reported no version");
                return GitPresence.NotFound("git ran but reported no version");
            }

            var path = await ResolveExecutableAsync(gitExecutable, ct).ConfigureAwait(false);
            log.Info(LogCategory.Git,
                     $"git found: {version.Trim()}" + (path.Length == 0 ? "" : $" at {path}"));

            return GitPresence.Found(version, path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            log.Error(LogCategory.Git, $"git could not be run: {ex.Message}", ex);
            return GitPresence.NotFound(ex.Message);
        }
    }

    /// <summary>Where the <c>git</c> that will run actually lives, or empty.</summary>
    /// <remarks>
    /// <c>git --exec-path</c> reports git's own libexec directory rather than the executable, so
    /// the question is put to the OS instead. A failure here is silent by design — see
    /// <see cref="ProbeAsync"/>.
    /// </remarks>
    private static async Task<string> ResolveExecutableAsync(string gitExecutable, CancellationToken ct)
    {
        if (Path.IsPathRooted(gitExecutable))
        {
            return gitExecutable;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add(gitExecutable);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return string.Empty;
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            // `where` lists every match, one per line. The first is the one PATH resolves to.
            return process.ExitCode == 0
                ? output.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return string.Empty;
        }
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

        // --prune on every fetch, not only after an in-app deletion. Without it a branch the
        // forge deleted - the ordinary end of every merged PR - leaves its tracking ref
        // behind, porcelain keeps counting against the ghost, and the panel says "Up to date
        // with origin/x" for a branch nobody else can see (FINDINGS 2026-08-23). There is no
        // scenario in which a tracking ref for a deleted branch is worth keeping.
        return RunAsync(["fetch", "--prune", "--progress"], progress, ct,
                        TimeSpan.FromMinutes(10));
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

    private static GitResult NoOp() =>
        new() { Success = true, ExitCode = 0 };

    /// <summary>
    /// Run a git verb whose operands are pathspecs, without putting them on argv.
    /// </summary>
    /// <remarks>
    /// Windows CreateProcess refuses a command line over 32,767 characters
    /// (Win32 206, "The filename or extension is too long"). Staging a TIA
    /// tree is hundreds of long paths — measured 2026-08-21: the panel's
    /// Stage all &amp; commit handed <c>git add --</c> 336 of them and the
    /// process never started. <c>--pathspec-from-file=-</c> with NUL
    /// separators keeps argv short. It also accepts a path that looks like
    /// a flag, which is why <c>--</c> was on the argv form.
    /// </remarks>
    private Task<GitResult> RunWithPathspecsAsync(
        string[] verb, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var stdin = string.Join('\0', paths);
        string[] args = [.. verb, "--pathspec-from-file=-", "--pathspec-file-nul"];
        return RunAsync(args, null, ct, stdin: stdin);
    }

    /// <param name="stdin">
    /// Written to the child's standard input and then closed, for the commands that read a
    /// patch rather than take one as an argument.
    /// </param>
    /// <param name="allowPrompt">
    /// Let git's credential helper put its own window up.
    /// <para>
    /// <b>False for everything except an explicit, user-pressed sign-in.</b> Every other call
    /// here can run without anybody watching - a status refresh, the background poller, a fetch
    /// - and a credential prompt on one of those either hangs behind a console that does not
    /// exist or pops a window nobody asked for. So prompts are suppressed by default and git
    /// fails fast instead, with a message this application recognises.
    /// </para>
    /// <para>
    /// It is exactly one moment that wants the opposite: the user pressed Sign in, and the
    /// helper's own window - GCM, a browser for OAuth, a token box - is the entire point.
    /// </para>
    /// </param>
    private async Task<GitResult> RunAsync(
        string[] args, IProgress<GitProgress>? progress, CancellationToken ct,
        TimeSpan? timeout = null, string? stdin = null, bool allowPrompt = false,
        bool quietFailure = false)
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

        // Never let git open an interactive prompt - except for the one call that asked for
        // one. Under a redirected console a prompt hangs forever with nothing on screen to
        // explain why, which is why this is the default and not the exception.
        //
        // The cost of suppressing it is a specific failure message, and that turned out to be
        // a benefit: "terminal prompts disabled" is unambiguous, because we are the ones who
        // caused it. GitAuthClassifier reads it as "no credential stored".
        if (!allowPrompt)
        {
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        }
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        GitExecutable.ApplyBundledDefaults(psi, gitExecutable);

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

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The process never started, so the slow-announce must not go on to say
            // "still running" about it — with the repository folder renamed out from
            // under an open session, every 20 s status poll logged exactly that line a
            // second after its start had already failed (FINDINGS 2026-10-14). Cancelled
            // here because the announcer was armed above, before the start could fail;
            // the throw still reaches the caller, which owns saying what it means.
            await announce.CancelAsync().ConfigureAwait(false);
            _log.Warn(LogCategory.Git, $"{display} could not start: {ex.Message}");
            throw;
        }

        ChildProcess.AssignToSession(process);
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
                // The success path cancels the slow-announce after the wait. Throwing
                // here used to skip that, so a cancelled command could still log
                // "still running" a second later — a line that looks like the kill missed.
                await announce.CancelAsync().ConfigureAwait(false);
                _log.Warn(LogCategory.Git, $"{display} cancelled — killed the process tree");
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
            //
            // At Debug for the one caller whose failure is an ordinary answer rather than
            // news. See AheadBehindAsync: the alternative is to say nothing at all, and a
            // command that ran and failed leaving no trace is the shape of bug this whole
            // line exists to prevent.
            _log.Log(quietFailure ? LogLevel.Debug : LogLevel.Warning, LogCategory.Git,
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
