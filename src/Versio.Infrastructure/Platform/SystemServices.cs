using System.Diagnostics;
using Versio.Core.Abstractions;
using Versio.Core.Locking;

namespace Versio.Infrastructure.Platform;

/// <summary>Real wall-clock time.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Machine identity for lock ownership.
/// </summary>
/// <remarks>
/// <see cref="BootId"/> is what lets a lock survive a reboot without being either lost or
/// falsely trusted: a different boot time proves the owning process is definitively gone.
/// It is derived from <c>Environment.TickCount64</c> and rounded, because the computed
/// boot instant drifts by milliseconds between calls and an exact comparison would make
/// every lock look foreign.
/// </remarks>
public sealed class MachineIdentity : IMachineIdentity
{
    private readonly Lazy<DateTimeOffset> _bootId = new(() =>
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var boot = DateTimeOffset.UtcNow - uptime;
        // Round to the minute: the derived instant jitters, the actual boot does not.
        return new DateTimeOffset(
            boot.Year, boot.Month, boot.Day, boot.Hour, boot.Minute, 0, TimeSpan.Zero);
    });

    public string MachineName => Environment.MachineName;

    public string UserName => Environment.UserName;

    public int CurrentProcessId => Environment.ProcessId;

    public DateTimeOffset BootId => _bootId.Value;
}

/// <summary>
/// Tells lock recovery whether the recorded owner is still running.
/// </summary>
/// <remarks>
/// Checks the process name as well as the id. PIDs are recycled, and mistaking an unrelated
/// process for a live Versio would leave a lock permanently unrecoverable.
/// </remarks>
public sealed class ProcessProbe(string expectedProcessName = "Versio") : IProcessProbe
{
    /// <summary>True when any process with this id is running.</summary>
    /// <remarks>
    /// No name check: the caller is asking about somebody else's process, typically a vendor
    /// IDE. <c>GetProcessById</c> throws for an id nobody holds, which is the answer.
    /// </remarks>
    public bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;   // no such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>True when any process of this name is running, whoever started it.</summary>
    /// <remarks>
    /// By name, deliberately: the caller is asking whether a vendor IDE is running at all,
    /// for a marker that names no pid. Every handle is disposed — <c>GetProcessesByName</c>
    /// hands back one per match, and a leak here would be one per occupancy check.
    /// </remarks>
    public bool IsAnyProcessRunning(string processName)
    {
        Process[] matches;
        try
        {
            matches = Process.GetProcessesByName(processName);
        }
        catch (InvalidOperationException)
        {
            return true;    // could not look; a marker that cannot be checked is believed
        }

        try
        {
            return matches.Length > 0;
        }
        finally
        {
            foreach (var match in matches)
            {
                match.Dispose();
            }
        }
    }

    public bool IsVersioProcessAlive(int processId, string sessionId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited &&
                   process.ProcessName.StartsWith(expectedProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;   // no such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
