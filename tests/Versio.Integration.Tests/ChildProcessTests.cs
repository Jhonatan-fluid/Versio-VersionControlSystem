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

    [Fact]
    public void Only_the_reported_pid_is_reaped_and_a_decoy_of_the_same_name_survives()
    {
        // The reap used to kill every process of the IDE's name that was not running when
        // the host started: an IDE the engineer opened during the run died with its
        // unsaved project (the 2026-10-24 review's item 28). The decoy here is exactly
        // that — same image name, started after the "host" — and it must outlive the reap.
        var hostStarted = DateTime.Now.AddSeconds(-1);
        using var mine = StartPing();
        using var decoy = StartPing();

        var killed = ChildProcess.KillOwned(mine.Id, "ping", hostStarted);

        Assert.Equal(1, killed);
        Assert.True(mine.WaitForExit(3000), "the reported pid was not killed");
        Assert.False(decoy.HasExited, "a process of the same name that was not reported was killed");
        decoy.Kill();
    }

    [Fact]
    public void No_reported_pid_means_nothing_is_reaped()
    {
        using var decoy = StartPing();

        Assert.Equal(0, ChildProcess.KillOwned(null, "ping", DateTime.Now.AddSeconds(-1)));
        Assert.False(decoy.HasExited);
        decoy.Kill();
    }

    [Fact]
    public void A_pid_whose_name_or_age_does_not_match_is_left_alone()
    {
        // A PID can be reused. The name and the start time are the two cheap facts that
        // tell the IDE this run created from whatever now holds its number.
        using var decoy = StartPing();

        Assert.Equal(0, ChildProcess.KillOwned(decoy.Id, "notepad", DateTime.Now.AddSeconds(-1)));
        Assert.Equal(0, ChildProcess.KillOwned(decoy.Id, "ping", DateTime.Now.AddMinutes(5)));
        Assert.False(decoy.HasExited);
        decoy.Kill();
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
