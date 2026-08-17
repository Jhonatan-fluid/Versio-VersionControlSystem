using Versio.Core.Logging;
using Versio.Core.Plc;
using Versio.Infrastructure.Plc;
using Versio.Plc.Codesys;

namespace Versio.Plc.Codesys.Infrastructure;

/// <summary>
/// Everything Versio needs to know to see and drive CODESYS, registered in one call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composition moved out of the registries because they were not registries.</b>
/// <see cref="PlcFormats"/> held a hardcoded array containing CODESYS and
/// <see cref="PlcToolchains"/> a hardcoded dictionary containing CODESYS, so
/// <c>Versio.Core</c> — the assembly the whole architectural rule exists to keep free of vendor
/// knowledge — named a vendor in its own field initialiser. Both start empty now, and whoever
/// composes the application says which vendors it has.
/// </para>
/// <para>
/// <b>This is what makes the vendor pack a separate assembly possible at all.</b> While
/// <c>PlcFormats</c> referenced <c>CodesysProjectFormat</c>, moving that type into its own
/// project would have made <c>Versio.Core</c> depend on a project that depends on
/// <c>Versio.Core</c>. The reference had to go before the code could move.
/// </para>
/// <para>
/// <b>Called explicitly rather than by a module initialiser.</b> A module initialiser runs when
/// the runtime happens to load the assembly, which is lazy and not guaranteed to happen before
/// something reads <see cref="PlcFormats.All"/> — so the set of vendors would depend on load
/// order, which is the least debuggable kind of dependency. An explicit call in the application's
/// startup is one line somebody can read and delete.
/// </para>
/// </remarks>
public static class CodesysPack
{
    /// <summary>Register the CODESYS format and toolchain. Safe to call more than once.</summary>
    /// <remarks>
    /// Idempotent because both registries replace by vendor rather than appending — so a host
    /// that registers twice gets one CODESYS, not two formats claiming <c>.project</c>.
    /// </remarks>
    public static void Register()
    {
        PlcFormats.Register(CodesysProjectFormat.Instance);
        PlcToolchains.Register(PlcVendor.Codesys, log => new CodesysToolchain(log));
    }

    // NO Unregister() COUNTERPART, deliberately, and the absence is the interesting part.
    //
    // One was written here — "exists so a test can return to a known state" — and then had zero
    // callers, which is exactly the defect this session opened by writing up: PlcFormats.Register
    // and PlcToolchains.Register had no callers at all while three documents described
    // registration as the architecture. Adding unused API on the same branch that found that,
    // with a docstring inventing a user for it, is not a mistake worth repeating twice.
    //
    // PlcFormats.Unregister and PlcToolchains.Unregister DO exist and ARE used: a test that
    // registers a stand-in vendor undoes exactly what it did. Nothing has yet needed to undo the
    // whole pack, and when something does, it is two lines at the call site or four here — by
    // which time whoever writes it will know which.
}
