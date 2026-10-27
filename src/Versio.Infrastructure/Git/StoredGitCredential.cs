using System.Diagnostics;
using System.Text;
using Versio.Core.Logging;

namespace Versio.Infrastructure.Git;

/// <summary>
/// The secret git's credential helper already holds for a host, read without prompting.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one place in the application that hands a stored credential back to its caller,
/// and it is deliberate.</b> <c>IGitClient.HasStoredCredentialAsync</c> is built around never
/// returning the value: git needs it, and nothing else in Versio does. The update check is
/// the exception. The releases live in a private GitHub repository, Velopack fetches them over
/// GitHub's API rather than through git, and the API needs the token itself. Asking every user
/// to create and paste a second token for the same account they already signed in with was the
/// alternative, and it was measured unnecessary: on 2026-10-27 the <c>gho_</c> OAuth token GCM
/// stores for github.com read <c>fluidstackio/versio-releases</c> with a 200 (FINDINGS).
/// </para>
/// <para>
/// So the secret goes to exactly one caller - the update source, which passes it to GitHub
/// over HTTPS and holds it no longer than one check - and it is never logged. The host and
/// the outcome are; the payload is not, at any level, the same rule as the credential region
/// of <see cref="GitCliClient"/>.
/// </para>
/// <para>
/// <b>Two environment variables, because one is not enough.</b> <c>GIT_TERMINAL_PROMPT=0</c>
/// stops git asking on a console, and does nothing about Git Credential Manager's own window.
/// <c>GCM_INTERACTIVE=never</c> is what stops that - without it, a launch on a machine with
/// no stored sign-in would pop a GitHub login nobody asked for, seconds after the window opened.
/// </para>
/// <para>
/// Not a method on <see cref="GitCliClient"/>, which is constructed per repository and runs in
/// its working tree. The launch check runs before any repository is open, and may run with
/// none open all session.
/// </para>
/// </remarks>
public static class StoredGitCredential
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The stored password (a token, for GitHub) for the host, or null.</summary>
    /// <remarks>
    /// No git, no helper, no credential and a timeout all mean null rather than an exception:
    /// each is an ordinary answer for a check that runs at every launch. Only the caller's own
    /// cancellation propagates.
    /// </remarks>
    public static async Task<string?> ReadAsync(
        string protocol, string host, IVersioLogger logger, CancellationToken ct)
    {
        var git = GitExecutable.Resolve();
        var psi = new ProcessStartInfo
        {
            FileName = git,
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("credential");
        psi.ArgumentList.Add("fill");
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        GitExecutable.ApplyBundledDefaults(psi, git);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                logger.Warn(LogCategory.Git, $"stored credential for {host}: git did not start");
                return null;
            }

            // The trailing blank line ends the request; without it the helper waits for more.
            await process.StandardInput.WriteAsync($"protocol={protocol}\nhost={host}\n\n")
                .ConfigureAwait(false);
            process.StandardInput.Close();

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Timeout);

            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                logger.Warn(LogCategory.Git,
                    $"stored credential for {host}: the helper did not answer in {Timeout.TotalSeconds:0} s");
                return null;
            }

            var reply = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);

            var secret = process.ExitCode == 0 ? PasswordOf(reply) : null;
            logger.Info(LogCategory.Git,
                $"stored credential for {protocol}://{host}: {(secret is null ? "none" : "found")} " +
                $"({stopwatch.ElapsedMilliseconds} ms)");
            return secret;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or IOException)
        {
            logger.Warn(LogCategory.Git, $"stored credential for {host}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The <c>password=</c> line's value from a helper's reply, or null.</summary>
    public static string? PasswordOf(string reply)
    {
        foreach (var line in reply.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("password=", StringComparison.Ordinal) && trimmed.Length > "password=".Length)
            {
                return trimmed["password=".Length..];
            }
        }

        return null;
    }
}
