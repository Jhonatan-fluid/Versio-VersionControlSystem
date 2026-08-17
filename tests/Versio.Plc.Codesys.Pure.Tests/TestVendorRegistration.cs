using System.Runtime.CompilerServices;
using Versio.Plc.Codesys.Infrastructure;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// Which vendors this test assembly knows about.
/// </summary>
/// <remarks>
/// Both halves — format and toolchain — because these tests resolve a runner through
/// <c>PlcToolchains</c> and would otherwise find nothing to drive. See
/// <c>Versio.Core.Tests.TestVendorRegistration</c> for why this is a module initialiser in a
/// test assembly and an explicit call in the application.
/// </remarks>
internal static class TestVendorRegistration
{
    [ModuleInitializer]
    internal static void Register() => CodesysPack.Register();
}
