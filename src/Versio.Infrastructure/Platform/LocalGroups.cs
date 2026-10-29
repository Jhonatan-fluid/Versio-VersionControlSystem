using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Versio.Core.Logging;
using Versio.Core.Plc;

namespace Versio.Infrastructure.Platform;

/// <summary>Where the current account stands with respect to one Windows local group.</summary>
/// <remarks>
/// Four answers, because two different things can be wrong and they have different fixes. A
/// process is admitted to a group-gated API by its <i>logon token</i>, and the token is
/// built at sign-in: an account added to a group five minutes ago is a member and is still
/// refused until it signs out and in. Telling those apart is what makes the message right
/// instead of merely true — on 2026-10-29 the owner ran the add, then <c>whoami /groups</c>,
/// and saw nothing, which looked like the add had failed.
/// </remarks>
public enum LocalGroupStanding
{
    /// <summary>The group is in the process token: the gated API will admit this process.</summary>
    InToken,

    /// <summary>The account is a member, but this sign-in predates the membership.</summary>
    MemberNotInToken,

    /// <summary>The group exists and the account is not in it.</summary>
    NotMember,

    /// <summary>No such local group on this machine.</summary>
    GroupMissing,

    /// <summary>The check itself failed; <see cref="LocalGroupCheck.Detail"/> says how.</summary>
    Unknown,
}

/// <summary>One check of one group for the current account.</summary>
/// <param name="Standing">The answer.</param>
/// <param name="Account">The account as Windows names it, e.g. <c>AzureAD\Name</c>.</param>
/// <param name="Sid">The account's SID — what the elevated join is given, because the
/// elevated process may run as a different account (a standard user typing an
/// administrator's credentials into the prompt) and must not add <i>itself</i>.</param>
/// <param name="Detail">What was looked at, for the log; the failure for <see cref="LocalGroupStanding.Unknown"/>.</param>
public sealed record LocalGroupCheck(LocalGroupStanding Standing, string Account, string Sid, string Detail);

/// <summary>
/// Local group membership of the current account: read it, and join a group with the user's
/// approval.
/// </summary>
/// <remarks>
/// Neutral: it knows about Windows groups, not about which vendor gates what behind one. The
/// vendor pack names the group (TIA Portal's <i>Siemens TIA Openness</i>) and asks here.
/// </remarks>
public interface ILocalGroups
{
    LocalGroupCheck Check(string groupName);

    /// <summary>
    /// Add the account to the group through an elevated helper, after Windows has asked the
    /// user for approval. Never silent: a declined prompt is a refusal, not an error.
    /// </summary>
    Task<PlcRemedyOutcome> JoinElevatedAsync(string groupName, LocalGroupCheck account, CancellationToken ct);
}

