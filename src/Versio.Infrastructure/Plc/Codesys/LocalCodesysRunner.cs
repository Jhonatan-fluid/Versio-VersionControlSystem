using System.Diagnostics;
using System.Text;
using Versio.Core.Abstractions;
using Versio.Core.Plc.Codesys;

namespace Versio.Infrastructure.Plc.Codesys;

/// <summary>
/// Runs a Versio script inside a headless CODESYS process.
/// </summary>
/// <remarks>
/// <para>
/// Everything here exists because of a measured failure mode:
/// </para>
/// <list type="bullet">
///   <item><b>Async pipe draining.</b> Reading a pipe to completion before
///     <c>WaitForExit</c> deadlocks once output exceeds the buffer, and a 2.5 MB
///     visualisation export exceeds it comfortably.</item>
///   <item><b>Job file, not command-line arguments.</b> Windows
///     <c>--scriptargs</c> quoting is genuinely awful; only one file path crosses the
///     boundary. The file is UTF-8 <b>without BOM</b>, because IronPython 2.7's
///     <c>json.load</c> fails on a BOM with "No JSON object could be decoded" — measured.</item>
///   <item><b>Process-tree kill on timeout.</b> A <c>--noUI</c> CODESYS sitting on a hidden
///     modal never exits, and it spawns children that outlive a plain
///     <c>Process.Kill()</c>.</item>
///   <item><b>Machine-wide mutex.</b> CODESYS is a desktop IDE, not a server; two
///     concurrent headless instances on one profile are not safe. This serialises across
///     every Versio process on the machine, not just within one.</item>
/// </list>
/// </remarks>
public sealed class LocalCodesysRunner(
    string codesysExecutablePath,
    ScriptExtractor scripts,
    Action<string>? log = null) : ICodesysRunner
{
    /// <summary>Global so two Versio windows cannot launch competing CODESYS instances.</summary>
    private const string GlobalMutexName = @"Global\Versio.Codesys.SingleInstance";

    private static readonly TimeSpan MutexWait = TimeSpan.FromMinutes(15);

    public async Task<CodesysResult> RunAsync(
        CodesysJob job,
        IProgress<CodesysProgress>? progress,
        CancellationToken cancellationToken)
    {
        var script = ScriptFor(job.Operation);
        var scriptPath = scripts.PathFor(script);
        var jobFile = WriteJobFile(job);

        try
        {
            // A named Mutex has THREAD AFFINITY: it must be released by the thread that
            // acquired it. Acquiring inside Task.Run and releasing in a finally after an
            // await lands on a different pool thread and throws
            //   ApplicationException: Object synchronization method was called from an
            //   unsynchronized block of code.
            //
            // So the whole guarded section runs on one dedicated long-running thread.
            // A named Semaphore would avoid the affinity problem but gives up abandonment
            // recovery - if a process died holding it the count would never come back, and
            // every future export on the machine would block forever. Mutex reports
            // AbandonedMutexException instead, which is exactly the case we must survive.
            return await Task.Factory.StartNew(
                () => RunGuarded(job, scriptPath, jobFile, progress, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(jobFile);
        }
    }

    /// <summary>Synchronous on purpose: it runs on its own long-running thread so the
    /// mutex is acquired and released by the same thread.</summary>
    private CodesysResult RunGuarded(
        CodesysJob job, string scriptPath, string jobFile,
        IProgress<CodesysProgress>? progress, CancellationToken cancellationToken)
    {
        using var mutex = new Mutex(false, GlobalMutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(MutexWait);
            }
            catch (AbandonedMutexException)
            {
                // A previous Versio died holding it. We are now the owner, and that is
                // safe: CODESYS state lives in files, not in the mutex.
                acquired = true;
                log?.Invoke($"[{job.OperationId}] recovered an abandoned CODESYS lock");
            }

            if (!acquired)
            {
                throw new TimeoutException(
                    "another CODESYS operation has been running for over 15 minutes");
            }

            // Blocking on this dedicated thread is fine and keeps acquire and release
            // together: the work is a long external process, not CPU-bound.
            return ExecuteAsync(job, scriptPath, jobFile, progress, cancellationToken)
                .GetAwaiter().GetResult();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private async Task<CodesysResult> ExecuteAsync(
        CodesysJob job, string scriptPath, string jobFile,
        IProgress<CodesysProgress>? progress, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = codesysExecutablePath,
            // NOTE: Arguments, not ArgumentList.
            //
            // ArgumentList applies .NET's standard quoting, which wraps the WHOLE argument
            // when it contains a space: "--profile=CODESYS V3.5 SP22 Patch 2". CODESYS does
            // not parse that - it exits 1 in under a second having done nothing. It expects
            // the quotes around the VALUE only: --profile="CODESYS V3.5 SP22 Patch 2".
            //
            // Building the string by hand is the reason the job payload is a file: exactly
            // one path crosses this boundary, and none of these values can contain a quote.
            Arguments = BuildArguments(job.Profile, scriptPath, jobFile),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // Our scripts JSON-escape non-ASCII, so this is belt-and-braces rather than a
            // fix for an observed failure - but the equivalent assumption did break the git
            // client on a path containing "ü", and CODESYS's own chatter is localised.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        var buffer = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        var gate = new object();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        void OnData(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            lock (gate)
            {
                buffer.AppendLine(e.Data);
            }

            ReportProgress(job, e.Data, stopwatch.Elapsed, progress);
        }

        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;

        log?.Invoke($"[{job.OperationId}] exec {codesysExecutablePath} {psi.Arguments}");

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        using var timeoutCts = new CancellationTokenSource(job.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            log?.Invoke($"[{job.OperationId}] " +
                        (timedOut ? "TIMEOUT" : "cancelled") + " — killing process tree");
            KillTree(process);

            if (!timedOut)
            {
                throw;
            }
        }

        // WaitForExitAsync can return before the async readers have flushed.
        await Task.Run(() => process.WaitForExit(), CancellationToken.None).ConfigureAwait(false);
        stopwatch.Stop();

        string stdout;
        lock (gate)
        {
            stdout = buffer.ToString();
        }

        var output = ScriptProtocol.Parse(stdout);
        var missingProfile = output.Chatter.Any(ProfileResolver.IsMissingProfileError);
        var exitCode = timedOut ? -1 : SafeExitCode(process);

        log?.Invoke($"[{job.OperationId}] exit={exitCode} in {stopwatch.Elapsed.TotalSeconds:F1}s " +
                    $"events={output.Events.Count} warnings={output.Warnings.Count()}");

        return new CodesysResult
        {
            ExitCode = exitCode,
            TimedOut = timedOut,
            Duration = stopwatch.Elapsed,
            Output = output,
            MissingProfile = missingProfile,
        };
    }

    private static void ReportProgress(
        CodesysJob job, string line, TimeSpan elapsed, IProgress<CodesysProgress>? progress)
    {
        if (progress is null || !line.Contains(ScriptProtocol.Marker, StringComparison.Ordinal))
        {
            return;
        }

        var parsed = ScriptProtocol.Parse(line);
        foreach (var evt in parsed.Events)
        {
            switch (evt.Type)
            {
                case "progress":
                    progress.Report(new CodesysProgress(job.OperationId, "working",
                        evt.Int("done"), evt.Int("total"), evt.String("obj"), elapsed));
                    break;
                case "phase":
                    progress.Report(new CodesysProgress(job.OperationId,
                        evt.String("name") ?? "phase", null, evt.Int("files"), null, elapsed));
                    break;
                case "opened":
                    progress.Report(new CodesysProgress(job.OperationId, "opened",
                        null, null, evt.String("path"), elapsed));
                    break;
            }
        }
    }

    /// <summary>
    /// Build the command line in the exact shape CODESYS accepts: quotes around each
    /// value, never around the whole <c>--name=value</c> pair.
    /// </summary>
    /// <remarks>
    /// A profile is omitted entirely when blank rather than passed as <c>--profile=""</c>,
    /// so the resulting failure is CODESYS's own "you must specify a profile" refusal —
    /// which the runner recognises and reports — instead of an opaque parse error.
    /// </remarks>
    internal static string BuildArguments(string profile, string scriptPath, string jobFile)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile))
        {
            parts.Add($"--profile=\"{profile}\"");
        }

        parts.Add("--noUI");
        parts.Add($"--runscript=\"{scriptPath}\"");
        parts.Add($"--scriptargs:\"{jobFile}\"");
        return string.Join(' ', parts);
    }

    private static string ScriptFor(CodesysOperation operation) => operation switch
    {
        CodesysOperation.Export => "versio_export.py",
        CodesysOperation.Import => "versio_import.py",
        CodesysOperation.Probe => "versio_probe.py",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static string WriteJobFile(CodesysJob job)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Versio", "jobs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"job-{job.OperationId}-{Guid.NewGuid():N}.json");

        // UTF-8 WITHOUT BOM. IronPython 2.7's json.load fails on a BOM with
        // "No JSON object could be decoded".
        File.WriteAllText(path, job.ToJson(), new UTF8Encoding(false));
        return path;
    }

    private static void KillTree(Process process)
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
            // Platform without tree-kill support; the single kill above is the best we can do.
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover job file is harmless; failing the operation over it is not.
        }
    }
}
