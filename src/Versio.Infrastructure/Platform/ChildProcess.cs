using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Versio.Infrastructure.Platform;

/// <summary>
/// How Versio waits on, kills, and reaps the processes it starts.
/// </summary>
/// <remarks>
/// <para>
/// TIA and TwinCAT used to sit in <c>WaitForExit(timeout)</c> and only then look at the
/// cancellation token, so "Stop it and continue" waited out a twenty-minute job. CODESYS
/// honoured the token once the process was running, but all three vendors ignored it for
/// the fifteen-minute machine mutex. One helper, so the three cannot drift.
/// </para>
/// <para>
/// <b>The session job is for true children only</b> — CODESYS.exe and git.exe.
/// <c>KILL_ON_JOB_CLOSE</c> means Task Manager killing Versio reaps them. The TIA and
/// TwinCAT hosts are left out of the job on purpose: they are COM clients, and killing
/// them before <c>Quit</c>/<c>Dispose</c> is how Portal and XAE survive a parent death.
/// Those hosts watch the parent PID themselves.
/// </para>
/// </remarks>
public static class ChildProcess
{
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInfoClass = 9;

    /// <summary>
    /// The process-wide job. Created at app startup; tests that need a job mint their own.
    /// </summary>
    public static KillOnCloseJob? Session { get; set; }

    /// <summary>
    /// How a host learns which Versio launched it. Not a job-object member: the host must
    /// Quit COM before it exits, and <c>KILL_ON_JOB_CLOSE</c> would skip that.
    /// </summary>
    public const string ParentPidVariable = "VERSIO_PARENT_PID";

    /// <summary>Stamp the parent pid so a host can Quit COM if Versio dies.</summary>
    public static void StampParent(ProcessStartInfo psi) =>
        psi.Environment[ParentPidVariable] = Environment.ProcessId.ToString();

    /// <summary>Assign a true child (CODESYS, git) to the session job, if one exists.</summary>
    public static void AssignToSession(Process process) => Session?.Assign(process);

    /// <summary>
    /// Wait for a process, kill its tree on timeout or cancel.
    /// </summary>
    /// <returns>True if it exited on its own; false if it was killed for timeout.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled; the tree was killed.</exception>
    public static async Task<bool> WaitForExitOrKillAsync(
        Process process, TimeSpan timeout, CancellationToken cancellationToken,
        bool closeStdinOnCancel = false)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            var timedOut = timeoutCts.IsCancellationRequested;
            if (!timedOut && closeStdinOnCancel)
            {
                TryCloseStdin(process);
                try
                {
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                    if (!timedOut)
                    {
                        throw;
                    }

                    return false;
                }
                catch (OperationCanceledException) when (!timedOut)
                {
                    // Grace expired; fall through to kill.
                }
            }

            KillTree(process);

            if (!timedOut)
            {
                throw;
            }

