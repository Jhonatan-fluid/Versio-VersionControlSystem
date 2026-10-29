using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Win32;
using Versio.Core.Logging;

namespace Versio.Plc.TiaPortal.Infrastructure;

/// <summary>Whether Portal's Openness access list already names the host it is about to be asked by.</summary>
public enum OpennessAccessStanding
{
    /// <summary>An entry names this host's path and hash: Portal connects without asking.</summary>
    Listed,

    /// <summary>No entry matches: Portal puts up its access dialog the first time this host connects.</summary>
    NotListed,

    /// <summary>The list or the host could not be read; Portal will say.</summary>
    Unknown,
}

/// <param name="Standing">What was found.</param>
/// <param name="Detail">Where it was found, or why nothing was — for the log, never for a decision.</param>
public sealed record OpennessAccessCheck(OpennessAccessStanding Standing, string Detail);

/// <summary>
/// Reads Portal's Openness access list, so the probe and the runner can say that a dialog is
/// coming. Faked in tests; the real one is the registry.
/// </summary>
public interface IOpennessAccessList
{
    OpennessAccessCheck Check(TiaPortalInstall install, string hostPath);
}

/// <summary>
/// Portal's Openness access list, as a read-only question: will this Portal ask before it lets
/// this host in?
/// </summary>
/// <remarks>
/// <para>
/// <b>Read, never written.</b> The list is Portal's security control: a person looked at this
/// exact binary and pressed <i>Yes to all</i>, and the entry is keyed to the binary's hash so a
/// changed or planted host does not inherit the answer. Versio could write the entry itself
/// from the elevated helper and spare the dialog after every update — it was considered on
/// 2026-10-29 and rejected: an auto-updater trusting its own next build moves the decision from
/// the person to whoever can publish a release, still needs an administrator (the list is under
/// HKLM), and depends on a format Siemens does not document. What cost the owner a morning was
/// not the click but the surprise, and the nine-minute silence when nobody was at the screen to
/// click. So the list is read, and the card says a dialog is coming.
/// </para>
/// <para>
/// <b>The shape, observed on 2026-10-29 with V20 and V21 side by side; Siemens documents none
/// of it.</b> Under <c>HKLM\SOFTWARE\Siemens\Automation\Openness</c>, V21 keeps
/// <c>AllowList\&lt;exe name&gt;\Entry</c>, <c>Entry (1)</c>, …; V20 keeps the same entries
/// under <c>20.0\Whitelist</c> — the older name, under the release's own key. Each entry has
/// <c>Path</c> (the process image, as Portal saw it), <c>FileHash</c> (base64 of the file's
/// SHA-256 — matched byte for byte against the installed host) and <c>DateModified</c> (the
/// file's last write in UTC, <c>yyyy/MM/dd HH:mm:ss.fff</c>; it matched the installed host's
/// to the millisecond). Three entries for one exe name on the owner's machine had three hashes:
/// the host of 2026-10-28, the host the morning's update brought, and the host the afternoon's
/// update brought — which is the dialog "after every update", in the registry.
/// </para>
/// <para>
/// A match is path and hash. The date is not compared: the two always change together under
/// an update, and a file copied with its content intact but a new timestamp is a case Portal's
/// own behaviour is not known for, so the check does not guess and the detail says what it saw.
/// Path comparison is case-insensitive, as NTFS is. An entry whose path matches with another
/// hash is the usual state right after an update, and the detail says so, because "not listed"
/// alone reads like a first run to somebody who answered the dialog last week.
/// </para>
/// <para>
/// One more trap, for whoever tests this from a shell: a process running under a packaged
/// (MSIX) application sees a virtualised <c>%LOCALAPPDATA%</c>, and the copy of the installed
/// host it reads there can be days older than the file Portal and the real Versio see. The
/// hash from such a shell is not the hash in the list, and the check is right to say so.
/// </para>
/// </remarks>
public sealed class OpennessAccessList(IVersioLogger? logger = null) : IOpennessAccessList
{
    /// <summary>Where every Portal release keeps its Openness settings.</summary>
    public const string OpennessKey = @"SOFTWARE\Siemens\Automation\Openness";

