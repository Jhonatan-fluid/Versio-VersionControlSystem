using System.Runtime.CompilerServices;
using Versio.Core.Plc;
using Versio.Core.Plc.Codesys;

namespace Versio.Core.Tests;

/// <summary>
/// Which vendors this test assembly knows about.
/// </summary>
/// <remarks>
/// <para>
/// <b>The registries stopped being pre-populated, so somebody has to say.</b>
/// <c>PlcFormats</c> held a hardcoded CODESYS entry until the composition moved to the
/// application's startup — which a test host never runs. Without this, every test that scans a
/// repository, resolves a format from a path, or renders an object type would be looking at a
/// registry with nothing in it.
/// </para>
/// <para>
/// <b>A module initialiser is right here and would be wrong in production.</b> The test host
/// loads this assembly in order to find the tests, so it is guaranteed to run before any of
/// them. In the application the same trick would tie the set of vendors to whenever the runtime
/// happened to load an assembly — which is lazy, and would make the answer depend on load order.
/// <c>App.xaml.cs</c> calls <c>CodesysPack.Register</c> explicitly instead, on a line somebody
/// can read and delete.
/// </para>
/// <para>
/// Only the format, not the toolchain: <c>PlcToolchains</c> lives in
/// <c>Versio.Infrastructure</c>, which this project deliberately does not reference and must not
/// start.
/// </para>
/// </remarks>
internal static class TestVendorRegistration
{
    [ModuleInitializer]
    internal static void Register() => PlcFormats.Register(CodesysProjectFormat.Instance);
}
