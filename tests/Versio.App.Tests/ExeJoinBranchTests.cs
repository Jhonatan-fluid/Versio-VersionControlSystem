using System.Diagnostics;
using System.IO;
using Versio.Infrastructure.Platform;
using Xunit;

namespace Versio.App.Tests;

/// <summary>
/// The built <c>Versio.exe</c>, started with the elevated helper's argument, does the one job
/// and exits — it does not run Velopack and open a window.
/// </summary>
/// <remarks>
/// <b>Review, 2026-10-29.</b> The branch in <c>Program.Main</c> is the dangerous one: were it
/// deleted, or compared against the wrong constant, the elevated re-launch would open a full
/// Versio window as administrator, and every in-process test would stay green because
/// <c>LocalGroupsTests</c> calls <c>RunJoin</c> directly. So this starts the exe the way the
/// helper does, with a SID that is not one — refused before PowerShell is reached, so nothing
/// is added and no elevation is needed — and reads the exit code and the result file.
/// </remarks>
public sealed class ExeJoinBranchTests
{
    [SkippableFact]
    public async Task The_exe_runs_the_join_and_exits_without_opening_a_window()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the helper is Windows-only");
        var exe = Path.Combine(AppContext.BaseDirectory, "Versio.exe");
        Skip.If(!File.Exists(exe), "Versio.exe is not beside the test assembly");

        var result = Path.Combine(Path.GetTempPath(), $"versio-join-exe-{Guid.NewGuid():N}.txt");
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { LocalGroups.JoinArgument, "Users", "not-a-sid", result },
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;

            // A window would never exit on its own; the helper exits in well under this.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                ChildProcess.KillTree(process);
                Assert.Fail("Versio.exe did not exit within 60 s with the join argument: it opened the app instead of running the helper");
            }

            Assert.Equal(2, process.ExitCode);
            Assert.Contains("is not a SID", File.ReadAllText(result));
        }
        finally
        {
            File.Delete(result);
        }
    }
}
