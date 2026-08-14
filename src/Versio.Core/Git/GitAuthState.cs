namespace Versio.Core.Git;

/// <summary>Why a network operation did not work, as far as credentials are concerned.</summary>
public enum GitAuthState
{
    /// <summary>Nothing is wrong, or nothing has been attempted.</summary>
    Fine,

    /// <summary>No credential is stored, and Versio suppressed the prompt that would ask.</summary>
    NoCredential,

    /// <summary>A credential exists and the remote refused it. Usually an expired token.</summary>
    Rejected,

    /// <summary>A password was offered where the host requires a token.</summary>
    PasswordNotSupported,

    /// <summary>
    /// Authenticated successfully, and not allowed to do that.
    /// </summary>
    /// <remarks>
    /// <b>Not a credential problem</b>, and keeping it separate is the entire reason this enum
    /// has more than three members. Telling somebody to sign in again when they are already
    /// signed in and simply lack write access sends them round a loop that cannot terminate.
    /// </remarks>
    NotAuthorised,

    /// <summary>An SSH key was not offered, or not accepted. No credential helper is involved.</summary>
    SshKeyRefused,

    /// <summary>The remote could not be reached at all. Nothing to do with credentials.</summary>
    Unreachable,

    /// <summary>It failed for some other reason, which is git's to explain.</summary>
    OtherFailure,
}

/// <summary>
/// What a failed network operation was actually complaining about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Versio does not implement login and must not start.</b> Driving <c>git.exe</c> is what
/// makes Git Credential Manager, SSH agents, 2FA and corporate proxies behave exactly as they
/// do in the engineer's own terminal, and every alternative is worse: storing a token means
/// owning a secret store and a breach story, and a username/password box cannot do 2FA, SSO or
/// device flow, which is most corporate git. So the job here is to <b>detect, explain and hand
/// off</b> — never to store.
/// </para>
/// <para>
/// <b>The most useful signature is one Versio creates itself.</b> Every git invocation sets
/// <c>GIT_TERMINAL_PROMPT=0</c>, which is correct for a GUI — a prompt under a redirected
/// console hangs forever with nothing on screen to explain why. The consequence is that with no
/// credential stored git does not ask, it fails, saying <c>terminal prompts disabled</c>. That
/// is inconvenient as an experience and unambiguous as a signal: it means precisely "no
/// credential is available and we suppressed the ask".
/// </para>
/// <para>
/// <b>Classification is by substring, and that is a deliberate limitation.</b> Git's messages
/// are not a machine format and are localised in some builds. The fallback is
/// <see cref="GitAuthState.OtherFailure"/>, which shows git's own words and offers no advice —
/// which is the correct behaviour for a message nothing recognised. Guessing a remedy from an
/// unrecognised error is how a user ends up re-entering credentials at a firewall.
/// </para>
/// </remarks>
public static class GitAuthClassifier
{
    /// <summary>
    /// Read a failure and say what it was.
    /// </summary>
    /// <param name="stderr">Everything git wrote to standard error.</param>
    /// <param name="transport">
    /// How the remote is reached. An SSH failure and an HTTPS failure can read similarly and
    /// have completely different remedies, and the transport is known without asking git.
    /// </param>
    public static GitAuthState Classify(string? stderr, RemoteTransport transport)
    {
        var text = stderr ?? string.Empty;

        if (text.Length == 0)
        {
            return GitAuthState.Fine;
        }

        // SSH first, and unconditionally: a key refusal says "Permission denied" and so does an
        // HTTPS authorisation failure. Reading the SSH one as a credential problem would offer
        // a sign-in button on a transport that has no credential helper at all.
        if (Contains(text, "permission denied (publickey", "host key verification failed",
                     "could not open a connection to your authentication agent"))
        {
            return GitAuthState.SshKeyRefused;
        }

        // Before the generic rejection tests: this message contains the word "authentication"
        // and means something more specific than a wrong password.
        if (Contains(text, "support for password authentication was removed",
                     "password authentication is temporarily disabled"))
        {
            return GitAuthState.PasswordNotSupported;
        }

        // Versio's own environment variable talking. Unambiguous, and the only signature here
        // that is not a guess about git's phrasing.
        if (Contains(text, "terminal prompts disabled", "could not read username",
                     "could not read password"))
        {
            return GitAuthState.NoCredential;
        }

        // 403 and "write access not granted" mean the credential worked. Checked BEFORE the
        // 401/authentication-failed tests, because a 403 body can mention authentication and
        // reading it as a credential failure is the loop that cannot terminate.
        if (Contains(text, "403", "write access to repository not granted",
                     "you do not have permission", "does not have permission",
                     "read-only", "forbidden"))
        {
            return GitAuthState.NotAuthorised;
        }

        if (Contains(text, "authentication failed", "invalid username or password",
                     "401", "unauthorized", "bad credentials", "access denied"))
        {
            return GitAuthState.Rejected;
        }

        if (Contains(text, "could not resolve host", "could not resolve hostname",
                     "failed to connect", "connection timed out", "connection refused",
                     "network is unreachable", "operation timed out",
                     "unable to access", "ssl certificate problem",
                     "repository not found", "does not appear to be a git repository"))
        {
            // Repository-not-found is here on purpose. On a private repository GitHub returns
            // it for an unauthenticated request as well, so it is genuinely ambiguous — and
            // "check the address and your connection" is safe advice either way, where "sign in
            // again" is wrong half the time.
            return GitAuthState.Unreachable;
        }

        return transport == RemoteTransport.Ssh && Contains(text, "permission denied")
            ? GitAuthState.SshKeyRefused
            : GitAuthState.OtherFailure;
    }

