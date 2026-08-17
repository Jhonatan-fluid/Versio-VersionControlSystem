using System.Runtime.CompilerServices;
using Versio.Infrastructure.Plc.Codesys;

namespace Versio.App.Tests;

/// <summary>
/// Which vendors this test assembly knows about.
/// </summary>
/// <remarks>
/// <para>
/// The shell scans a repository for every registered format the moment one is opened, so
/// without this the panel tests would be exercising a build that recognises no project files at
/// all — and would pass, because "no projects found" is a perfectly valid state to render.
/// </para>
/// <para>
/// <b>Registering the toolchain here does not un-do <c>TestShell</c>'s <c>toolchains:</c>
/// argument.</b> That passes a factory in, which wins over the registry; the registry is only
/// consulted when nothing was injected. So the tests that deliberately run with no toolchain —
/// the ones that must not go looking for CODESYS on whatever machine is running them — still
/// do.
/// </para>
/// <para>
/// See <c>Versio.Core.Tests.TestVendorRegistration</c> for why this is a module initialiser here
/// and an explicit call in the application.
/// </para>
/// </remarks>
internal static class TestVendorRegistration
{
    [ModuleInitializer]
    internal static void Register() => CodesysPack.Register();
}
