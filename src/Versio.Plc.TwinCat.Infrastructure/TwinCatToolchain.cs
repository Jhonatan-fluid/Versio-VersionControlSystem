using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.Plc;
using Versio.Plc.TwinCat;

namespace Versio.Plc.TwinCat.Infrastructure;

/// <summary>
/// TwinCAT XAE, as something the application can ask for by vendor rather than by name.
/// </summary>
public sealed class TwinCatToolchain(IVersioLogger? logger = null) : IPlcToolchain
{
    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    public IPlcProjectFormat Format => TwinCatProjectFormat.Instance;

    public Task<PlcToolchainStatus> ProbeAsync(CancellationToken ct)
    {
        var install = new TwinCatLocator(_log).FindPreferred();
        var host = LocalTwinCatRunner.FindHost();

        if (install is null)
        {
            var status = new PlcToolchainStatus
            {
                Available = false,
                Explanation =
                    "TcXaeShell.DTE.17.0 was not found. Install TwinCAT 3 XAE (VS2022-based), " +
                    "or register the 17.0 ProgID, before exporting.",
            };
            _log.Warn(TwinCatLog.Category, $"toolchain unavailable: {status.Explanation}");
            return Task.FromResult(status);
        }

        if (host is null)
        {
            var status = new PlcToolchainStatus
            {
                Available = false,
                Version = install.Version,
                Explanation =
                    "TwinCAT XAE is installed but the Versio host executable was not found " +
                    "beside the application. Rebuild Versio.Plc.TwinCat.Host.",
            };
            _log.Warn(TwinCatLog.Category, $"toolchain unavailable: {status.Explanation}");
            return Task.FromResult(status);
        }

        var ready = new PlcToolchainStatus
        {
            Available = true,
            Profile = install.ProgId,
            Version = install.Version,
            ExecutablePath = host,
            Explanation = $"{install.ProgId} — TwinCAT {install.Version} at {install.InstallFolder}",
        };
        _log.Info(TwinCatLog.Category, $"toolchain ready: {ready.Explanation}");
        return Task.FromResult(ready);
    }

    public IPlcRunner CreateRunner(PlcToolchainStatus status) =>
        status is { Available: true, ExecutablePath: { } host, Profile: { } profile }
            ? new LocalTwinCatRunner(host, profile, _log)
            : new UnavailableToolchainRunner(
                status.Explanation ?? "the toolchain is not available on this machine");
}
