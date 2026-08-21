using System.Diagnostics;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.Integration.Tests;

/// <summary>
/// Waiting on a child honours cancel, and the session job reaps what Task Manager would leave.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ChildProcessTests
{
    [Fact]
    public async Task A_cancelled_wait_kills_the_child_within_a_second()
    {
        using var process = StartPing();
        using var cts = new CancellationTokenSource();

        var waiting = ChildProcess.WaitForExitOrKillAsync(
            process, TimeSpan.FromMinutes(1), cts.Token);

        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        Assert.True(process.HasExited);
    }

    [Fact]
    public void Closing_the_job_handle_kills_the_assigned_child()
    {
        using var job = new ChildProcess.KillOnCloseJob();
        Assert.True(job.IsActive, "CreateJobObject was refused; cannot prove KILL_ON_JOB_CLOSE");

        using var process = StartPing();
        job.Assign(process);

        job.Dispose();

        Assert.True(process.WaitForExit(3000), "child was still alive after the job handle closed");
    }

    private static Process StartPing()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "127.0.0.1 -n 30 -w 1000",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var process = Process.Start(psi);
        Assert.NotNull(process);
        return process;
    }
}