            return false;
        }
        finally
        {
            // WaitForExitAsync can return before the async readers have flushed.
            await Task.Run(process.WaitForExit, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous wrapper for the mutex thread that cannot await.</summary>
    public static bool WaitForExitOrKill(
        Process process, TimeSpan timeout, CancellationToken cancellationToken,
        bool closeStdinOnCancel = false) =>
        WaitForExitOrKillAsync(process, timeout, cancellationToken, closeStdinOnCancel)
            .GetAwaiter().GetResult();

    /// <summary>
    /// Acquire a named mutex, or throw if the caller cancelled before the timeout.
    /// </summary>
    /// <returns>True if this thread owns it.</returns>
    /// <remarks>
    /// Sliced <c>WaitOne</c> rather than <c>WaitAny</c>, so <see cref="AbandonedMutexException"/>
    /// still surfaces and the caller can log that a previous Versio died holding it. A single
    /// 15-minute <c>WaitOne</c> is what made Stop sit idle until the other run finished.
    /// </remarks>
    public static bool WaitMutex(Mutex mutex, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = timeout - waited.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            var slice = remaining < TimeSpan.FromMilliseconds(200)
                ? remaining
                : TimeSpan.FromMilliseconds(200);

            // AbandonedMutexException is left to the caller: each vendor logs it in its
            // own words. WaitOne(slice) throws it and we rethrow, ownership already taken.
            if (mutex.WaitOne(slice))
            {
                return true;
            }
        }
    }

    public static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone between the check and the kill.
        }
        catch (NotSupportedException)
        {
            // Platform without tree-kill support.
        }
    }

    /// <summary>
    /// Kill the one IDE process a host reported as its own, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A COM server is not a child of Host.exe, so KillTree on the host leaves it behind;
    /// something has to reap it on Stop or timeout. The first version reaped every process
    /// of the IDE's name that was not running when the host started — a snapshot diff over
    /// the whole run, which is the mutex wait, the export or import and the timeout. A
    /// Portal or XAE the engineer opened by hand in that window was "new" and died with its
    /// unsaved project (the 2026-10-24 review's item 28). Now the host names the PID it
    /// created, measured across the creation call alone, and this kills that PID only —
    /// after checking the image name and that it started after the host did, so a reused
    /// PID cannot stand in for it.
    /// </para>
    /// <para>
    /// No PID means nothing is reaped by name: the caller logs that the IDE may be left
    /// running, which is the recoverable failure. Killing by guess is the other one.
    /// </para>
    /// </remarks>
    /// <param name="pid">What the host reported, or null when it reported nothing.</param>
    /// <param name="processName">The IDE's image name without extension, as a cross-check.</param>
    /// <param name="notBefore">The host's start; a process older than this is not ours.</param>
    /// <returns>How many processes were asked to die. Zero is the usual case: it had already gone.</returns>
    public static int KillOwned(int? pid, string processName, DateTime notBefore)
    {
        if (pid is not { } id)
        {
            return 0;
        }

        Process candidate;
        try
        {
            candidate = Process.GetProcessById(id);
        }
        catch (ArgumentException)
        {
            return 0;                               // already gone
        }
        catch (InvalidOperationException)
        {
            return 0;
        }

        using (candidate)
        {
            try
            {
                if (!string.Equals(candidate.ProcessName, processName, StringComparison.OrdinalIgnoreCase)
                    || candidate.StartTime < notBefore)
                {
                    return 0;                       // the PID was reused by something else
                }

                candidate.Kill(entireProcessTree: true);
                return 1;
            }
            catch (InvalidOperationException)
            {
                return 0;                           // exited between the lookup and the kill
            }
            catch (NotSupportedException)
            {
                return 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return 0;                           // access denied: not ours to kill either
            }
        }
    }

    private static void TryCloseStdin(Process process)
    {
        try
        {
            if (process.StartInfo.RedirectStandardInput)
            {
                process.StandardInput.Close();
            }
        }
        catch (InvalidOperationException)
        {
            // Already closed, or never redirected.
        }
        catch (IOException)
        {
            // The pipe is already gone.
        }
    }

    /// <summary>
    /// A Windows job with <c>KILL_ON_JOB_CLOSE</c>. Closing the handle kills every process
    /// still assigned — which is what Task Manager kill of Versio must do to hidden CODESYS.
    /// </summary>
    public sealed class KillOnCloseJob : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;

        public KillOnCloseJob()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };

            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInfoClass, ptr, (uint)size))
                {
                    CloseHandle(_handle);
                    _handle = IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>True when the OS accepted the job. Tests skip when this is false.</summary>
        public bool IsActive => _handle != IntPtr.Zero;

        public void Assign(Process process)
        {
            if (!OperatingSystem.IsWindows() || _handle == IntPtr.Zero || process.HasExited)
            {
                return;
            }

            try
            {
                AssignProcessToJobObject(_handle, process.Handle);
            }
            catch (InvalidOperationException)
            {
                // Exited between HasExited and Handle.
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr job, int infoClass, IntPtr info, uint infoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public IntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
