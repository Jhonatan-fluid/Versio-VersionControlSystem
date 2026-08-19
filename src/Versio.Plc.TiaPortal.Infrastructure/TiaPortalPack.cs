using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.Plc;
using Versio.Plc.TiaPortal;

namespace Versio.Plc.TiaPortal.Infrastructure;

/// <summary>
/// Everything Versio needs to know to see and drive TIA Portal, registered in one call.
/// </summary>
public static class TiaPortalPack
{
    /// <summary>Register the format and toolchain. Safe to call more than once.</summary>
    /// <remarks>
    /// Called after the first vendor's pack so <see cref="PlcFormats.All"/>[0] stays the
    /// default for anything that still picks the first registered format.
    /// </remarks>
    public static void Register()
    {
        PlcFormats.Register(TiaPortalProjectFormat.Instance);
        PlcToolchains.Register(PlcVendor.TiaPortal, log => new TiaPortalToolchain(log));
    }
}
