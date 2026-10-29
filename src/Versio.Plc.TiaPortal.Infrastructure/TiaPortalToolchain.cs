using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.Platform;
using Versio.Infrastructure.Plc;
using Versio.Plc.TiaPortal;

namespace Versio.Plc.TiaPortal.Infrastructure;

/// <summary>
/// TIA Portal, as something the application can ask for by vendor rather than by name.
/// </summary>
/// <remarks>
/// The probe answers four questions in order, each cheaper than Portal: is a Portal
/// installed, is the host beside the application, is this account admitted to Openness
/// (§2.2 of the driving notes), and then which releases. Until 2026-10-29 the third was not
/// asked at all: the first export launched Portal, Portal refused in three seconds with
/// <i>"Owner … is not member of the windows group 'Siemens TIA Openness'"</i>, and the owner
/// read that as a failure card an hour into a new machine, with the fix a command in chat.
/// The token says the same thing in about twenty milliseconds (a member list is read when
/// the token says no), before anything is launched, and the status now carries the fix as a
/// button.
/// </remarks>
public sealed class TiaPortalToolchain(
    IVersioLogger? logger = null,
    IEnumerable<string>? searchRoots = null,
    ILocalGroups? groups = null) : IPlcToolchain
{
    /// <summary>
    /// The local group Portal's Openness admits. The Portal setup creates it (Openness has
    /// installed with Portal since V14 SP1), so a Portal machine without it has had the group
    /// deleted or was imaged without it.
    /// </summary>
    public const string OpennessGroup = "Siemens TIA Openness";

    /// <summary>What the user does after the add, because the token is built at sign-in.</summary>
    public const string SignOutAndIn =
        "Sign out of Windows and back in, then try again: the group joins this account's logon token at the next sign-in.";

    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    private readonly ILocalGroups _groups = groups ?? (OperatingSystem.IsWindows()
        ? new LocalGroups(logger)
        : new NoLocalGroups());

    private LocalGroupCheck? _lastCheck;

    public IPlcProjectFormat Format => TiaPortalProjectFormat.Instance;

    public Task<PlcToolchainStatus> ProbeAsync(CancellationToken ct)
    {
        var installs = new TiaPortalLocator(searchRoots, _log).FindInstalls();
        var host = LocalTiaPortalRunner.FindHost();

        if (installs.Count == 0)
        {
            var status = new PlcToolchainStatus
            {
                Available = false,
                Explanation =
                    "TIA Portal was not found under Program Files\\Siemens\\Automation. " +
                    "Install it, or register the PublicAPI folder, before exporting.",
            };
            _log.Warn(TiaPortalLog.Category, $"toolchain unavailable: {status.Explanation}");
            return Task.FromResult(status);
        }

        if (host is null)
        {
            var status = new PlcToolchainStatus
            {
                Available = false,
                Version = string.Join(", ", installs.Select(i => i.Version)),
                Explanation =
                    "TIA Portal is installed but the Versio host executable was not found " +
                    "beside the application. Rebuild Versio.Plc.TiaPortal.Host.",
            };
            _log.Warn(TiaPortalLog.Category, $"toolchain unavailable: {status.Explanation}");
            return Task.FromResult(status);
        }

        if (OpennessRefusal(installs) is { } refused)
        {
            _log.Warn(TiaPortalLog.Category, $"toolchain unavailable: {refused.Explanation}");
            return Task.FromResult(refused);
        }

        // Several releases side by side used to make the toolchain unavailable "so a project
        // is not silently upgraded", with a Candidates list for the user to pick from. Nothing
        // ever offered the pick — the list only reached Settings → System information — and
        // the refusal was aimed at the wrong place: the upgrade risk is in which Portal OPENS
        // a given project, and the project's own extension answers that. The runner chooses
        // per project now (LocalTiaPortalRunner.ChooseInstall) and refuses a release that is
        // not installed, so every installed release is usable and the toolchain says so. The
        // pinned profile is the newest release; it only matters for a path with no number in
        // its extension, which this format does not claim. 2026-10-29.
        var newest = installs[0];
        var ready = new PlcToolchainStatus
        {
            Available = true,
            Profile = newest.Version,
            Version = string.Join(", ", installs.Select(i => i.Version)),
            ExecutablePath = host,
            Candidates = installs.Count > 1 ? [.. installs.Select(i => i.Version)] : [],
            Explanation = installs.Count == 1
                ? $"{newest.InstallFolder} — {newest.Version}"
                : string.Join("; ", installs.Select(i => $"{i.InstallFolder} — {i.Version}"))
                  + ". The project file's extension picks which one opens it.",
        };
        _log.Info(TiaPortalLog.Category, $"toolchain ready: {ready.Explanation}");
        return Task.FromResult(ready);
    }

    /// <summary>
    /// The status for an account Portal would refuse, or null when Openness will admit it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three refusals, three sentences, because they have three different fixes: no group
    /// means the Openness feature is not installed and the Portal setup is where it is added;
    /// not a member is the one this code can do something about, with the administrator's
    /// approval, and it is the only state that carries a <see cref="PlcToolchainRemedy"/>;
    /// a member whose sign-in predates the add has nothing to do but sign out — the add
    /// already happened, and offering it again would succeed and change nothing, which is
    /// how a button teaches people that buttons do nothing.
    /// </para>
    /// <para>
    /// A check that could not be made does not block: Portal is asked as before and refuses
    /// in its own words if it must. The failure is logged, because a check that fails every
    /// time on some machine is a defect worth hearing about.
    /// </para>
    /// </remarks>
    private PlcToolchainStatus? OpennessRefusal(IReadOnlyList<TiaPortalInstall> installs)
    {
        var check = _groups.Check(OpennessGroup);
        _lastCheck = check;
        var version = string.Join(", ", installs.Select(i => i.Version));

        switch (check.Standing)
        {
            case LocalGroupStanding.InToken:
                return null;

            case LocalGroupStanding.Unknown:
                _log.Warn(TiaPortalLog.Category,
                    $"could not tell whether {check.Account} may use Openness ({check.Detail}); Portal will say");
                return null;

            case LocalGroupStanding.GroupMissing:
                return new PlcToolchainStatus
                {
                    Available = false,
                    Version = version,
                    Explanation =
                        $"TIA Portal {version} is installed, but the Windows group '{OpennessGroup}' does not " +
                        "exist on this machine. The Portal setup creates it, so it has been deleted or the " +
                        "machine was imaged without it: an administrator can recreate it (net localgroup " +
                        $"\"{OpennessGroup}\" /add) or repair the Portal installation.",
                };

            case LocalGroupStanding.MemberNotInToken:
                return new PlcToolchainStatus
                {
                    Available = false,
                    Version = version,
                    Explanation =
                        $"TIA Portal {version} is installed and {check.Account} is in the Windows group " +
                        $"'{OpennessGroup}', but this Windows session started before it was added, so Portal " +
                        $"still refuses Openness to it. {SignOutAndIn}",
                };

            default:
                return new PlcToolchainStatus
                {
                    Available = false,
                    Version = version,
                    Explanation =
                        $"TIA Portal {version} is installed, but {check.Account} is not in the Windows group " +
                        $"'{OpennessGroup}', and Portal refuses Openness to any account outside it. Add the " +
                        $"account to the group (administrator rights), then sign out and back in.",
                    Remedy = new PlcToolchainRemedy
                    {
                        Action = $"Add this account to the {OpennessGroup} group…",
                        Explanation =
                            $"Adds {check.Account} to the local Windows group '{OpennessGroup}'. Windows asks " +
                            "for administrator approval first; nothing else on the machine is changed.",
                        Afterwards = SignOutAndIn,
                    },
                };
        }
    }

    public IPlcRunner CreateRunner(PlcToolchainStatus status) =>
        status is { Available: true, ExecutablePath: { } host, Profile: { } profile }
            ? new LocalTiaPortalRunner(host, profile, _log, searchRoots)
            : new UnavailableToolchainRunner(
                status.Explanation ?? "the toolchain is not available on this machine");

    /// <inheritdoc />
    /// <remarks>
    /// Re-checks before acting rather than trusting the status it was handed: the status may
    /// be minutes old, and the account the probe saw is the account to add. The elevated
    /// helper is given the SID, never the name, because the process Windows elevates may be
    /// somebody else's (a standard user typing an administrator's credentials).
    /// </remarks>
    public async Task<PlcRemedyOutcome> ApplyRemedyAsync(PlcToolchainStatus status, CancellationToken ct)
    {
        if (status.Remedy is null)
        {
            return PlcRemedyOutcome.Refused("this status offers no fix.");
        }

        var check = _groups.Check(OpennessGroup);
        _lastCheck = check;
        switch (check.Standing)
        {
            case LocalGroupStanding.InToken:
                return PlcRemedyOutcome.Done($"{check.Account} is already admitted to Openness; nothing to add.");
            case LocalGroupStanding.MemberNotInToken:
                return PlcRemedyOutcome.Done($"{check.Account} is already a member of '{OpennessGroup}'. {SignOutAndIn}");
            case LocalGroupStanding.GroupMissing:
                return PlcRemedyOutcome.Refused($"the group '{OpennessGroup}' does not exist on this machine; an administrator has to recreate it first.");
            case LocalGroupStanding.Unknown:
                return PlcRemedyOutcome.Refused($"the account's standing could not be read: {check.Detail}");
        }

        _log.Info(TiaPortalLog.Category, $"adding {check.Account} to '{OpennessGroup}' at the user's request");
        var outcome = await _groups.JoinElevatedAsync(OpennessGroup, check, ct).ConfigureAwait(false);
        return outcome.Succeeded
            ? PlcRemedyOutcome.Done($"{Capitalise(outcome.Message)} {SignOutAndIn}")
            : outcome;
    }

    /// <summary>The last standing read, for a test or a log line; null before any probe.</summary>
    public LocalGroupCheck? LastGroupCheck => _lastCheck;

    private static string Capitalise(string sentence) =>
        sentence.Length == 0 ? sentence : char.ToUpperInvariant(sentence[0]) + sentence[1..];

    /// <summary>Off Windows there are no local groups to ask; the probe goes on to Portal, which is not there either.</summary>
    private sealed class NoLocalGroups : ILocalGroups
    {
        public LocalGroupCheck Check(string groupName) =>
            new(LocalGroupStanding.Unknown, string.Empty, string.Empty, "not Windows");

        public Task<PlcRemedyOutcome> JoinElevatedAsync(string groupName, LocalGroupCheck account, CancellationToken ct) =>
            Task.FromResult(PlcRemedyOutcome.Refused("not Windows"));
    }
}
