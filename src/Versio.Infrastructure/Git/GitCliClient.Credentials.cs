using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Versio.Core.Abstractions;
using Versio.Core.Git;
using Versio.Core.Logging;

namespace Versio.Infrastructure.Git;

/// <summary>
/// Whether git can authenticate to the remote, and the two commands that change that.
/// </summary>
/// <remarks>
/// A part of <see cref="GitCliClient"/> rather than a file of its own, split out because the
/// one file had reached 1,544 lines and already carried these section markers. The primary
/// constructor stays on the part that runs the process; nothing here needs its arguments.
/// </remarks>
public sealed partial class GitCliClient
{
    // ------------------------------------------------------------------- credentials
    //
    // NOTHING IN THIS REGION LOGS WHAT IT SENDS OR RECEIVES.
    //
    // `git credential` speaks a key=value protocol over stdin and stdout, and one of those
    // keys is `password`. Redact() covers URLs and would not help here, because the secret
    // arrives on its own line with a label. The host, the outcome and the elapsed time are
    // logged; the payload never is, at any level.

    /// <summary>Whether a credential is already stored for this host, without asking for one.</summary>
    /// <remarks>
    /// <para>
    /// Runs with prompts suppressed, which is what makes it a query rather than a request: with
    /// no credential stored and no way to ask, the helper returns nothing and git fails. That
    /// is the answer, not an error.
    /// </para>
    /// <para>
    /// The presence of a <c>password</c> or <c>username</c> line in the reply is what counts.
    /// A successful exit is not enough on its own — a helper with nothing to say can still exit
    /// zero after echoing back only the protocol and host it was given.
    /// </para>
    /// </remarks>
    public async Task<bool> HasStoredCredentialAsync(
        string protocol, string host, CancellationToken ct)
    {
        if (protocol.Length == 0 || host.Length == 0)
        {
            return false;
        }

        var result = await RunAsync(
            ["credential", "fill"], null, ct, TimeSpan.FromSeconds(15),
            stdin: Query(protocol, host)).ConfigureAwait(false);

        var stored = result.Success && HasSecret(result.StdOut);

        _log.Debug(LogCategory.Git,
                   $"credential for {protocol}://{host}: {(stored ? "stored" : "none")}");

        return stored;
    }

    /// <summary>Ask the configured helper for a credential, letting it prompt.</summary>
    /// <remarks>
    /// The one call in the application that passes <c>allowPrompt</c>. The helper's own window
    /// is the entire point of pressing Sign in, and it can take as long as a person takes —
    /// hence the five-minute ceiling rather than the seconds every other call gets.
    /// </remarks>
    public async Task<GitResult> SignInAsync(string protocol, string host, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, $"asking the credential helper for {protocol}://{host}");

        var result = await RunAsync(
            ["credential", "fill"], null, ct, TimeSpan.FromMinutes(5),
            stdin: Query(protocol, host), allowPrompt: true).ConfigureAwait(false);

        var ok = result.Success && HasSecret(result.StdOut);

        _log.Info(LogCategory.Git,
                  ok ? $"signed in to {host}" : $"no credential obtained for {host}");

