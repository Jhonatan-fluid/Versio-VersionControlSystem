using System.Diagnostics;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// The real process probe, against real processes. The occupancy policy for a pid-less marker
/// (TIA Portal's <c>.info</c>) rests on this one answer, and a probe that said "running" for
/// everything would keep every leftover marker believed without a single unit test noticing —
/// the interface's default does exactly that, on purpose, so the real one has to be seen to
/// override it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProcessProbeTests
{
    [Fact]
    public void A_process_that_is_running_is_reported_running_by_name()
    {
        // This test host, whatever it is called on this runner.
        using var self = Process.GetCurrentProcess();

        Assert.True(new ProcessProbe().IsAnyProcessRunning(self.ProcessName));
    }

    [Fact]
    public void A_process_nobody_runs_is_reported_not_running()
    {
        Assert.False(new ProcessProbe().IsAnyProcessRunning(
            "Versio.NoSuchProcess." + Guid.NewGuid().ToString("N")));
    }
}