    private static bool Contains(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when signing in could plausibly fix this.</summary>
    /// <remarks>
    /// The gate on the Sign in button. <see cref="GitAuthState.NotAuthorised"/> and
    /// <see cref="GitAuthState.SshKeyRefused"/> are both failures a sign-in cannot touch, and
    /// offering the button on either is offering something that cannot work.
    /// </remarks>
    public static bool SignInWouldHelp(GitAuthState state) =>
        state is GitAuthState.NoCredential
              or GitAuthState.Rejected
              or GitAuthState.PasswordNotSupported;

    /// <summary>True when the stored credential is the thing that is wrong, so it should be
    /// erased before asking for a new one.</summary>
    public static bool ShouldEraseFirst(GitAuthState state) =>
        state is GitAuthState.Rejected or GitAuthState.PasswordNotSupported;

    /// <summary>
    /// True when retrying cannot possibly help.
    /// </summary>
    /// <remarks>
    /// Read by the background poller: an auth failure must stop it <b>immediately</b> rather
    /// than after the usual run of attempts. Retrying a rejected credential every few seconds
    /// cannot succeed and may lock an account, which is a considerably worse outcome than a
    /// stale count.
    /// </remarks>
    public static bool IsTerminal(GitAuthState state) =>
        state is GitAuthState.NoCredential
              or GitAuthState.Rejected
              or GitAuthState.PasswordNotSupported
              or GitAuthState.NotAuthorised
              or GitAuthState.SshKeyRefused;

    /// <summary>What to tell the user, naming the host so the sentence is about somewhere.</summary>
    public static string Explain(GitAuthState state, string host) =>
        state switch
        {
            GitAuthState.Fine => string.Empty,

            GitAuthState.NoCredential => host.Length > 0
                ? $"Sign in to {host} to fetch, pull or push."
                : "Sign in to the remote to fetch, pull or push.",

            GitAuthState.Rejected =>
                $"{Where(host)} refused the saved credentials. They have probably expired.",

            GitAuthState.PasswordNotSupported =>
                $"{Where(host)} no longer accepts passwords. Sign in again and use a token.",

            GitAuthState.NotAuthorised =>
                $"You are signed in to {Lower(host)}, but this account cannot write to this " +
                "repository. Ask for access — signing in again will not change it.",

            GitAuthState.SshKeyRefused =>
                $"{Where(host)} refused the SSH key. This remote uses keys rather than a " +
                "credential helper, so check your key and ssh-agent — try ssh -T first.",

            GitAuthState.Unreachable =>
                $"{Where(host)} could not be reached. Check the address and your connection.",

            _ => string.Empty,
        };

    private static string Where(string host) => host.Length > 0 ? host : "The remote";

    private static string Lower(string host) => host.Length > 0 ? host : "the remote";
}
