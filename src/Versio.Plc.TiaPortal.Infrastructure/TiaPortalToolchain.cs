using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.Plc;
using Versio.Plc.TiaPortal;

namespace Versio.Plc.TiaPortal.Infrastructure;

/// <summary>
/// TIA Portal, as something the application can ask for by vendor rather than by name.
/// </summary>
public sealed class TiaPortalToolchain(
    IVersioLogger? logger = null,
    IEnumerable<string>? searchRoots = null) : IPlcToolchain
{
    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

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
                Version = installs[0].Version,
                Explanation =
                    "TIA Portal is installed but the Versio host executable was not found " +
                    "beside the application. Rebuild Versio.Plc.TiaPortal.Host.",
            };
            _log.Warn(TiaPortalLog.Category, $"toolchain unavailable: {status.Explanation}");
            return Task.FromResult(status);
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

    public IPlcRunner CreateRunner(PlcToolchainStatus status) =>
        status is { Available: true, ExecutablePath: { } host, Profile: { } profile }
            ? new LocalTiaPortalRunner(host, profile, _log, searchRoots)
            : new UnavailableToolchainRunner(
                status.Explanation ?? "the toolchain is not available on this machine");
}
