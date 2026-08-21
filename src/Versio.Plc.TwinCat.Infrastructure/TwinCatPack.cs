using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.Plc;
using Versio.Plc.TwinCat;

namespace Versio.Plc.TwinCat.Infrastructure;

/// <summary>
/// Everything Versio needs to know to see and drive TwinCAT, registered in one call.
/// </summary>
public static class TwinCatPack
{
    /// <summary>Register the format and toolchain. Safe to call more than once.</summary>
    /// <remarks>
    /// Called after the first two vendors' packs. Probe and adopt walk the whole
    /// registry; registration order is not a session pin.
    /// </remarks>
    public static void Register()
    {
        PlcFormats.Register(TwinCatProjectFormat.Instance);
        PlcToolchains.Register(PlcVendor.TwinCat, log => new TwinCatToolchain(log));
    }
}
