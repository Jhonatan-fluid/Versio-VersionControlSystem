using System.Diagnostics;

namespace Versio.Plc.TiaPortal.Host;

/// <summary>
/// Quit COM if Versio dies or Stop closes stdin. Host.exe is not in the kill-on-close
/// job, because killing it before Dispose is how Portal survives a parent death.
/// </summary>
internal sealed class HostLifetime : IDisposable
{
    private readonly Action _stop;
    private readonly CancellationTokenSource _cts = new();
    private int _stopping;

    public static HostLifetime Attach(Action stop) => new(stop);

    private HostLifetime(Action stop)
    {
        _stop = stop;
        var token = _cts.Token;
        new Thread(() => WatchParent(token)) { IsBackground = true, Name = "versio-parent" }.Start();
        new Thread(() => WatchStdin(token)) { IsBackground = true, Name = "versio-stdin" }.Start();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _cts.Cancel();
    }

    private void WatchParent(CancellationToken token)
    {
        var raw = Environment.GetEnvironmentVariable("VERSIO_PARENT_PID");
        if (!int.TryParse(raw, out var pid) || pid <= 0)
        {
            return;
        }

        try
        {
            using var parent = Process.GetProcessById(pid);
            while (!token.IsCancellationRequested && !parent.WaitForExit(500))
            {
            }

            if (!token.IsCancellationRequested)
            {
                Stop();
            }
        }
        catch (ArgumentException)
        {
            // Parent already gone.
            Stop();
        }
    }

    private void WatchStdin(CancellationToken token)
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[1];
            while (!token.IsCancellationRequested)
            {
                var n = stdin.Read(buffer, 0, 1);
                if (n <= 0)
                {
                    break;
                }
            }
        }
        catch (IOException)
        {
            // Pipe closed.
        }

        if (!token.IsCancellationRequested)
        {
            Stop();
        }
    }

    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        try
        {
            Console.Error.WriteLine(
                "host: Versio closed stdin or the parent died — quitting COM");
        }
        catch
        {
            // stderr may already be gone; Quit still has to run.
        }

        try
        {
            _stop();
        }
        catch
        {
            // Teardown is best-effort; Environment.Exit still runs.
        }

        Environment.Exit(1);
    }
}
