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
    public Task<GitResult> VerifyRemoteAsync(string url, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, $"verifying remote {Redact(url)}");
        return RunAsync(["ls-remote", "--heads", url], null, ct, TimeSpan.FromSeconds(20));
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

    private async Task<GitResult> RunAsync(
        string[] args, IProgress<GitProgress>? progress, CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = gitExecutable,
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // git emits UTF-8. Without this, .NET decodes using the console codepage
            // (CP1252 on a typical Windows box) and a path like
            // "Kühlschrank Regelung.st" arrives mangled - the parser then reports a path
            // that does not exist on disk. Same class of bug as the UTF-8 BOM that broke
            // IronPython's json.load: an encoding assumption at a process boundary.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
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
        _log.Debug(LogCategory.Git, display);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

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
