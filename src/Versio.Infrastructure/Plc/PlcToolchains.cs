using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.Plc.Codesys;

namespace Versio.Infrastructure.Plc;

/// <summary>
/// Every toolchain Versio can drive, resolved by vendor.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="PlcFormats"/>, which says what Versio can <i>recognise</i>.
/// This says what it can <i>run</i>, and the two are deliberately separate: a format may be
/// registered with no toolchain behind it, so its projects are detected, listed and explained
/// as unsupported rather than silently ignored.
/// </para>
/// <para>
/// It lives in <c>Versio.Infrastructure</c> because a toolchain starts processes and reads the
/// filesystem, which <c>Versio.Core</c> is not allowed to do. Adding Rockwell, Siemens or
/// TwinCAT means writing the toolchain and registering it here — nothing above this line
/// changes.
/// </para>
/// </remarks>
public static class PlcToolchains
{
    private static readonly Dictionary<PlcVendor, Func<IVersioLogger?, IPlcToolchain>> Factories =
        new()
        {
            [PlcVendor.Codesys] = log => new CodesysToolchain(log),
        };

    /// <summary>Vendors that can actually be driven, not merely recognised.</summary>
    public static IReadOnlyList<PlcVendor> Supported
    {
        get
        {
            lock (Factories)
            {
                return [.. Factories.Keys];
            }
        }
    }

    /// <summary>Register a toolchain, replacing any already known for its vendor.</summary>
    public static void Register(PlcVendor vendor, Func<IVersioLogger?, IPlcToolchain> factory)
    {
        lock (Factories)
        {
            Factories[vendor] = factory;
        }
    }

    /// <summary>Forget a vendor's toolchain. True when one was registered.</summary>
    /// <remarks>
    /// The counterpart to <see cref="Register"/>, for the same reason
    /// <c>PlcFormats.Unregister</c> exists: without it a registration is permanent for the life
    /// of the process, and a test that registers a stand-in vendor cannot put the registry back
    /// the way it found it.
    /// </remarks>
    public static bool Unregister(PlcVendor vendor)
    {
        lock (Factories)
        {
            return Factories.Remove(vendor);
        }
    }

    /// <summary>
    /// The toolchain for a vendor, or null when Versio cannot drive that one.
    /// </summary>
    /// <remarks>
    /// Null rather than a throw. A repository can legitimately contain a project Versio can
    /// see and not run — a TwinCAT project on a machine that only has CODESYS support built —
    /// and that is a state to report, not an exception to handle.
    /// </remarks>
    public static IPlcToolchain? For(PlcVendor vendor, IVersioLogger? logger = null)
    {
        lock (Factories)
        {
            return Factories.TryGetValue(vendor, out var factory) ? factory(logger) : null;
        }
    }
}