        // The stdout carries the secret. Return a result that says only whether it worked.
        return ok
            ? new GitResult { Success = true, ExitCode = 0, Duration = result.Duration }
            : new GitResult
            {
                Success = false,
                ExitCode = result.ExitCode,
                StdErr = result.StdErr,
                Duration = result.Duration,
            };
    }

    /// <summary>Erase the stored credential for a host.</summary>
    /// <remarks>
    /// <c>reject</c> rather than <c>erase</c>: they are the same operation to every helper, and
    /// <c>reject</c> is the documented spelling for "this credential did not work".
    /// </remarks>
    public async Task<GitResult> SignOutAsync(string protocol, string host, CancellationToken ct)
    {
        _log.Info(LogCategory.Git, $"erasing the stored credential for {protocol}://{host}");

        var result = await RunAsync(
            ["credential", "reject"], null, ct, TimeSpan.FromSeconds(30),
            stdin: Query(protocol, host)).ConfigureAwait(false);

        return new GitResult
        {
            Success = result.Success,
            ExitCode = result.ExitCode,
            StdErr = result.StdErr,
            Duration = result.Duration,
        };
    }

    /// <summary>
    /// The credential protocol's request form: key=value lines, then a blank one.
    /// </summary>
    /// <remarks>
    /// The trailing blank line is required and is not decorative — it is what tells git the
    /// request is complete. Without it the helper waits for more input and the call times out.
    /// </remarks>
    private static string Query(string protocol, string host) =>
        $"protocol={protocol}\nhost={host}\n\n";

    /// <summary>Whether a helper's reply actually carried a credential.</summary>
    private static bool HasSecret(string reply) =>
        reply.Contains("password=", StringComparison.Ordinal)
        || reply.Contains("username=", StringComparison.Ordinal);

    /// <summary>Ask the remote what a branch points at, transferring no objects.</summary>
    /// <remarks>
    /// <para>
    /// The full ref name rather than the short one: <c>ls-remote origin main</c> would also
    /// match a tag called <c>main</c>, and answering with the wrong one would make the poller
    /// fetch on every probe for as long as the tag existed.
    /// </para>
    /// <para>
    /// A short timeout because this runs on a timer. A probe that sits for minutes behind a
    /// stalled connection is a probe that has already failed as far as the panel is concerned,
    /// and the next one will ask again anyway.
    /// </para>
    /// </remarks>
    public async Task<RemoteBranchProbe> LsRemoteAsync(
        string remote, string branch, CancellationToken ct)
    {
        var result = await RunAsync(
            ["ls-remote", "--heads", remote, $"refs/heads/{branch}"],
            null, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        if (!result.Success)
        {
            return RemoteBranchProbe.NoAnswer;
        }

        // "<sha>\trefs/heads/<branch>". No line at all means the branch is not on the remote,
        // which is an answer rather than a failure - the exit code is what tells those two
        // apart, and collapsing them into one empty string is how a deleted upstream used to
        // read as an unreachable remote (FINDINGS 2026-08-23).
        var line = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                .FirstOrDefault();

        return new RemoteBranchProbe
        {
            Answered = true,
            Head = line?.Split('\t', ' ')[0].Trim() ?? string.Empty,
        };
    }

    /// <summary>List refs on the remote matching a pattern, transferring no objects.</summary>
    /// <remarks>
    /// <para>
    /// No <c>--heads</c>, which is the whole point: the refs this is for —
    /// <c>refs/pull/*/head</c> and <c>refs/merge-requests/*/head</c> — are outside
    /// <c>refs/heads</c>, and <c>--heads</c> filters them all away. That is also why this could
    /// not be folded into <see cref="LsRemoteAsync"/>.
    /// </para>
    /// <para>
    /// The pattern is one argument in the array, so it reaches git literally: it is never
    /// expanded by a shell, and the glob is git's to interpret. A short timeout because this
    /// runs on the poller's schedule, and the raw lines go back unparsed because parsing
    /// belongs in Core.
    /// </para>
    /// </remarks>
    public async Task<string> LsRemoteRefsAsync(
        string remote, string pattern, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return string.Empty;
        }

        var result = await RunAsync(
            ["ls-remote", remote, pattern], null, ct, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        // Empty on failure. A forge that refuses the probe, a network blip or a remote with no
        // such namespace are indistinguishable here and all mean the same thing to the caller:
        // no number, so the button offers to create one.
        return result.Success ? result.StdOut : string.Empty;
    }

    /// <summary>Resolve a revision locally.</summary>
    /// <remarks>
    /// <c>--verify</c> so an unresolvable revision exits non-zero instead of echoing back what
    /// it was given — without it, a missing upstream would return the string "origin/main" and
    /// be compared against a SHA, which never matches and would fetch on every probe.
    /// </remarks>
    public async Task<string> RevParseAsync(string revision, CancellationToken ct)
    {
        var result = await RunAsync(
            ["rev-parse", "--verify", "--quiet", revision], null, ct, TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        return result.Success ? result.StdOut.Trim() : string.Empty;
    }

    /// <summary>A file's content as HEAD has it, or empty when HEAD has no such file.</summary>
    /// <remarks>
    /// <para>
    /// <b><c>HEAD:./path</c> rather than <c>HEAD:path</c>.</b> Without the <c>./</c> git treats
    /// the argument as relative to the repository root only by luck of the working directory,
    /// and a leading component that happens to match a revision name is ambiguous. The explicit
    /// form is what makes the path a path.
    /// </para>
    /// <para>
    /// <b>Not trimmed, unlike every other reader here.</b> This is file content, and a trailing
    /// newline is part of it — the exporter writes one and hashes what it writes, so trimming
    /// would make every comparison against a recorded hash fail for a reason that has nothing
    /// to do with the file.
    /// </para>
    /// <para>
    /// A missing path, an empty repository and a mirror that was never committed all exit
    /// non-zero and all mean the same thing to the caller: there is no old copy. Reported as
    /// empty rather than thrown, because for the caller it is an ordinary answer.
    /// </para>
    /// </remarks>
    public async Task<string> ShowAtHeadAsync(string path, CancellationToken ct)
    {
        var result = await RunAsync(
            ["show", $"HEAD:./{path.Replace('\\', '/')}"], null, ct, TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);

        return result.Success ? result.StdOut : string.Empty;
    }
}
