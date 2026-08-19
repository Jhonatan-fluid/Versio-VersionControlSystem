using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.Plc;
using Versio.Plc.TiaPortal;

namespace Versio.Plc.TiaPortal.Infrastructure;

/// <summary>
/// TIA Portal, as something the application can ask for by vendor rather than by name.
/// </summary>
public sealed class TiaPortalToolchain(IVersioLogger? logger = null) : IPlcToolchain
{
    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    public IPlcProjectFormat Format => TiaPortalProjectFormat.Instance;

    public Task<PlcToolchainStatus> ProbeAsync(CancellationToken ct)
    {
        var installs = new TiaPortalLocator(logger: _log).FindInstalls();
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

        if (installs.Count > 1)
        {
            var status = new PlcToolchainStatus
            {
                Available = false,
                Candidates = [.. installs.Select(i => i.Version)],
                Explanation =
                    $"{installs.Count} TIA Portal versions installed — pick the one this " +
                    "repository targets before exporting, so a project is not silently upgraded.",
            };
            _log.Warn(TiaPortalLog.Category, $"toolchain unavailable: {status.Explanation}");
            return Task.FromResult(status);
        }

        var install = installs[0];
        var ready = new PlcToolchainStatus
        {
            Available = true,
            Profile = install.Version,
            Version = install.Version,
            ExecutablePath = host,
            Explanation = $"{install.InstallFolder} — {install.Version}",
        };
        _log.Info(TiaPortalLog.Category, $"toolchain ready: {ready.Explanation}");
        return Task.FromResult(ready);
    }

    public IPlcRunner CreateRunner(PlcToolchainStatus status) =>
        status is { Available: true, ExecutablePath: { } host, Profile: { } profile }
            ? new LocalTiaPortalRunner(host, profile, _log)
            : new UnavailableToolchainRunner(
                status.Explanation ?? "the toolchain is not available on this machine");
}
