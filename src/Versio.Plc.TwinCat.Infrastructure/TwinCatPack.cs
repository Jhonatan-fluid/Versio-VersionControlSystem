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
    /// Called after the first two vendors' packs so <see cref="PlcFormats.All"/>[0] stays
    /// the default for anything that still picks the first registered format.
    /// </remarks>
    public static void Register()
    {
        PlcFormats.Register(TwinCatProjectFormat.Instance);
        PlcToolchains.Register(PlcVendor.TwinCat, log => new TwinCatToolchain(log));
    }
}
