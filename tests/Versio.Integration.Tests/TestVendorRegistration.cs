using System.Runtime.CompilerServices;
using Versio.Infrastructure.Plc.Codesys;

namespace Versio.Integration.Tests;

/// <summary>
/// Which vendors this test assembly knows about.
/// </summary>
/// <remarks>
/// Both halves here, unlike <c>Versio.Core.Tests</c>: this project references
/// <c>Versio.Infrastructure</c>, so it can register the toolchain as well as the format — which
/// it must, because the CODESYS-tagged tests resolve a runner through <c>PlcToolchains</c> and
/// would otherwise find nothing to drive. See <c>Versio.Core.Tests.TestVendorRegistration</c>
/// for why this is a module initialiser here and an explicit call in the application.
/// </remarks>
internal static class TestVendorRegistration
{
    [ModuleInitializer]
    internal static void Register() => CodesysPack.Register();
}