/// <summary>
/// The real thing: the process token, <c>NetLocalGroupGetMembers</c> for the member list,
/// and a re-launch of this executable with the <c>runas</c> verb for the add.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a re-launch.</b> Adding a member to a local group needs administrator rights, and
/// Versio runs — and installs — per user, without them. The only honest way to get them is
/// the UAC prompt, and the only way to put a prompt in front of the user is to start a
/// process with the <c>runas</c> verb. That process is this same executable with
/// <see cref="JoinArgument"/>, which does the one thing and exits before WPF is touched
/// (<c>Program.Main</c>); its answer comes back through a file, because an elevated child's
/// standard output cannot be captured by an unelevated parent.
/// </para>
/// <para>
/// <b>Why PowerShell's <c>Add-LocalGroupMember</c></b> rather than <c>net localgroup</c>: it
/// accepts a SID, and the SID is the one identity that survives every account form — local,
/// domain, Azure AD, Microsoft account — and the possibility that the elevated process is
/// running as somebody else. <c>net localgroup</c> wants a name, and a name is what the
/// owner typed by hand on 2026-10-29; it worked, and it is not what a program should rely on.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class LocalGroups(IVersioLogger? logger = null) : ILocalGroups
{
    /// <summary>The first argument of the elevated helper: <c>Versio.exe --join-local-group &lt;group&gt; &lt;sid&gt; &lt;result file&gt;</c>.</summary>
    public const string JoinArgument = "--join-local-group";

    private static readonly TimeSpan HelperTimeout = TimeSpan.FromMinutes(5);

    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    public LocalGroupCheck Check(string groupName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var account = identity.Name;
        var sid = identity.User?.Value ?? string.Empty;

        // Scoped to this machine's accounts. A bare name is looked up in the built-in and
        // local databases and then in the primary and trusted domains, so a domain-joined
        // laptop with no local group of that name would wait on an unreachable domain
        // controller at every probe, and an enterprise that provisions a DOMAIN group of the
        // same name would resolve to it and read as "could not be read" instead of "missing"
        // (review, 2026-10-29). The group Portal consults is the local one.
        // Two local authorities: a group somebody created (MACHINE\Name, the Openness group)
        // and a built-in alias (BUILTIN\Users). Neither lookup leaves the machine.
        SecurityIdentifier? groupSid = null;
        try
        {
            foreach (var authority in new[] { Environment.MachineName, "BUILTIN" })
            {
                try
                {
                    groupSid = (SecurityIdentifier)new NTAccount(authority, groupName)
                        .Translate(typeof(SecurityIdentifier));
                    break;
                }
                catch (IdentityNotMappedException)
                {
                    // Not under this authority; the next one may have it.
                }
            }
        }
        catch (SystemException ex)
        {
            return new LocalGroupCheck(LocalGroupStanding.Unknown, account, sid,
                $"could not resolve the group '{groupName}': {ex.Message}");
        }

        if (groupSid is null)
        {
            return new LocalGroupCheck(LocalGroupStanding.GroupMissing, account, sid,
                $"no local group named '{groupName}' on this machine");
        }

        if (new WindowsPrincipal(identity).IsInRole(groupSid))
        {
            return new LocalGroupCheck(LocalGroupStanding.InToken, account, sid,
                $"{account} has '{groupName}' in its logon token");
        }

        // Not in the token. Member and not yet signed in again, or not a member at all?
        var members = ReadMembers(groupName, out var failure, out var status);
        if (members is null)
        {
            if (status is NerrGroupNotFound or ErrorNoSuchAlias)
            {
                return new LocalGroupCheck(LocalGroupStanding.GroupMissing, account, sid,
                    $"no local group named '{groupName}' on this machine (NetLocalGroupGetMembers {status})");
            }

            _log.Warn(LogCategory.Plc, $"could not list the members of '{groupName}': {failure}");
            return new LocalGroupCheck(LocalGroupStanding.Unknown, account, sid,
                $"'{groupName}' is not in {account}'s logon token and its member list could not be read");
        }

        var listed = identity.User is { } me && members.Any(m => m.Equals(me));
        return listed
            ? new LocalGroupCheck(LocalGroupStanding.MemberNotInToken, account, sid,
                $"{account} is a member of '{groupName}' but this sign-in predates it")
            : new LocalGroupCheck(LocalGroupStanding.NotMember, account, sid,
                $"{account} is not a member of '{groupName}' ({members.Count} member(s))");
    }

    /// <summary>
    /// The group's members as SIDs, from <c>NetLocalGroupGetMembers</c>, or null when the
    /// list could not be read.
    /// </summary>
    /// <remarks>
    /// The API rather than <c>net localgroup</c>, which this read in its first version: that
    /// command prints names in the display language, closes with a localised "The command
    /// completed successfully" line that an English-only parser would have counted as a
    /// member, and prints an account whose SID no longer resolves as the SID — three ways to
    /// be wrong on a machine that is not this one. The API hands back SIDs, compares with
    /// the token's own SID exactly, and spawns nothing.
    /// </remarks>
    /// <summary>Whether the SID is a direct member of the group, as the group says now; false when it cannot be read.</summary>
    private static bool IsListed(string groupName, SecurityIdentifier sid) =>
        ReadMembers(groupName, out _, out _)?.Any(m => m.Equals(sid)) == true;

    /// <summary>NERR_GroupNotFound: no local group of that name.</summary>
    private const int NerrGroupNotFound = 2220;

    /// <summary>ERROR_NO_SUCH_ALIAS: the same answer from the SAM's side.</summary>
    private const int ErrorNoSuchAlias = 1376;

    /// <summary>
    /// Direct members only (level 0): an account admitted through a nested group reads as
    /// not a member here; the token, asked first, already answered for anyone admitted.
    /// </summary>
    private static IReadOnlyList<SecurityIdentifier>? ReadMembers(string groupName, out string? failure, out int status)
    {
        var buffer = IntPtr.Zero;
        try
        {
            status = NetLocalGroupGetMembers(
                null, groupName, 0, out buffer, MaxPreferredLength, out var read, out _, IntPtr.Zero);
            if (status != 0)
            {
                failure = $"NetLocalGroupGetMembers returned {status}";
                return null;
            }

            var members = new List<SecurityIdentifier>((int)read);
            for (var i = 0; i < read; i++)
            {
                // LOCALGROUP_MEMBERS_INFO_0 is one pointer: the member's SID.
                var sidPointer = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                members.Add(new SecurityIdentifier(sidPointer));
            }

            failure = null;
            return members;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
        {
            failure = ex.Message;
            status = -1;
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NetApiBufferFree(buffer);
            }
        }
    }

    private const uint MaxPreferredLength = unchecked((uint)-1);

    /// <summary>Windows PowerShell 5.1, by its absolute path under the system directory.</summary>
    private static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NetLocalGroupGetMembers(
        string? serverName, string localGroupName, int level, out IntPtr buffer,
        uint preferredMaximumLength, out uint entriesRead, out uint totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern int NetApiBufferFree(IntPtr buffer);

    public async Task<PlcRemedyOutcome> JoinElevatedAsync(string groupName, LocalGroupCheck account, CancellationToken ct)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return PlcRemedyOutcome.Refused("the path of the running Versio could not be determined, so no elevated helper can be started.");
        }

        if (string.IsNullOrEmpty(account.Sid))
        {
            return PlcRemedyOutcome.Refused($"{account.Account} has no SID to add; nothing was started.");
        }

        var resultFile = Path.Combine(Path.GetTempPath(), $"versio-join-{Guid.NewGuid():N}.txt");
        _log.Info(LogCategory.Plc,
            $"asking Windows for approval to add {account.Account} ({account.Sid}) to the local group '{groupName}' via {exe}");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            ArgumentList = { JoinArgument, groupName, account.Sid, resultFile },
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process helper;
        try
        {
            // Process.Start with the runas verb returns only once the consent prompt has been
            // answered, and this is called from the UI thread: started inline, the window would
            // stop painting under the prompt and Windows would ghost it as not responding
            // (review, 2026-10-29).
            helper = await Task.Run(() => Process.Start(psi), ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user said No to the prompt. A refusal, said as one.
            _log.Info(LogCategory.Plc, "the administrator prompt was declined; nothing was changed");
            return PlcRemedyOutcome.Refused("the administrator prompt was declined, so nothing was changed.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log.Error(LogCategory.Plc, "the elevated helper could not be started", ex);
            return PlcRemedyOutcome.Refused($"the elevated helper could not be started: {ex.Message}");
        }

        using (helper)
        {
            // A timed wait and no kill: an unelevated parent cannot kill an elevated child
            // (Kill throws access denied), and ChildProcess.WaitForExitOrKillAsync would try
            // and then wait on it without a timeout. The helper runs one cmdlet and exits on
            // its own; if it has not within the timeout, say so and leave it to finish.
            using var timeout = new CancellationTokenSource(HelperTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await helper.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // Not "nothing was changed": the helper is still running and may yet add the
                // account. Said as uncertain, with what to do before pressing again.
                _log.Warn(LogCategory.Plc, $"the elevated helper did not finish within {HelperTimeout.TotalMinutes:0} minutes; left running");
                return PlcRemedyOutcome.Unknown(
                    $"the elevated helper has not finished after {HelperTimeout.TotalMinutes:0} minutes and is still running; "
                    + $"it may still add the account to '{groupName}'. Check the group, or probe again, before pressing again.");
            }

            var message = ReadResult(resultFile) ?? $"the elevated helper exited {helper.ExitCode} and left no result";
            if (helper.ExitCode == 0)
            {
                _log.Info(LogCategory.Plc, message);
                return PlcRemedyOutcome.Done(message);
            }

            _log.Warn(LogCategory.Plc, $"the elevated helper exited {helper.ExitCode}: {message}");
            return PlcRemedyOutcome.Refused(message);
        }
    }

    /// <summary>
    /// The elevated helper's whole job: add the SID to the group, write one line saying what
    /// happened, exit 0 on success. Called from <c>Program.Main</c> before WPF is touched.
    /// </summary>
    /// <remarks>
    /// "Already a member" is a success: the user pressed the button twice, or somebody else
    /// added them meanwhile, and the state they wanted is the state there is.
    /// </remarks>
    public static int RunJoin(IReadOnlyList<string> args)
    {
        if (args.Count < 4 || args[0] != JoinArgument)
        {
            return 2;
        }

        var group = args[1];
        var sid = args[2];
        var resultFile = args[3];
        string message;
        int code;

        // Whoever can start this exe elevated with arguments of their choosing is already an
        // administrator, so this is not a privilege boundary — but the two values go into a
        // PowerShell command line, and a SID is the one shape they ever legitimately have.
        // A group name is quoted and may carry spaces; it may not carry a quote, a dollar or
        // a backtick, which are PowerShell's own.
        if (!SidShape().IsMatch(sid) || group.Length == 0 || group.IndexOfAny(['\'', '"', '$', '`', '\n', '\r']) >= 0)
        {
            WriteResult(resultFile, $"refused: '{sid}' is not a SID or '{group}' is not a plain group name.");
            return 2;
        }

        // Already a member — the user pressed twice, or somebody else added them meanwhile —
        // is the state they wanted, read from the group itself and not from the cmdlet's
        // error text, which is in the display language. Checked before and after the add.
        var me = new SecurityIdentifier(sid);
        if (IsListed(group, me))
        {
            WriteResult(resultFile, $"{sid} was already a member of the local group '{group}'.");
            return 0;
        }

        try
        {
            using var ps = Process.Start(new ProcessStartInfo
            {
                // The absolute path: this process is elevated, and a bare "powershell.exe"
                // is looked for in the application and current directories before PATH.
                FileName = PowerShellPath,
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    $"Add-LocalGroupMember -Group '{group}' -Member '{sid}'",
                },
                UseShellExecute = false,
                CreateNoWindow = true,
                // Only stderr is read. Two redirected pipes read one after the other is the
                // classic deadlock shape; the cmdlet writes nothing to stdout, so it is not
                // redirected at all.
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("powershell.exe did not start");

            var error = ps.StandardError.ReadToEnd().Trim();
            ps.WaitForExit();

            if (ps.ExitCode == 0)
            {
                code = 0;
                message = $"added {sid} to the local group '{group}'.";
            }
            else if (IsListed(group, me))
            {
                code = 0;
                message = $"{sid} was already a member of the local group '{group}'.";
            }
            else
            {
                code = 1;
                message = $"Add-LocalGroupMember exited {ps.ExitCode}: {FirstLine(error)}";
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            code = 1;
            message = $"could not run Add-LocalGroupMember: {ex.Message}";
        }

        WriteResult(resultFile, message);
        return code;
    }

    private static void WriteResult(string resultFile, string message)
    {
        try
        {
            File.WriteAllText(resultFile, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The parent then reports the exit code without a sentence; better than hiding the code.
        }
    }

    [GeneratedRegex(@"^S-1-\d+(-\d+)+$")]
    private static partial Regex SidShape();

    private static string? ReadResult(string resultFile)
    {
        try
        {
            if (!File.Exists(resultFile))
            {
                return null;
            }

            var text = File.ReadAllText(resultFile).Trim();
            File.Delete(resultFile);
            return text.Length == 0 ? null : text;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line ?? "(no message)";
    }
}
