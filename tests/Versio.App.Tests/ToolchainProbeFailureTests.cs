using Versio.App.ViewModels;
using Versio.Core.Logging;
using Versio.Core.Plc;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// A vendor whose probe throws costs that vendor, not the attach.
/// </summary>
/// <remarks>
/// The probe runs inside the repository attach, before the orchestrator is built and the
/// watcher started. The CODESYS probe threw on a machine with no CODESYS (an empty install
/// list, indexed), and the attach ended there: no tree, no watcher, and the vendors after
/// it never asked (the 2026-10-24 review's item 36). The toolchain itself is fixed in its own
/// pack; this is the view model's half — a probe is one vendor reading its own install from
/// disk, and whatever it throws is reported as that vendor being unavailable.
/// </remarks>
public sealed class ToolchainProbeFailureTests
{
    private sealed class ThrowingToolchain : IPlcToolchain
    {
        public IPlcProjectFormat Format => Versio.Plc.Codesys.CodesysProjectFormat.Instance;

        public Task<PlcToolchainStatus> ProbeAsync(CancellationToken ct) =>
            throw new ArgumentOutOfRangeException("index", "Index was out of range.");

        public IPlcRunner CreateRunner(PlcToolchainStatus status) =>
            throw new InvalidOperationException("never reached");
    }

    [Fact]
    public async Task A_probe_that_throws_is_reported_as_unavailable_and_the_attach_completes()
    {
        using var repo = new TempRepo();
        ImportConfirmationTests.GivenATrackedProject(repo);

        using var shell = TestShell.Build(out var sink, toolchain: new ThrowingToolchain());
        await shell.OpenAsync(repo.Path);

        // The attach got past the probe: the tree was built.
        Assert.Contains(shell.Sync.Tree, r => r.IsProject);

        Assert.False(shell.Sync.ToolchainAvailable);
        Assert.Contains("probe failed", shell.Sync.ToolchainState);
        Assert.Contains("ArgumentOutOfRangeException", shell.Sync.ToolchainState);

        // Said in the log, with the exception: a vendor pack throwing is a defect to fix,
        // not a state to sit in quietly.
        Assert.Contains(sink.Snapshot(), e =>
            e.Level == LogLevel.Error && e.Message.Contains("toolchain probe failed", StringComparison.Ordinal));
    }
}
