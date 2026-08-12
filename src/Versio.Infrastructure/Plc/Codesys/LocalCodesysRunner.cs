using System.Diagnostics;
using System.Text;
using Versio.Core.Abstractions;
using Versio.Core.Logging;
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
    IVersioLogger? logger = null) : ICodesysRunner
{
    /// <summary>Global so two Versio windows cannot launch competing CODESYS instances.</summary>
    private const string GlobalMutexName = @"Global\Versio.Codesys.SingleInstance";

    private static readonly TimeSpan MutexWait = TimeSpan.FromMinutes(15);

    private readonly IVersioLogger _log = logger ?? NullLogger.Instance;

    public async Task<CodesysResult> RunAsync(
        CodesysJob job,
        IProgress<CodesysProgress>? progress,
        CancellationToken cancellationToken)
    {
        // The job carries its own id, and it is the same id the Python script stamps on its
        // JSON-lines output — so opening the scope here correlates the C# side, the
        // subprocess chatter and the heartbeat into one filterable stream.
        using var scope = _log.BeginOperation(job.OperationId);

        var script = ScriptFor(job.Operation);
        var scriptPath = scripts.PathFor(script);
        var jobFile = WriteJobFile(job);

        _log.Info(LogCategory.Codesys,
            $"{job.Operation} of {job.Projects.Count} project(s) using {script}" +
            (string.IsNullOrWhiteSpace(job.Profile) ? string.Empty : $" [{job.Profile}]"));

        foreach (var project in job.Projects)
        {
            _log.Debug(LogCategory.Codesys,
                $"{project.BindingId}: {project.ProjectPath} ↔ {project.Path}");
        }

        _log.Debug(LogCategory.Codesys, $"job file {jobFile}, timeout {job.Timeout}");

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
        var waited = Stopwatch.StartNew();
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
                _log.Warn(LogCategory.Codesys,
                    "recovered an abandoned CODESYS lock — a previous Versio process died " +
                    "while holding it");
            }

            waited.Stop();

            if (!acquired)
            {
                _log.Error(LogCategory.Codesys,
                    $"gave up waiting for the machine-wide CODESYS lock after " +
                    $"{waited.Elapsed.TotalMinutes:F0} minutes");
                throw new TimeoutException(
                    "another CODESYS operation has been running for over 15 minutes");
            }

            // Only when it actually blocked. CODESYS is serialised machine-wide, so a wait
            // here means another Versio — possibly in another window, possibly another user
            // session — is mid-export, and that is the explanation for an export that
            // appeared to hang before it had even started.
            if (waited.Elapsed > TimeSpan.FromSeconds(1))
            {
                _log.Info(LogCategory.Codesys,
                    $"waited {waited.Elapsed.TotalSeconds:F1}s for the machine-wide CODESYS lock");
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

            // CreateNoWindow alone is not enough here. It sets CREATE_NO_WINDOW, which
            // suppresses the console a CONSOLE subsystem child would allocate - and
            // CODESYS.exe is a GUI application. Under --noUI it still creates a top-level
            // window, and the engineer sees a CODESYS shell flash up and sit there for the
            // twenty seconds of an export they never asked to watch.
            //
            // WindowStyle.Hidden makes .NET pass STARTF_USESHOWWINDOW with SW_HIDE in the
            // STARTUPINFO, which is the request a GUI child honours for its first window.
            // The two flags cover different subsystems, so both are set deliberately.
            WindowStyle = ProcessWindowStyle.Hidden,

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

        // The exact command line, because CODESYS argument quoting is the single most
        // reliable source of "exits 1 in under a second having done nothing" and the only
        // way to diagnose it is to see what was actually passed.
        _log.Debug(LogCategory.Codesys, $"exec {codesysExecutablePath} {psi.Arguments}");

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
            if (timedOut)
            {
                _log.Error(LogCategory.Codesys,
                    $"timed out after {job.Timeout} — killing the CODESYS process tree. A " +
                    "--noUI CODESYS sitting on a hidden modal never exits on its own");
            }
            else
            {
                _log.Warn(LogCategory.Codesys, "cancelled — killing the CODESYS process tree");
            }

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

        _log.Log(exitCode == 0 && !timedOut ? LogLevel.Information : LogLevel.Error,
            LogCategory.Codesys,
            $"exit={exitCode} in {stopwatch.Elapsed.TotalSeconds:F1}s " +
            $"events={output.Events.Count} warnings={output.Warnings.Count()}");

        if (missingProfile)
        {
            // A specific, recognisable failure with a specific fix, and one nobody guesses:
            // the profile string cannot be derived from the install folder name.
            _log.Error(LogCategory.Codesys,
                $"CODESYS rejected the profile '{job.Profile}'. The profile name is not the " +
                @"install folder name — read it from …\CODESYS\Profiles\*.profile.xml");
        }

        // CODESYS's own output, kept at Debug. It is verbose and mostly uninteresting until
        // an export fails for a reason our protocol did not model, at which point it is the
        // only evidence there is.
        foreach (var line in output.Chatter)
        {
            _log.Debug(LogCategory.Codesys, $"› {line}");
        }

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
