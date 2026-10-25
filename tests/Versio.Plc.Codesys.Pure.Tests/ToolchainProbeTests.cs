using Versio.Plc.Codesys.Infrastructure;
using Xunit;

namespace Versio.Plc.Codesys.Tests;

/// <summary>
/// A machine with no CODESYS is reported, not thrown at.
/// </summary>
/// <remarks>
/// The probe runs for every registered vendor when a repository attaches. On a machine that
/// has TIA Portal and no CODESYS the CODESYS probe found an empty list of installs and
/// indexed it — <c>installs[0]</c> for the install that owns the resolved profile — before
/// the resolver's <c>NoInstallFound</c> could be reported, so the attach aborted on an
/// <see cref="ArgumentOutOfRangeException"/> and the vendors after it were never probed
/// (the 2026-10-24 review's item 36). The search root here is an empty temp folder, which
/// is the same empty list the locator returns on such a machine.
/// </remarks>
public sealed class ToolchainProbeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "Versio", "probe-" + Guid.NewGuid().ToString("N"));

    public ToolchainProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp clean-up only; a leftover folder fails nothing.
        }
    }

    [Fact]
    public async Task A_machine_without_CODESYS_probes_as_unavailable_rather_than_throwing()
    {
        var toolchain = new CodesysToolchain(logger: null, searchRoots: [_root]);

        var status = await toolchain.ProbeAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Contains("no CODESYS installation", status.Explanation);
        Assert.Null(status.ExecutablePath);
    }
}