    /// <summary>The window Portal puts up; the notice names it so the user knows what to look for.</summary>
    public const string DialogTitle = "Openness access";

    /// <summary>What an unanswered dialog becomes, measured at 8 min 53 s (driving notes §2.3).</summary>
    public const string TimeoutText = "Security error. The operation has timed out.";

    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    /// <summary>
    /// The list keys a release reads, relative to <see cref="OpennessKey"/>, in the order they
    /// are tried.
    /// </summary>
    /// <remarks>
    /// Observed: V21 at the root <c>AllowList</c>, V20 under <c>20.0\Whitelist</c>. Both
    /// spellings are tried under the release's own key for every release, and the root list for
    /// V21 and later, because the name changed between those two releases and nothing says a
    /// later one will not move it back. A key that is not there costs one failed open.
    /// </remarks>
    public static IReadOnlyList<string> ListKeysFor(int release)
    {
        var own = new List<string> { $@"{release}.0\Whitelist", $@"{release}.0\AllowList" };
        return release >= 21 ? ["AllowList", .. own] : own;
    }

    /// <summary>The value Portal stores in <c>FileHash</c>: base64 of the file's SHA-256.</summary>
    public static string HashOf(string path) =>
        Convert.ToBase64String(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>The value Portal stores in <c>DateModified</c>: the file's last write, UTC, to the millisecond.</summary>
    public static string StampOf(string path) =>
        File.GetLastWriteTimeUtc(path).ToString("yyyy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>An entry names this host when its path and hash do; the date is not consulted.</summary>
    public static bool Matches(string? entryPath, string? entryHash, string hostPath, string hostHash) =>
        string.Equals(entryPath, hostPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(entryHash, hostHash, StringComparison.Ordinal);

    public OpennessAccessCheck Check(TiaPortalInstall install, string hostPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(OpennessAccessStanding.Unknown, "not Windows");
        }

        if (install.ReleaseNumber < 0)
        {
            return new(OpennessAccessStanding.Unknown, $"'{install.Version}' does not read as a release number");
        }

        string hash;
        try
        {
            hash = HashOf(hostPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(OpennessAccessStanding.Unknown, $"could not hash {hostPath}: {ex.Message}");
        }

        try
        {
            return ReadList(install.ReleaseNumber, hostPath, hash);
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            // A locked-down registry is not a reason to refuse: Portal is asked as before and
            // puts up its dialog or not. Logged, because a check that fails on every probe on
            // some machine is worth hearing about.
            _log.Warn(TiaPortalLog.Category, $"could not read Portal's Openness access list: {ex.Message}");
            return new(OpennessAccessStanding.Unknown, ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static OpennessAccessCheck ReadList(int release, string hostPath, string hash)
    {
        var exeName = Path.GetFileName(hostPath);
        var keys = ListKeysFor(release);
        var entries = 0;
        string? stale = null;

        foreach (var listKey in keys)
        {
            using var exeKey = Registry.LocalMachine.OpenSubKey($@"{OpennessKey}\{listKey}\{exeName}");
            if (exeKey is null)
            {
                continue;
            }

            foreach (var entryName in exeKey.GetSubKeyNames())
            {
                using var entry = exeKey.OpenSubKey(entryName);
                if (entry is null)
                {
                    continue;
                }

                entries++;
                var entryPath = entry.GetValue("Path") as string;
                var entryHash = entry.GetValue("FileHash") as string;
                if (Matches(entryPath, entryHash, hostPath, hash))
                {
                    return new(OpennessAccessStanding.Listed,
                        $@"HKLM\{OpennessKey}\{listKey}\{exeName}\{entryName} names this host");
                }

                if (string.Equals(entryPath, hostPath, StringComparison.OrdinalIgnoreCase))
                {
                    stale = $"an entry names this path with another hash, dated {entry.GetValue("DateModified")}: "
                        + "the host has changed since that answer, which is what an update does";
                }
            }
        }

        var detail = stale
            ?? (entries == 0
                ? $@"no entry for {exeName} under HKLM\{OpennessKey}\{{{string.Join(", ", keys)}}}"
                : $"{entries} entr{(entries == 1 ? "y" : "ies")} for {exeName}, none for this path and hash");
        return new(OpennessAccessStanding.NotListed, detail);
    }
}
